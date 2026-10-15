using System.ComponentModel;
using System.Windows;
using ThoseIdles.Services;
using ThoseIdles.ViewModels;

namespace ThoseIdles;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Func<Task> _shutdown;
    private bool _closingDone;

    public MainWindow(MainViewModel vm, AppSettings settings, Func<Task> shutdown)
    {
        _settings = settings;
        _shutdown = shutdown;
        InitializeComponent();
        DataContext = vm;
        RestorePlacement();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Barra de título escura (Windows 10 20H1+ / 11). Falha silenciosa em versões sem suporte.
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    private void RestorePlacement()
    {
        var w = _settings.Window;
        Width = Math.Max(MinWidth, w.Width);
        Height = Math.Max(MinHeight, w.Height);
        if (w.Left is double left && w.Top is double top && double.IsFinite(left) && double.IsFinite(top))
        {
            // Só restaura posição se ela ainda cair dentro da área virtual da tela.
            var vsLeft = SystemParameters.VirtualScreenLeft;
            var vsTop = SystemParameters.VirtualScreenTop;
            if (left >= vsLeft - 50 && top >= vsTop - 50 &&
                left < vsLeft + SystemParameters.VirtualScreenWidth - 100 &&
                top < vsTop + SystemParameters.VirtualScreenHeight - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (w.Maximized) WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty || !double.IsFinite(r.Left) || !double.IsFinite(r.Width)) return;
        _settings.Window.Left = r.Left;
        _settings.Window.Top = r.Top;
        _settings.Window.Width = r.Width;
        _settings.Window.Height = r.Height;
        _settings.Window.Maximized = WindowState == WindowState.Maximized;
    }

    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closingDone)
        {
            base.OnClosing(e);
            return;
        }
        // Encerramento assíncrono: fecha os WebView2 e espera o navegador sair antes de deixar a janela fechar.
        e.Cancel = true;
        SavePlacement();
        IsEnabled = false;
        try
        {
            await _shutdown();
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error("Erro no encerramento", ex);
        }
        _closingDone = true;
        // Fora do OnClosing atual: se o encerramento terminou de forma síncrona (nenhuma conta
        // aberta), chamar Close() aqui dentro lançaria InvalidOperationException e a janela travaria.
        await Dispatcher.InvokeAsync(Close, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnSplitCommitted(object? sender, EventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.SaveSettings();
    }

    private void OnDismissNotice(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.Notice = "";
    }
}
