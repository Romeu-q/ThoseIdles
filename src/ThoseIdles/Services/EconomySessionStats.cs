using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// A "prancheta" do modo econômico: o mesmo que a prancheta de sessão do próprio jogo conta
/// (`sessao-hunt.mjs`, `contarNaSessao`), a partir dos eventos de batalha que o servidor manda:
///  · morte (selvagem) → abate, XP, ouro e drops raros;
///  · bola → bola gasta (por tipo) e shiny VISTO (a bola revela o shiny);
///  · capturado → captura e shiny capturado;
///  · bossMorto → XP, ouro e os drops do boss.
/// As pokébolas que a conta TEM vêm do estado do servidor (`balls`: id → quantidade), atualizadas
/// sempre que ele as manda; os nomes vêm de `catalogoBolas` no "welcome".
/// </summary>
public sealed class EconomySessionStats
{
    /// <summary>Os drops raros que o jogo destaca (ids do catálogo do servidor; o nome é a rede de segurança).</summary>
    private static readonly (int Id, string Nome, string Rotulo)[] Raros =
    {
        (70000, "Bronze Boss Token", "Boss Token"),
        (70011, "Key Fragment", "Frag. de Chave"),
        (70012, "Shiny Stone Fragment", "Frag. Shiny Stone"),
        (70013, "Bicycle Fragment", "Frag. de Bicicleta"),
        (70014, "Mega Stone Fragment", "Frag. Mega"),
        (70015, "Mega Shiny Stone Fragment", "Frag. Mega Shiny"),
        (59194, "TM Disk Piece", "Peça de TM"),
        (40530, "AoE TM Disk Piece", "Peça de TM AoE"),
        (0, "Mystic Ticket", "Mystic Ticket"),
        (0, "Safari Ticket", "Safari Ticket"),
        (0, "Shiny Egg Fragment", "Frag. Ovo Shiny"),
    };

    private readonly object _lock = new();
    public DateTime Inicio { get; } = DateTime.UtcNow;
    public int Abates { get; private set; }
    public int Capturas { get; private set; }
    public int ShiniesVistos { get; private set; }
    public int ShiniesCapturados { get; private set; }
    public long XpTreinador { get; private set; }
    public long Ouro { get; private set; }
    public List<string> UltimosShinies { get; } = new();

    public long? NivelTreinador { get; private set; }
    public string? AtivoNome { get; private set; }
    public long? AtivoNivel { get; private set; }
    private long? _ativoId;

    // ---- a hunt de agora (as taxas por hora são desde que a conta entrou nela) ----
    public string? HuntSlug { get; private set; }
    public DateTime HuntDesde { get; private set; } = DateTime.UtcNow;
    public int AbatesHunt { get; private set; }
    public long XpHunt { get; private set; }
    public long OuroHunt { get; private set; }
    /// <summary>Quando chegou o último abate (a conta "parada" é a que passa muito tempo sem abate).</summary>
    public DateTime? UltimoAbateUtc { get; private set; }

    /// <summary>Uma captura recente (a lista "últimas capturas" do resumo).</summary>
    public sealed record Recente(string Nome, long Nivel, bool Shiny, int Potencia, double Qualidade, double? Nota, DateTime Em);
    private readonly List<Recente> _recentes = new();
    public IReadOnlyList<Recente> Recentes { get { lock (_lock) return _recentes.ToList(); } }

    /// <summary>Um shiny que apareceu (a bola revela). Capturado = o servidor confirmou a captura.</summary>
    public sealed class Aparicao
    {
        public DateTime Em { get; init; }
        public string Nome { get; set; } = "Shiny";
        public bool Capturado { get; set; }
    }
    private readonly List<Aparicao> _aparicoes = new();
    private Aparicao? _shinyAberto; // o shiny que está na tela agora: várias bolas nele contam UMA vez
    public IReadOnlyList<Aparicao> Aparicoes { get { lock (_lock) return _aparicoes.Select(a => new Aparicao { Em = a.Em, Nome = a.Nome, Capturado = a.Capturado }).ToList(); } }

    /// <summary>O que acabou de acontecer (abates, capturas, níveis…): o "registro" da tela mínima. O mais novo primeiro.</summary>
    private readonly List<string> _registro = new();
    public IReadOnlyList<string> Registro { get { lock (_lock) return _registro.ToList(); } }
    public void Anotar(string texto)
    {
        lock (_lock)
        {
            _registro.Insert(0, $"{DateTime.Now:HH:mm:ss}  {texto}");
            if (_registro.Count > 150) _registro.RemoveRange(150, _registro.Count - 150);
        }
    }

    /// <summary>O que um lote de eventos somou (para o total de HOJE, que sobrevive a reiniciar o app).</summary>
    public readonly record struct Soma(int Abates, int Capturas, long Xp, long Ouro, int ShiniesVistos, int ShiniesCapturados);
    public event Action<Soma>? Somou;

    /// <summary>Taxas por hora desde que a conta entrou na hunt de agora.</summary>
    public (double Ouro, double Xp, double Abates) PorHoraNaHunt()
    {
        lock (_lock)
        {
            var h = Math.Max((DateTime.UtcNow - HuntDesde).TotalHours, 1 / 60d);
            return (OuroHunt / h, XpHunt / h, AbatesHunt / h);
        }
    }

    /// <summary>Uma captura guardada para achar a de maior nota (a nota precisa do catálogo de espécies).</summary>
    private sealed record Captura(int SpeciesId, string Nome, long Nivel, bool Shiny, int Potencia, double Qualidade,
        IReadOnlyDictionary<string, int>? Ivs, int? IvSoma, DateTime Em);
    private readonly List<Captura> _capturas = new();

    private readonly Dictionary<string, int> _raros = new();
    private readonly Dictionary<int, int> _bolasGastas = new();
    private readonly Dictionary<int, long> _bolasTem = new();
    private readonly Dictionary<int, string> _nomeBola = new();

    /// <summary>Lê o "welcome": nomes das bolas, quantas a conta tem e o estado (níveis).</summary>
    public void LerWelcome(JsonElement root)
    {
        lock (_lock)
        {
            if (Achar(root, "catalogoBolas", 0) is { ValueKind: JsonValueKind.Array } cat)
                foreach (var b in cat.EnumerateArray())
                    if (b.TryGetProperty("id", out var id) && id.TryGetInt32(out var i) && b.TryGetProperty("nome", out var n) && n.GetString() is { } nome)
                        _nomeBola[i] = nome;
            LerBolas(root);
        }
        LerEstado(root);
    }

    /// <summary>
    /// O estado da conta ("welcome" e as atualizações "estado", que trazem só o que mudou): nível do
    /// treinador e o Pokémon ativo (activeId + a lista de Pokémon).
    /// </summary>
    public bool LerEstado(JsonElement root)
    {
        var est = root.TryGetProperty("estado", out var e) && e.ValueKind == JsonValueKind.Object ? e : root;
        var mudou = false;
        lock (_lock)
        {
            if (Num(est, "level") is { } lv) { NivelTreinador = lv; mudou = true; }
            if (Num(est, "activeId") is { } aid) { _ativoId = aid; mudou = true; }
            if (est.TryGetProperty("huntSlug", out var hs))
            {
                var slug = hs.ValueKind == JsonValueKind.String ? hs.GetString() : null;
                if (slug != HuntSlug)
                {
                    // Hunt nova: as taxas por hora recomeçam daqui.
                    HuntSlug = slug;
                    HuntDesde = DateTime.UtcNow;
                    AbatesHunt = 0; XpHunt = 0; OuroHunt = 0;
                    mudou = true;
                }
            }
            if (est.TryGetProperty("pokemons", out var pks) && pks.ValueKind == JsonValueKind.Array && _ativoId is { } ativo)
                foreach (var p in pks.EnumerateArray())
                    if (p.ValueKind == JsonValueKind.Object && Num(p, "id") == ativo)
                    {
                        AtivoNome = Str(p, "apelido") ?? Str(p, "nome") ?? AtivoNome;
                        AtivoNivel = Num(p, "level") ?? AtivoNivel;
                        mudou = true;
                        break;
                    }
        }
        return mudou;
    }

    /// <summary>Qualquer mensagem do servidor que traga `balls` atualiza o que a conta tem.</summary>
    public bool LerBolas(JsonElement root)
    {
        if (Achar(root, "balls", 0) is not { ValueKind: JsonValueKind.Object } balls) return false;
        lock (_lock)
        {
            foreach (var p in balls.EnumerateObject())
                if (int.TryParse(p.Name, out var id) && p.Value.TryGetInt64(out var q)) _bolasTem[id] = q;
        }
        return true;
    }

    /// <summary>Soma os eventos de um "batalha". Devolve true se algo mudou.</summary>
    public bool Somar(JsonElement root)
    {
        if (!root.TryGetProperty("ev", out var ev) || ev.ValueKind != JsonValueKind.Array) return false;
        var mudou = false;
        Soma soma;
        lock (_lock)
        {
            var (abates0, caps0, xp0, ouro0, sv0, sc0) = (Abates, Capturas, XpTreinador, Ouro, ShiniesVistos, ShiniesCapturados);
            foreach (var e in ev.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                switch (Str(e, "k"))
                {
                    case "spawn":
                        _shinyAberto = null; // apareceu outro: o shiny anterior (se não foi capturado) fugiu
                        break;
                    case "fugiu":
                        _shinyAberto = null;
                        if (Str(e, "nome") is { } fn) Anotar($"{fn}{(Num(e, "level") is { } fl ? $" Nv {fl}" : "")} escapou");
                        break;
                    case "morte" when Str(e, "quem") == "selvagem":
                    {
                        _shinyAberto = null;
                        var xp = Num(e, "xpTreinador") ?? Num(e, "xp") ?? 0;
                        var ouro = Num(e, "ouro") ?? 0;
                        Abates++; AbatesHunt++;
                        XpTreinador += xp; XpHunt += xp;
                        Ouro += ouro; OuroHunt += ouro;
                        UltimoAbateUtc = DateTime.UtcNow;
                        SomarDrops(e);
                        if (Abates % 10 == 0 || xp > 0 && Abates < 10) Anotar($"abateu {Str(e, "nome") ?? "selvagem"}: +{xp:N0} XP · +{ouro:N0} ouro");
                        mudou = true;
                        break;
                    }
                    case "bossMorto":
                    {
                        var xp = Num(e, "xpTreinador") ?? Num(e, "xp") ?? 0;
                        var ouro = Num(e, "valor") ?? 0;
                        XpTreinador += xp; XpHunt += xp;
                        Ouro += ouro; OuroHunt += ouro;
                        SomarDrops(e);
                        Anotar($"boss derrotado: +{xp:N0} XP · +{ouro:N0} ouro");
                        mudou = true;
                        break;
                    }
                    case "bola":
                        if (e.TryGetProperty("shiny", out var sh) && sh.ValueKind == JsonValueKind.True && _shinyAberto is null)
                        {
                            // A bola revela o shiny: UMA aparição por shiny, por mais bolas que se joguem nele.
                            ShiniesVistos++;
                            _shinyAberto = new Aparicao { Em = DateTime.Now, Nome = Str(e, "nome") ?? "Shiny" };
                            _aparicoes.Insert(0, _shinyAberto);
                            if (_aparicoes.Count > 60) _aparicoes.RemoveAt(60);
                            Anotar($"✨ apareceu um shiny{(Str(e, "nome") is { } sn ? $" ({sn})" : "")}!");
                        }
                        else if (e.TryGetProperty("shiny", out sh) && sh.ValueKind == JsonValueKind.True && _shinyAberto is { Nome: "Shiny" } semNome && Str(e, "nome") is { } nm2)
                            semNome.Nome = nm2;
                        if (Num(e, "ballId") is { } bid and > 0)
                        {
                            var id = (int)bid;
                            _bolasGastas[id] = _bolasGastas.GetValueOrDefault(id) + 1;
                            // A bolsa anda junto até o servidor mandar o número certo de novo.
                            if (_bolasTem.TryGetValue(id, out var tem) && tem > 0) _bolasTem[id] = tem - 1;
                        }
                        mudou = true;
                        break;
                    case "levelup":
                        if (Num(e, "level") is { } nv2)
                        {
                            if (Str(e, "quem") == "treinador") NivelTreinador = nv2; else AtivoNivel = nv2;
                            Anotar(Str(e, "quem") == "treinador" ? $"treinador subiu para o Nv {nv2:N0}" : $"{AtivoNome ?? "pokémon"} subiu para o Nv {nv2:N0}");
                            mudou = true;
                        }
                        break;
                    case "evolucao":
                        Anotar($"{Str(e, "de") ?? "pokémon"} evoluiu para {Str(e, "nome") ?? "?"}");
                        break;
                    case "capturado":
                        Capturas++;
                        if (e.TryGetProperty("pokemon", out var cap) && cap.ValueKind == JsonValueKind.Object)
                            GuardarCaptura(cap);
                        if (e.TryGetProperty("pokemon", out var pk) && pk.ValueKind == JsonValueKind.Object
                            && pk.TryGetProperty("shiny", out var s) && s.ValueKind == JsonValueKind.True)
                        {
                            ShiniesCapturados++;
                            var nome = Str(pk, "nome") ?? "?";
                            var nivel = Num(pk, "level");
                            UltimosShinies.Insert(0, $"{nome}{(nivel is { } nv ? $" Nv {nv}" : "")} ({DateTime.Now:HH:mm})");
                            if (UltimosShinies.Count > 5) UltimosShinies.RemoveAt(5);
                            if (_shinyAberto is { } ab) { ab.Capturado = true; ab.Nome = nome; }
                            else _aparicoes.Insert(0, new Aparicao { Em = DateTime.Now, Nome = nome, Capturado = true });
                            Anotar($"✨ SHINY capturado: {nome}{(nivel is { } nvs ? $" Nv {nvs}" : "")}!");
                        }
                        else if (e.TryGetProperty("pokemon", out pk) && pk.ValueKind == JsonValueKind.Object)
                            Anotar($"capturou {Str(pk, "nome") ?? "?"}{(Num(pk, "level") is { } nvc ? $" Nv {nvc}" : "")}");
                        _shinyAberto = null;
                        mudou = true;
                        break;
                }
            }
            soma = new Soma(Abates - abates0, Capturas - caps0, XpTreinador - xp0, Ouro - ouro0, ShiniesVistos - sv0, ShiniesCapturados - sc0);
        }
        if (soma != default) Somou?.Invoke(soma);
        return mudou;
    }

    private void GuardarCaptura(JsonElement pk)
    {
        Dictionary<string, int>? ivs = null;
        if (pk.TryGetProperty("ivs", out var iv) && iv.ValueKind == JsonValueKind.Object)
        {
            ivs = new();
            foreach (var p in iv.EnumerateObject())
                if (p.Value.ValueKind == JsonValueKind.Number) ivs[p.Name] = (int)p.Value.GetDouble();
        }
        var soma = Num(pk, "iv") ?? Num(pk, "ivTotal");
        double q = pk.TryGetProperty("quality", out var qv) && qv.ValueKind == JsonValueKind.Number ? qv.GetDouble()
                 : pk.TryGetProperty("qualidade", out qv) && qv.ValueKind == JsonValueKind.Number ? qv.GetDouble() : 1;
        _capturas.Add(new Captura(
            (int)(Num(pk, "speciesId") ?? 0), Str(pk, "nome") ?? "?", Num(pk, "level") ?? 0,
            pk.TryGetProperty("shiny", out var sh) && sh.ValueKind == JsonValueKind.True,
            (int)(Num(pk, "potencia") ?? 1), q, ivs, soma is { } s ? (int)s : null, DateTime.Now));
        if (_capturas.Count > 2000) _capturas.RemoveAt(0);
        var c = _capturas[^1];
        double? nota = SpeciesCatalog.Pronto && c.SpeciesId > 0 && (c.Ivs is not null || c.IvSoma is not null)
            ? PokemonNota.NotaComLinhagem(c.SpeciesId, c.Ivs, c.IvSoma, c.Qualidade, c.Potencia, c.Shiny) : null;
        _recentes.Insert(0, new Recente(c.Nome, c.Nivel, c.Shiny, c.Potencia, c.Qualidade, nota, c.Em));
        if (_recentes.Count > 40) _recentes.RemoveAt(40);
    }

    /// <summary>Quantas pokébolas a conta jogou nesta sessão e o ritmo por hora (para prever quando acabam).</summary>
    public (int Gastas, double PorHora) RitmoDeBolas()
    {
        lock (_lock)
        {
            var gastas = _bolasGastas.Values.Sum();
            var h = Math.Max((DateTime.UtcNow - Inicio).TotalHours, 1 / 60d);
            return (gastas, gastas / h);
        }
    }

    /// <summary>Os drops raros da sessão (“Frag. Shiny Stone” ×2…), do mais para o menos.</summary>
    public IReadOnlyList<(string Nome, int Qtd)> RarosDaSessao()
    {
        lock (_lock) return _raros.OrderByDescending(k => k.Value).Select(k => (k.Key, k.Value)).ToList();
    }

    /// <summary>A captura de maior nota da sessão (null se ainda não há catálogo ou captura com IVs).</summary>
    public (string Texto, double Nota)? MelhorCaptura()
    {
        if (!SpeciesCatalog.Pronto) return null;
        lock (_lock)
        {
            (Captura C, double Nota)? melhor = null;
            foreach (var c in _capturas)
            {
                if (c.SpeciesId <= 0 || (c.Ivs is null && c.IvSoma is null)) continue;
                var nota = PokemonNota.NotaComLinhagem(c.SpeciesId, c.Ivs, c.IvSoma, c.Qualidade, c.Potencia, c.Shiny);
                if (melhor is null || nota > melhor.Value.Nota) melhor = (c, nota);
            }
            if (melhor is not { } m) return null;
            var pt = CultureInfo.GetCultureInfo("pt-BR");
            var c2 = m.C;
            return ($"{c2.Nome}{(c2.Shiny ? " ✨" : "")} Nv {c2.Nivel} · P{c2.Potencia} · qualidade {c2.Qualidade.ToString("0.000", pt)} · nota {m.Nota.ToString("0.000", pt)}{(m.Nota >= 3.5 ? " (pode RMT)" : "")} ({c2.Em:HH:mm})", m.Nota);
        }
    }

    /// <summary>A melhor captura em poucas palavras ("Charizard ✨ 4,283") para a tabela do resumo; null se ainda não dá.</summary>
    public string? MelhorCapturaCurta()
    {
        lock (_lock)
        {
            Recente? melhor = null;
            foreach (var r in _recentes)
                if (r.Nota is { } n && (melhor is null || n > melhor.Nota)) melhor = r;
            if (melhor is null) return null;
            return $"{melhor.Nome}{(melhor.Shiny ? " ✨" : "")} {melhor.Nota!.Value.ToString("0.000", CultureInfo.GetCultureInfo("pt-BR"))}";
        }
    }

    // ---- TODOS os drops (não só os raros): o total da sessão e o que veio enquanto a conta estava na OUTLAND ----
    private readonly Dictionary<string, (long Qtd, bool Raro)> _dropsSessao = new();
    private readonly Dictionary<string, (long Qtd, bool Raro)> _dropsOutland = new();

    /// <summary>A hunt de agora é da Outland? (quem cria as estatísticas liga isto ao estado da conta.)</summary>
    public Func<bool> EmOutland { get; set; } = () => false;

    /// <summary>Os drops da sessão (ou só os da Outland): os raros primeiro, depois do mais para o menos.</summary>
    public IReadOnlyList<(string Nome, long Qtd, bool Raro)> Drops(bool soOutland)
    {
        lock (_lock)
            return (soOutland ? _dropsOutland : _dropsSessao).Select(kv => (kv.Key, kv.Value.Qtd, kv.Value.Raro))
                .OrderByDescending(x => x.Raro).ThenByDescending(x => x.Qtd).ThenBy(x => x.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void SomarDrops(JsonElement e)
    {
        if (!e.TryGetProperty("drops", out var drops) || drops.ValueKind != JsonValueKind.Array) return;
        foreach (var d in drops.EnumerateArray())
        {
            if (d.ValueKind != JsonValueKind.Object) continue;
            var id = (int)(Num(d, "itemId") ?? 0);
            var nome = Str(d, "nome");
            var qtd = (int)(Num(d, "qtd") ?? 1);
            {
                var chave = nome ?? $"item #{id}";
                var raro = Raros.Any(r => (r.Id != 0 && r.Id == id) || (nome is not null && string.Equals(nome, r.Nome, StringComparison.OrdinalIgnoreCase)));
                var q = Math.Max(1, qtd);
                _dropsSessao[chave] = (_dropsSessao.GetValueOrDefault(chave).Qtd + q, raro);
                if (EmOutland()) _dropsOutland[chave] = (_dropsOutland.GetValueOrDefault(chave).Qtd + q, raro);
            }
            foreach (var r in Raros)
                if ((r.Id != 0 && r.Id == id) || (nome is not null && string.Equals(nome, r.Nome, StringComparison.OrdinalIgnoreCase)))
                {
                    _raros[r.Rotulo] = _raros.GetValueOrDefault(r.Rotulo) + Math.Max(1, qtd);
                    break;
                }
        }
    }

    /// <summary>O texto do quadro do modo econômico (várias linhas).</summary>
    public string Resumo()
    {
        var pt = CultureInfo.GetCultureInfo("pt-BR");
        string N(long v) => v.ToString("N0", pt);
        string Curto(long v) => v >= 1_000_000 ? (v / 1_000_000d).ToString("0.#", pt) + " mi" : v >= 10_000 ? (v / 1000d).ToString("0.#", pt) + " mil" : N(v);
        lock (_lock)
        {
            var t = DateTime.UtcNow - Inicio;
            var horas = Math.Max(t.TotalHours, 1 / 60d);
            var sb = new StringBuilder();
            if (NivelTreinador is not null || AtivoNivel is not null)
                sb.Append($"🧑 Treinador Nv {(NivelTreinador is { } nt ? N(nt) : "?")} · 🐾 {AtivoNome ?? "Pokémon ativo"} Nv {(AtivoNivel is { } na ? N(na) : "?")}\n");
            sb.Append($"⏱ {(int)t.TotalHours}h{t.Minutes:00} · {N(Abates)} abates · {N(Capturas)} capturas");
            if (XpTreinador > 0 || Ouro > 0) sb.Append($" · XP {Curto(XpTreinador)} ({Curto((long)(XpTreinador / horas))}/h) · ouro {Curto(Ouro)}");
            sb.Append($"\n✨ Shinies: {ShiniesVistos} visto(s) · {ShiniesCapturados} capturado(s)");
            if (UltimosShinies.Count > 0) sb.Append($" — {string.Join(", ", UltimosShinies)}");
            if (_capturas.Count > 0)
            {
                var melhor = MelhorCaptura(); // o lock do C# é reentrante
                sb.Append(melhor is { } m ? $"\n🏆 Melhor captura: {m.Texto}" : SpeciesCatalog.Pronto ? "" : "\n🏆 Melhor captura: calculando (baixando o catálogo de espécies)…");
            }
            sb.Append("\n🎁 Drops raros: ");
            sb.Append(_raros.Count == 0 ? "nenhum ainda" : string.Join(" · ", _raros.OrderByDescending(k => k.Value).Select(k => $"{k.Key} ×{k.Value}")));
            if (_bolasTem.Count > 0 || _bolasGastas.Count > 0)
            {
                var ids = _bolasTem.Keys.Union(_bolasGastas.Keys).OrderBy(i => i)
                    .Where(i => _bolasTem.GetValueOrDefault(i) > 0 || _bolasGastas.GetValueOrDefault(i) > 0);
                var partes = ids.Select(i =>
                {
                    var nome = _nomeBola.GetValueOrDefault(i, $"Bola #{i}");
                    var gasto = _bolasGastas.GetValueOrDefault(i);
                    return $"{nome} {(_bolasTem.ContainsKey(i) ? N(_bolasTem[i]) : "?")}{(gasto > 0 ? $" (−{N(gasto)})" : "")}";
                });
                sb.Append($"\n⚪ Pokébolas: {string.Join(" · ", partes)}");
                var acabando = _bolasTem.Where(kv => kv.Value is > 0 and < 50 && _bolasGastas.ContainsKey(kv.Key)).Select(kv => _nomeBola.GetValueOrDefault(kv.Key, $"#{kv.Key}")).ToList();
                if (_bolasTem.Count > 0 && _bolasTem.Values.All(v => v <= 0)) sb.Append(" — ⚠ ACABARAM as pokébolas: não vai capturar");
                else if (acabando.Count > 0) sb.Append($" — ⚠ acabando: {string.Join(", ", acabando)}");
            }
            return sb.ToString();
        }
    }

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n
        : e.TryGetProperty(k, out v) && v.ValueKind == JsonValueKind.Number ? (long)v.GetDouble() : null;

    /// <summary>Primeira propriedade com esse nome, em qualquer nível (até 6 de profundidade).</summary>
    internal static JsonElement? Achar(JsonElement e, string name, int depth)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Object)
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals(name)) return p.Value;
                if (p.Value.ValueKind == JsonValueKind.Object && Achar(p.Value, name, depth + 1) is { } f) return f;
            }
        return null;
    }
}
