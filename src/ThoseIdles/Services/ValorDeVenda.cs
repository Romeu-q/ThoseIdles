using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// Quanto um Pokémon vale na venda ao NPC (shop.sellPokemon), com a mesma conta do cliente do jogo
/// (shared/sell-value.mjs e shared/valor-cadeia.mjs): o valor da espécie (sellValue / priceNpc, elevado ao nível da hunt
/// onde ela aparece e alinhado à cadeia de evolução), vezes (1 + nível/50, até o Nv 100), a qualidade e ×10 se shiny.
/// É uma estimativa: o jogo ainda aplica ajustes de catálogo (megas, formas) que o app não replica.
/// </summary>
public static class ValorDeVenda
{
    public sealed record Especie(int Id, string Nome, int HuntLevel, long Sell, long Npc, int EvolvesTo, bool Outland);

    private const int NivelAncora = 150;
    private const long OuroAncora = 12000;
    private const long TetoPriceNpcVenda = 500_000;
    private const int NivelTetoVendaPokemon = 100;
    private static readonly long TetoSellPokemon = OuroDoNivel(2500);
    private static readonly Dictionary<int, long> PrecoNpcFixo = new() { [2027] = 60_000, [2050] = 60_000, [2006] = 32_000, [212] = 88_000, [3212] = 88_000 };

    private static readonly object _lock = new();
    private static Dictionary<int, Especie> _especies = new();
    private static Dictionary<int, int> _menorHunt = new();
    private static Dictionary<int, long>? _base; // espécie → valor-base de venda (recalculado quando algo muda)

    public static long OuroDoNivel(double n) => (long)Math.Round(OuroAncora * Math.Pow(n / NivelAncora, 0.6));

    public static bool Pronto { get { lock (_lock) return _especies.Count > 0; } }

    /// <summary>As espécies do catálogo (creatures*.json), com os campos de economia.</summary>
    public static void Registrar(IEnumerable<Especie> especies)
    {
        lock (_lock)
        {
            foreach (var e in especies) _especies[e.Id] = e;
            _base = null;
        }
    }

    /// <summary>As hunts do welcome: o menor nível de hunt onde cada espécie aparece (eleva o valor das de região alta).</summary>
    public static void HuntsDoWelcome(JsonElement hunts)
    {
        var menor = new Dictionary<int, int>();
        foreach (var h in hunts.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.Object || !h.TryGetProperty("especies", out var es) || es.ValueKind != JsonValueKind.Array) continue;
            var nivel = h.TryGetProperty("nivel", out var nv) && nv.TryGetInt32(out var n) ? n : 0;
            foreach (var e in es.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("pokeId", out var p) || !p.TryGetInt32(out var id)) continue;
                if (!menor.TryGetValue(id, out var antes) || nivel < antes) menor[id] = nivel;
            }
        }
        if (menor.Count == 0) return;
        lock (_lock) { _menorHunt = menor; _base = null; }
    }

    /// <summary>O preço de venda do Pokémon capturado (null se a espécie não está no catálogo).</summary>
    public static long? Preco(int speciesId, int? especieOrigem, int level, double quality, bool shiny)
    {
        lock (_lock)
        {
            _base ??= CalcularBases();
            var id = especieOrigem is { } o && _base.ContainsKey(o) ? o : speciesId;
            if (!_base.TryGetValue(id, out var b)) return null;
            var nivel = Math.Min(Math.Max(1, level), NivelTetoVendaPokemon);
            var q = quality > 0 ? quality : 1;
            var valor = b * (1 + nivel / 50.0) * q * (shiny ? 10 : 1);
            return Math.Max(1, (long)Math.Floor(valor));
        }
    }

    /// <summary>O preço de venda a partir do objeto "pokemon" do evento de captura.</summary>
    public static long? Preco(JsonElement pk)
    {
        int? I(string k) => pk.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
        if (I("speciesId") is not { } sid) return null;
        var q = pk.TryGetProperty("quality", out var qv) && qv.ValueKind == JsonValueKind.Number ? qv.GetDouble() : 1;
        var shiny = pk.TryGetProperty("shiny", out var s) && s.ValueKind == JsonValueKind.True;
        return Preco(sid, I("especieOrigem"), I("level") ?? 1, q, shiny);
    }

    // ------------------------------------------------------------------ a conta do jogo

    private sealed class Valor { public long Sell, Npc; }

    private static long Economico(Especie e, Valor v)
    {
        var hl = Math.Max(1, e.HuntLevel);
        if (v.Sell > 0 && v.Sell <= TetoPriceNpcVenda) return v.Sell;
        if (v.Sell > TetoPriceNpcVenda) return OuroDoNivel(hl);
        if (v.Npc > 0 && v.Npc <= TetoPriceNpcVenda) return v.Npc;
        return OuroDoNivel(hl);
    }

    private static Dictionary<int, long> CalcularBases()
    {
        var valores = new Dictionary<int, Valor>();
        // normalizarSellValue: região alta (Nv > 150) vale o ouro do nível; o sellValue vira o valor econômico.
        foreach (var e in _especies.Values)
        {
            var v = new Valor { Sell = e.Sell, Npc = e.Npc };
            if (e.HuntLevel > NivelAncora) { var o = OuroDoNivel(e.HuntLevel); v.Sell = o; v.Npc = o; }
            var b = Economico(e, v);
            v.Sell = b;
            if (v.Npc > TetoPriceNpcVenda) v.Npc = b;
            valores[e.Id] = v;
        }

        // elevarAoNivelDaHunt: espécie de Nv ≤ 150 que aparece numa hunt de nível maior vale o ouro daquele nível.
        foreach (var e in _especies.Values)
        {
            if (e.HuntLevel > NivelAncora || !_menorHunt.TryGetValue(e.Id, out var nivel) || nivel <= NivelAncora) continue;
            var valor = OuroDoNivel(nivel);
            var v = valores[e.Id];
            if (Economico(e, v) >= valor) continue;
            v.Sell = valor; v.Npc = valor;
        }

        // alinharValorDaCadeia: a pré-evolução não vale mais que a evolução (vale uma fração dela).
        var evolve = _especies.Values.Where(e => !e.Outland && e.EvolvesTo > 0 && _especies.ContainsKey(e.EvolvesTo)).ToDictionary(e => e.Id, e => e.EvolvesTo);
        var temPai = evolve.Values.ToHashSet();
        var estagios = new Dictionary<int, (int Estagio, int Elos)>();
        foreach (var raiz in _especies.Values.Where(e => !temPai.Contains(e.Id) && evolve.ContainsKey(e.Id)))
        {
            var cadeia = new List<int> { raiz.Id };
            var atual = raiz.Id;
            while (evolve.TryGetValue(atual, out var prox) && !cadeia.Contains(prox) && cadeia.Count < 6) { cadeia.Add(prox); atual = prox; }
            for (var i = 0; i < cadeia.Count; i++) estagios.TryAdd(cadeia[i], (i, cadeia.Count));
        }
        var porNome = new Dictionary<string, (int Estagio, int Elos)>();
        foreach (var e in _especies.Values)
            if (estagios.TryGetValue(e.Id, out var info) && (!porNome.TryGetValue(e.Nome, out var a) || info.Elos > a.Elos)) porNome[e.Nome] = info;
        var pre = _especies.Values
            .Where(e => evolve.ContainsKey(e.Id) && porNome.ContainsKey(e.Nome))
            .Select(e => (e, info: porNome[e.Nome], alvo: _especies[evolve[e.Id]]))
            .OrderByDescending(x => x.info.Estagio)
            .ToList();
        foreach (var (e, info, alvo) in pre)
        {
            var valorAlvo = Economico(alvo, valores[alvo.Id]);
            if (Economico(e, valores[e.Id]) < valorAlvo) continue;
            var valor = Math.Max(1, (long)Math.Round(valorAlvo * Razao(info.Estagio, info.Elos)));
            valores[e.Id].Sell = valor; valores[e.Id].Npc = valor;
        }

        // sellValueBaseDe
        var bases = new Dictionary<int, long>();
        foreach (var e in _especies.Values)
        {
            if (PrecoNpcFixo.TryGetValue(e.Id, out var fixo)) { bases[e.Id] = Math.Min(fixo, TetoSellPokemon); continue; }
            var valor = Economico(e, valores[e.Id]);
            var b = e.HuntLevel > NivelAncora ? Math.Min(OuroDoNivel(e.HuntLevel), valor) : valor;
            bases[e.Id] = Math.Min(b, TetoSellPokemon);
        }
        return bases;
    }

    private static double Razao(int estagio, int elos)
    {
        var tabela = Math.Clamp(elos, 2, 3) == 2 ? new[] { 0.517 } : new[] { 0.462, 0.444 };
        return tabela[Math.Min(estagio, tabela.Length - 1)];
    }
}
