using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ThoseIdles.Mvvm;

namespace ThoseIdles.Services;

public enum SessionStatus
{
    /// <summary>Instância fechada pelo usuário (sem processos).</summary>
    Closed,
    Loading,
    /// <summary>Página aberta, mas nenhum WebSocket do jogo aberto (ex.: tela de login).</summary>
    AwaitingConnection,
    /// <summary>Página aberta; monitor de conexão desligado, então a conexão não foi verificada.</summary>
    Loaded,
    /// <summary>WebSocket do servidor do jogo aberto (handshake 101 confirmado).</summary>
    Connected,
    /// <summary>Estava conectada e o socket caiu; o próprio jogo tenta reconectar.</summary>
    Disconnected,
    /// <summary>Sem rede no Windows.</summary>
    Offline,
    Error,
}

/// <summary>Papel visual do painel. Só muda a apresentação; nunca pausa a conta.</summary>
public enum PanelRole
{
    Grid,
    Focused,
    Thumbnail,
}

/// <summary>
/// Ciclo de vida de UMA conta: cria o controle WebView2 no perfil da conta, navega, acompanha estado,
/// recupera falhas e libera tudo ao fechar. Todas as chamadas acontecem na thread da UI.
/// </summary>
public sealed partial class BrowserSession : ObservableObject
{
    private static readonly int[] RetryDelaysSeconds = { 5, 15, 30, 60, 120, 300 };

    private readonly ProfileManager _profiles;
    private readonly AppSettings _settings;
    private readonly NavigationPolicy _policy;
    private readonly Dispatcher _dispatcher;

    private WebView2? _webView;
    private CoreWebView2? _core;
    private CoreWebView2DevToolsProtocolEventReceiver? _wsCreated, _wsHandshake, _wsClosed;

    private SessionStatus _status = SessionStatus.Closed;
    private string _statusDetail = "Fechada";
    private int _generation;
    private int _retryAttempt;
    private DispatcherTimer? _retryTimer;
    private DispatcherTimer? _unresponsiveTimer;
    private bool _pageLoaded;
    private bool _wasConnected;
    private bool _networkAvailable = true;
    private ulong _blockedNavigationId;
    private DateTime _lastExternalOpenUtc;
    private TaskCompletionSource<bool>? _navigationWaiter;
    private PanelRole _role = PanelRole.Grid;
    private double _zoom = 1.0;
    private Stopwatch? _startWatch;
    private bool _reportedLoaded, _reportedConnected;

    /// <summary>requestIds de WebSockets do jogo: criados (aguardando handshake) e abertos.</summary>
    private readonly HashSet<string> _pendingSockets = new();
    private readonly HashSet<string> _openSockets = new();

    private readonly HuntExtensionService? _hunt;
    private bool _huntActive;

    public BrowserSession(AccountSettings account, ProfileManager profiles, AppSettings settings, NavigationPolicy policy,
        HuntExtensionService? hunt = null)
    {
        Account = account;
        _profiles = profiles;
        _settings = settings;
        _policy = policy;
        _hunt = hunt;
        _dispatcher = Dispatcher.CurrentDispatcher;
    }

    /// <summary>As configurações desta conta mudaram (ex.: id da extensão instalada) e devem ser salvas.</summary>
    public event EventHandler? SettingsChanged;

    /// <summary>A extensão /hunt está carregada e ligada nesta conta agora.</summary>
    public bool HuntActive
    {
        get => _huntActive;
        private set => Set(ref _huntActive, value);
    }

    public AccountSettings Account { get; }
    public int Slot => Account.Slot;
    public string ProfileName => Account.ProfileName;

    /// <summary>O controle WPF atual (null quando fechada). A view apenas o hospeda.</summary>
    public WebView2? WebView
    {
        get => _webView;
        private set
        {
            if (Set(ref _webView, value)) OnPropertyChanged(nameof(IsRunning));
        }
    }

    public CoreWebView2? Core => _core;
    public bool IsRunning => _webView is not null || _eco is not null;

    public SessionStatus Status
    {
        get => _status;
        private set => Set(ref _status, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => Set(ref _statusDetail, value);
    }

    /// <summary>Id do frame principal, usado pelo MemoryMonitor para atribuir o renderer à conta.</summary>
    public uint MainFrameId { get; private set; }

    public int OpenGameSockets => _openSockets.Count;

    /// <summary>Marcos de inicialização (para o benchmark): "carregada" e "conectada".</summary>
    public event EventHandler<(string Milestone, TimeSpan Elapsed)>? Milestone;

    // ------------------------------------------------------------------ ciclo de vida

    public async Task StartAsync()
    {
        if (_webView is not null || _eco is not null) return;
        var gen = ++_generation;
        CancelTimers();
        _retryAttempt = 0;
        _startWatch = Stopwatch.StartNew();
        _reportedLoaded = _reportedConnected = false;
        SetStatus(SessionStatus.Loading, "Iniciando navegador…");

        // Conta em modo econômico: sem página do jogo; o cliente leve mantém a conta conectada.
        if (TryStartEconomyFromSaved())
        {
            if (_settings.ReopenLives && Account.LiveChannels.Count > 0) await OpenSavedLivesAsync();
            return;
        }

        var wv = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 22, 22, 26),
            ZoomFactor = _zoom,
        };
        wv.ZoomFactorChanged += OnZoomFactorChanged;
        WebView = wv; // a view coloca o controle na árvore visual; o HWND nasce aí.

        try
        {
            var env = await _profiles.GetEnvironmentAsync();
            if (gen != _generation) return;

            await wv.EnsureCoreWebView2Async(env, _profiles.CreateControllerOptions(env, Slot));
            if (gen != _generation) return;

            _core = wv.CoreWebView2;
            await ConfigureAsync(_core);
            if (gen != _generation) return;

            // Antes da navegação: os scripts da extensão rodam em document_start.
            await SyncHuntExtensionAsync(_core);
            if (gen != _generation) return;

            // Voltando do modo econômico: a página recebe o token renovado antes de carregar.
            await ApplyPendingTokenAsync(_core);
            if (gen != _generation) return;

            ApplyView(_role, _zoom);
            LoggingService.Log.Info($"[conta{Slot}] WebView2 pronto (perfil {ProfileName}, browser pid {_core.BrowserProcessId}).");
            _core.Navigate(_settings.StartUrl);

            // A live que estava aberta nesta conta volta junto (opção "reabrir a live com a conta").
            if (_settings.ReopenLives && Account.LiveChannels.Count > 0)
                await OpenSavedLivesAsync();
        }
        catch (Exception ex) when (gen == _generation)
        {
            LoggingService.Log.Error($"[conta{Slot}] Falha ao iniciar", ex);
            DetachAndDispose();
            SetStatus(SessionStatus.Error, $"Falha ao iniciar: {ex.Message}");
            ScheduleRetry();
        }
        catch (Exception ex)
        {
            // A sessão foi fechada/reiniciada durante a inicialização; resultado descartado.
            LoggingService.Log.Info($"[conta{Slot}] Inicialização descartada ({ex.GetType().Name}).");
        }
    }

    /// <summary>Fecha somente esta conta: descarta o controller, o que encerra o renderer dela.</summary>
    public void Close(string reason = "Fechada")
    {
        _generation++;
        CancelTimers();
        DetachAndDispose();
        _ = StopEconomyClientAsync(); // o modo fica salvo: a conta reabre em modo econômico
        _wasConnected = false;
        SetStatus(SessionStatus.Closed, reason);
        LoggingService.Log.Info($"[conta{Slot}] Fechada ({reason}).");
    }

    public async Task RestartAsync()
    {
        LoggingService.Log.Info($"[conta{Slot}] Reiniciando.");
        Close("Reiniciando…");
        await StartAsync();
    }

    public async Task ReloadAsync()
    {
        if (_eco is not null)
        {
            // Modo econômico: "recarregar" = reconectar o cliente leve.
            await StopEconomyClientAsync();
            if (!TryStartEconomyFromSaved()) await StartAsync();
            return;
        }
        if (_core is null)
        {
            await StartAsync();
            return;
        }
        CancelTimers();
        // Se uma versão nova da extensão foi baixada, ela entra nesta recarga.
        await SyncHuntExtensionAsync(_core);
        _core?.Reload();
    }

    public void OpenDevTools()
    {
        if (_settings.DeveloperMode) _core?.OpenDevToolsWindow();
    }

    public void ApplyDeveloperMode()
    {
        if (_core is not null) _core.Settings.AreDevToolsEnabled = _settings.DeveloperMode;
    }

    /// <summary>
    /// Apaga cookies, armazenamento e cache SOMENTE do perfil desta conta. A página sai para about:blank
    /// antes, para que o jogo em memória não regrave o token logo após a limpeza.
    /// </summary>
    public async Task ClearDataAsync()
    {
        LoggingService.Log.Info($"[conta{Slot}] Limpando dados do perfil {ProfileName}.");
        // Limpar desloga: o modo econômico (e as credenciais guardadas dele) vai junto.
        if (_eco is not null || Account.EconomyMode || Account.EconomySessionProtected is not null)
        {
            Account.EconomySessionProtected = null;
            if (_eco is not null) await ExitEconomyAsync();
            Account.EconomyMode = false;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        if (_core is null) await StartAsync();
        var core = _core ?? throw new InvalidOperationException("Não foi possível iniciar a conta para limpar os dados.");

        CancelTimers();
        CloseLive(forget: true); // a live usa o mesmo perfil (login da Twitch), que vai ser apagado
        SetStatus(SessionStatus.Loading, "Limpando dados…");
        await NavigateAndWaitAsync(core, "about:blank", TimeSpan.FromSeconds(10));
        await core.Profile.ClearBrowsingDataAsync();
        _openSockets.Clear();
        _pendingSockets.Clear();
        _wasConnected = false;
        LoggingService.Log.Info($"[conta{Slot}] Dados do perfil {ProfileName} apagados.");
        core.Navigate(_settings.StartUrl);
    }

    /// <summary>Liga/desliga a extensão /hunt só nesta conta e recarrega a página dela.</summary>
    public async Task SetHuntEnabledAsync(bool enabled)
    {
        Account.HuntEnabled = enabled;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        await ApplyHuntExtensionAsync();
    }

    /// <summary>Reaplica a extensão (nova versão baixada, liga/desliga) e recarrega se a conta estiver aberta.</summary>
    public async Task ApplyHuntExtensionAsync()
    {
        if (_core is not { } core) return;
        await SyncHuntExtensionAsync(core);
        if (_core is not null && _pageLoaded) _core.Reload();
    }

    /// <summary>
    /// Deixa o perfil desta conta com exatamente a versão atual da extensão, no estado ligado/desligado
    /// escolhido. Falhas não impedem a conta de abrir: o jogo roda sem a extensão.
    /// </summary>
    private async Task SyncHuntExtensionAsync(CoreWebView2 core)
    {
        if (_hunt is null || !_profiles.ExtensionsEnabled)
        {
            HuntActive = false;
            return;
        }
        try
        {
            var folder = _settings.EnableHuntExtension ? _hunt.InstalledFolder : null;
            var profile = core.Profile;
            CoreWebView2BrowserExtension? current = null;
            foreach (var ext in await profile.GetBrowserExtensionsAsync())
            {
                var ours = ext.Id == Account.HuntExtensionId || ext.Name == HuntExtensionService.ExtensionName;
                if (!ours) continue;
                var upToDate = folder is not null && ext.Id == Account.HuntExtensionId &&
                               string.Equals(Account.HuntExtensionFolder, folder, StringComparison.OrdinalIgnoreCase);
                if (current is null && upToDate)
                {
                    current = ext;
                }
                else
                {
                    await ext.RemoveAsync();
                    LoggingService.Log.Info($"[conta{Slot}] Versão antiga da extensão removida do perfil.");
                }
            }

            if (current is null && folder is not null)
            {
                current = await profile.AddBrowserExtensionAsync(folder);
                Account.HuntExtensionId = current.Id;
                Account.HuntExtensionFolder = folder;
                SettingsChanged?.Invoke(this, EventArgs.Empty);
                LoggingService.Log.Info($"[conta{Slot}] Extensão instalada no perfil {ProfileName} ({Path.GetFileName(folder)}).");
            }

            if (current is not null && current.IsEnabled != Account.HuntEnabled)
                await current.EnableAsync(Account.HuntEnabled);
            HuntActive = current is not null && Account.HuntEnabled;
        }
        catch (Exception ex)
        {
            HuntActive = false;
            LoggingService.Log.Error($"[conta{Slot}] Não foi possível aplicar a extensão", ex);
        }
    }

    /// <summary>O jogador mudou o zoom desta conta (Ctrl + roda do mouse / Ctrl +/−) fora da miniatura.</summary>
    public event EventHandler<double>? UserZoomChanged;

    private bool _applyingZoom;

    private void OnZoomFactorChanged(object? sender, EventArgs e)
    {
        if (_applyingZoom || _webView is null || _role == PanelRole.Thumbnail) return;
        var z = Math.Round(_webView.ZoomFactor, 2);
        if (Math.Abs(z - _zoom) < 0.005) return;
        _zoom = z;
        UserZoomChanged?.Invoke(this, z);
    }

    /// <summary>Muda zoom/meta de memória conforme o papel visual. Nunca oculta o controle.</summary>
    public void ApplyView(PanelRole role, double zoom)
    {
        _role = role;
        _zoom = Math.Clamp(zoom, 0.25, 3.0);
        if (_webView is null) return;
        try
        {
            if (Math.Abs(_webView.ZoomFactor - _zoom) > 0.001)
            {
                _applyingZoom = true;
                try { _webView.ZoomFactor = _zoom; }
                finally { _applyingZoom = false; }
            }
            if (_core is not null)
            {
                _core.MemoryUsageTargetLevel = role == PanelRole.Thumbnail && _settings.LowMemoryTargetForThumbnails
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[conta{Slot}] ApplyView falhou: {ex.Message}");
        }
    }

    /// <summary>O processo do navegador caiu: os controles estão mortos e precisam ser recriados.</summary>
    public void OnEnvironmentLost()
    {
        if (_webView is null) return;
        _generation++;
        CancelTimers();
        DetachAndDispose();
        SetStatus(SessionStatus.Error, "O processo do navegador terminou");
    }

    public void OnNetworkAvailabilityChanged(bool available)
    {
        _networkAvailable = available;
        if (_webView is null) return;
        if (!available)
        {
            SetStatus(SessionStatus.Offline, "Sem conexão de rede no Windows");
        }
        else if (Status == SessionStatus.Error)
        {
            RetryNow();
        }
        else if (Status == SessionStatus.Offline)
        {
            UpdateConnectionStatus();
        }
    }

    // ------------------------------------------------------------------ configuração

    private async Task ConfigureAsync(CoreWebView2 core)
    {
        var s = core.Settings;
        s.AreDevToolsEnabled = _settings.DeveloperMode;
        s.IsStatusBarEnabled = false;
        // Nenhuma ponte página → app: sem objetos do host e sem postMessage. A página não tem
        // como pedir ao aplicativo para executar nada.
        s.AreHostObjectsAllowed = false;
        s.IsWebMessageEnabled = false;
        s.IsSwipeNavigationEnabled = false;
        s.IsBuiltInErrorPageEnabled = true;
        core.Profile.IsPasswordAutosaveEnabled = false;
        core.Profile.IsGeneralAutofillEnabled = false;

        // FrameId só é válido a partir do primeiro ContentLoading (documentação do WebView2).
        core.ContentLoading += OnContentLoading;
        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.ProcessFailed += OnProcessFailed;
        core.PermissionRequested += OnPermissionRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;

        if (_settings.MonitorGameConnection)
            await EnableConnectionMonitorAsync(core);

        // Botão "Exportar TXT" na página Shinies (o mesmo script da extensão do Chrome).
        if (_settings.EnableShinyExport && ShinyExportScript.Value is { Length: > 0 } script)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private static readonly Lazy<string> ShinyExportScript = new(() =>
    {
        using var s = typeof(BrowserSession).Assembly.GetManifestResourceStream("ShinyExport.js");
        if (s is null) return "";
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    });

    /// <summary>Pasta onde o .txt exportado é salvo (Downloads do usuário; o autoteste troca).</summary>
    public static string ExportFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>Um arquivo exportado pela página foi salvo (caminho completo).</summary>
    public event EventHandler<string>? FileExported;

    [System.Text.RegularExpressions.GeneratedRegex(@"^shinies-pokeidle-\d{8}-\d{4}\.txt$")]
    private static partial System.Text.RegularExpressions.Regex ShinyExportName();

    /// <summary>
    /// Observa (somente leitura) a abertura/fechamento do WebSocket do jogo pelo DevTools Protocol.
    /// Não lê nem altera mensagens: só os eventos de criação, handshake e fechamento.
    /// Os buffers de corpo de resposta são zerados para não reter memória.
    /// </summary>
    private async Task EnableConnectionMonitorAsync(CoreWebView2 core)
    {
        _wsCreated = core.GetDevToolsProtocolEventReceiver("Network.webSocketCreated");
        _wsHandshake = core.GetDevToolsProtocolEventReceiver("Network.webSocketHandshakeResponseReceived");
        _wsClosed = core.GetDevToolsProtocolEventReceiver("Network.webSocketClosed");
        _wsCreated.DevToolsProtocolEventReceived += OnWebSocketCreated;
        _wsHandshake.DevToolsProtocolEventReceived += OnWebSocketHandshake;
        _wsClosed.DevToolsProtocolEventReceived += OnWebSocketClosed;
        try
        {
            await core.CallDevToolsProtocolMethodAsync("Network.enable",
                "{\"maxTotalBufferSize\":0,\"maxResourceBufferSize\":0,\"maxPostDataSize\":0}");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[conta{Slot}] Network.enable com buffers zerados falhou ({ex.Message}); usando padrão.");
            await core.CallDevToolsProtocolMethodAsync("Network.enable", "{}");
        }
    }

    private void DetachAndDispose()
    {
        // A live vai junto com a conta, mas o canal fica guardado para reabrir com ela.
        CloseLive(forget: false);
        DetachAndDisposeGame();
    }

    /// <summary>Fecha só a página do jogo (as lives da conta continuam) — usado pelo modo econômico.</summary>
    private void DetachAndDisposeGame()
    {
        var core = _core;
        _core = null;
        if (core is not null)
        {
            try
            {
                core.ContentLoading -= OnContentLoading;
                core.NavigationStarting -= OnNavigationStarting;
                core.NavigationCompleted -= OnNavigationCompleted;
                core.NewWindowRequested -= OnNewWindowRequested;
                core.ProcessFailed -= OnProcessFailed;
                core.PermissionRequested -= OnPermissionRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            }
            catch (Exception ex)
            {
                // O processo pode já ter morrido; desinscrever de um objeto COM inválido pode lançar.
                LoggingService.Log.Warn($"[conta{Slot}] Desinscrição de eventos: {ex.Message}");
            }
        }

        try
        {
            if (_wsCreated is not null) _wsCreated.DevToolsProtocolEventReceived -= OnWebSocketCreated;
            if (_wsHandshake is not null) _wsHandshake.DevToolsProtocolEventReceived -= OnWebSocketHandshake;
            if (_wsClosed is not null) _wsClosed.DevToolsProtocolEventReceived -= OnWebSocketClosed;
        }
        catch { }
        _wsCreated = _wsHandshake = _wsClosed = null;

        _navigationWaiter?.TrySetResult(false);
        _navigationWaiter = null;
        _openSockets.Clear();
        _pendingSockets.Clear();
        _pageLoaded = false;
        MainFrameId = 0;
        HuntActive = false;

        var wv = _webView;
        WebView = null; // sai da árvore visual antes de ser descartado
        if (wv is not null)
        {
            wv.ZoomFactorChanged -= OnZoomFactorChanged;
            try { wv.Dispose(); }
            catch (Exception ex) { LoggingService.Log.Warn($"[conta{Slot}] Dispose do WebView2: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ eventos de navegação

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Clique num canal da Twitch dentro do jogo: abre no painel de live da conta, sem tirar o jogo da tela.
        if (e.IsUserInitiated && !e.IsRedirected && TryParseTwitchChannel(e.Uri, out var channel))
        {
            e.Cancel = true;
            _blockedNavigationId = e.NavigationId;
            _ = OpenLiveAsync(channel);
            return;
        }

        var decision = _policy.Evaluate(e.Uri, e.IsRedirected, e.IsUserInitiated);
        if (decision != NavigationDecision.Allow)
        {
            e.Cancel = true;
            _blockedNavigationId = e.NavigationId;
            LoggingService.Log.Warn($"[conta{Slot}] Navegação {(decision == NavigationDecision.OpenExternally ? "enviada ao navegador padrão" : "bloqueada")}: {LoggingService.SafeUrl(e.Uri)}");
            if (decision == NavigationDecision.OpenExternally) OpenExternal(e.Uri);
            return;
        }

        _pageLoaded = false;
        _openSockets.Clear();
        _pendingSockets.Clear();
        if (e.Uri != "about:blank")
            SetStatus(SessionStatus.Loading, $"Carregando {HostOf(e.Uri)}…");
    }

    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    {
        if (_core is null) return;
        try { MainFrameId = _core.FrameId; }
        catch { }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.NavigationId == _blockedNavigationId) return; // nossa própria recusa; a página atual segue.

        var waiter = _navigationWaiter;
        _navigationWaiter = null;
        waiter?.TrySetResult(e.IsSuccess);

        if (e.IsSuccess)
        {
            _retryAttempt = 0;
            _pageLoaded = true;
            if (!_reportedLoaded && _core?.Source != "about:blank")
            {
                _reportedLoaded = true;
                Milestone?.Invoke(this, ("carregada", _startWatch?.Elapsed ?? TimeSpan.Zero));
            }
            UpdateConnectionStatus();
            return;
        }

        if (e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled)
            return; // substituída por outra navegação

        var http = e.HttpStatusCode;
        LoggingService.Log.Warn($"[conta{Slot}] Falha de navegação: {e.WebErrorStatus}, HTTP {http}");
        if (!_networkAvailable)
        {
            SetStatus(SessionStatus.Offline, "Sem conexão de rede no Windows");
            return;
        }

        SetStatus(SessionStatus.Error, http > 0 ? $"Site respondeu HTTP {http}" : $"Falha ao carregar ({e.WebErrorStatus})");
        // 4xx (ex.: desafio/bloqueio do Cloudflare, 429) não é recarregado em loop: precisa de ação do jogador.
        if (http is 0 or >= 500) ScheduleRetry();
    }

    private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        // Nenhuma janela pop-up é criada dentro do app. Links externos (Discord, termos, pagamento)
        // vão para o navegador padrão, com limite de frequência para a página não abrir abas em série.
        e.Handled = true;
        // Os links dos canais oficiais na tela "Twitch" do jogo (target=_blank) abrem no painel de live
        // desta conta, que tem o login da Twitch dela — no navegador padrão o login seria outro.
        if (TryParseTwitchChannel(e.Uri, out var channel))
        {
            _ = OpenLiveAsync(channel);
            return;
        }
        if (!NavigationPolicy.IsSafeExternalUrl(e.Uri)) return;
        if (DateTime.UtcNow - _lastExternalOpenUtc < TimeSpan.FromSeconds(2)) return;
        OpenExternal(e.Uri);
    }

    private void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
    {
        // Câmera, microfone, localização, notificações, leitura da área de transferência etc.
        // O jogo não precisa de nenhuma.
        e.State = CoreWebView2PermissionState.Deny;
        e.SavesInProfile = false;
        LoggingService.Log.Info($"[conta{Slot}] Permissão negada: {e.PermissionKind}");
    }

    private void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Handled = true; // nunca mostra a barra de downloads do navegador

        // Única exceção: o .txt do "Exportar shinies", gerado pela própria página do jogo (blob da
        // origem do jogo) e com o nome que o script usa. Vai para a pasta Downloads.
        var uri = e.DownloadOperation.Uri ?? "";
        var name = Path.GetFileName(e.ResultFilePath ?? "");
        var gameOrigin = new Uri(_settings.StartUrl).GetLeftPart(UriPartial.Authority);
        if (_settings.EnableShinyExport && uri.StartsWith("blob:" + gameOrigin + "/", StringComparison.OrdinalIgnoreCase)
            && ShinyExportName().IsMatch(name))
        {
            Directory.CreateDirectory(ExportFolder);
            var target = Path.Combine(ExportFolder, name);
            e.ResultFilePath = target;
            var op = e.DownloadOperation;
            op.StateChanged += (_, _) =>
            {
                if (op.State == CoreWebView2DownloadState.Completed)
                {
                    LoggingService.Log.Info($"[conta{Slot}] Shinies exportados para {target}.");
                    FileExported?.Invoke(this, op.ResultFilePath);
                }
            };
            return;
        }

        e.Cancel = true;
        LoggingService.Log.Info($"[conta{Slot}] Download bloqueado: {LoggingService.SafeUrl(uri)}");
    }

    private void OnLaunchingExternalUriScheme(object? sender, CoreWebView2LaunchingExternalUriSchemeEventArgs e)
    {
        e.Cancel = true;
        LoggingService.Log.Info($"[conta{Slot}] Esquema externo bloqueado.");
    }

    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        LoggingService.Log.Warn($"[conta{Slot}] ProcessFailed: {e.ProcessFailedKind}, motivo {e.Reason}, código {e.ExitCode}, {e.ProcessDescription}");
        switch (e.ProcessFailedKind)
        {
            case CoreWebView2ProcessFailedKind.RenderProcessExited:
                _openSockets.Clear();
                _pageLoaded = false;
                SetStatus(SessionStatus.Error, "O processo da página terminou — recarregando");
                ScheduleRetry();
                break;

            case CoreWebView2ProcessFailedKind.RenderProcessUnresponsive:
                SetStatus(SessionStatus.Error, "A página não está respondendo");
                StartUnresponsiveProbe();
                break;

            case CoreWebView2ProcessFailedKind.BrowserProcessExited:
                // Tratado pelo BrowserSessionManager via ProfileManager.EnvironmentLost.
                break;

            default:
                // GPU/utilitários/iframes: o runtime recria sozinho; só registramos.
                break;
        }
    }

    // ------------------------------------------------------------------ WebSocket do jogo

    private void OnWebSocketCreated(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!TryReadSocketEvent(e, out var id, out var root)) return;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (IsGameSocket(url)) _pendingSockets.Add(id);
    }

    private void OnWebSocketHandshake(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!TryReadSocketEvent(e, out var id, out var root)) return;
        if (!_pendingSockets.Remove(id)) return;
        var status = root.TryGetProperty("response", out var r) && r.TryGetProperty("status", out var s) ? s.GetDouble() : 0;
        if ((int)status == 101) _openSockets.Add(id);
        UpdateConnectionStatus();
    }

    private void OnWebSocketClosed(object? sender, CoreWebView2DevToolsProtocolEventReceivedEventArgs e)
    {
        if (!TryReadSocketEvent(e, out var id, out _)) return;
        _pendingSockets.Remove(id);
        if (_openSockets.Remove(id)) UpdateConnectionStatus();
    }

    private static bool TryReadSocketEvent(CoreWebView2DevToolsProtocolEventReceivedEventArgs e, out string id, out JsonElement root)
    {
        id = "";
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(e.ParameterObjectAsJson);
            root = doc.RootElement.Clone();
            id = root.GetProperty("requestId").GetString() ?? "";
            return id.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Só conta o socket cujo host é o mesmo do jogo (ou subdomínio).</summary>
    private bool IsGameSocket(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var ws)) return false;
        if (!Uri.TryCreate(_settings.StartUrl, UriKind.Absolute, out var start)) return false;
        return ws.Host.Equals(start.Host, StringComparison.OrdinalIgnoreCase)
               || ws.Host.EndsWith("." + start.Host, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateConnectionStatus()
    {
        if (_webView is null || !_pageLoaded) return;
        if (!_networkAvailable)
        {
            SetStatus(SessionStatus.Offline, "Sem conexão de rede no Windows");
            return;
        }
        if (!_settings.MonitorGameConnection)
        {
            SetStatus(SessionStatus.Loaded, "Página carregada (conexão não monitorada)");
            return;
        }
        if (_openSockets.Count > 0)
        {
            _wasConnected = true;
            SetStatus(SessionStatus.Connected, "Conectada ao servidor do jogo");
            if (!_reportedConnected)
            {
                _reportedConnected = true;
                Milestone?.Invoke(this, ("conectada", _startWatch?.Elapsed ?? TimeSpan.Zero));
            }
        }
        else if (_wasConnected)
        {
            SetStatus(SessionStatus.Disconnected, "Conexão do jogo caiu — o jogo tenta reconectar sozinho");
        }
        else
        {
            SetStatus(SessionStatus.AwaitingConnection, "Página aberta, sem conexão do jogo (login pendente?)");
        }
    }

    // ------------------------------------------------------------------ recuperação

    private void ScheduleRetry()
    {
        _retryTimer?.Stop();
        var delay = RetryDelaysSeconds[Math.Min(_retryAttempt, RetryDelaysSeconds.Length - 1)];
        _retryAttempt++;
        var gen = _generation;
        StatusDetail = $"{StatusDetail} · nova tentativa em {delay}s";
        _retryTimer = new DispatcherTimer(TimeSpan.FromSeconds(delay), DispatcherPriority.Background, (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            if (gen == _generation) RetryNow();
        }, _dispatcher);
        _retryTimer.Start();
    }

    private void RetryNow()
    {
        _retryTimer?.Stop();
        if (!_networkAvailable) return;
        LoggingService.Log.Info($"[conta{Slot}] Tentando novamente (tentativa {_retryAttempt}).");
        if (_core is null)
        {
            _ = StartAsync();
            return;
        }
        try
        {
            _core.Navigate(_settings.StartUrl);
        }
        catch (Exception ex)
        {
            // Controller inválido (processo morto): recria só esta conta.
            LoggingService.Log.Warn($"[conta{Slot}] Navigate falhou ({ex.Message}); recriando a instância.");
            _ = RestartAsync();
        }
    }

    /// <summary>
    /// Renderer travado: espera 30 s e testa com um script trivial. Se responder, segue; se não,
    /// reinicia só esta conta (descartar o controller mata o renderer travado).
    /// </summary>
    private void StartUnresponsiveProbe()
    {
        if (_unresponsiveTimer is not null) return;
        var gen = _generation;
        _unresponsiveTimer = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, async (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            _unresponsiveTimer = null;
            if (gen != _generation || _core is null) return;
            var probe = _core.ExecuteScriptAsync("1");
            var finished = await Task.WhenAny(probe, Task.Delay(TimeSpan.FromSeconds(5)));
            if (gen != _generation) return;
            if (finished == probe && probe.IsCompletedSuccessfully)
            {
                LoggingService.Log.Info($"[conta{Slot}] Página voltou a responder.");
                UpdateConnectionStatus();
            }
            else
            {
                LoggingService.Log.Warn($"[conta{Slot}] Página segue travada; reiniciando a instância.");
                await RestartAsync();
            }
        }, _dispatcher);
        _unresponsiveTimer.Start();
    }

    private void CancelTimers()
    {
        _retryTimer?.Stop();
        _retryTimer = null;
        _unresponsiveTimer?.Stop();
        _unresponsiveTimer = null;
    }

    // ------------------------------------------------------------------ utilitários

    private Task<bool> NavigateAndWaitAsync(CoreWebView2 core, string url, TimeSpan timeout)
    {
        _navigationWaiter?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navigationWaiter = tcs;
        core.Navigate(url);
        return Task.WhenAny(tcs.Task, Task.Delay(timeout)).ContinueWith(t => t.Result == tcs.Task && tcs.Task.Result,
            TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OpenExternal(string url)
    {
        if (!NavigationPolicy.IsSafeExternalUrl(url)) return;
        _lastExternalOpenUtc = DateTime.UtcNow;
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[conta{Slot}] Não foi possível abrir no navegador padrão: {ex.Message}");
        }
    }

    private void SetStatus(SessionStatus status, string detail)
    {
        Status = status;
        StatusDetail = detail;
    }

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : "página";
}
