using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ThoseIdles.Services;
using ThoseIdles.ViewModels;

namespace ThoseIdles.Views;

/// <summary>
/// A versão mínima do jogo para a conta em MODO ECONÔMICO (sem a página do jogo, poucos MB de RAM):
/// caçar em qualquer hunt liberada, trocar o Pokémon em campo, ir ao Centro Pokémon e curar, mexer no
/// Depot (mandar ao time, Coleção, vender com confirmação), ligar as automações de bola/revive/poção e
/// comprar bolas. Cada botão manda ao servidor a MESMA mensagem do clique no jogo (hunt.select, team.active,
/// team.move, colecao.mover, shop.sellPokemon, auto.set, centro.ir/curar/desistir, shop.buy) — nada além.
/// O servidor continua mandando em tudo: nível, trava de troca, luta em andamento e Safari são dele.
/// </summary>
public partial class EconomyGameView : UserControl
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private const int PorPagina = 25;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private string _sigHunts = "", _sigTime = "", _sigDepot = "", _sigBolas = "", _sigMarket = "", _sigMelhores = "";
    private int _paginaDepot;
    private string _avisoTexto = "";
    private DateTime _avisoAte = DateTime.MinValue;

    public EconomyGameView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => { if (IsVisible) Atualizar(); };
        Loaded += (_, _) => { _timer.Start(); Atualizar(); };
        Unloaded += (_, _) => _timer.Stop();
    }

    private BrowserSession? Sessao => (DataContext as AccountViewModel)?.Session;
    private EconomyClient? Eco => Sessao?.Economy;

    // ------------------------------------------------------------------ as linhas das listas

    public sealed record MelhorLinha(string Slug, string Titulo, string Detalhe, string Botao, bool PodeCacar);
    public sealed record HuntLinha(string Slug, string Nome, string Detalhe, string Botao, bool PodeCacar, string Borda, double Opacidade);
    public sealed record TimeLinha(long Id, string Titulo, string Detalhe, string Botao, bool PodeColocar, string Borda, double HpFrac, string HpCor, Visibility HpVisivel);
    public sealed record DepotLinha(long Id, string Titulo, string Detalhe, string BotaoColecao, bool PodeVender, string DicaVender);
    public sealed record BolaLinha(int Id, string Titulo, string Quantidade, string Borda);
    public sealed record MarketLinha(int Id, string Nome, string Detalhe, bool PodeComprar = true, double Opacidade = 1, string AutoTexto = "🔄 Auto", string AutoBorda = "#2C2C35");

    // ------------------------------------------------------------------ atualização

    private static string Curto(double v) => SummaryBuilder.Curto(v);

    private void Mensagem(string texto)
    {
        _avisoTexto = texto;
        _avisoAte = DateTime.UtcNow.AddSeconds(8);
    }

    /// <summary>Redesenha o que mudou (a cada 0,7 s enquanto a tela está à vista; público para o autoteste).</summary>
    public void Atualizar()
    {
        if (Eco is not { } eco)
        {
            Situacao.Text = "Modo econômico desligado.";
            return;
        }
        var g = eco.Game;
        var st = eco.Stats;

        // Treinador
        Nick.Text = g.Nick ?? eco.Session.Nick;
        Nivel.Text = g.Level > 0 ? $"Nv {g.Level:N0}" : "";
        Ouro.Text = $"🪙 {g.Gold.ToString("N0", Pt)}";
        Diamantes.Text = $"💎 {g.Diamonds.ToString("N0", Pt)}";
        Gemas.Text = $"◆ {g.Orbs.ToString("N0", Pt)}";
        var dentro = Math.Max(0, g.Xp - g.XpNivel);
        var faixa = Math.Max(1, g.XpProximo - g.XpNivel);
        BarraXp.Value = Math.Min(1, (double)dentro / faixa);
        TextoXp.Text = g.XpProximo > 0 ? $"{dentro.ToString("N0", Pt)} / {faixa.ToString("N0", Pt)} xp" : "";

        // Situação
        var hunt = g.HuntAtual;
        switch (eco.State)
        {
            case EconomyState.Connecting: Situacao.Text = "Conectando…"; break;
            case EconomyState.Reconnecting: Situacao.Text = "Reconectando…"; break;
            case EconomyState.Failed: Situacao.Text = eco.Detail; break;
            default:
                Situacao.Text = !g.Recebeu ? "Aguardando os dados da conta…"
                    : g.NoSafari ? "No Safari"
                    : g.HuntSlug is null ? (g.NoCentro ? "No Centro Pokémon" : "Sem hunt — escolha uma em “Caçar”")
                    : $"Caçando em {hunt?.Nome ?? g.HuntSlug}{(hunt is not null ? $" (Nv {hunt.Nivel})" : "")}{(hunt is { Outland: true } ? "  ·  🌌 Outland" : "")}";
                break;
        }
        var partes = new List<string>();
        if (g.Ativo is { } at) partes.Add($"{at.Nome} Nv {at.Level:N0}{(at.MaxHp > 0 ? $" · HP {at.Hp:N0}/{at.MaxHp:N0}" : "")}");
        if (g.HuntSlug is not null)
        {
            var (o, x, a) = st.PorHoraNaHunt();
            partes.Add($"{Curto(o)} ouro/h · {Curto(x)} XP/h · {Curto(a)} abates/h");
        }
        partes.Add($"{st.Capturas} capturas · ✨ {st.ShiniesVistos}/{st.ShiniesCapturados}");
        Detalhe.Text = string.Join("  ·  ", partes);

        if (_avisoAte > DateTime.UtcNow) { Aviso.Text = _avisoTexto; Aviso.Visibility = Visibility.Visible; }
        else if (eco.UltimoErro is { } err && DateTime.UtcNow - eco.UltimoErroEm < TimeSpan.FromSeconds(12))
        {
            Aviso.Text = $"⚠ o jogo recusou: {err}";
            Aviso.Visibility = Visibility.Visible;
        }
        else Aviso.Visibility = Visibility.Collapsed;

        if (PainelCacar.Visibility == Visibility.Visible) { AtualizarMelhores(g); AtualizarHunts(g); }
        if (PainelTime.Visibility == Visibility.Visible) AtualizarTime(g);
        if (PainelDepot.Visibility == Visibility.Visible) AtualizarDepot(g);
        if (PainelMarket.Visibility == Visibility.Visible) AtualizarMarket(g);
        if (PainelBolas.Visibility == Visibility.Visible) AtualizarBolas(g);
        if (PainelRegistro.Visibility == Visibility.Visible) TextoRegistro.Text = string.Join("\n", st.Registro);

        Rodape.Text = $"atualizado {DateTime.Now:HH:mm:ss} · {eco.MessagesReceived:N0} mensagens";
        BtCentro.IsEnabled = g.HuntSlug is not null;
    }

    private void AtualizarHunts(AccountGameState g)
    {
        var filtro = Filtro.Text.Trim();
        var melhor = Sessao?.MelhoresHunts.FirstOrDefault()?.Slug;
        var sig = $"{g.Level}|{g.HuntSlug}|{filtro}|{g.Hunts.Count}|{melhor}";
        if (sig == _sigHunts) return;
        _sigHunts = sig;
        var linhas = g.Hunts
            .Where(h => filtro.Length == 0 || h.Nome.Contains(filtro, StringComparison.CurrentCultureIgnoreCase) || (h.Regiao?.Contains(filtro, StringComparison.CurrentCultureIgnoreCase) ?? false))
            .OrderByDescending(h => h.Nivel <= g.Level).ThenByDescending(h => h.Nivel <= g.Level ? h.Nivel : -h.Nivel)
            .Take(250)
            .Select(h =>
            {
                var libera = h.Nivel <= g.Level;
                var aqui = h.Slug == g.HuntSlug;
                return new HuntLinha(h.Slug, h.Nome, $"Nv {h.Nivel:N0}{(h.Outland ? " · 🌌 Outland" : h.Regiao is { Length: > 0 } r ? $" · {r}" : "")}{(h.Slug == melhor ? " · ★ melhor XP agora" : "")}",
                    aqui ? "● Aqui" : libera ? "▶ Caçar" : $"Nv {h.Nivel:N0}", libera && !aqui, aqui ? "#3FC46E" : "#2C2C35", libera ? 1 : 0.45);
            }).ToList();
        ListaHunts.ItemsSource = linhas;
    }


    private void AtualizarMelhores(AccountGameState g)
    {
        if (Sessao is not { } s) return;
        // Calcula (sozinho) quando a aba está à vista e o ranking está velho, ou quando o Pokémon / os bônus mudaram.
        if (s.PrecisaDoRanking()) _ = s.AtualizarRankingAsync();
        MelhoresInfo.Text = s.RankingStatus.Length > 0 ? s.RankingStatus
            : s.RankingPara.Length > 0 ? $"Para {s.RankingPara}, na ordem do XP/h · calculado às {s.RankingQuando:HH:mm} · {s.RankingOrigem} (renova a cada 5 min ou ao trocar de Pokémon, nível ou bônus)."
            : "Escolha um Pokémon em campo para calcular.";
        var lista = s.MelhoresHunts;
        var sig = $"{s.RankingPara}|{s.RankingQuando}|{g.HuntSlug}|{g.Level}|{lista.Count}";
        if (sig == _sigMelhores) return;
        _sigMelhores = sig;
        ListaMelhores.ItemsSource = lista.Select(m =>
        {
            var info = g.Hunts.FirstOrDefault(h => h.Slug == m.Slug);
            var aqui = m.Slug == g.HuntSlug;
            var livre = info is null || info.Nivel <= g.Level;
            return new MelhorLinha(m.Slug, $"{(m.Posicao == 1 ? "★ " : "")}{m.Posicao}. {m.Nome}{(m.Outland || info is { Outland: true } ? "  🌌 Outland" : "")}",
                $"{(m.Regiao.Length > 0 ? char.ToUpper(m.Regiao[0], Pt) + m.Regiao[1..] + " · " : "")}Nv {m.Nivel:N0} · XP/h {m.XpH}{(m.AbatesH is { Length: > 0 } and not "—" ? $" · {m.AbatesH} abates/h" : "")}{(m.MataEm is { Length: > 0 } and not "—" ? $" · mata em {m.MataEm}" : "")} · risco {m.Risco} · ouro/h {m.OuroH}{(m.Golpe.Length > 0 ? $" · {m.Golpe}" : "")}",
                aqui ? "● Aqui" : "▶ Caçar", !aqui && livre);
        }).ToList();
    }

    private void OnRecalcular(object sender, RoutedEventArgs e)
    {
        if (Sessao is { } s) { _sigMelhores = ""; _ = s.AtualizarRankingAsync(forcar: true); }
    }

    /// <summary>Em painéis baixos a tela inteira rola (o cabeçalho junto); nos altos, só as listas rolam.</summary>
    private void OnTamanho(object sender, SizeChangedEventArgs e) => Raiz.Height = Math.Max(e.NewSize.Height, 440);

    private void AtualizarTime(AccountGameState g)
    {
        var time = g.Time;
        var sig = string.Join(";", time.Select(p => $"{p.Id}:{p.Level}:{p.Hp}:{p.MaxHp}")) + $"|{g.ActiveId}";
        if (sig == _sigTime) return;
        _sigTime = sig;
        ListaTime.ItemsSource = time.Select(p =>
        {
            var ativo = p.Id == g.ActiveId;
            var frac = p.MaxHp is > 0 ? Math.Clamp((double)(p.Hp ?? 0) / p.MaxHp.Value, 0, 1) : 0;
            return new TimeLinha(p.Id, $"{(p.Slot ?? 0) + 1}. {p.Nome}{(p.Shiny ? " ✨" : "")}",
                $"Nv {p.Level:N0} · P{p.Potencia}{(p.MaxHp > 0 ? $" · HP {p.Hp:N0}/{p.MaxHp:N0}" : "")}",
                ativo ? "● Em campo" : "Em campo", !ativo, ativo ? "#3FC46E" : "#2C2C35", frac,
                frac > 0.5 ? "#3FC46E" : frac > 0.2 ? "#F0B429" : "#E5484D", p.MaxHp is > 0 ? Visibility.Visible : Visibility.Collapsed);
        }).ToList();
    }

    private void AtualizarDepot(AccountGameState g)
    {
        var filtro = FiltroDepot.Text.Trim();
        var todos = g.Depot.Where(p => filtro.Length == 0 || p.Nome.Contains(filtro, StringComparison.CurrentCultureIgnoreCase)).ToList();
        var paginas = Math.Max(1, (todos.Count + PorPagina - 1) / PorPagina);
        _paginaDepot = Math.Clamp(_paginaDepot, 0, paginas - 1);
        var pagina = todos.Skip(_paginaDepot * PorPagina).Take(PorPagina).ToList();
        var sig = $"{filtro}|{_paginaDepot}|{todos.Count}|" + string.Join(",", pagina.Select(p => $"{p.Id}:{p.Level}:{g.Travado(p.Id)}"));
        DepotPagina.Text = $"{todos.Count:N0} · pág. {_paginaDepot + 1}/{paginas}";
        if (sig == _sigDepot) return;
        _sigDepot = sig;
        ListaDepot.ItemsSource = pagina.Select(p =>
        {
            var travado = g.Travado(p.Id);
            var pode = !p.Starter && !travado;
            return new DepotLinha(p.Id, $"{p.Nome}{(p.Shiny ? " ✨" : "")}{(travado ? " 🔒" : "")}", $"Nv {p.Level:N0} · P{p.Potencia}",
                travado ? "🔓 Soltar" : "🔒 Coleção", pode,
                p.Starter ? "O Pokémon inicial não pode ser vendido." : travado ? "Está na Coleção: tire de lá antes de vender." : "Vender este Pokémon ao NPC (pede confirmação).");
        }).ToList();
    }

    private void SincronizarControlesAutoCompra()
    {
        if (Sessao?.Account is not { } acc) return;
        AutoCompraBolas.IsChecked = AutoCompraBolasMarket.IsChecked = acc.AutoBuyBalls;
        AutoCompraPocoes.IsChecked = AutoCompraPocoesMarket.IsChecked = acc.AutoBuyPotions;
        AutoCompraRevives.IsChecked = AutoCompraRevivesMarket.IsChecked = acc.AutoBuyRevives;
        var txtBolas = $"⚪ < {acc.AutoBuyBallsMin:N0} → +{acc.AutoBuyBallsQty:N0}";
        var txtItens = $"💊 < {acc.AutoBuyItemsMin:N0} → +{acc.AutoBuyItemsQty:N0}";
        BtRegraBolas.Content = BtRegraBolasMarket.Content = txtBolas;
        BtRegraItens.Content = BtRegraItensMarket.Content = txtItens;
    }

    private void AtualizarBolas(AccountGameState g)
    {
        SincronizarControlesAutoCompra();
        AutoNota.Text = !g.Recebeu ? "Aguardando as automações do jogo…"
            : g.Vip ? "Conta VIP: as duas bolas automáticas estão liberadas."
            : "Sem VIP: as bolas automáticas são do VIP. Reviver, poção e voltar à hunt funcionam sem.";
        AutoAteCapturar.IsChecked = g.Auto("autoBallAteCapturar") && g.Vip;
        AutoSemParar.IsChecked = (g.Auto("autoBallSemParar") || (g.Auto("autoBall") && !g.Auto("autoBallAteCapturar"))) && g.Vip;
        AutoRevive.IsChecked = g.Auto("autoRevive");
        AutoPotion.IsChecked = g.Auto("autoPotion");
        AutoVoltar.IsChecked = g.Auto("autoVoltarHunt");

        var ids = g.BallIds;
        var potIds = g.PotionIds;
        var revIds = g.ReviveIds;
        var pocoes = g.CatalogoMarket.Where(m => m.Categoria == "potion").ToList();
        var revives = g.CatalogoMarket.Where(m => m.Categoria == "revive").ToList();
        var sig = string.Join(",", g.CatalogoBolas.Select(b => $"{b.Id}:{g.Balls(b.Id)}")) + "|" + string.Join(",", ids)
                + "|" + string.Join(",", pocoes.Select(p => $"{p.Id}:{g.Items(p.Id)}")) + "|" + string.Join(",", potIds)
                + "|" + string.Join(",", revives.Select(r => $"{r.Id}:{g.Items(r.Id)}")) + "|" + string.Join(",", revIds);
        if (sig == _sigBolas) return;
        _sigBolas = sig;
        ListaBolas.ItemsSource = g.CatalogoBolas.Select(b =>
        {
            var on = ids.Contains(b.Id);
            return new BolaLinha(b.Id, $"{(on ? "✓ " : "")}{b.Nome}", g.Balls(b.Id).ToString("N0", Pt), on ? "#3FC46E" : "#2C2C35");
        }).ToList();
        ListaAutoPocoes.ItemsSource = pocoes.Select(p =>
        {
            var on = potIds.Contains(p.Id);
            return new BolaLinha(p.Id, $"{(on ? "✓ " : "")}{p.Nome} ({p.Descricao})", g.Items(p.Id).ToString("N0", Pt), on ? "#3FC46E" : "#2C2C35");
        }).ToList();
        ListaAutoRevives.ItemsSource = revives.Select(r =>
        {
            var on = revIds.Contains(r.Id);
            return new BolaLinha(r.Id, $"{(on ? "✓ " : "")}{r.Nome} ({r.Descricao})", g.Items(r.Id).ToString("N0", Pt), on ? "#3FC46E" : "#2C2C35");
        }).ToList();
    }

    private void AtualizarMarket(AccountGameState g)
    {
        SincronizarControlesAutoCompra();
        var eco = Eco;
        var acc = Sessao?.Account;
        var bolas = g.CatalogoBolas;
        var catalogo = g.CatalogoMarket;
        var sig = string.Join(",", bolas.Select(b => $"{b.Id}:{g.Balls(b.Id)}:{b.Preco}:{b.Compravel}:{eco?.EstaEmAutoCompraBola(b.Id)}"))
                + "|" + string.Join(",", catalogo.Select(m => $"{m.Id}:{g.Items(m.Id)}:{m.Preco}:{eco?.EstaEmAutoCompraItem(m.Id)}"))
                + $"|{acc?.AutoBuyBallsMin}:{acc?.AutoBuyBallsQty}:{acc?.AutoBuyItemsMin}:{acc?.AutoBuyItemsQty}";
        if (sig == _sigMarket) return;
        _sigMarket = sig;

        ListaMarketBolas.ItemsSource = bolas.Select(b =>
        {
            var pode = b.Compravel && (b.Preco is null or > 0);
            var auto = eco?.EstaEmAutoCompraBola(b.Id) == true;
            var mult = b.CatchRate is { } cr and > 0 ? $"Taxa ×{cr.ToString("0.##", Pt)} · " : "";
            var preco = !b.Compravel ? "Só na loja de diamantes" : b.Preco is { } p and > 0 ? $"🪙 {p.ToString("N0", Pt)} ouro" : "Loja NPC";
            var infoAuto = auto && acc is not null ? $" · 🔄 repõe < {acc.AutoBuyBallsMin:N0} (+{acc.AutoBuyBallsQty:N0})" : "";
            return new MarketLinha(b.Id, b.Nome, $"{mult}{preco} · na bolsa: {g.Balls(b.Id).ToString("N0", Pt)}{infoAuto}", pode, pode ? 1 : 0.5,
                auto ? "✓ Auto" : "🔄 Auto", auto ? "#3FC46E" : "#2C2C35");
        }).ToList();

        ListaMarketPocoes.ItemsSource = catalogo.Where(m => m.Categoria == "potion")
            .Select(m =>
            {
                var auto = eco?.EstaEmAutoCompraItem(m.Id) == true;
                var infoAuto = auto && acc is not null ? $" · 🔄 repõe < {acc.AutoBuyItemsMin:N0} (+{acc.AutoBuyItemsQty:N0})" : "";
                return new MarketLinha(m.Id, m.Nome, $"{m.Descricao} · 🪙 {m.Preco.ToString("N0", Pt)} ouro · na bolsa: {g.Items(m.Id).ToString("N0", Pt)}{infoAuto}",
                    true, 1, auto ? "✓ Auto" : "🔄 Auto", auto ? "#3FC46E" : "#2C2C35");
            })
            .ToList();

        ListaMarketRevives.ItemsSource = catalogo.Where(m => m.Categoria == "revive")
            .Select(m =>
            {
                var auto = eco?.EstaEmAutoCompraItem(m.Id) == true;
                var infoAuto = auto && acc is not null ? $" · 🔄 repõe < {acc.AutoBuyItemsMin:N0} (+{acc.AutoBuyItemsQty:N0})" : "";
                return new MarketLinha(m.Id, m.Nome, $"{m.Descricao} · 🪙 {m.Preco.ToString("N0", Pt)} ouro · na bolsa: {g.Items(m.Id).ToString("N0", Pt)}{infoAuto}",
                    true, 1, auto ? "✓ Auto" : "🔄 Auto", auto ? "#3FC46E" : "#2C2C35");
            })
            .ToList();

        ListaMarketOvos.ItemsSource = catalogo.Where(m => m.Categoria == "egg")
            .Select(m => new MarketLinha(m.Id, m.Nome, $"{m.Descricao} · 🪙 {m.Preco.ToString("N0", Pt)} ouro · na bolsa: {g.Items(m.Id).ToString("N0", Pt)}"))
            .ToList();
    }

    // ------------------------------------------------------------------ abas e filtros

    private void OnAba(object sender, RoutedEventArgs e)
    {
        if (PainelCacar is null) return; // ainda montando
        MostrarPaineis();
        _sigHunts = _sigTime = _sigDepot = _sigBolas = _sigMarket = _sigMelhores = "";
        Atualizar();
    }

    private void MostrarPaineis()
    {
        PainelCacar.Visibility = AbaCacar.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelTime.Visibility = AbaTime.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelDepot.Visibility = AbaDepot.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelMarket.Visibility = AbaMarket.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelBolas.Visibility = AbaBolas.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PainelRegistro.Visibility = AbaRegistro.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFiltro(object sender, TextChangedEventArgs e) { _sigHunts = ""; if (IsLoaded) Atualizar(); }
    private void OnFiltroDepot(object sender, TextChangedEventArgs e) { _paginaDepot = 0; _sigDepot = ""; if (IsLoaded) Atualizar(); }
    private void OnDepotProxima(object sender, RoutedEventArgs e) { _paginaDepot++; _sigDepot = ""; Atualizar(); }
    private void OnDepotAnterior(object sender, RoutedEventArgs e) { _paginaDepot = Math.Max(0, _paginaDepot - 1); _sigDepot = ""; Atualizar(); }

    // ------------------------------------------------------------------ ações

    private async Task<bool> Enviar(object payload, string? nota = null)
    {
        if (Eco is not { } eco) return false;
        var ok = await eco.EnviarAsync(payload);
        if (!ok) Mensagem("A conta está desconectada agora; tente de novo em instantes.");
        else if (nota is not null) eco.Stats.Anotar(nota);
        return ok;
    }

    /// <summary>Só para o autoteste: responde às perguntas (vender, desistir, comprar) sem abrir a janela.</summary>
    internal static Func<string, bool>? ConfirmarParaTeste { get; set; }

    private bool Confirma(string texto, string titulo, MessageBoxImage icone) =>
        ConfirmarParaTeste is { } f ? f(texto)
        : MessageBox.Show(Window.GetWindow(this), texto, titulo, MessageBoxButton.YesNo, icone, MessageBoxResult.No) == MessageBoxResult.Yes;

    private static string? Slug(object sender) => (sender as FrameworkElement)?.Tag as string;
    private static long? Id(object sender) => (sender as FrameworkElement)?.Tag is long l ? l : null;

    private async void OnCacar(object sender, RoutedEventArgs e)
    {
        if (Slug(sender) is not { } slug || Eco is not { } eco) return;
        var (_, msg) = await eco.IrCacarAsync(slug, Mensagem); // espera as travas do jogo (3 s sem dano, porta de 10 s, guarda de 4 s)
        Mensagem(msg);
        _sigHunts = "";
    }

    private async void OnAtivo(object sender, RoutedEventArgs e)
    {
        if (Id(sender) is not { } id || Eco is not { } eco) return;
        var (ok, msg) = await eco.TrocarAtivoAsync(id, Mensagem); // o jogo pede 3 s entre duas trocas
        if (ok) eco.Stats.Anotar("trocou o Pokémon em campo"); else Mensagem(msg);
    }

    private async void OnTirarDoTime(object sender, RoutedEventArgs e)
    {
        if (Id(sender) is not { } id || Eco is not { } eco) return;
        // Quem está na Coleção volta ao Depot antes de sair (a mesma ordem do jogo).
        if (eco.Game.Travado(id))
        {
            await Enviar(new { t = "colecao.mover", pokemonId = id, para = "depot" });
            await Task.Delay(700);
        }
        await Enviar(new { t = "team.move", pokemonId = id, slot = (int?)null }, "mandou um Pokémon do time para o Depot");
        _sigTime = _sigDepot = "";
    }

    private async void OnParaTime(object sender, RoutedEventArgs e)
    {
        if (Id(sender) is not { } id || Eco is not { } eco) return;
        if (eco.Game.VagaLivre is not { } vaga) { Mensagem("O time está cheio (5). Tire alguém primeiro, na aba Time."); return; }
        await Enviar(new { t = "team.move", pokemonId = id, slot = vaga }, "colocou um Pokémon do Depot no time");
        _sigTime = _sigDepot = "";
    }

    private async void OnColecao(object sender, RoutedEventArgs e)
    {
        if (Id(sender) is not { } id || Eco is not { } eco) return;
        await Enviar(new { t = "colecao.mover", pokemonId = id, para = eco.Game.Travado(id) ? "depot" : "colecao" });
        _sigDepot = "";
    }

    private async void OnVender(object sender, RoutedEventArgs e)
    {
        if (Id(sender) is not { } id || Eco is not { } eco) return;
        var p = eco.Game.Pokemons.FirstOrDefault(x => x.Id == id);
        if (p is null || p.Starter || eco.Game.Travado(id) || p.Slot is not null) return;
        var aviso = p.Shiny || p.Potencia >= 4
            ? $"\n\n⚠ ATENÇÃO: {(p.Shiny ? "é um SHINY" : "tem Potência alta")}. Pelo mercado ele costuma valer muito mais que o NPC paga."
            : "";
        if (!Confirma($"Vender {p.Nome}{(p.Shiny ? " ✨" : "")} (Nv {p.Level:N0}, P{p.Potencia}) ao NPC?\n\nIsso não tem volta.{aviso}",
                "Vender Pokémon", p.Shiny || p.Potencia >= 4 ? MessageBoxImage.Warning : MessageBoxImage.Question)) return;
        await Enviar(new { t = "shop.sellPokemon", pokemonId = id }, $"vendeu {p.Nome} Nv {p.Level:N0} ao NPC");
        _sigDepot = "";
    }

    private async void OnCentro(object sender, RoutedEventArgs e)
    {
        if (Eco is not { } eco) return;
        var (ok, msg) = await eco.IrAoCentroAsync(Mensagem); // espera os 3 s sem dano e a guarda de 4 s
        Mensagem(msg);
        if (ok) eco.Stats.Anotar("voltou ao Centro Pokémon");
    }

    private async void OnCurar(object sender, RoutedEventArgs e) => await Enviar(new { t = "centro.curar" }, "pediu para curar o time no Centro");

    private async void OnDesistir(object sender, RoutedEventArgs e)
    {
        if (Confirma("Desistir do combate atual?", "Desistir", MessageBoxImage.Question)) await Enviar(new { t = "centro.desistir" }, "desistiu do combate");
    }

    private async Task MandarAutomacao(Dictionary<string, object?> mudanca)
    {
        if (Eco is not { } eco) return;
        if (eco.Game.PedidoAutomacao(mudanca) is not { } pedido) { Mensagem("Ainda não recebi as automações do jogo; espere alguns segundos."); return; }
        await Enviar(pedido);
        _sigBolas = "";
    }

    private async void OnAutoBola(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || Eco is not { } eco) return;
        var ligado = cb.IsChecked == true;
        if (ligado && !eco.Game.Vip)
        {
            cb.IsChecked = false;
            Mensagem("As bolas automáticas são do VIP desta conta.");
            return;
        }
        // Os dois modos são excludentes (a mesma regra do jogo).
        var captura = ReferenceEquals(cb, AutoAteCapturar);
        await MandarAutomacao(captura
            ? new() { ["autoBallAteCapturar"] = ligado, ["autoBallSemParar"] = false }
            : new() { ["autoBallSemParar"] = ligado, ["autoBallAteCapturar"] = false });
    }

    private async void OnAutoSimples(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb) return;
        var chave = ReferenceEquals(cb, AutoRevive) ? "autoRevive" : ReferenceEquals(cb, AutoPotion) ? "autoPotion" : "autoVoltarHunt";
        await MandarAutomacao(new() { [chave] = cb.IsChecked == true });
    }

    private async void OnBola(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int id || Eco is not { } eco) return;
        var ids = eco.Game.BallIds.ToList();
        if (!ids.Remove(id)) { ids.Add(id); Sessao?.Account.AutoBuyDisabledIds.Remove(id); }
        await MandarAutomacao(new() { ["ballIds"] = ids });
        _sigMarket = "";
    }

    private async void OnPocaoAuto(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int id || Eco is not { } eco) return;
        var ids = eco.Game.PotionIds.ToList();
        if (!ids.Remove(id)) { ids.Add(id); Sessao?.Account.AutoBuyDisabledIds.Remove(id); }
        await MandarAutomacao(new() { ["potionIds"] = ids });
        _sigMarket = "";
    }

    private async void OnReviveAuto(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not int id || Eco is not { } eco) return;
        var ids = eco.Game.ReviveIds.ToList();
        if (!ids.Remove(id)) { ids.Add(id); Sessao?.Account.AutoBuyDisabledIds.Remove(id); }
        await MandarAutomacao(new() { ["reviveIds"] = ids });
        _sigMarket = "";
    }

    private void OnAutoCompraCategoria(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox cb || Sessao is not { } s) return;
        var ligado = cb.IsChecked == true;
        if (ReferenceEquals(cb, AutoCompraBolas) || ReferenceEquals(cb, AutoCompraBolasMarket)) s.Account.AutoBuyBalls = ligado;
        else if (ReferenceEquals(cb, AutoCompraPocoes) || ReferenceEquals(cb, AutoCompraPocoesMarket)) s.Account.AutoBuyPotions = ligado;
        else if (ReferenceEquals(cb, AutoCompraRevives) || ReferenceEquals(cb, AutoCompraRevivesMarket)) s.Account.AutoBuyRevives = ligado;
        s.NotifySettingsChanged();
        _sigMarket = _sigBolas = "";
        Atualizar();
        if (Eco is { } eco) _ = eco.VerificarAutoCompraAsync();
    }

    private void OnCiclarRegraBolas(object sender, RoutedEventArgs e)
    {
        if (Sessao is not { } s) return;
        (int Min, int Qty)[] faixas = { (50, 200), (100, 500), (250, 1000), (500, 2000) };
        var idx = Array.FindIndex(faixas, f => f.Min == s.Account.AutoBuyBallsMin && f.Qty == s.Account.AutoBuyBallsQty);
        var prox = faixas[(idx + 1) % faixas.Length];
        s.Account.AutoBuyBallsMin = prox.Min;
        s.Account.AutoBuyBallsQty = prox.Qty;
        s.NotifySettingsChanged();
        _sigMarket = _sigBolas = "";
        Atualizar();
        if (Eco is { } eco) _ = eco.VerificarAutoCompraAsync();
    }

    private void OnCiclarRegraItens(object sender, RoutedEventArgs e)
    {
        if (Sessao is not { } s) return;
        (int Min, int Qty)[] faixas = { (15, 50), (30, 100), (50, 250), (100, 500) };
        var idx = Array.FindIndex(faixas, f => f.Min == s.Account.AutoBuyItemsMin && f.Qty == s.Account.AutoBuyItemsQty);
        var prox = faixas[(idx + 1) % faixas.Length];
        s.Account.AutoBuyItemsMin = prox.Min;
        s.Account.AutoBuyItemsQty = prox.Qty;
        s.NotifySettingsChanged();
        _sigMarket = _sigBolas = "";
        Atualizar();
        if (Eco is { } eco) _ = eco.VerificarAutoCompraAsync();
    }

    private void OnAlternarAutoBola(object sender, RoutedEventArgs e) => AlternarAutoItemOuBola(sender, ehBola: true);
    private void OnAlternarAutoItem(object sender, RoutedEventArgs e) => AlternarAutoItemOuBola(sender, ehBola: false);

    private void AlternarAutoItemOuBola(object sender, bool ehBola)
    {
        if ((sender as FrameworkElement)?.Tag is not int id || Eco is not { } eco || Sessao is not { } s) return;
        eco.AlternarAutoCompra(id, ehBola);
        s.NotifySettingsChanged();
        _sigMarket = _sigBolas = "";
        Atualizar();
        _ = eco.VerificarAutoCompraAsync();
    }

    private void OnComprar10(object sender, RoutedEventArgs e) => Comprar(sender, 10);
    private void OnComprar100(object sender, RoutedEventArgs e) => Comprar(sender, 100);
    private void OnComprar1000(object sender, RoutedEventArgs e) => Comprar(sender, 1000);

    private void OnComprarItem1(object sender, RoutedEventArgs e) => ComprarItem(sender, 1);
    private void OnComprarItem10(object sender, RoutedEventArgs e) => ComprarItem(sender, 10);
    private void OnComprarItem100(object sender, RoutedEventArgs e) => ComprarItem(sender, 100);
    private void OnComprarItem1000(object sender, RoutedEventArgs e) => ComprarItem(sender, 1000);

    private async void Comprar(object sender, int qtd)
    {
        if ((sender as FrameworkElement)?.Tag is not int id || Eco is not { } eco) return;
        var bola = eco.Game.CatalogoBolas.FirstOrDefault(b => b.Id == id);
        if (bola is { Compravel: false }) { Mensagem("Esta bola não é vendida por ouro no NPC."); return; }
        var preco = bola?.Preco is { } p ? $"\nTotal: {(p * qtd).ToString("N0", Pt)} de ouro (se o preço for o do catálogo)." : "";
        if (!Confirma($"Comprar {qtd:N0} × {bola?.Nome ?? $"bola #{id}"} na loja?{preco}\n\nO servidor confere o seu saldo.", "Comprar bolas", MessageBoxImage.Question)) return;
        await Enviar(new { t = "shop.buy", kind = "ball", id, qty = qtd }, $"comprou {qtd:N0} × {bola?.Nome ?? $"bola #{id}"}");
        _sigBolas = _sigMarket = "";
    }

    private async void ComprarItem(object sender, int qtd)
    {
        if ((sender as FrameworkElement)?.Tag is not int id || Eco is not { } eco) return;
        var item = eco.Game.CatalogoMarket.FirstOrDefault(m => m.Id == id);
        var nome = item?.Nome ?? $"item #{id}";
        var preco = item is { Preco: > 0 } m ? $"\nTotal: {(m.Preco * qtd).ToString("N0", Pt)} de ouro." : "";
        if (!Confirma($"Comprar {qtd:N0} × {nome} na loja NPC?{preco}\n\nO servidor confere o seu saldo.", "Comprar no Market", MessageBoxImage.Question)) return;
        await Enviar(new { t = "shop.buy", kind = "item", id, qty = qtd }, $"comprou {qtd:N0} × {nome}");
        _sigBolas = _sigMarket = "";
    }
}
