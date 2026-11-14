using System.Globalization;

namespace ThoseIdles.Services;

/// <summary>Uma linha da tabela "por conta" do resumo (texto pronto + a chave de ordenação).</summary>
public sealed record SummaryRow(
    int Slot, string Conta, string Cor, string Hunt, string Tempo,
    string OuroH, string XpH, string AbatesH, string Capturas, string Shiny, string Melhor, string Ultima,
    string Status, string StatusCor, bool Ativa,
    double OuroHNum, double XpHNum, double AbatesHNum, int CapturasNum, double TempoNum)
{
    public IReadOnlyDictionary<string, IComparable> Chaves => new Dictionary<string, IComparable>
    {
        ["conta"] = Slot, ["hunt"] = Hunt, ["tempo"] = TempoNum, ["ouro"] = OuroHNum, ["xp"] = XpHNum,
        ["abates"] = AbatesHNum, ["capturas"] = CapturasNum, ["shiny"] = Shiny, ["melhor"] = Melhor, ["ultima"] = Ultima, ["status"] = Status,
    };
}

/// <summary>Os cartões do topo (somas das contas ativas).</summary>
public sealed record SummaryTotals(string OuroH, string XpH, string AbatesH, string Capturas, string Shiny, string Ativas);

public sealed record CapturaLinha(string Conta, string Cor, string Nome, string Detalhe, string Hora, DateTime Em);
public sealed record ShinyLinha(string Conta, string Cor, string Nome, bool Capturado, string Hora, DateTime Em)
{
    public string Resultado => Capturado ? "✓ capturado" : "✗ não capturado";
    public string ResultadoCor => Capturado ? "#3FC46E" : "#E5484D";
}
public sealed record HojeResumo(string Data, string Ouro, string Xp, string Abates, string Capturas, string Shiny, string SeteDias);

public sealed record SummarySnapshot(
    SummaryTotals Totais, IReadOnlyList<SummaryRow> Linhas,
    IReadOnlyList<CapturaLinha> Capturas, IReadOnlyList<ShinyLinha> Shinies, HojeResumo Hoje);

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

    public static SummarySnapshot Build(IEnumerable<BrowserSession> sessoes, DailyTotals dia, int visiveis, DateTime? agora = null)
    {
        var now = agora ?? DateTime.UtcNow;
        var linhas = new List<SummaryRow>();
        var capturas = new List<CapturaLinha>();
        var shinies = new List<ShinyLinha>();
        double ouroH = 0, xpH = 0, abatesH = 0;
        int caps = 0, sv = 0, sc = 0, ativas = 0, total = 0;

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
            var hunt = game.HuntAtual?.Nome ?? game.HuntSlug ?? (game.NoSafari ? "Safari" : "—");
            var tempo = ativa && st.HuntSlug is not null ? now - st.HuntDesde : TimeSpan.Zero;
            var recentes = st.Recentes;
            var ultima = recentes.Count > 0 ? $"{recentes[0].Nome}{(recentes[0].Shiny ? " ✨" : "")} · {recentes[0].Em:HH:mm}" : "—";

            ouroH += o; xpH += x; abatesH += a;
            caps += st.Capturas; sv += st.ShiniesVistos; sc += st.ShiniesCapturados;

            linhas.Add(new SummaryRow(s.Slot, nome, cor, hunt, tempo > TimeSpan.Zero ? Duracao(tempo) : "—",
                ativa ? Curto(o, true) : "—", ativa ? Curto(x) : "—", ativa ? Curto(a) : "—",
                st.Capturas.ToString(Pt), $"{st.ShiniesVistos} / {st.ShiniesCapturados}", st.MelhorCapturaCurta() ?? "—", ultima,
                status, statusCor, ativa, o, x, a, st.Capturas, tempo.TotalSeconds));

            foreach (var r in recentes.Take(12))
                capturas.Add(new CapturaLinha(nome, cor, $"{r.Nome}{(r.Shiny ? " ✨" : "")}",
                    $"Nv {r.Nivel:N0} · P{r.Potencia} · ×{r.Qualidade.ToString("0.00", Pt)}{(r.Nota is { } n ? $" · nota {n.ToString("0.000", Pt)}" : "")}", r.Em.ToString("HH:mm"), r.Em));
            foreach (var ap in st.Aparicoes.Take(12))
                shinies.Add(new ShinyLinha(nome, cor, ap.Nome, ap.Capturado, ap.Em.ToString("HH:mm"), ap.Em));
        }

        var totais = new SummaryTotals(Curto(ouroH, true), Curto(xpH), Curto(abatesH), caps.ToString("N0", Pt), $"{sv} / {sc}", $"{ativas} / {total}");

        long hOuro = 0, hXp = 0; int hAb = 0, hCap = 0, hSv = 0, hSc = 0;
        foreach (var slot in Enumerable.Range(1, Math.Max(visiveis, 1)))
        {
            var d = dia.Da(slot);
            hOuro += d.Ouro; hXp += d.Xp; hAb += d.Abates; hCap += d.Capturas; hSv += d.ShiniesVistos; hSc += d.ShiniesCapturados;
        }
        var hoje = new HojeResumo(dia.Data, Curto(hOuro, true), Curto(hXp), hAb.ToString("N0", Pt), hCap.ToString("N0", Pt), $"{hSv} / {hSc}", "");

        return new SummarySnapshot(totais, linhas,
            capturas.OrderByDescending(c => c.Em).Take(14).ToList(),
            shinies.OrderByDescending(c => c.Em).Take(14).ToList(), hoje);
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
