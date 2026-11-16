using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThoseIdles.Services;

/// <summary>
/// Observa as atualizações do PokeIdle (a cada 6 h): lê a lista de novidades que o próprio jogo publica
/// (/shared/aviso-jogo.mjs — número do aviso, versão e data) e os textos dela em português (/i18n.mjs, chaves "aviso.vNN…").
/// Quando sai uma versão nova: renova os dados do jogo que o app guarda, mostra o changelog e aponta o que pode ter
/// afetado o ThoseIdles — pelo texto do changelog (assuntos que o app usa) e por conferências diretas: as regras do jogo
/// que o app copia (valor de venda, tiers da Outland) e os campos que o servidor manda (welcome/estado).
/// Não muda código: a adaptação é feita depois, à mão.
/// </summary>
public static class GameUpdateWatcher
{
    public static string BaseUrl { get; set; } = "https://pokeidle.io/";

    /// <summary>Uma novidade do jogo (um aviso do "Novidades no PokeIdle").</summary>
    public sealed record Novidade(int Aviso, string Versao, string Data, IReadOnlyList<string> Itens);

    /// <summary>O resultado de uma checagem com versão nova.</summary>
    public sealed record Relatorio(IReadOnlyList<Novidade> Novidades, IReadOnlyList<string> PodeAfetar, IReadOnlyList<string> Conferencias, DateTime Em)
    {
        public Novidade? MaisNova => Novidades.Count > 0 ? Novidades[0] : null;
        public int Alertas => PodeAfetar.Count + Conferencias.Count;
    }

    /// <summary>As regras do jogo que o app reproduz em C#: se o arquivo mudar, a cópia do app pode ter ficado velha.</summary>
    public static readonly (string Caminho, string NoApp)[] ModulosCopiados =
    {
        ("shared/sell-value.mjs", "valor de venda das capturas (ValorDeVenda.cs — ouro da sessão)"),
        ("shared/valor-cadeia.mjs", "valor de venda pela cadeia de evolução (ValorDeVenda.cs)"),
        ("shared/outland-tiers.mjs", "tiers da Outland (AccountGameState.OutlandTiers — Melhor Outland no Resumo)"),
    };

    /// <summary>Assuntos do changelog → a parte do ThoseIdles que usa aquilo.</summary>
    private static readonly (string Padrao, string Parte)[] Assuntos =
    {
        (@"market|loja|\bnpc\b|po[cç][aã]o|po[cç][oõ]es|potion|revive", "Market e auto-compra (Ultra Ball, Max Revive, Golden Potion)"),
        (@"pok[eé]bola|\bbolas?\b|\bball", "pokébolas (automação, auto-compra, Resumo)"),
        (@"vend[ae]|vender|pre[cç]o|sell", "valor de venda das capturas (ouro da sessão)"),
        (@"outland|\btier", "Outland (Melhor Outland, tiers, drops)"),
        (@"\bhunts?\b|\bxp\b|experi[eê]ncia|ritmo|abates?", "melhor hunt de XP (motor do /hunt) e taxas por hora"),
        (@"centro pok|curar|\bcura\b", "Centro Pokémon e as travas de tempo"),
        (@"automa[cç]", "automações do modo econômico"),
        (@"depot|cole[cç][aã]o|\btime\b|equipe", "Depot e Time do modo econômico"),
        (@"\bdrops?\b|loot", "drops no Resumo"),
        (@"shiny", "shinies no Resumo"),
        (@"login|entrar|sess[aã]o|token|conex|reconex|turnstile", "login e conexão do modo econômico"),
        (@"\bkick\b|twitch|\blive", "bônus das lives (Kick/Twitch)"),
        (@"guild|boost|vip|evento", "bônus de XP no Resumo"),
    };

    private sealed class Estado
    {
        public int UltimoAviso { get; set; }
        public string? Versao { get; set; }
        public Dictionary<string, string> Modulos { get; set; } = new();
        public int DispensadoAviso { get; set; }
    }

    private static string CaminhoEstado => Path.Combine(AppPaths.Root, "jogo-novidades.json");
    private static readonly object _lock = new();
    private static readonly List<string> _protocolo = new();

    /// <summary>O último relatório (o da versão nova), para a faixa e a janela de novidades.</summary>
    public static Relatorio? Ultimo { get; private set; }

    /// <summary>A checagem achou uma versão nova (chamado na thread da checagem).</summary>
    public static event Action<Relatorio>? VersaoNova;

    /// <summary>Até quando o aviso desta versão foi dispensado.</summary>
    public static bool Dispensado(Relatorio r) => r.MaisNova is { } n && LerEstado().DispensadoAviso >= n.Aviso;

    public static void Dispensar(Relatorio r)
    {
        if (r.MaisNova is not { } n) return;
        var e = LerEstado();
        e.DispensadoAviso = n.Aviso;
        Salvar(e);
    }

    // ------------------------------------------------------------------ conferência do que o servidor manda

    private static readonly string[] CamposWelcome = { "hunts", "catalogoBolas" };
    private static readonly string[] CamposEstado = { "level", "gold", "pokemons" };

    /// <summary>
    /// Confere um "welcome" do servidor: se faltar um campo que o app lê, ele vai para a lista de conferências
    /// (aparece no próximo relatório e já fica no log).
    /// </summary>
    public static void ConferirWelcome(JsonElement root)
    {
        try
        {
            var faltam = CamposWelcome.Where(c => !root.TryGetProperty(c, out _)).ToList();
            if (root.TryGetProperty("estado", out var est) && est.ValueKind == JsonValueKind.Object)
                faltam.AddRange(CamposEstado.Where(c => !est.TryGetProperty(c, out _)).Select(c => "estado." + c));
            if (faltam.Count == 0) return;
            var msg = $"o servidor parou de mandar {string.Join(", ", faltam)} no welcome (o app lê esses campos)";
            lock (_lock)
            {
                if (_protocolo.Contains(msg)) return;
                _protocolo.Add(msg);
            }
            LoggingService.Log.Warn("Jogo: " + msg + ".");
        }
        catch { }
    }

    public static IReadOnlyList<string> ProblemasDoProtocolo { get { lock (_lock) return _protocolo.ToList(); } }

    // ------------------------------------------------------------------ a checagem

    /// <summary>Checa agora. Devolve o relatório se saiu versão nova desde a última checagem (null se não, ou na primeira vez).</summary>
    public static async Task<Relatorio?> ChecarAsync(CancellationToken ct = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("ThoseIdles");
            var lista = LerLista(await http.GetStringAsync(BaseUrl + "shared/aviso-jogo.mjs", ct));
            if (lista.Count == 0) return null;
            var estado = LerEstado();
            var hashes = new Dictionary<string, string>();
            foreach (var (caminho, _) in ModulosCopiados)
            {
                try { hashes[caminho] = Hash(await http.GetStringAsync(BaseUrl + caminho, ct)); }
                catch (Exception ex) { LoggingService.Log.Info($"Jogo: {caminho} não baixou ({ex.Message})."); }
            }

            var maisNova = lista[0];
            if (estado.UltimoAviso == 0)
            {
                // Primeira vez: só guarda onde o jogo está (não tem "novidade" para mostrar).
                estado.UltimoAviso = maisNova.Aviso; estado.Versao = maisNova.Versao; estado.Modulos = hashes;
                Salvar(estado);
                LoggingService.Log.Info($"Jogo: na versão {maisNova.Versao} (aviso {maisNova.Aviso}); próximas atualizações serão avisadas.");
                return null;
            }

            var novas = lista.Where(n => n.Aviso > estado.UltimoAviso).ToList();
            var mudados = ModulosCopiados.Where(m => hashes.TryGetValue(m.Caminho, out var h) && estado.Modulos.TryGetValue(m.Caminho, out var antes) && antes != h).ToList();
            if (novas.Count == 0 && mudados.Count == 0) return null;

            // Os textos das novidades (em português) vêm das traduções do jogo.
            if (novas.Count > 0)
            {
                string? i18n = null;
                try { i18n = await http.GetStringAsync(BaseUrl + "i18n.mjs", ct); }
                catch (Exception ex) { LoggingService.Log.Info($"Jogo: textos das novidades não baixaram ({ex.Message})."); }
                if (i18n is not null) novas = novas.Select(n => n with { Itens = TextosDoAviso(i18n, n.Aviso) }).ToList();
            }

            var texto = string.Join("\n", novas.SelectMany(n => n.Itens));
            var podeAfetar = Assuntos.Where(a => Regex.IsMatch(texto, a.Padrao, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                                     .Select(a => a.Parte).Distinct().ToList();
            var conferencias = mudados.Select(m => $"o jogo mudou {m.Caminho}: o app copia essa regra em {m.NoApp}").ToList();
            conferencias.AddRange(ProblemasDoProtocolo);

            var rel = new Relatorio(novas, podeAfetar, conferencias, DateTime.Now);
            estado.UltimoAviso = Math.Max(estado.UltimoAviso, maisNova.Aviso); estado.Versao = maisNova.Versao;
            foreach (var (k, v) in hashes) estado.Modulos[k] = v;
            Salvar(estado);
            Ultimo = rel;
            File.WriteAllText(Path.Combine(AppPaths.Root, "jogo-novidades.txt"), TextoParaClaude(rel));
            LoggingService.Log.Info($"Jogo atualizou: {string.Join(", ", novas.Select(n => $"v{n.Versao}"))}; pode afetar: {(podeAfetar.Count == 0 ? "nada do app" : string.Join("; ", podeAfetar))}; conferências: {conferencias.Count}.");
            RenovarDados();
            VersaoNova?.Invoke(rel);
            return rel;
        }
        catch (Exception ex)
        {
            LoggingService.Log.Info($"Jogo: checagem de atualização falhou ({ex.Message}).");
            return null;
        }
    }

    /// <summary>Renova os dados do jogo que o app guarda (catálogo de espécies e valores de venda).</summary>
    public static void RenovarDados()
    {
        try { SpeciesCatalog.Renovar(); }
        catch (Exception ex) { LoggingService.Log.Info($"Jogo: renovar o catálogo falhou ({ex.Message})."); }
    }

    /// <summary>O relatório em texto, para colar numa conversa com o Claude e pedir a adaptação.</summary>
    public static string TextoParaClaude(Relatorio r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"O PokeIdle atualizou ({string.Join(", ", r.Novidades.Select(n => $"v{n.Versao} de {n.Data}"))}). Adapte o ThoseIdles (v{AppUpdateService.CurrentVersion}) se precisar.");
        foreach (var n in r.Novidades)
        {
            sb.AppendLine().AppendLine($"## Novidades v{n.Versao} ({n.Data}, aviso {n.Aviso})");
            foreach (var i in n.Itens) sb.AppendLine("- " + i);
        }
        if (r.PodeAfetar.Count > 0)
        {
            sb.AppendLine().AppendLine("## Pode afetar no ThoseIdles");
            foreach (var p in r.PodeAfetar) sb.AppendLine("- " + p);
        }
        if (r.Conferencias.Count > 0)
        {
            sb.AppendLine().AppendLine("## Conferências que falharam");
            foreach (var c in r.Conferencias) sb.AppendLine("- " + c);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ leitura dos arquivos do jogo

    /// <summary>A lista de novidades (aviso-jogo.mjs), da mais nova para a mais velha.</summary>
    public static List<Novidade> LerLista(string mjs)
    {
        var lista = new List<Novidade>();
        foreach (Match m in Regex.Matches(mjs, @"aviso:\s*(\d+)\s*,\s*versao:\s*'([^']+)'\s*,\s*data:\s*'([^']+)'"))
            lista.Add(new Novidade(int.Parse(m.Groups[1].Value), m.Groups[2].Value, m.Groups[3].Value, Array.Empty<string>()));
        return lista.OrderByDescending(n => n.Aviso).ToList();
    }

    /// <summary>
    /// Os textos de um aviso, em português: as chaves "aviso.vNN…" do primeiro bloco de traduções (o pt-BR vem primeiro),
    /// sem HTML e sem o botão "ok".
    /// </summary>
    public static IReadOnlyList<string> TextosDoAviso(string i18n, int aviso)
    {
        var vistos = new HashSet<string>();
        var itens = new List<string>();
        var re = new Regex(@"'aviso\.v" + aviso + @"(?![0-9])([A-Za-z0-9_]*)'\s*:\s*'((?:[^'\\]|\\.)*)'");
        foreach (Match m in re.Matches(i18n))
        {
            var chave = m.Groups[1].Value;
            if (!vistos.Add(chave)) break; // a mesma chave de novo = começou o bloco do próximo idioma
            if (chave.Equals("Ok", StringComparison.OrdinalIgnoreCase)) continue;
            string t;
            try { t = Regex.Unescape(m.Groups[2].Value.Replace("\\'", "'")); } catch { t = m.Groups[2].Value; }
            t = Regex.Replace(t, "<br\\s*/?>", " ");
            t = Regex.Replace(t, "<[^>]+>", "");
            t = System.Net.WebUtility.HtmlDecode(t).Trim();
            if (t.Length > 0) itens.Add(t);
        }
        return itens;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n"))));

    private static Estado LerEstado()
    {
        try
        {
            if (File.Exists(CaminhoEstado)) return JsonSerializer.Deserialize<Estado>(File.ReadAllText(CaminhoEstado)) ?? new Estado();
        }
        catch { }
        return new Estado();
    }

    private static void Salvar(Estado e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            File.WriteAllText(CaminhoEstado, JsonSerializer.Serialize(e, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { LoggingService.Log.Info($"Jogo: não salvou o estado das novidades ({ex.Message})."); }
    }
}
