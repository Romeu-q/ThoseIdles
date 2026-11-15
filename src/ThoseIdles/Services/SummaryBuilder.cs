using System.Globalization;

namespace ThoseIdles.Services;

/// <summary>Uma linha da tabela "por conta" do resumo (texto pronto + a chave de ordenação).</summary>
public sealed record SummaryRow(
    int Slot, string Conta, string Cor, string Hunt, string Area, string AreaCor, bool Outland, string Tempo,
    string OuroH, string XpH, string AbatesH, string Capturas, string Shiny, string Melhor, string Ultima,
    string Status, string StatusCor, bool Ativa,
    double OuroHNum, double XpHNum, double AbatesHNum, int CapturasNum, double TempoNum)
{
    public IReadOnlyDictionary<string, IComparable> Chaves => new Dictionary<string, IComparable>
    {
        ["conta"] = Slot, ["hunt"] = Hunt, ["area"] = Area, ["tempo"] = TempoNum, ["ouro"] = OuroHNum, ["xp"] = XpHNum,
        ["abates"] = AbatesHNum, ["capturas"] = CapturasNum, ["shiny"] = Shiny, ["melhor"] = Melhor, ["ultima"] = Ultima, ["status"] = Status,
    };
}

/// <summary>Os cartões do topo (somas das contas ativas).</summary>
public sealed record SummaryTotals(string OuroH, string XpH, string AbatesH, string Capturas, string Shiny, string Ativas, string Outland);

public sealed record CapturaLinha(string Conta, string Cor, string Nome, string Detalhe, string Hora, DateTime Em);
public sealed record ShinyLinha(string Conta, string Cor, string Nome, bool Capturado, string Hora, DateTime Em)
{
    public string Resultado => Capturado ? "✓ capturado" : "✗ não capturado";
    public string ResultadoCor => Capturado ? "#3FC46E" : "#E5484D";
}
/// <summary>Os drops de uma conta que está (ou esteve, nesta sessão) na Outland.</summary>
public sealed record OutlandLinha(string Conta, string Cor, string Hunt, bool Agora, string Itens, long Total)
{
    public string Local => Agora ? $"farmando agora em {Hunt}" : "já farmou na Outland nesta sessão";
}
public sealed record HojeResumo(string Data, string Ouro, string Xp, string Abates, string Capturas, string Shiny, string SeteDias);

/// <summary>Uma linha do cartão de uma conta (“Pokébolas” → “Ultra Ball 1.240 · ~6h20”).</summary>
public sealed record CartaoLinha(string Rotulo, string Valor, string Cor = "#E6E6EA");

/// <summary>O cartão de UMA conta: nível do treinador (com a barra até o próximo) e o que importa para ela seguir farmando.</summary>
public sealed record ContaCartao(int Slot, string Conta, string Cor, string Status, string StatusCor,
    string Nivel, double NivelPct, string NivelDetalhe, IReadOnlyList<CartaoLinha> Linhas)
{
    public bool TemNivel => Nivel.Length > 0;
}

/// <summary>Um aviso do topo do Resumo: o que precisa de atenção agora (conta parada, sem bolas, bônus acabando…).</summary>
public sealed record Alerta(int Slot, string Conta, string Cor, string Texto, string TextoCor);

public sealed record SummarySnapshot(
    SummaryTotals Totais, IReadOnlyList<SummaryRow> Linhas,
    IReadOnlyList<CapturaLinha> Capturas, IReadOnlyList<ShinyLinha> Shinies, HojeResumo Hoje, IReadOnlyList<OutlandLinha> Outland,
    IReadOnlyList<ContaCartao> Cartoes, IReadOnlyList<Alerta> Alertas, string TudoCerto);

/// <summary>
/// Monta o RESUMO de todas as contas (o que o "Modo Simples" do PokeGrid mostra): por conta, a hunt,
/// o tempo nela, ouro/h, XP/h, abates/h, capturas, shinies, melhor e última captura e o status; e as
/// somas, as últimas capturas, os shinies e o total de hoje. Funciona igual com a página do jogo
/// aberta ou no modo econômico (os números vêm de <see cref="BrowserSession.Stats"/>).
/// </summary>
public static class SummaryBuilder
{
    public static readonly string[] Cores = { "#3FC46E", "#4C8DFF", "#F0B429", "#B58CFF" };
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string Curto(double v, bool sinal = false)
    {
        var a = Math.Abs(v);
        var s = a >= 1_000_000 ? (a / 1_000_000).ToString(a >= 10_000_000 ? "0" : "0.#", Pt) + "M"
              : a >= 1_000 ? (a / 1_000).ToString(a >= 10_000 ? "0" : "0.#", Pt) + "K"
              : a.ToString("0", Pt);
        return (v < 0 ? "−" : sinal && v > 0 ? "+" : "") + s;
    }

    public static string Duracao(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h{t.Minutes:00}" : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes}m {t.Seconds:00}s" : $"{(int)t.TotalSeconds}s";

    /// <summary>Um prazo curto: “3d 4h”, “2h10”, “7 min”.</summary>
    public static string Prazo(TimeSpan t)
    {
        if (t <= TimeSpan.Zero) return "0 min";
        if (t.TotalDays >= 1) return t.Hours > 0 && t.TotalDays < 10 ? $"{(int)t.TotalDays}d {t.Hours}h" : $"{(int)t.TotalDays}d";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h{t.Minutes:00}";
        return $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))} min";
    }

    private const string Verde = "#3FC46E", Ambar = "#F0B429", Vermelho = "#E5484D", Cinza = "#9A9AA6", Texto = "#E6E6EA";

    /// <summary>Um bônus de XP com menos que isto para acabar entra nos avisos.</summary>
    private static readonly TimeSpan BonusAcabando = TimeSpan.FromMinutes(15);
    /// <summary>Pokébolas que acabam antes disto (no ritmo da sessão) entram nos avisos.</summary>
    private static readonly TimeSpan BolasAcabando = TimeSpan.FromHours(1);

    /// <summary>
    /// O cartão de uma conta e os avisos dela: nível do treinador e quanto falta para o próximo (no ritmo da hunt de agora),
    /// o Pokémon em campo, o saldo, o que a sessão rendeu, as pokébolas (e quando acabam), os bônus de XP (e quando acabam),
    /// a melhor hunt de XP (se o /hunt já calculou) e os drops raros.
    /// </summary>
    private static ContaCartao Cartao(BrowserSession s, string nome, string cor, string status, string statusCor, bool ativa,
        double xpH, DateTime now, List<Alerta> alertas)
    {
        var st = s.Stats;
        var game = s.Game;
        var linhas = new List<CartaoLinha>();
        void Avisar(string texto, string corTexto) => alertas.Add(new Alerta(s.Slot, nome, cor, texto, corTexto));

        // Status que pede atenção (desconectada, parada, na cidade…) vira aviso.
        if (s.IsRunning && statusCor is Vermelho or Ambar) Avisar(status, statusCor);

        // ---- nível do treinador ----
        string nivel = "", nivelDet = "";
        double pct = 0;
        var lv = game.Level > 0 ? game.Level : st.NivelTreinador ?? 0;
        if (lv > 0)
        {
            nivel = $"Treinador Nv {lv.ToString("N0", Pt)}";
            if (game.XpProximo > game.XpNivel && game.XpProximo > 0)
            {
                var faixa = game.XpProximo - game.XpNivel;
                var dentro = Math.Clamp(game.Xp - game.XpNivel, 0, faixa);
                pct = (double)dentro / faixa;
                var falta = faixa - dentro;
                nivel += $" · {(pct * 100).ToString("0", Pt)}%";
                nivelDet = $"faltam {Curto(falta)} XP";
                if (ativa && xpH > 0) nivelDet += $" · próximo nível em ~{Prazo(TimeSpan.FromHours(falta / xpH))}";
            }
        }

        // ---- Pokémon em campo ----
        if (game.Ativo is { } p)
        {
            var hpTxt = "";
            var corPk = Texto;
            if (p.MaxHp is > 0 && p.Hp is { } hp)
            {
                var hpPct = Math.Clamp((double)hp / p.MaxHp.Value, 0, 1);
                hpTxt = $" · HP {(hpPct * 100).ToString("0", Pt)}%";
                if (hpPct < 0.3) corPk = Ambar;
            }
            linhas.Add(new CartaoLinha("Pokémon", $"{p.Nome}{(p.Shiny ? " ✨" : "")} Nv {p.Level.ToString("N0", Pt)} · P{p.Potencia}{hpTxt}", corPk));
        }
        else if (st.AtivoNome is { } an)
            linhas.Add(new CartaoLinha("Pokémon", $"{an}{(st.AtivoNivel is { } al ? $" Nv {al.ToString("N0", Pt)}" : "")}"));

        // ---- saldo ----
        if (game.Recebeu)
        {
            var saldo = $"{Curto(game.Gold)} ouro";
            if (game.Diamonds > 0) saldo += $" · 💎 {game.Diamonds.ToString("N0", Pt)}";
            if (game.Orbs > 0) saldo += $" · ◆ {game.Orbs.ToString("N0", Pt)}";
            linhas.Add(new CartaoLinha("Saldo", saldo));
        }

        // ---- o que a sessão rendeu (desde que o app abriu) ----
        if (st.Abates > 0 || st.Ouro > 0 || st.XpTreinador > 0)
            linhas.Add(new CartaoLinha("Sessão",
                $"{Duracao(now - st.Inicio)} · {Curto(st.Ouro, true)} ouro · +{Curto(st.XpTreinador)} XP · {st.Abates.ToString("N0", Pt)} abates"));

        // ---- pokébolas: as que a automação usa (ou todas, se nenhuma marcada) e quando acabam no ritmo da sessão ----
        var catalogo = game.CatalogoBolas;
        if (catalogo.Count > 0 || game.BallIds.Count > 0)
        {
            var marcadas = game.BallIds;
            var ids = marcadas.Count > 0 ? marcadas.ToList() : catalogo.Select(b => b.Id).Where(id => game.Balls(id) > 0).ToList();
            var restantes = ids.Sum(id => game.Balls(id));
            string NomeBola(int id) => catalogo.FirstOrDefault(b => b.Id == id)?.Nome ?? $"Bola #{id}";
            var partes = ids.Select(id => $"{NomeBola(id)} {game.Balls(id).ToString("N0", Pt)}").ToList();
            var (gastas, porHora) = st.RitmoDeBolas();
            var texto = partes.Count == 0 ? "nenhuma" : string.Join(" · ", partes);
            var corBola = Texto;
            if (marcadas.Count > 0 && restantes <= 0)
            {
                corBola = Vermelho;
                texto += " — acabaram";
                if (ativa) Avisar("Sem pokébolas: não vai capturar nada", Vermelho);
            }
            else if (gastas >= 3 && porHora > 0 && restantes > 0)
            {
                var dura = TimeSpan.FromHours(restantes / porHora);
                texto += $" · ~{Prazo(dura)} no ritmo atual";
                if (dura < BolasAcabando) { corBola = Ambar; if (ativa) Avisar($"Pokébolas acabam em ~{Prazo(dura)}", Ambar); }
            }
            if (s.Economy is { } ecoB && ids.Any(ecoB.EstaEmAutoCompraBola)) texto += " · 🔄 auto-compra";
            linhas.Add(new CartaoLinha("Pokébolas", texto, corBola));
        }

        // ---- poções e revives na bolsa ----
        var itensCura = game.CatalogoMarket
            .Where(m => m.Categoria is "potion" or "revive" && (game.Items(m.Id) > 0 || game.PotionIds.Contains(m.Id) || game.ReviveIds.Contains(m.Id) || s.Economy?.EstaEmAutoCompraItem(m.Id) == true))
            .ToList();
        if (itensCura.Count > 0)
        {
            var txtCura = string.Join(" · ", itensCura.Select(m => $"{m.Nome} {game.Items(m.Id).ToString("N0", Pt)}"));
            if (s.Economy is { } ecoC && itensCura.Any(m => ecoC.EstaEmAutoCompraItem(m.Id))) txtCura += " · 🔄 auto-compra";
            linhas.Add(new CartaoLinha("Cura", txtCura));
        }

        // ---- bônus de XP ligados e quanto falta ----
        if (game.Recebeu)
        {
            var bonus = game.BonusAtivos();
            if (bonus.Count == 0) linhas.Add(new CartaoLinha("Bônus XP", "nenhum ligado", Cinza));
            else
            {
                var acabando = bonus.Where(b => b.Resta is { } r && r < BonusAcabando).ToList();
                linhas.Add(new CartaoLinha("Bônus XP",
                    string.Join(" · ", bonus.Select(b => b.Resta is { } r ? $"{b.Nome} {Prazo(r)}" : b.Nome)),
                    acabando.Count > 0 ? Ambar : Verde));
                foreach (var b in acabando) Avisar($"{b.Nome} acaba em {Prazo(b.Resta!.Value)}", Ambar);
            }
        }

        // ---- melhor hunt de XP (o mesmo ranking do /hunt; só aparece se já foi calculado) ----
        var ranking = s.MelhoresHunts;
        if (ranking.Count > 0 && game.HuntSlug is { } slugAtual)
        {
            var top = ranking[0];
            if (top.Slug == slugAtual)
                linhas.Add(new CartaoLinha("Melhor hunt", $"✓ já está na melhor ({top.XpH} XP/h)", Verde));
            else
            {
                var pos = ranking.ToList().FindIndex(h => h.Slug == slugAtual);
                linhas.Add(new CartaoLinha("Melhor hunt",
                    $"{top.Nome} · {top.XpH} XP/h ({(pos >= 0 ? $"a atual é a {pos + 1}ª" : "a atual está fora do top 5")})", Ambar));
            }
        }

        // ---- drops raros da sessão ----
        var raros = st.RarosDaSessao();
        if (raros.Count > 0)
            linhas.Add(new CartaoLinha("Drops raros", string.Join(" · ", raros.Select(r => $"{r.Nome} ×{r.Qtd.ToString("N0", Pt)}")), "#B58CFF"));

        return new ContaCartao(s.Slot, nome, cor, status, statusCor, nivel, pct, nivelDet, linhas);
    }

    public static SummarySnapshot Build(IEnumerable<BrowserSession> sessoes, DailyTotals dia, int visiveis, DateTime? agora = null)
    {
        var now = agora ?? DateTime.UtcNow;
        var linhas = new List<SummaryRow>();
        var capturas = new List<CapturaLinha>();
        var shinies = new List<ShinyLinha>();
        var outlandLinhas = new List<OutlandLinha>();
        var cartoes = new List<ContaCartao>();
        var alertas = new List<Alerta>();
        double ouroH = 0, xpH = 0, abatesH = 0;
        int caps = 0, sv = 0, sc = 0, ativas = 0, total = 0, outland = 0;

        foreach (var s in sessoes.Where(x => x.Slot <= visiveis).OrderBy(x => x.Slot))
        {
            total++;
            var cor = Cores[(s.Slot - 1) % Cores.Length];
            var nome = string.IsNullOrWhiteSpace(s.Account.DisplayName) ? $"Conta {s.Slot}" : s.Account.DisplayName;
            var st = s.Stats;
            var game = s.Game;

            var (status, statusCor, ativa) = Status(s, st, game, now);
            if (ativa) ativas++;

            var (o, x, a) = ativa ? st.PorHoraNaHunt() : (0, 0, 0);
            var huntInfo = game.HuntAtual;
            var hunt = huntInfo?.Nome ?? game.HuntSlug ?? (game.NoSafari ? "Safari" : "—");
            // A área: Outland (🌌, em destaque) ou a região da hunt (Kanto, Johto…). Só conta quem está de fato caçando.
            var emOutland = ativa && huntInfo is { Outland: true };
            if (emOutland) outland++;
            var area = !ativa || huntInfo is null ? "—"
                : emOutland ? "🌌 Outland"
                : huntInfo.Regiao is { Length: > 0 } reg ? char.ToUpper(reg[0], Pt) + reg[1..] : "—";
            var areaCor = emOutland ? "#B58CFF" : "#9A9AA6";
            var tempo = ativa && st.HuntSlug is not null ? now - st.HuntDesde : TimeSpan.Zero;
            var recentes = st.Recentes;
            var ultima = recentes.Count > 0 ? $"{recentes[0].Nome}{(recentes[0].Shiny ? " ✨" : "")} · {recentes[0].Em:HH:mm}" : "—";

            ouroH += o; xpH += x; abatesH += a;
            caps += st.Capturas; sv += st.ShiniesVistos; sc += st.ShiniesCapturados;

            linhas.Add(new SummaryRow(s.Slot, nome, cor, hunt, area, areaCor, emOutland, tempo > TimeSpan.Zero ? Duracao(tempo) : "—",
                ativa ? Curto(o, true) : "—", ativa ? Curto(x) : "—", ativa ? Curto(a) : "—",
                st.Capturas.ToString(Pt), $"{st.ShiniesVistos} / {st.ShiniesCapturados}", st.MelhorCapturaCurta() ?? "—", ultima,
                status, statusCor, ativa, o, x, a, st.Capturas, tempo.TotalSeconds));

            cartoes.Add(Cartao(s, nome, cor, status, statusCor, ativa, x, now, alertas));

            // Quem está na Outland (ou já dropou algo lá nesta sessão) mostra os DROPS: raros primeiro, tudo à mostra.
            var dropsOutland = st.Drops(soOutland: true);
            if (emOutland || dropsOutland.Count > 0)
                outlandLinhas.Add(new OutlandLinha(nome, cor, huntInfo?.Nome ?? hunt, emOutland,
                    dropsOutland.Count == 0 ? "nenhum drop ainda" : string.Join("  ·  ", dropsOutland.Select(d => $"{(d.Raro ? "★ " : "")}{d.Nome} ×{d.Qtd:N0}")),
                    dropsOutland.Sum(d => d.Qtd)));

            foreach (var r in recentes.Take(12))
                capturas.Add(new CapturaLinha(nome, cor, $"{r.Nome}{(r.Shiny ? " ✨" : "")}",
                    $"Nv {r.Nivel:N0} · P{r.Potencia} · ×{r.Qualidade.ToString("0.00", Pt)}{(r.Nota is { } n ? $" · nota {n.ToString("0.000", Pt)}" : "")}", r.Em.ToString("HH:mm"), r.Em));
            foreach (var ap in st.Aparicoes.Take(12))
                shinies.Add(new ShinyLinha(nome, cor, ap.Nome, ap.Capturado, ap.Em.ToString("HH:mm"), ap.Em));
        }

        var totais = new SummaryTotals(Curto(ouroH, true), Curto(xpH), Curto(abatesH), caps.ToString("N0", Pt), $"{sv} / {sc}", $"{ativas} / {total}", $"{outland} / {ativas}");

        long hOuro = 0, hXp = 0; int hAb = 0, hCap = 0, hSv = 0, hSc = 0;
        foreach (var slot in Enumerable.Range(1, Math.Max(visiveis, 1)))
        {
            var d = dia.Da(slot);
            hOuro += d.Ouro; hXp += d.Xp; hAb += d.Abates; hCap += d.Capturas; hSv += d.ShiniesVistos; hSc += d.ShiniesCapturados;
        }
        var hoje = new HojeResumo(dia.Data, Curto(hOuro, true), Curto(hXp), hAb.ToString("N0", Pt), hCap.ToString("N0", Pt), $"{hSv} / {hSc}", "");

        var tudoCerto = total == 0 ? "Nenhuma conta aberta."
            : ativas == total ? $"✓ Tudo certo — {(total == 1 ? "a conta está farmando" : $"as {total} contas estão farmando")}."
            : $"✓ Nada urgente — {ativas} de {total} contas farmando.";

        return new SummarySnapshot(totais, linhas,
            capturas.OrderByDescending(c => c.Em).Take(14).ToList(),
            shinies.OrderByDescending(c => c.Em).Take(14).ToList(), hoje, outlandLinhas,
            cartoes, alertas, tudoCerto);
    }

    /// <summary>"Caçando · Charizard Nv337", "Na cidade", "⚠ Parada há 12 min", "Desconectada"… e a cor (verde farmando, âmbar atenção, vermelho parou).</summary>
    private static (string Texto, string Cor, bool Ativa) Status(BrowserSession s, EconomySessionStats st, AccountGameState game, DateTime now)
    {
        const string verde = "#3FC46E", ambar = "#F0B429", vermelho = "#E5484D", cinza = "#9A9AA6";
        if (!s.IsRunning) return ("Desligada", cinza, false);
        switch (s.Status)
        {
            case SessionStatus.Loading: return ("Carregando…", cinza, false);
            case SessionStatus.AwaitingConnection: return ("Na tela de login", ambar, false);
            case SessionStatus.Disconnected: return ("Desconectada", vermelho, false);
            case SessionStatus.Error: return ("Erro ao carregar", vermelho, false);
        }
        if (!game.Recebeu) return ("Aguardando…", cinza, false);
        if (game.NoSafari) return ("No Safari", verde, true);
        if (game.HuntSlug is null) return (game.NoCentro ? "Na cidade" : "Sem hunt", ambar, false);

        var ativo = game.Ativo is { } p ? $"{p.Nome} Nv{p.Level:N0}" : st.AtivoNome is { } n ? $"{n}{(st.AtivoNivel is { } l ? $" Nv{l:N0}" : "")}" : "";
        var ultimo = st.UltimoAbateUtc ?? st.HuntDesde;
        var parada = now - ultimo;
        if (parada > TimeSpan.FromMinutes(10) && now - st.HuntDesde > TimeSpan.FromMinutes(10))
            return ($"⚠ Parada há {(int)parada.TotalMinutes} min", ambar, true);
        return ($"Caçando{(ativo.Length > 0 ? " · " + ativo : "")}", verde, true);
    }
}
