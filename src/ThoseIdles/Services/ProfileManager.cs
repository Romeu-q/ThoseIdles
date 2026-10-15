using Microsoft.Web.WebView2.Core;

namespace ThoseIdles.Services;

/// <summary>
/// Dono do único <see cref="CoreWebView2Environment"/> do aplicativo e do mapeamento conta → perfil.
///
/// Por que UM ambiente com QUATRO perfis (e não quatro ambientes):
///  - Cada ambiente (= pasta de dados) inicia sua própria árvore de processos do navegador:
///    browser + GPU + network service + storage/utility + crashpad. Quatro ambientes = quatro árvores,
///    praticamente o mesmo custo de quatro Chromes independentes.
///  - Com um ambiente e perfis nomeados (CoreWebView2ControllerOptions.ProfileName), os processos
///    browser/GPU/rede/utilitários são compartilhados, mas cada perfil tem diretório próprio para
///    cookies, Local Storage, IndexedDB, cache e Service Workers. Perfis diferentes nunca compartilham
///    processo de renderização, porque o Chromium isola StoragePartitions por perfil.
///
/// O nome do perfil é derivado do slot (conta1…conta4) e nunca do nome exibido, por isso renomear
/// uma conta não muda a sessão.
/// </summary>
public sealed class ProfileManager
{
    /// <summary>
    /// Flags que impedem o Chromium de desacelerar contas fora de vista. São exatamente as proteções
    /// de "economia" que o requisito 3.3 proíbe que afetem o jogo:
    ///  - disable-background-timer-throttling: setInterval/setTimeout não são limitados a 1/s.
    ///  - IntensiveWakeUpThrottling: impede o limite de 1 despertar por minuto após 5 min oculto.
    ///  - disable-renderer-backgrounding: o renderer não perde prioridade de CPU.
    ///  - disable-backgrounding-occluded-windows + CalculateNativeWinOcclusion: janela coberta
    ///    ou minimizada não é tratada como oculta.
    /// Nenhuma delas desliga GPU, JavaScript, armazenamento ou rede.
    /// </summary>
    public const string BrowserArguments =
        "--disable-background-timer-throttling " +
        "--disable-backgrounding-occluded-windows " +
        "--disable-renderer-backgrounding " +
        "--disable-features=CalculateNativeWinOcclusion,IntensiveWakeUpThrottling";

    private readonly string _userDataFolder;
    private readonly Func<bool> _wantExtensions;
    private CoreWebView2Environment? _environment;
    private Task<CoreWebView2Environment>? _creating;
    private TaskCompletionSource? _exited;

    /// <summary>O processo do navegador terminou. bool = true quando foi falha (não encerramento normal).</summary>
    public event EventHandler<bool>? EnvironmentLost;

    /// <param name="wantExtensions">Lido ao criar o ambiente: liga o suporte a extensões (/hunt).</param>
    public ProfileManager(string userDataFolder, Func<bool>? wantExtensions = null)
    {
        _userDataFolder = userDataFolder;
        _wantExtensions = wantExtensions ?? (() => false);
    }

    public CoreWebView2Environment? Environment => _environment;

    /// <summary>Se o ambiente ATUAL foi criado com extensões. Mudar exige recriar o ambiente.</summary>
    public bool ExtensionsEnabled { get; private set; }

    /// <summary>
    /// Espera o processo do navegador sair depois que todas as contas foram fechadas, para que o
    /// próximo ambiente possa ser criado com outras opções na mesma pasta de dados.
    /// </summary>
    public async Task WaitForShutdownAsync(TimeSpan timeout)
    {
        var tcs = _exited;
        if (_environment is null || tcs is null) return;
        await Task.WhenAny(tcs.Task, Task.Delay(timeout));
        if (!tcs.Task.IsCompleted)
        {
            LoggingService.Log.Warn("O navegador não saiu a tempo; o ambiente será descartado mesmo assim.");
            _environment = null;
        }
    }

    public static string ProfileNameForSlot(int slot) => $"conta{slot}";

    public static string? GetRuntimeVersion()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch (WebView2RuntimeNotFoundException) { return null; }
    }

    /// <summary>Retorna o ambiente compartilhado, criando-o na primeira chamada. Deve rodar na thread da UI.</summary>
    public Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_environment is not null) return Task.FromResult(_environment);
        // Reaproveita só uma criação EM ANDAMENTO. (Com "_creating ??= CreateAsync()", uma criação que
        // terminasse de forma síncrona deixava a Task concluída guardada para sempre, e o ambiente
        // antigo voltava a ser usado depois de um encerramento.)
        if (_creating is { IsCompleted: false } pending) return pending;
        _creating = CreateAsync();
        return _creating;
    }

    private async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(_userDataFolder);
        var extensions = _wantExtensions();
        var options = new CoreWebView2EnvironmentOptions(BrowserArguments, "pt-BR")
        {
            // Só liga quando o usuário ativa a extensão /hunt; desligado, nada de extensão carrega.
            AreBrowserExtensionsEnabled = extensions,
            // Só este app usa a pasta; outro processo não pode "pegar carona" no navegador.
            ExclusiveUserDataFolderAccess = true,
        };
        var env = await CoreWebView2Environment.CreateAsync(browserExecutableFolder: null, _userDataFolder, options);
        env.BrowserProcessExited += OnBrowserProcessExited;
        _exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _environment = env;
        ExtensionsEnabled = extensions;
        LoggingService.Log.Info($"Ambiente WebView2 criado (extensões {(extensions ? "ligadas" : "desligadas")}). Runtime {env.BrowserVersionString}; dados em {_userDataFolder}");
        return env;
    }

    private void OnBrowserProcessExited(object? sender, CoreWebView2BrowserProcessExitedEventArgs e)
    {
        // Só existe um ambiente inscrito por vez (a inscrição é removida aqui), então a saída é
        // sempre do ambiente atual. Não dependemos da identidade do wrapper COM do "sender".
        if (sender is CoreWebView2Environment env) env.BrowserProcessExited -= OnBrowserProcessExited;
        _environment = null;
        _exited?.TrySetResult();
        var failed = e.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Failed;
        LoggingService.Log.Warn($"Processo do navegador (pid {e.BrowserProcessId}) terminou: {e.BrowserProcessExitKind}");
        EnvironmentLost?.Invoke(this, failed);
    }

    public CoreWebView2ControllerOptions CreateControllerOptions(CoreWebView2Environment env, int slot)
    {
        var o = env.CreateCoreWebView2ControllerOptions();
        o.ProfileName = ProfileNameForSlot(slot);
        o.IsInPrivateModeEnabled = false; // perfil persistente em disco
        o.ScriptLocale = "pt-BR";
        return o;
    }
}
