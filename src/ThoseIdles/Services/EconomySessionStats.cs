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

    private readonly Dictionary<string, int> _raros = new();
    private readonly Dictionary<int, int> _bolasGastas = new();
    private readonly Dictionary<int, long> _bolasTem = new();
    private readonly Dictionary<int, string> _nomeBola = new();

    /// <summary>Lê o "welcome": nomes das bolas e quantas a conta tem.</summary>
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
        lock (_lock)
        {
            foreach (var e in ev.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                switch (Str(e, "k"))
                {
                    case "morte" when Str(e, "quem") == "selvagem":
                        Abates++;
                        XpTreinador += Num(e, "xpTreinador") ?? Num(e, "xp") ?? 0;
                        Ouro += Num(e, "ouro") ?? 0;
                        SomarDrops(e);
                        mudou = true;
                        break;
                    case "bossMorto":
                        XpTreinador += Num(e, "xpTreinador") ?? Num(e, "xp") ?? 0;
                        Ouro += Num(e, "valor") ?? 0;
                        SomarDrops(e);
                        mudou = true;
                        break;
                    case "bola":
                        if (e.TryGetProperty("shiny", out var sh) && sh.ValueKind == JsonValueKind.True) ShiniesVistos++;
                        if (Num(e, "ballId") is { } bid and > 0)
                        {
                            var id = (int)bid;
                            _bolasGastas[id] = _bolasGastas.GetValueOrDefault(id) + 1;
                            // A bolsa anda junto até o servidor mandar o número certo de novo.
                            if (_bolasTem.TryGetValue(id, out var tem) && tem > 0) _bolasTem[id] = tem - 1;
                        }
                        mudou = true;
                        break;
                    case "capturado":
                        Capturas++;
                        if (e.TryGetProperty("pokemon", out var pk) && pk.ValueKind == JsonValueKind.Object
                            && pk.TryGetProperty("shiny", out var s) && s.ValueKind == JsonValueKind.True)
                        {
                            ShiniesCapturados++;
                            var nome = Str(pk, "nome") ?? "?";
                            var nivel = Num(pk, "level");
                            UltimosShinies.Insert(0, $"{nome}{(nivel is { } nv ? $" Nv {nv}" : "")} ({DateTime.Now:HH:mm})");
                            if (UltimosShinies.Count > 5) UltimosShinies.RemoveAt(5);
                        }
                        mudou = true;
                        break;
                }
            }
        }
        return mudou;
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
            sb.Append($"⏱ {(int)t.TotalHours}h{t.Minutes:00} · {N(Abates)} abates · {N(Capturas)} capturas");
            if (XpTreinador > 0 || Ouro > 0) sb.Append($" · XP {Curto(XpTreinador)} ({Curto((long)(XpTreinador / horas))}/h) · ouro {Curto(Ouro)}");
            sb.Append($"\n✨ Shinies: {ShiniesVistos} visto(s) · {ShiniesCapturados} capturado(s)");
            if (UltimosShinies.Count > 0) sb.Append($" — {string.Join(", ", UltimosShinies)}");
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
