using System.Net.Http;
using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// A NOTA de um Pokémon (0–10 dentro da espécie), a mesma conta da Calculadora do jogo
/// (`shared/nota-pokemon.mjs`, `notaPokemon`), portada linha a linha:
///  · soma dos stats de nascimento: Σ round((base + 2×IV) × qualidade^expoente × potência × shiny);
///  · nota de força numa régua LOGARÍTMICA entre o pior e o melhor nascimento da espécie de referência;
///  · reescalada entre o pior (P1, IV mínimo, qualidade 0,8) e o melhor (P5 shiny, IV 192, qualidade 1,8)
///    da própria espécie.
/// Não aplica o "piso da linhagem" (forma evoluída nunca abaixo da anterior + 0,1): a nota de uma
/// forma evoluída pode sair um pouco abaixo da que o jogo mostra.
/// </summary>
public static class PokemonNota
{
    private static readonly string[] Stats = { "hp", "atk", "def", "spAtk", "spDef", "speed" };
    private static readonly Dictionary<string, double> Expo = new()
    {
        ["hp"] = 0.95, ["atk"] = 0.8, ["def"] = 0.8, ["spAtk"] = 0.8, ["spDef"] = 0.8, ["speed"] = 0.95,
    };
    private static readonly double[] BonusPotencia = { 0, 0.05, 0.1, 0.25, 1 };
    private const int IvMin = 6, IvMax = 192;
    private const double QMin = 0.8, QMax = 1.8;
    private static readonly Dictionary<string, double> RefBase = Stats.ToDictionary(k => k, _ => 80d);

    public sealed record Bases(double Hp, double Atk, double Def, double SpAtk, double SpDef, double Speed)
    {
        internal double this[string k] => k switch
        {
            "hp" => Hp, "atk" => Atk, "def" => Def, "spAtk" => SpAtk, "spDef" => SpDef, _ => Speed,
        };
    }

    /// <summary>
    /// Nota com o PISO DA LINHAGEM do jogo (`linhagem-nota.mjs`): no mínimo a nota que o mesmo
    /// nascimento tiraria em cada forma anterior + 0,1 por evolução (teto 10).
    /// </summary>
    public static double NotaComLinhagem(int speciesId, IReadOnlyDictionary<string, int>? ivs, int? ivSoma, double qualidade, int potencia, bool shiny)
    {
        var nota = Nota(SpeciesCatalog.Get(speciesId), ivs, ivSoma, qualidade, potencia, shiny);
        foreach (var (bases, passos) in SpeciesCatalog.Ancestrais(speciesId))
        {
            var piso = Math.Min(10, Nota(bases, ivs, ivSoma, qualidade, potencia, shiny) + passos * 0.1);
            if (piso > nota) nota = piso;
        }
        return Math.Round(nota * 1000) / 1000;
    }

    /// <summary>Nota 0–10 (3 casas). ivs por stat (hp, atk, def, spAtk, spDef, speed) ou só a soma.</summary>
    public static double Nota(Bases? bases, IReadOnlyDictionary<string, int>? ivs, int? ivSoma, double qualidade, int potencia, bool shiny)
    {
        Func<string, double> b = bases is null ? k => RefBase[k] : k => bases[k];
        var limitesRef = LimitesForca(k => RefBase[k]);
        double Score(Func<string, double> bb, IReadOnlyDictionary<string, int>? iv, int? soma, double q, int p, bool sh) =>
            Math.Clamp(NotaDeForca(SomaNascimento(bb, iv, soma, q, p, sh), limitesRef) / 10, 0, 1);

        var min = Score(b, null, IvMin, QMin, 1, false);
        var max = Math.Max(min, Score(b, null, IvMax, QMax, 5, true));
        var s = Math.Clamp(Score(b, ivs, ivSoma, qualidade, potencia, shiny), min, max);
        if (max <= min) return 0;
        return Math.Round(10 * (s - min) / (max - min) * 1000) / 1000;
    }

    private static double SomaNascimento(Func<string, double> b, IReadOnlyDictionary<string, int>? ivsRaw, int? ivSoma, double qualidade, int potencia, bool shiny)
    {
        var ivs = ivsRaw ?? IvsDeSoma(ivSoma ?? IvMin);
        var q = Math.Clamp(qualidade <= 0 ? 1 : qualidade, QMin, QMax);
        var p = Math.Clamp(potencia <= 0 ? 1 : potencia, 1, 5);
        var mult = (1 + BonusPotencia[p - 1]) * (shiny ? 3 : 1);
        double soma = 0;
        foreach (var k in Stats)
        {
            var iv = ivs.TryGetValue(k, out var v) && v > 0 ? v : 1;
            soma += Math.Round((b(k) + 2 * iv) * Math.Pow(q, Expo[k]) * mult, MidpointRounding.AwayFromZero);
        }
        return soma;
    }

    private static Dictionary<string, int> IvsDeSoma(int soma)
    {
        var s = Math.Clamp(soma, IvMin, IvMax);
        var med = s / 6;
        var rest = s - med * 6;
        var d = Stats.ToDictionary(k => k, _ => med);
        for (var i = 0; i < rest; i++) d[Stats[i % 6]]++;
        return d;
    }

    private static (double Min, double Max) LimitesForca(Func<string, double> b)
    {
        var min = SomaNascimento(b, null, IvMin, QMin, 1, false);
        var max = SomaNascimento(b, null, IvMax, QMax, 5, true);
        return (Math.Max(1, min), Math.Max(min + 1, max));
    }

    private static double NotaDeForca(double soma, (double Min, double Max) l)
    {
        var s = Math.Clamp(soma, l.Min, l.Max);
        if (l.Max <= l.Min) return 0;
        var bruta = 10 * (Math.Log(Math.Max(1, s)) - Math.Log(l.Min)) / (Math.Log(l.Max) - Math.Log(l.Min));
        return Math.Round(Math.Clamp(bruta, 0, 10) * 1000) / 1000;
    }
}

/// <summary>
/// Status base das espécies, do catálogo PÚBLICO do jogo (os mesmos arquivos que a página baixa:
/// creatures.json, creatures-novos.json e creatures-outland-novos.json). Baixado uma vez por dia e
/// guardado na pasta de dados; sem internet, usa a cópia guardada.
/// </summary>
public static class SpeciesCatalog
{
    private static readonly string[] Arquivos = { "creatures.json", "creatures-novos.json", "creatures-outland-novos.json" };
    private static Dictionary<int, PokemonNota.Bases>? _porId;
    private static Dictionary<int, int> _anterior = new(); // espécie → de quem ela evolui
    private static Task? _carregando;

    /// <summary>As formas anteriores da espécie (bases e a quantos degraus estão), da mais próxima à primeira.</summary>
    public static IEnumerable<(PokemonNota.Bases Bases, int Passos)> Ancestrais(int speciesId)
    {
        var passos = 0;
        var atual = speciesId;
        var vistos = new HashSet<int> { speciesId };
        while (_anterior.TryGetValue(atual, out var ant) && vistos.Add(ant) && passos < 6)
        {
            passos++;
            if (Get(ant) is { } b) yield return (b, passos);
            atual = ant;
        }
    }

    /// <summary>Endereço base dos arquivos (o autoteste troca).</summary>
    public static string BaseUrl { get; set; } = "https://pokeidle.io/assets/";

    public static PokemonNota.Bases? Get(int speciesId) =>
        _porId is { } m && m.TryGetValue(speciesId, out var b) ? b : null;

    public static bool Pronto => _porId is not null;

    public static Task EnsureLoadedAsync() => _carregando ??= Task.Run(CarregarAsync);

    private static async Task CarregarAsync()
    {
        var cache = Path.Combine(AppPaths.Root, "catalogo-especies-v2.json");
        var mapa = new Dictionary<int, PokemonNota.Bases>();
        var anterior = new Dictionary<int, int>();
        try
        {
            if (!File.Exists(cache) || DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) > TimeSpan.FromDays(1))
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ThoseIdles");
                foreach (var a in Arquivos)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(await http.GetStringAsync(BaseUrl + a));
                        Ler(doc.RootElement, mapa, anterior);
                    }
                    catch (Exception ex) { LoggingService.Log.Info($"Catálogo de espécies: {a} não baixou ({ex.Message})."); }
                }
                if (mapa.Count > 0)
                    await File.WriteAllTextAsync(cache, JsonSerializer.Serialize(new Guardado(
                        mapa.ToDictionary(k => k.Key.ToString(), k => k.Value),
                        anterior.ToDictionary(k => k.Key.ToString(), k => k.Value))));
            }
            if (mapa.Count == 0 && File.Exists(cache))
            {
                var g = JsonSerializer.Deserialize<Guardado>(await File.ReadAllTextAsync(cache));
                foreach (var (k, v) in g?.Bases ?? new()) if (int.TryParse(k, out var id)) mapa[id] = v;
                foreach (var (k, v) in g?.Anterior ?? new()) if (int.TryParse(k, out var id)) anterior[id] = v;
            }
            _anterior = anterior;
            _porId = mapa;
            LoggingService.Log.Info($"Catálogo de espécies: {mapa.Count} espécies.");
        }
        catch (Exception ex)
        {
            _porId = mapa;
            LoggingService.Log.Warn($"Catálogo de espécies falhou: {ex.Message}");
        }
    }

    private sealed record Guardado(Dictionary<string, PokemonNota.Bases> Bases, Dictionary<string, int> Anterior);

    private static void Ler(JsonElement root, Dictionary<int, PokemonNota.Bases> mapa, Dictionary<int, int> anterior)
    {
        if (!root.TryGetProperty("creatures", out var lista) || lista.ValueKind != JsonValueKind.Array) return;
        foreach (var c in lista.EnumerateArray())
        {
            double N(string k) => c.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 80;
            if (!c.TryGetProperty("pokeId", out var id) || !id.TryGetInt32(out var pid)) continue;
            mapa[pid] = new PokemonNota.Bases(N("baseHp"), N("baseAtk"), N("baseDef"), N("baseSpAtk"), N("baseSpDef"), N("baseSpeed"));
            if (c.TryGetProperty("evolvesToId", out var ev) && ev.ValueKind == JsonValueKind.Number && ev.TryGetInt32(out var para) && para > 0)
                anterior.TryAdd(para, pid);
        }
    }

    /// <summary>Só para o teste.</summary>
    internal static void Definir(Dictionary<int, PokemonNota.Bases> mapa, Dictionary<int, int>? anterior = null)
    {
        _porId = mapa;
        _anterior = anterior ?? new();
    }
}
