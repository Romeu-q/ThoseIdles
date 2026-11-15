using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThoseIdles.Services;
using ThoseIdles.ViewModels;

namespace ThoseIdles.Views;

/// <summary>
/// O RESUMO das contas: não é uma tela à parte, é uma função da janela principal que observa as quatro contas de uma vez
/// (como o "Modo Simples" do PokeGrid): cartões com as somas, a tabela por conta (clicar no cabeçalho ordena, na linha
/// abre a conta), os drops de quem está na Outland, últimas capturas, shinies e o total de hoje. Fica no lugar da grade de
/// jogos quando o modo Resumo está ligado, e atualiza a cada segundo (só enquanto está à vista) a partir de
/// <see cref="BrowserSession.Stats"/>, com a página aberta ou no modo econômico.
/// </summary>
public partial class SummaryView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _ordem = "conta";
    private bool _decrescente;

    public SummaryView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Atualizar();
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true) { Atualizar(); _timer.Start(); }
            else _timer.Stop();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>Recalcula tudo (público: o autoteste chama).</summary>
    public SummarySnapshot? Atualizar()
    {
        if (Vm is not { } vm) return null;
        var snap = SummaryBuilder.Build(vm.Accounts.Select(a => a.Session), DailyTotals.Instance, vm.VisibleAccounts);
        Mostrar(snap);
        return snap;
    }

    private void Mostrar(SummarySnapshot s)
    {
        Avisos.ItemsSource = s.Alertas;
        CartaoAvisos.Visibility = s.Alertas.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        CartaoTudoCerto.Visibility = s.Alertas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TudoCerto.Text = s.TudoCerto;

        Linhas.ItemsSource = Ordenar(s.Linhas);
        Cartoes.Tag = Math.Clamp(s.Cartoes.Count, 1, 2);
        Cartoes.ItemsSource = s.Cartoes;
        ListaOutland.ItemsSource = s.Outland;
        CartaoOutland.Visibility = s.Outland.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        Capturas.ItemsSource = s.Capturas;
        SemCapturas.Visibility = s.Capturas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Shinies.ItemsSource = s.Shinies;
        SemShinies.Visibility = s.Shinies.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>Clicar na linha leva à conta: sai do Resumo e põe aquela conta em destaque.</summary>
    private void OnOpenAccount(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int slot } || Vm is not { } vm) return;
        vm.IsSummaryMode = false;
        if (!(vm.IsFocusMode && vm.FocusedIndex == slot - 1)) vm.ToggleFocus(slot);
    }
}
