using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using ThoseIdles.Mvvm;
using ThoseIdles.Services;

namespace ThoseIdles.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly BrowserSessionManager _manager;
    private readonly MemoryMonitor _memory;
    private readonly BenchmarkService _benchmark;

    private bool _isFocusMode;
    private int _focusedSlot;
    private bool _showSettings;
    private string _summaryText = "";
    private string _memoryText = "RAM: medindo…";
    private string _memoryTooltip = "";
    private string _notice = "";

    private readonly HuntExtensionService? _hunt;
    private bool _huntBusy;

    public MainViewModel(SettingsService settingsService, BrowserSessionManager manager, MemoryMonitor memory, BenchmarkService benchmark,
        HuntExtensionService? hunt = null)
    {
        _settingsService = settingsService;
        _settings = settingsService.Current;
        _manager = manager;
        _memory = memory;
        _benchmark = benchmark;
        _hunt = hunt;
        DownloadHuntCommand = new AsyncCommand(DownloadHuntAsync, () => _hunt is not null && !_huntBusy);

        Accounts = manager.Sessions.Select(s => new AccountViewModel(s, this, _settings)).ToList();
        _isFocusMode = _settings.FocusMode;
        _focusedSlot = _settings.FocusedSlot;
        ApplyRoles();

        StartAllCommand = new AsyncCommand(() => _manager.StartAllAsync());
        CloseAllCommand = new RelayCommand(() => _manager.CloseAll());
        RestartAllCommand = new AsyncCommand(() => _manager.RestartAllAsync());
        ToggleLayoutCommand = new RelayCommand(() => IsFocusMode = !IsFocusMode);
        ToggleSettingsCommand = new RelayCommand(() => ShowSettings = !ShowSettings);
        ExportSnapshotCommand = new AsyncCommand(ExportSnapshotAsync);
        OpenLogsCommand = new RelayCommand(() => OpenFolder(AppPaths.LogsFolder));
        OpenDataCommand = new RelayCommand(() => OpenFolder(AppPaths.Root));

        _memory.Sampled += OnMemorySampled;
        OnSessionStatusChanged();
    }

    public IReadOnlyList<AccountViewModel> Accounts { get; }
    public AccountViewModel Account1 => Accounts[0];
    public AccountViewModel Account2 => Accounts[1];
    public AccountViewModel Account3 => Accounts[2];
    public AccountViewModel Account4 => Accounts[3];

    public ICommand StartAllCommand { get; }
    public ICommand CloseAllCommand { get; }
    public ICommand RestartAllCommand { get; }
    public ICommand ToggleLayoutCommand { get; }
    public ICommand ToggleSettingsCommand { get; }
    public ICommand ExportSnapshotCommand { get; }
    public ICommand OpenLogsCommand { get; }
    public ICommand OpenDataCommand { get; }

    public string RuntimeVersion { get; } = ProfileManager.GetRuntimeVersion() ?? "não encontrado";
    public string DataFolder => AppPaths.Root;

    // ------------------------------------------------------------------ layout

    public bool IsFocusMode
    {
        get => _isFocusMode;
        set
        {
            if (!Set(ref _isFocusMode, value)) return;
            OnPropertyChanged(nameof(LayoutButtonText));
            _settings.FocusMode = value;
            ApplyRoles();
            SaveSettings();
        }
    }

    /// <summary>Índice 0-based da conta em foco (usado pelo painel de layout).</summary>
    public int FocusedIndex => _focusedSlot - 1;

    public string LayoutButtonText => _isFocusMode ? "Grade 2x2" : "Uma conta";

    /// <summary>Foca a conta; se ela já está em foco, volta para a grade.</summary>
    public void ToggleFocus(int slot)
    {
        if (_isFocusMode && _focusedSlot == slot)
        {
            IsFocusMode = false;
            return;
        }
        _focusedSlot = slot;
        _settings.FocusedSlot = slot;
        OnPropertyChanged(nameof(FocusedIndex));
        if (!_isFocusMode) IsFocusMode = true;
        else { ApplyRoles(); SaveSettings(); }
    }

    private void ApplyRoles()
    {
        foreach (var a in Accounts)
        {
            a.Role = !_isFocusMode ? PanelRole.Grid
                : a.Slot == _focusedSlot ? PanelRole.Focused
                : PanelRole.Thumbnail;
        }
    }

    // ------------------------------------------------------------------ estado geral

    public string SummaryText
    {
        get => _summaryText;
        private set => Set(ref _summaryText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => Set(ref _memoryText, value);
    }

    public string MemoryTooltip
    {
        get => _memoryTooltip;
        private set => Set(ref _memoryTooltip, value);
    }

    public string Notice
    {
        get => _notice;
        set => Set(ref _notice, value);
    }

    public void OnSessionStatusChanged()
    {
        var c = Accounts.Count(a => a.Session.Status == SessionStatus.Connected);
        var running = Accounts.Count(a => a.Session.IsRunning);
        var errors = Accounts.Count(a => a.Session.Status == SessionStatus.Error);
        var sb = new StringBuilder();
        sb.Append(c).Append("/4 conectadas");
        if (running < 4) sb.Append(" · ").Append(4 - running).Append(" fechada(s)");
        if (errors > 0) sb.Append(" · ").Append(errors).Append(" com erro");
        SummaryText = sb.ToString();
        _benchmark.SetConnectedCount(c);
    }

    private void OnMemorySampled(object? sender, MemorySnapshot s)
    {
        var pws = s.Total.PrivateWorkingSet > 0 ? s.Total.PrivateWorkingSet : s.Total.WorkingSet;
        MemoryText = $"RAM ≈ {MemoryMonitor.Format(pws)}";

        var tip = new StringBuilder();
        tip.AppendLine($"Total do app + WebView2 ({s.Total.Processes} processos)");
        tip.AppendLine($"  Memória (privada em RAM, como no Gerenciador de Tarefas): {MemoryMonitor.Format(s.Total.PrivateWorkingSet)}");
        tip.AppendLine($"  Commit privado: {MemoryMonitor.Format(s.Total.PrivateBytes)}");
        tip.AppendLine($"  Working set (inclui DLLs compartilhadas): {MemoryMonitor.Format(s.Total.WorkingSet)}");
        tip.AppendLine();
        tip.AppendLine($"App WPF: {MemoryMonitor.Format(s.Host.PrivateWorkingSet)}");
        tip.AppendLine($"Compartilhado (navegador, GPU, rede, utilitários): {MemoryMonitor.Format(s.Shared.PrivateWorkingSet)} em {s.Shared.Processes} processos");
        for (var i = 0; i < s.Accounts.Length; i++)
            tip.AppendLine($"{Accounts[i].DisplayName}: {MemoryMonitor.Format(s.Accounts[i].PrivateWorkingSet)} em {s.Accounts[i].Processes} renderer(s)");
        MemoryTooltip = tip.ToString().TrimEnd();

        for (var i = 0; i < Accounts.Count; i++)
        {
            var g = s.Accounts[i];
            Accounts[i].MemoryText = Accounts[i].IsRunning && g.Processes > 0
                ? MemoryMonitor.Format(g.PrivateWorkingSet > 0 ? g.PrivateWorkingSet : g.PrivateBytes)
                : "";
        }
        _benchmark.OnMemorySample(s);
    }

    // ------------------------------------------------------------------ configurações

    public bool ShowSettings
    {
        get => _showSettings;
        set => Set(ref _showSettings, value);
    }

    public bool StartAllOnLaunch
    {
        get => _settings.StartAllOnLaunch;
        set { _settings.StartAllOnLaunch = value; OnPropertyChanged(); SaveSettings(); }
    }

    public bool DeveloperMode
    {
        get => _settings.DeveloperMode;
        set
        {
            _settings.DeveloperMode = value;
            OnPropertyChanged();
            foreach (var a in Accounts) a.Session.ApplyDeveloperMode();
            CommandManager.InvalidateRequerySuggested();
            SaveSettings();
        }
    }

    public bool MonitorGameConnection
    {
        get => _settings.MonitorGameConnection;
        set
        {
            _settings.MonitorGameConnection = value;
            OnPropertyChanged();
            SaveSettings();
            Notice = "O monitor de conexão vale para contas abertas/reiniciadas a partir de agora.";
        }
    }

    public bool ScaleThumbnails
    {
        get => _settings.ScaleThumbnails;
        set { _settings.ScaleThumbnails = value; OnPropertyChanged(); ApplyAllViews(); SaveSettings(); }
    }

    public bool LowMemoryTargetForThumbnails
    {
        get => _settings.LowMemoryTargetForThumbnails;
        set { _settings.LowMemoryTargetForThumbnails = value; OnPropertyChanged(); ApplyAllViews(); SaveSettings(); }
    }

    public bool SoftwareRenderingForUi
    {
        get => _settings.SoftwareRenderingForUi;
        set
        {
            _settings.SoftwareRenderingForUi = value;
            OnPropertyChanged();
            SaveSettings();
            Notice = "A renderização da interface muda na próxima vez que o app abrir.";
        }
    }

    public bool RecordBenchmarkCsv
    {
        get => _settings.RecordBenchmarkCsv;
        set { _settings.RecordBenchmarkCsv = value; OnPropertyChanged(); SaveSettings(); }
    }

    /// <summary>
    /// Zoom aplicado a TODAS as contas de uma vez (25–200%). Cada conta continua podendo ter o seu
    /// próprio zoom com Ctrl + roda do mouse na página.
    /// </summary>
    public double GridZoomPercent
    {
        get => Math.Round(_settings.GridZoom * 100);
        set
        {
            _settings.GridZoom = Math.Clamp(Math.Round(value / 5) * 5 / 100.0, 0.25, 2.0);
            foreach (var a in Accounts) a.Session.Account.Zoom = _settings.GridZoom;
            OnPropertyChanged();
            ApplyAllViews();
            SaveSettingsSoon();
        }
    }

    // ------------------------------------------------------------------ divisórias

    public double RowSplit { get => _settings.Splits.Row; set { _settings.Splits.Row = value; OnPropertyChanged(); } }
    public double TopSplit { get => _settings.Splits.Top; set { _settings.Splits.Top = value; OnPropertyChanged(); } }
    public double BottomSplit { get => _settings.Splits.Bottom; set { _settings.Splits.Bottom = value; OnPropertyChanged(); } }
    public double FocusSplit { get => _settings.Splits.Focus; set { _settings.Splits.Focus = value; OnPropertyChanged(); } }

    private System.Windows.Threading.DispatcherTimer? _saveTimer;

    /// <summary>Salva 1 s depois da última mudança (zoom com a roda do mouse gera muitos eventos).</summary>
    public void SaveSettingsSoon()
    {
        _saveTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromSeconds(1),
            System.Windows.Threading.DispatcherPriority.Background, (s, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                SaveSettings();
            }, System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    public int[] PollIntervals { get; } = { 2, 5, 10, 30, 60 };

    public int MemoryPollSeconds
    {
        get => _settings.MemoryPollSeconds;
        set
        {
            _settings.MemoryPollSeconds = Math.Clamp(value, 2, 60);
            _memory.SetInterval(_settings.MemoryPollSeconds);
            OnPropertyChanged();
            SaveSettings();
        }
    }

    // ------------------------------------------------------------------ extensão /hunt

    public ICommand DownloadHuntCommand { get; }

    /// <summary>Extensão baixada e ligada: mostra o botão liga/desliga em cada conta.</summary>
    public bool HuntAvailable => _settings.EnableHuntExtension && _hunt?.InstalledFolder is not null;

    public bool EnableHuntExtension
    {
        get => _settings.EnableHuntExtension;
        set
        {
            if (value && _hunt?.InstalledFolder is null)
            {
                Notice = "Baixe a extensão primeiro (botão \"Baixar / atualizar\").";
                OnPropertyChanged();
                return;
            }
            _settings.EnableHuntExtension = value;
            SaveSettings();
            OnHuntChanged();
            _ = ApplyHuntAsync(value
                ? "Extensão /hunt ligada. As contas abertas foram reabertas com ela."
                : "Extensão /hunt desligada. As contas abertas foram reabertas sem ela.");
        }
    }

    public string HuntStatusText
    {
        get
        {
            if (_hunt is null) return "";
            if (_huntBusy) return "aplicando…";
            if (_hunt.InstalledVersion is not { } v) return "não baixada";
            if (!_settings.EnableHuntExtension) return $"v{v} baixada · desligada";
            var on = Accounts.Count(a => a.Session.HuntActive);
            return $"v{v} · ativa em {on}/4 contas abertas";
        }
    }

    private async Task DownloadHuntAsync()
    {
        if (_hunt is null) return;
        SetHuntBusy(true);
        try
        {
            var (ok, message) = await _hunt.DownloadLatestAsync(RuntimeVersion);
            Notice = message;
            if (!ok) return;
            _settings.EnableHuntExtension = true;
            SaveSettings();
            OnHuntChanged();
            await ApplyHuntAsync($"{message} Aplicada às contas abertas.");
            _hunt.CleanupOldVersions();
        }
        finally
        {
            SetHuntBusy(false);
        }
    }

    public bool AutoUpdateHuntExtension
    {
        get => _settings.AutoUpdateHuntExtension;
        set { _settings.AutoUpdateHuntExtension = value; OnPropertyChanged(); SaveSettings(); }
    }

    private System.Windows.Threading.DispatcherTimer? _huntUpdateTimer;

    /// <summary>Liga a verificação periódica (a cada 12 h) da extensão.</summary>
    public void StartHuntUpdateTimer()
    {
        _huntUpdateTimer ??= new System.Windows.Threading.DispatcherTimer(TimeSpan.FromHours(12),
            System.Windows.Threading.DispatcherPriority.Background, async (_, _) => await CheckHuntUpdateAsync(TimeSpan.FromSeconds(60)),
            System.Windows.Threading.Dispatcher.CurrentDispatcher);
        _huntUpdateTimer.Start();
    }

    /// <summary>
    /// Atualização automática: consulta a loja e, havendo versão nova, baixa e valida (hash SHA-256 +
    /// manifesto). Contas que abrirem depois já usam a nova; contas abertas NÃO são recarregadas
    /// sozinhas — pegam a nova versão na próxima recarga/reabertura.
    /// </summary>
    public async Task CheckHuntUpdateAsync(TimeSpan timeout)
    {
        if (_hunt is null || _huntBusy || !_settings.EnableHuntExtension || !_settings.AutoUpdateHuntExtension || _hunt.InstalledVersion is null)
            return;
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            var info = await _hunt.CheckForUpdateAsync(RuntimeVersion, cts.Token);
            if (info is null)
            {
                LoggingService.Log.Info($"Extensão /hunt v{_hunt.InstalledVersion} já é a mais recente.");
                return;
            }
            var (ok, message) = await _hunt.InstallUpdateAsync(info, cts.Token);
            if (!ok)
            {
                Notice = $"Atualização automática da extensão /hunt falhou: {message}";
                return;
            }
            OnHuntChanged();
            var open = Accounts.Count(a => a.Session.IsRunning);
            Notice = open == 0
                ? $"Extensão /hunt atualizada para v{info.Version}."
                : $"Extensão /hunt v{info.Version} baixada. Ela entra em cada conta quando você recarregar ou reabrir a conta (ou clique em \"Baixar / atualizar\" para aplicar já).";
        }
        catch (Exception ex)
        {
            // Sem rede, loja fora do ar ou tempo esgotado: tenta de novo na próxima rodada.
            LoggingService.Log.Warn($"Verificação de atualização da extensão falhou: {ex.Message}");
        }
    }

    private async Task ApplyHuntAsync(string doneMessage)
    {
        SetHuntBusy(true);
        try
        {
            await _manager.ApplyHuntExtensionAsync();
            Notice = doneMessage;
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error("Falha ao aplicar a extensão", ex);
            Notice = $"Falha ao aplicar a extensão: {ex.Message}";
        }
        finally
        {
            SetHuntBusy(false);
        }
    }

    private void SetHuntBusy(bool busy)
    {
        _huntBusy = busy;
        OnPropertyChanged(nameof(HuntStatusText));
        CommandManager.InvalidateRequerySuggested();
    }

    public void OnHuntChanged()
    {
        OnPropertyChanged(nameof(EnableHuntExtension));
        OnPropertyChanged(nameof(HuntAvailable));
        OnPropertyChanged(nameof(HuntStatusText));
        foreach (var a in Accounts) a.OnHuntAvailabilityChanged();
    }

    private void ApplyAllViews()
    {
        foreach (var a in Accounts) a.ApplyView();
    }

    public void SaveSettings() => _settingsService.Save();

    private async Task ExportSnapshotAsync()
    {
        var snap = await _memory.SampleAsync();
        var path = _benchmark.ExportSnapshot(snap);
        Notice = $"Snapshot salvo em {path}";
        LoggingService.Log.Info($"Snapshot de memória exportado: {path}");
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false });
    }

    public void Dispose()
    {
        _memory.Sampled -= OnMemorySampled;
    }
}
