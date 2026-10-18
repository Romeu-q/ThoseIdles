using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ThoseIdles.SelfTest;
using ThoseIdles.Services;
using ThoseIdles.ViewModels;

namespace ThoseIdles;

/// <summary>
/// Composição do aplicativo. Argumentos de linha de comando:
///   --data-root &lt;pasta&gt;          usa outra pasta de dados (perfis/logs/configurações)
///   --selftest &lt;resultado.json&gt;   autoteste, fase 1 (exige --data-root)
///   --selftest-verify &lt;json&gt;     autoteste, fase 2 (persistência)
///   --selftest-port &lt;porta&gt;      porta fixa do servidor local de teste
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstance;
    private SettingsService? _settingsService;
    private BrowserSessionManager? _manager;
    private MemoryMonitor? _memory;
    private BenchmarkService? _benchmark;
    private MainViewModel? _vm;
    private SelfTestServer? _testServer;
    private int? _exitCode;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var args = ParseArgs(e.Args);
        var selfTestFile = args.GetValueOrDefault("selftest") ?? args.GetValueOrDefault("selftest-verify")
                           ?? args.GetValueOrDefault("selftest-hunt") ?? args.GetValueOrDefault("selftest-twitch");
        var verifyPhase = args.ContainsKey("selftest-verify");
        // Teste on-line: baixa a extensão /hunt real e abre o pokeidle.io de verdade (sem login).
        var huntOnline = args.ContainsKey("selftest-hunt");
        // Medição on-line: custo de memória/CPU de lives reais da Twitch (página neutra no lugar do jogo).
        var twitchOnline = args.ContainsKey("selftest-twitch");
        huntOnline |= twitchOnline; // mesmas condições de "on-line" (sem servidor local)
        var isSelfTest = selfTestFile is not null;

        if (args.TryGetValue("data-root", out var root) && root is not null) AppPaths.UseRoot(root);
        if (isSelfTest && root is null)
        {
            // O autoteste nunca roda sobre os perfis reais.
            AppPaths.UseRoot(Path.Combine(Path.GetTempPath(), "ThoseIdles-selftest"));
        }
        // Só na pasta padrão: traz os dados da época em que o app se chamava "PokeIdle Multi".
        var migration = root is null && !isSelfTest ? AppPaths.MigrateLegacyRoot() : null;
        Directory.CreateDirectory(AppPaths.Root);
        LoggingService.Initialize(AppPaths.LogsFolder);
        LoggingService.Log.Info($"==== ThoseIdles {typeof(App).Assembly.GetName().Version} iniciando (pid {Environment.ProcessId}, .NET {Environment.Version}) ====");
        if (migration is not null) LoggingService.Log.Info(migration);

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => LoggingService.Log.Error("Exceção não tratada (AppDomain)", ev.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, ev) =>
        {
            LoggingService.Log.Error("Task com exceção não observada", ev.Exception);
            ev.SetObserved();
        };

        if (!AcquireSingleInstance())
        {
            MessageBox.Show("O ThoseIdles já está aberto.", "ThoseIdles", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(1);
            return;
        }

        var runtime = ProfileManager.GetRuntimeVersion();
        if (runtime is null)
        {
            LoggingService.Log.Error("WebView2 Runtime não encontrado.");
            MessageBox.Show(
                "O Microsoft Edge WebView2 Runtime não está instalado.\n\n" +
                "Instale o \"Evergreen Bootstrapper\" em https://developer.microsoft.com/microsoft-edge/webview2/ e abra o app novamente.",
                "ThoseIdles", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(2);
            return;
        }
        LoggingService.Log.Info($"WebView2 Runtime {runtime}");

        LoggingService.Log.Info(ProcessJob.TryAttachCurrentProcess()
            ? "Job Object ativo: processos do WebView2 serão encerrados junto com o app."
            : "Job Object indisponível; o encerramento depende do desligamento normal do WebView2.");

        _settingsService = new SettingsService(AppPaths.SettingsFile);
        var settings = _settingsService.Current;
        if (twitchOnline)
        {
            settings.StartUrl = "https://example.com/";
            if (!settings.AllowedHosts.Contains("example.com")) settings.AllowedHosts.Add("example.com");
            settings.LiveLightMode = args.ContainsKey("live-light");
            settings.StartAllOnLaunch = false;
            settings.EnableHuntExtension = false;
            settings.FocusMode = false;
        }
        else if (huntOnline)
        {
            settings.StartUrl = "https://pokeidle.io/app";
            settings.StartAllOnLaunch = false;
            settings.EnableHuntExtension = false;
            settings.FocusMode = false;
        }
        else if (isSelfTest)
        {
            var port = int.TryParse(args.GetValueOrDefault("selftest-port"), out var p) ? p : 0;
            _testServer = new SelfTestServer(port);
            settings.StartUrl = _testServer.Url;
            // A live de teste é servida localmente (127.0.0.1), não pela Twitch.
            var server = _testServer;
            BrowserSession.LiveUrlForChannel = c => server.LiveUrl(c);
            BrowserSession.LiveQualityHosts = new[] { "twitch.tv", "127.0.0.1" };
            EconomyClient.PingEvery = TimeSpan.FromSeconds(2); // o oficial usa 25 s; o teste encurta
            BrowserSession.ExportFolder = Path.Combine(AppPaths.Root, "exportados"); // não toca na sua pasta Downloads
            AppUpdateService.LatestReleaseApi = _testServer.Url + "releases/latest";
            settings.StartAllOnLaunch = false;
            settings.MonitorGameConnection = true;
            settings.FocusMode = false;
        }

        if (settings.SoftwareRenderingForUi)
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;

        _benchmark = new BenchmarkService(AppPaths.BenchmarkFolder, settings);
        // A extensão /hunt só pode rodar no jogo (e, no autoteste, na página local).
        var huntMatches = isSelfTest && !huntOnline ? new[] { "https://pokeidle.io/*", "http://localhost/*" } : new[] { "https://pokeidle.io/*" };
        var hunt = new HuntExtensionService(AppPaths.HuntExtensionFolder, huntMatches);
        if (isSelfTest && !verifyPhase && !huntOnline) SelfTestRunner.PrepareTestExtension(hunt, settings);
        var profiles = new ProfileManager(AppPaths.UserDataFolder, () => settings.EnableHuntExtension);
        var policy = new NavigationPolicy(() => settings.AllowedHosts) { AllowLocalhostHttp = isSelfTest && !huntOnline };
        _manager = new BrowserSessionManager(settings, profiles, policy, hunt);
        _benchmark.Attach(_manager);
        _memory = new MemoryMonitor(_manager, settings.MemoryPollSeconds);
        _vm = new MainViewModel(_settingsService, _manager, _memory, _benchmark, hunt);

        var window = new MainWindow(_vm, settings, ShutdownServicesAsync);
        if (isSelfTest)
        {
            window.Title += " — AUTOTESTE";
            window.WindowState = WindowState.Normal;
        }
        MainWindow = window;

        window.Loaded += async (_, _) =>
        {
            _benchmark.Mark("janela exibida");
            _memory.Start();
            if (isSelfTest)
            {
                var runner = new SelfTestRunner(_manager, _vm, _memory, window, selfTestFile!);
                var ok = twitchOnline ? await runner.RunTwitchMeasureAsync(args.GetValueOrDefault("live-channel") ?? "lofigirl")
                       : huntOnline ? await runner.RunHuntOnlineAsync(hunt)
                       : await runner.RunAsync(verifyPhase);
                _exitCode = ok ? 0 : 3;
                window.Close();
            }
            else
            {
                // Antes de abrir as contas (no máx. 8 s), para elas já carregarem a versão nova da /hunt.
                await _vm.CheckHuntUpdateAsync(TimeSpan.FromSeconds(8));
                _vm.StartHuntUpdateTimer();
                _vm.StartAppUpdateChecks();
                if (settings.StartAllOnLaunch) await _manager.StartAllAsync();
            }
        };
        window.Show();
    }

    private async Task ShutdownServicesAsync()
    {
        LoggingService.Log.Info("Encerrando…");
        _memory?.Stop();
        _benchmark?.WriteTimeline();
        if (_manager is not null)
        {
            await _manager.ShutdownAsync(TimeSpan.FromSeconds(5));
            _manager.Dispose();
        }
        _memory?.Dispose();
        _vm?.Dispose();
        _settingsService?.Save();
        _testServer?.Dispose();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_exitCode is int code) e.ApplicationExitCode = code;
        LoggingService.Log.Info($"==== Encerrado (código {e.ApplicationExitCode}) ====");
        LoggingService.Log.Dispose();
        try { _singleInstance?.ReleaseMutex(); } catch { }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LoggingService.Log.Error("Exceção não tratada na UI", e.Exception);
        // Mantém o app vivo: uma falha num botão não pode derrubar as quatro contas.
        e.Handled = true;
        if (_vm is not null) _vm.Notice = $"Erro inesperado: {e.Exception.Message} (detalhes no log)";
    }

    /// <summary>Uma instância por pasta de dados (dois processos não podem abrir os mesmos perfis).</summary>
    private bool AcquireSingleInstance()
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Root.ToLowerInvariant())))[..16];
        _singleInstance = new Mutex(true, $"Local\\ThoseIdles-{hash}", out var created);
        if (!created)
        {
            _singleInstance.Dispose();
            _singleInstance = null;
        }
        return created;
    }

    private static Dictionary<string, string?> ParseArgs(string[] args)
    {
        var d = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            var key = args[i][2..];
            string? value = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
            d[key] = value;
        }
        return d;
    }
}
