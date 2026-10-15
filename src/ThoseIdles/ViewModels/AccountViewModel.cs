using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using ThoseIdles.Mvvm;
using ThoseIdles.Services;

namespace ThoseIdles.ViewModels;

public sealed class AccountViewModel : ObservableObject
{
    private static readonly Brush Gray = Freeze(0x6B, 0x6E, 0x78);
    private static readonly Brush Blue = Freeze(0x4C, 0x9A, 0xFF);
    private static readonly Brush Amber = Freeze(0xE0, 0xA8, 0x2E);
    private static readonly Brush Green = Freeze(0x3F, 0xC4, 0x6E);
    private static readonly Brush Orange = Freeze(0xF0, 0x7A, 0x2A);
    private static readonly Brush Red = Freeze(0xE5, 0x48, 0x4D);

    private readonly MainViewModel _main;
    private readonly AppSettings _settings;
    private PanelRole _role = PanelRole.Grid;
    private bool _isEditingName;
    private string _editName = "";
    private bool _isConfirmingClear;
    private string _memoryText = "";
    private double _panelWidth;

    public AccountViewModel(BrowserSession session, MainViewModel main, AppSettings settings)
    {
        Session = session;
        _main = main;
        _settings = settings;
        session.PropertyChanged += OnSessionChanged;
        session.SettingsChanged += (_, _) => _main.SaveSettings();
        session.UserZoomChanged += (_, z) =>
        {
            Session.Account.Zoom = z;
            OnPropertyChanged(nameof(ZoomText));
            _main.SaveSettingsSoon();
        };
        ResetZoomCommand = new RelayCommand(() => SetZoom(1.0));
        ZoomInCommand = new RelayCommand(() => SetZoom(Math.Round((Session.Account.Zoom + 0.1) * 10) / 10));
        ZoomOutCommand = new RelayCommand(() => SetZoom(Math.Round((Session.Account.Zoom - 0.1) * 10) / 10));
        ToggleHuntCommand = new AsyncCommand(async () =>
        {
            await Session.SetHuntEnabledAsync(!Session.Account.HuntEnabled);
            OnHuntAvailabilityChanged();
            _main.OnHuntChanged();
        });

        FocusCommand = new RelayCommand(() => _main.ToggleFocus(Slot));
        ReloadCommand = new AsyncCommand(() => Session.ReloadAsync());
        RestartCommand = new AsyncCommand(() => Session.RestartAsync());
        ToggleOpenCommand = new AsyncCommand(async () =>
        {
            if (Session.IsRunning) Session.Close();
            else await Session.StartAsync();
        });
        DevToolsCommand = new RelayCommand(() => Session.OpenDevTools(), () => _settings.DeveloperMode && Session.Core is not null);
        BeginRenameCommand = new RelayCommand(() => { EditName = DisplayName; IsEditingName = true; });
        CommitRenameCommand = new RelayCommand(CommitRename);
        CancelRenameCommand = new RelayCommand(() => IsEditingName = false);
        RequestClearCommand = new RelayCommand(() => IsConfirmingClear = true);
        CancelClearCommand = new RelayCommand(() => IsConfirmingClear = false);
        ConfirmClearCommand = new AsyncCommand(async () =>
        {
            IsConfirmingClear = false;
            await Session.ClearDataAsync();
        });
    }

    public BrowserSession Session { get; }
    public int Slot => Session.Slot;
    public string ProfileName => Session.ProfileName;

    public string DisplayName => Session.Account.DisplayName;

    public PanelRole Role
    {
        get => _role;
        set
        {
            if (!Set(ref _role, value)) return;
            OnPropertyChanged(nameof(IsCompact));
            OnPropertyChanged(nameof(IsFocused));
            OnPropertyChanged(nameof(ZoomText));
            ApplyView();
        }
    }

    public bool IsCompact => _role == PanelRole.Thumbnail;
    public bool IsFocused => _role == PanelRole.Focused;
    public bool IsRunning => Session.IsRunning;
    public string StatusDetail => Session.StatusDetail;

    public string StatusText => Session.Status switch
    {
        SessionStatus.Closed => "Fechada",
        SessionStatus.Loading => "Carregando",
        SessionStatus.AwaitingConnection => "Sem conexão do jogo",
        SessionStatus.Loaded => "Carregada",
        SessionStatus.Connected => "Conectada",
        SessionStatus.Disconnected => "Reconectando",
        SessionStatus.Offline => "Offline",
        SessionStatus.Error => "Erro",
        _ => "",
    };

    public Brush StatusBrush => Session.Status switch
    {
        SessionStatus.Connected => Green,
        SessionStatus.Loading => Blue,
        SessionStatus.AwaitingConnection or SessionStatus.Loaded => Amber,
        SessionStatus.Disconnected => Orange,
        SessionStatus.Error => Red,
        _ => Gray,
    };

    /// <summary>Faixa de aviso acima da página (não pode ser sobreposição: o WebView2 é um HWND).</summary>
    public bool ShowBanner => Session.Status is SessionStatus.Error or SessionStatus.Offline || !Session.IsRunning;

    public string MemoryText
    {
        get => _memoryText;
        set => Set(ref _memoryText, value);
    }

    public bool IsEditingName
    {
        get => _isEditingName;
        set => Set(ref _isEditingName, value);
    }

    public string EditName
    {
        get => _editName;
        set => Set(ref _editName, value);
    }

    public bool IsConfirmingClear
    {
        get => _isConfirmingClear;
        set => Set(ref _isConfirmingClear, value);
    }

    // ------------------------------------------------------------------ extensão /hunt

    public ICommand ToggleHuntCommand { get; }

    /// <summary>Mostra o botão da extensão no cabeçalho quando ela está baixada e ligada no app.</summary>
    public bool ShowHuntToggle => _main.HuntAvailable;

    public bool HuntEnabled => Session.Account.HuntEnabled;
    public bool HuntActive => Session.HuntActive;

    public string HuntTooltip => Session.Account.HuntEnabled
        ? "Extensão /hunt LIGADA nesta conta (clique para desligar; recarrega só esta conta)"
        : "Extensão /hunt desligada nesta conta (clique para ligar; recarrega só esta conta)";

    public void OnHuntAvailabilityChanged()
    {
        OnPropertyChanged(nameof(ShowHuntToggle));
        OnPropertyChanged(nameof(HuntEnabled));
        OnPropertyChanged(nameof(HuntTooltip));
    }

    public ICommand FocusCommand { get; }
    public ICommand ReloadCommand { get; }
    public ICommand RestartCommand { get; }
    public ICommand ToggleOpenCommand { get; }
    public ICommand DevToolsCommand { get; }
    public ICommand BeginRenameCommand { get; }
    public ICommand CommitRenameCommand { get; }
    public ICommand CancelRenameCommand { get; }
    public ICommand RequestClearCommand { get; }
    public ICommand CancelClearCommand { get; }
    public ICommand ConfirmClearCommand { get; }

    /// <summary>Chamado pela view quando o painel muda de tamanho (zoom das miniaturas depende disso).</summary>
    public void OnPanelResized(double width)
    {
        if (Math.Abs(width - _panelWidth) < 1) return;
        _panelWidth = width;
        if (_role == PanelRole.Thumbnail) ApplyView();
    }

    public void ApplyView()
    {
        var zoom = _role switch
        {
            // Miniatura: reduz por zoom para manter o layout "desktop" da página (~1280 px CSS)
            // em vez de forçar o layout mobile. A página continua visível e executando.
            PanelRole.Thumbnail when _settings.ScaleThumbnails && _panelWidth > 0 => Math.Clamp(_panelWidth / 1280.0, 0.25, 1.0),
            // Grade e foco: o zoom próprio desta conta.
            _ => Session.Account.Zoom,
        };
        Session.ApplyView(_role, zoom);
        OnPropertyChanged(nameof(ZoomText));
    }

    // ------------------------------------------------------------------ zoom por conta

    public ICommand ResetZoomCommand { get; }
    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }

    /// <summary>"110%"; vazio em 100% e nas miniaturas.</summary>
    public string ZoomText => _role == PanelRole.Thumbnail || Math.Abs(Session.Account.Zoom - 1.0) < 0.005
        ? ""
        : $"{Session.Account.Zoom * 100:0}%";

    public void SetZoom(double zoom)
    {
        Session.Account.Zoom = Math.Clamp(zoom, 0.25, 3.0);
        ApplyView();
        _main.SaveSettingsSoon();
    }

    private void CommitRename()
    {
        var name = (EditName ?? "").Trim();
        if (name.Length > 0)
        {
            Session.Account.DisplayName = name.Length > 40 ? name[..40] : name;
            OnPropertyChanged(nameof(DisplayName));
            _main.SaveSettings();
            LoggingService.Log.Info($"[conta{Slot}] Renomeada (perfil {ProfileName} inalterado).");
        }
        IsEditingName = false;
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BrowserSession.Status):
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(StatusBrush));
                OnPropertyChanged(nameof(ShowBanner));
                _main.OnSessionStatusChanged();
                break;
            case nameof(BrowserSession.StatusDetail):
                OnPropertyChanged(nameof(StatusDetail));
                break;
            case nameof(BrowserSession.IsRunning):
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(ShowBanner));
                if (!Session.IsRunning) MemoryText = "";
                CommandManager.InvalidateRequerySuggested();
                break;
            case nameof(BrowserSession.HuntActive):
                OnPropertyChanged(nameof(HuntActive));
                OnPropertyChanged(nameof(HuntEnabled));
                OnPropertyChanged(nameof(HuntTooltip));
                _main.OnHuntChanged();
                break;
            case nameof(BrowserSession.WebView):
                // Novo controle (reinício): reaplica zoom/meta de memória.
                if (Session.WebView is not null) ApplyView();
                break;
        }
    }

    private static Brush Freeze(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
