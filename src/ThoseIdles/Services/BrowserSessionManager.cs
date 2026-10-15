using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Windows.Threading;

namespace ThoseIdles.Services;

/// <summary>Coordena as quatro sessões: abrir/fechar todas, queda do navegador, rede e encerramento.</summary>
public sealed class BrowserSessionManager : IDisposable
{
    private readonly ProfileManager _profiles;
    private readonly Dispatcher _dispatcher;
    private readonly HashSet<int> _runningBeforeCrash = new();
    private bool _shuttingDown;

    public IReadOnlyList<BrowserSession> Sessions { get; }
    public ProfileManager Profiles => _profiles;

    private readonly AppSettings _settings;

    public BrowserSessionManager(AppSettings settings, ProfileManager profiles, NavigationPolicy policy, HuntExtensionService? hunt = null)
    {
        _settings = settings;
        _profiles = profiles;
        _dispatcher = Dispatcher.CurrentDispatcher;
        Sessions = settings.Accounts
            .OrderBy(a => a.Slot)
            .Select(a => new BrowserSession(a, profiles, settings, policy, hunt))
            .ToList();

        _profiles.EnvironmentLost += OnEnvironmentLost;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
    }

    public BrowserSession this[int slot] => Sessions[slot - 1];

    /// <summary>
    /// Abre as contas habilitadas. O ambiente é criado uma vez; os quatro controllers são criados em
    /// paralelo sobre o mesmo processo de navegador.
    /// </summary>
    public Task StartAllAsync() =>
        Task.WhenAll(Sessions.Where(s => s.Account.Enabled && !s.IsRunning).Select(s => s.StartAsync()));

    public void CloseAll()
    {
        foreach (var s in Sessions) s.Close();
    }

    public async Task RestartAllAsync()
    {
        CloseAll();
        await StartAllAsync();
    }

    /// <summary>
    /// Aplica a extensão /hunt às contas abertas. Se o ambiente atual foi criado com a opção de
    /// extensões diferente da desejada, fecha as contas abertas, espera o navegador sair e as
    /// reabre num ambiente novo (as sessões continuam logadas: ficam no disco).
    /// </summary>
    public async Task ApplyHuntExtensionAsync()
    {
        var wantExtensions = _settings.EnableHuntExtension;
        if (_profiles.Environment is not null && _profiles.ExtensionsEnabled != wantExtensions)
        {
            var reopen = Sessions.Where(s => s.IsRunning).Select(s => s.Slot).ToList();
            LoggingService.Log.Info($"Recriando o ambiente WebView2 (extensões {(wantExtensions ? "ligadas" : "desligadas")}); reabrindo contas {string.Join(",", reopen)}.");
            CloseAll();
            await _profiles.WaitForShutdownAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(reopen.Select(slot => this[slot].StartAsync()));
            return;
        }
        foreach (var s in Sessions.Where(s => s.IsRunning))
            await s.ApplyHuntExtensionAsync();
    }

    private void OnEnvironmentLost(object? sender, bool failed)
    {
        if (_shuttingDown) return;
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnEnvironmentLost(sender, failed));
            return;
        }
        if (!failed) return; // encerramento normal: todas as contas foram fechadas.

        LoggingService.Log.Error("O processo do navegador WebView2 caiu. Recriando o ambiente e as contas que estavam abertas.");
        _runningBeforeCrash.Clear();
        foreach (var s in Sessions.Where(s => s.IsRunning))
        {
            _runningBeforeCrash.Add(s.Slot);
            s.OnEnvironmentLost();
        }

        var timer = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, async (t, _) =>
        {
            ((DispatcherTimer)t!).Stop();
            if (_shuttingDown) return;
            var slots = _runningBeforeCrash.ToList();
            _runningBeforeCrash.Clear();
            await Task.WhenAll(slots.Select(slot => this[slot].StartAsync()));
        }, _dispatcher);
        timer.Start();
    }

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e)
    {
        // Disparado numa thread do pool.
        _dispatcher.BeginInvoke(() =>
        {
            LoggingService.Log.Info($"Rede do Windows {(e.IsAvailable ? "disponível" : "indisponível")}.");
            foreach (var s in Sessions) s.OnNetworkAvailabilityChanged(e.IsAvailable);
        });
    }

    /// <summary>
    /// Encerramento limpo: fecha os controllers, espera o processo do navegador sair e, se ele não
    /// sair a tempo, encerra a árvore (evita msedgewebview2.exe órfãos).
    /// </summary>
    public async Task ShutdownAsync(TimeSpan timeout)
    {
        _shuttingDown = true;
        int? browserPid = null;
        foreach (var s in Sessions)
        {
            try
            {
                if (s.Core is { } core) { browserPid = (int)core.BrowserProcessId; break; }
            }
            catch { }
        }

        CloseAll();

        if (browserPid is null) return;
        try
        {
            using var browser = Process.GetProcessById(browserPid.Value);
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await browser.WaitForExitAsync(cts.Token);
                LoggingService.Log.Info("Processo do navegador encerrado normalmente.");
            }
            catch (OperationCanceledException)
            {
                LoggingService.Log.Warn($"Navegador (pid {browserPid}) não saiu em {timeout.TotalSeconds:0}s; encerrando a árvore.");
                browser.Kill(entireProcessTree: true);
            }
        }
        catch (ArgumentException)
        {
            // Já saiu.
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"Encerramento do navegador: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _profiles.EnvironmentLost -= OnEnvironmentLost;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
    }
}
