using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThoseIdles.Services;
using ThoseIdles.ViewModels;

namespace ThoseIdles.Views;

/// <summary>
/// O resumo de todas as contas (como o "Modo Simples" do PokeGrid): cartões com as somas, a tabela por
/// conta (clique no cabeçalho ordena, na linha abre a conta), últimas capturas, shinies e o total de hoje.
/// Atualiza a cada segundo a partir de <see cref="BrowserSession.Stats"/>, nos dois modos da conta.
/// </summary>
public partial class SummaryWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly Func<IEnumerable<BrowserSession>> _sessoes;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _ordem = "conta";
    private bool _decrescente;

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public SummaryWindow(MainViewModel vm, Func<IEnumerable<BrowserSession>> sessoes)
    {
        _vm = vm;
        _sessoes = sessoes;
        InitializeComponent();
        _timer.Tick += (_, _) => Atualizar();
        Loaded += (_, _) => { Atualizar(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var on = 1;
        if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
    }

    /// <summary>Recalcula tudo (público: o autoteste chama).</summary>
    public SummarySnapshot Atualizar()
    {
        var snap = SummaryBuilder.Build(_sessoes(), DailyTotals.Instance, _vm.VisibleAccounts);
        Mostrar(snap);
        return snap;
    }

    private void Mostrar(SummarySnapshot s)
    {
        TotOuro.Text = s.Totais.OuroH;
        TotXp.Text = s.Totais.XpH;
        TotAbates.Text = s.Totais.AbatesH;
        TotCaps.Text = s.Totais.Capturas;
        TotShiny.Text = s.Totais.Shiny;
        TotAtivas.Text = s.Totais.Ativas;
        TotOutland.Text = s.Totais.Outland;

        var linhas = Ordenar(s.Linhas);
        Linhas.ItemsSource = linhas;
        Capturas.ItemsSource = s.Capturas;
        SemCapturas.Visibility = s.Capturas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Shinies.ItemsSource = s.Shinies;
        SemShinies.Visibility = s.Shinies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        HojeTitulo.Text = $"HOJE · {s.Hoje.Data}";
        HojeOuro.Text = s.Hoje.Ouro;
        HojeXp.Text = s.Hoje.Xp;
        HojeAbates.Text = s.Hoje.Abates;
        HojeCaps.Text = s.Hoje.Capturas;
        HojeShiny.Text = s.Hoje.Shiny;
        Atualizado.Text = $"atualizado {DateTime.Now:HH:mm:ss}";
    }

    private List<SummaryRow> Ordenar(IReadOnlyList<SummaryRow> linhas)
    {
        var lista = linhas.ToList();
        lista.Sort((a, b) =>
        {
            var x = a.Chaves[_ordem];
            var y = b.Chaves[_ordem];
            var c = x is string sx && y is string sy ? string.Compare(sx, sy, StringComparison.CurrentCultureIgnoreCase) : x.CompareTo(y);
            if (c == 0) c = a.Slot.CompareTo(b.Slot);
            return _decrescente ? -c : c;
        });
        return lista;
    }

    private void OnSort(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string chave }) return;
        // Os números começam do maior; o texto, de A a Z. O segundo clique inverte.
        var numerico = chave is "tempo" or "ouro" or "xp" or "abates" or "capturas";
        if (_ordem == chave) _decrescente = !_decrescente;
        else { _ordem = chave; _decrescente = numerico; }
        Atualizar();
    }

    private void OnOpenAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int slot }) return;
        // Leva a janela principal para a frente, com a conta em destaque (a menos que ela já esteja).
        if (!(_vm.IsFocusMode && _vm.FocusedIndex == slot - 1)) _vm.ToggleFocus(slot);
        if (Owner is { } dono)
        {
            if (dono.WindowState == WindowState.Minimized) dono.WindowState = WindowState.Normal;
            dono.Activate();
        }
    }
}
