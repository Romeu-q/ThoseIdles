using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>Uma hunt do jogo (a lista vem no "welcome").</summary>
public sealed record HuntInfo(string Slug, string Nome, int Nivel, string? Regiao)
{
    /// <summary>A hunt é da Outland (a região "outland" do jogo)?</summary>
    public bool Outland => string.Equals(Regiao, "outland", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Um Pokémon da conta (só o que as telas do app precisam).</summary>
/// <param name="Nome">O apelido, se tiver; senão a espécie.</param>
/// <param name="Especie">A espécie (o campo "nome" do jogo), que o PokéIdle Hub reconhece.</param>
public sealed record PokemonInfo(long Id, string Nome, long Level, int? Slot, long? Hp, long? MaxHp, bool Shiny, int Potencia, int SpeciesId, bool Starter = false, string? Especie = null, double? Qualidade = null);

/// <summary>Uma pokébola do catálogo do jogo.</summary>
public sealed record BallInfo(int Id, string Nome, long? Preco, bool Compravel = true, double? CatchRate = null);

/// <summary>Um item comprável por ouro no Market (NPC) do jogo: poções, revives e ovos.</summary>
public sealed record ShopItemInfo(int Id, string Nome, string Categoria, string Descricao, long Preco);

/// <summary>
/// O estado de UMA conta, remontado a partir do que o servidor manda ("welcome" e "estado" em delta: só
/// vêm os campos que mudaram; a coleção de Pokémon vem em pkMud/pkFora). É a mesma regra do jogo
/// (shared/estado-delta.mjs): `cheio` recomeça, chave ausente = não mudou, `null` = apagou.
///
/// Guarda só o que o resumo das contas e a tela mínima do modo econômico mostram — não a coleção
/// inteira como JSON —, para continuar custando poucos MB por conta.
/// </summary>
public sealed class AccountGameState
{
    private readonly object _lock = new();

    // ---- as travas do jogo para trocar de hunt e voltar ao Centro (o servidor também as confere) ----
    // · centroLivreEm: 3 s sem trocar dano na hunt (carimbo no relógio do SERVIDOR; o estado traz "servidorAgora");
    // · trocaLivreEm: a porta de 10 s desde a última ENTRADA numa hunt pelo mapa (vem no evento "hunt");
    // · a guarda local: 4 s entre duas trocas de hunt/idas ao Centro nossas (ou vistas), para não martelar o servidor.
    private long _centroLivreEm, _trocaHuntLivreEm, _offsetMs;
    private DateTime _guardaAte = DateTime.MinValue;
    private static long AgoraLocalMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private long AgoraServidorMs => AgoraLocalMs + _offsetMs;

    /// <summary>A guarda entre trocas de hunt / idas ao Centro, em milissegundos.</summary>
    public const int GuardaTrocaMs = 4000;

    /// <summary>Quanto falta (ms) para a hunt poder ser deixada: os 3 s sem dano (carimbo do servidor). 0 = livre.</summary>
    public double EsperaCombateMs() { lock (_lock) return Math.Max(0, _centroLivreEm - AgoraServidorMs); }

    /// <summary>Quanto falta (ms) da porta de 10 s desde a última entrada numa hunt. 0 = livre.</summary>
    public double EsperaPortaMs() { lock (_lock) return Math.Max(0, _trocaHuntLivreEm - AgoraServidorMs); }

    /// <summary>Quanto falta (ms) da guarda local de 4 s. 0 = livre.</summary>
    public double EsperaGuardaMs() { lock (_lock) return Math.Max(0, (_guardaAte - DateTime.UtcNow).TotalMilliseconds); }

    /// <summary>Arma a guarda (depois de mandar uma troca de hunt ou a ida ao Centro).</summary>
    public void ArmarGuarda(int ms = GuardaTrocaMs) { lock (_lock) _guardaAte = DateTime.UtcNow.AddMilliseconds(ms); }

    /// <summary>
    /// Os eventos de uma mensagem "batalha": a porta de 10 s ("hunt"/"safari" trazem trocaLivreEm) e o rearme dos 3 s a
    /// cada golpe trocado (a mesma regra do jogo: qualquer dano rearma a contagem).
    /// </summary>
    public void Evento(JsonElement root)
    {
        if (!root.TryGetProperty("ev", out var ev) || ev.ValueKind != JsonValueKind.Array) return;
        lock (_lock)
        {
            foreach (var e in ev.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.Object) continue;
                switch (Str(e, "k"))
                {
                    case "hunt" or "safari":
                        if (Num(e, "trocaLivreEm") is { } t) _trocaHuntLivreEm = t;
                        break;
                    case "ataque" when HuntSlug is not null && !NoCentro:
                        _centroLivreEm = AgoraServidorMs + 3000;
                        break;
                    case "bola":
                        if (Num(e, "ballId") is { } bid and > 0 && _balls.TryGetValue((int)bid, out var qb) && qb > 0)
                            _balls[(int)bid] = qb - 1;
                        break;
                    case "pocao" or "revive" or "cura":
                        if (Num(e, "itemId") is { } iid and > 0 && _items.TryGetValue((int)iid, out var qi) && qi > 0)
                            _items[(int)iid] = qi - 1;
                        break;
                    case "morte" when Str(e, "quem") == "selvagem":
                        if (Num(e, "ouro") is { } ouro and > 0) Gold += ouro;
                        break;
                    case "bossMorto":
                        if (Num(e, "valor") is { } val and > 0) Gold += val;
                        break;
                }
            }
        }
    }

    // ---- o estado BRUTO que o motor do /hunt precisa (ele lê o estado inteiro do pokémon e os campos do `eu`) ----
    private static readonly string[] ChavesDoWelcome =
    {
        "hunts", "tabelaTipos", "ampliacaoHunt", "multHpSelvagem", "multDanoSelvagem", "tiposBoost",
        "catalogoBolas", "captura", "chanceShiny", "shinyLooks", "shinyLooksOutland",
    };
    private static readonly HashSet<string> FORA_DOS_CAMPOS = new() { "pokemons", "pkMud", "pkFora", "dexMud", "dexFora", "cheio", "pokedex" };
    private readonly Dictionary<string, string> _welcomeBruto = new();
    private readonly Dictionary<string, string> _camposBrutos = new();
    private readonly Dictionary<long, string> _pokBrutos = new();
    private const int MaxCampoBruto = 400_000;

    /// <summary>
    /// A entrada do motor "Onde caçar?" (/hunt): o welcome (hunts, tabelas e multiplicadores), o `eu` (os campos do estado) e o
    /// pokémon em campo — tudo como o servidor mandou, em JSON. null enquanto não houver welcome, pokémon em campo ou stats.
    /// </summary>
    public string? EntradaDoMotor()
    {
        lock (_lock)
        {
            if (ActiveId is not { } id || !_pokBrutos.TryGetValue(id, out var pk) || _welcomeBruto.Count == 0 || !_welcomeBruto.ContainsKey("hunts")) return null;
            var sb = new System.Text.StringBuilder(64 * 1024);
            sb.Append("{\"welcome\":{");
            sb.Append(string.Join(",", _welcomeBruto.Select(kv => $"\"{kv.Key}\":{kv.Value}")));
            sb.Append("},\"eu\":{");
            foreach (var kv in _camposBrutos) sb.Append($"\"{kv.Key}\":{kv.Value},");
            sb.Append($"\"pokemons\":[{pk}]}},\"pk\":{pk}}}");
            return sb.ToString();
        }
    }

    /// <summary>
    /// O que muda o ranking do /hunt: o pokémon em campo (id, nível, stats, golpes), o bônus de XP que o servidor aplica e o dia
    /// sazonal. Não inclui o XP que ele ganha a cada abate — senão recalcularia o tempo todo.
    /// </summary>
    public string ChaveDoMotor()
    {
        lock (_lock)
        {
            if (ActiveId is not { } id || !_pokBrutos.TryGetValue(id, out var pk)) return "";
            string stats = "";
            long level = 0;
            try
            {
                using var d = JsonDocument.Parse(pk);
                var r = d.RootElement;
                level = Num(r, "level") ?? 0;
                stats = r.TryGetProperty("stats", out var st) ? st.GetRawText() : "";
                var aoe = r.TryGetProperty("tmAoe", out var a) ? a.GetRawText() : "";
                stats += aoe;
            }
            catch { }
            _camposBrutos.TryGetValue("bonusXp", out var bonus);
            _camposBrutos.TryGetValue("diaSazonal", out var dia);
            return $"{id}|{level}|{stats}|{bonus}|{dia}";
        }
    }

    public string? Nick { get; private set; }
    public long Level { get; private set; }

    /// <summary>O tier da Outland que a conta escolheu (`outlandTier` do estado; 1 se não veio).</summary>
    public int OutlandTierEscolhido { get; private set; } = 1;

    /// <summary>Os tiers da Outland do jogo (shared/outland-tiers.mjs): liberados pelo nível do treinador; o mult multiplica os drops.</summary>
    public static readonly (int Tier, string Rotulo, int Nivel, double Mult)[] OutlandTiers =
    {
        (1, "Outland 1", 150, 1), (2, "Outland 2", 1000, 1.25), (3, "Outland 3", 2000, 1.5), (4, "Outland 4", 4000, 2),
        (5, "Outland 5", 8000, 3), (6, "Outland 6", 16000, 4), (7, "Outland 7", 24000, 6), (8, "Outland 8", 36000, 8),
    };

    /// <summary>O tier em que a conta caça na Outland: o escolhido, se o nível libera; senão o maior liberado (como o jogo).</summary>
    public (int Tier, string Rotulo, int Nivel, double Mult) OutlandTierAtual
    {
        get
        {
            var escolhido = OutlandTiers.FirstOrDefault(x => x.Tier == OutlandTierEscolhido);
            if (escolhido.Tier == 0) escolhido = OutlandTiers[0];
            if (Level >= escolhido.Nivel) return escolhido;
            for (var i = OutlandTiers.Length - 1; i >= 0; i--) if (Level >= OutlandTiers[i].Nivel) return OutlandTiers[i];
            return OutlandTiers[0];
        }
    }

    /// <summary>O maior tier da Outland que o nível do treinador libera.</summary>
    public (int Tier, string Rotulo, int Nivel, double Mult) OutlandTierMaximo
        => OutlandTiers.LastOrDefault(x => Level >= x.Nivel) is { Tier: > 0 } t ? t : OutlandTiers[0];
    public long Gold { get; private set; }
    public long Diamonds { get; private set; }
    public long Orbs { get; private set; }
    public long Xp { get; private set; }
    public long XpNivel { get; private set; }
    public long XpProximo { get; private set; }
    public string? HuntSlug { get; private set; }
    public long? ActiveId { get; private set; }
    public bool NoCentro { get; private set; }
    public bool NoSafari { get; private set; }
    public bool Recebeu { get; private set; }

    private readonly Dictionary<string, bool> _auto = new();
    private readonly Dictionary<string, JsonElement> _autoBruta = new();
    private List<int> _ballIds = new();
    private List<int> _potionIds = new();
    private List<int> _reviveIds = new();
    private HashSet<long> _travados = new();
    public bool Vip { get; private set; }

    // ---- o que dá bônus de XP agora (a Calculadora de XP do Hub precisa disto) ----
    private long _vipAte, _boostXp, _boostPokexp, _guildBoostAte, _twitchXp, _kickXp, _atrasadosAlvo, _eventoTermina, _guildRank;
    private double _guildPct, _eventoPct;

    /// <summary>
    /// Os bônus de XP ligados AGORA, no formato das "escolhas" da Calculadora de XP do Hub (VIP, XP Boost, ranking e Boost da
    /// guild, Twitch, Kick, evento, Atrasados). Os prazos são carimbos do relógio do servidor.
    /// </summary>
    public Dictionary<string, object> EscolhasXp()
    {
        lock (_lock)
        {
            var agora = AgoraServidorMs;
            bool Vale(long t) => t > agora;
            return new Dictionary<string, object>
            {
                ["vip"] = Vale(_vipAte),
                ["xpBoost"] = Vale(_boostXp),
                ["xpBoostPokemon"] = Vale(_boostPokexp),
                ["guildPosicao"] = _guildPct > 0 && _guildRank > 0 ? (int)Math.Min(_guildRank, 10) : 0,
                ["boostGuild"] = Vale(_guildBoostAte),
                ["twitch"] = Vale(_twitchXp),
                ["kick"] = Vale(_kickXp),
                ["eventoPct"] = Vale(_eventoTermina) ? _eventoPct : 0d,
                ["atrasados"] = _atrasadosAlvo > 0 && Level < _atrasadosAlvo,
                ["atrasadosAlvo"] = _atrasadosAlvo > 0 ? _atrasadosAlvo : 0L,
            };
        }
    }

    /// <summary>Um bônus de XP ligado agora e quanto falta para acabar (null = sem prazo, como o ranking da guild).</summary>
    public sealed record BonusAtivo(string Nome, TimeSpan? Resta);

    /// <summary>
    /// Os bônus de XP ligados AGORA, com o tempo que falta (VIP, XP Boost, XP Boost de Pokémon, Boost e ranking da guild,
    /// Twitch, Kick, evento e Atrasados). O Resumo das contas mostra e avisa quando um está acabando.
    /// </summary>
    public IReadOnlyList<BonusAtivo> BonusAtivos()
    {
        lock (_lock)
        {
            var agora = AgoraServidorMs;
            var lista = new List<BonusAtivo>();
            TimeSpan Resta(long t) => TimeSpan.FromMilliseconds(t - agora);
            void Prazo(string nome, long ate) { if (ate > agora) lista.Add(new BonusAtivo(nome, Resta(ate))); }

            if (_vipAte > agora) lista.Add(new BonusAtivo("VIP", Resta(_vipAte)));
            else if (Vip) lista.Add(new BonusAtivo("VIP", null));
            Prazo("XP Boost", _boostXp);
            Prazo("Boost Pokémon", _boostPokexp);
            Prazo("Boost da guild", _guildBoostAte);
            Prazo("Twitch", _twitchXp);
            Prazo("Kick", _kickXp);
            if (_eventoTermina > agora)
                lista.Add(new BonusAtivo(_eventoPct > 0 ? $"Evento +{_eventoPct:0.#}%" : "Evento", Resta(_eventoTermina)));
            if (_guildPct > 0 && _guildRank is > 0 and <= 10) lista.Add(new BonusAtivo($"Guild {_guildRank}º (+{_guildPct:0.#}%)", null));
            if (_atrasadosAlvo > 0 && Level > 0 && Level < _atrasadosAlvo) lista.Add(new BonusAtivo("Atrasados", null));
            return lista;
        }
    }

    /// <summary>O Pokémon está na Coleção (protegido: a automação e a venda em lote não o tocam)?</summary>
    public bool Travado(long id) { lock (_lock) return _travados.Contains(id); }

    /// <summary>Os Pokémon guardados (sem vaga no time): shinies primeiro, depois do maior nível ao menor.</summary>
    public IReadOnlyList<PokemonInfo> Depot { get { lock (_lock) return _pokemons.Values.Where(p => p.Slot is null).OrderByDescending(p => p.Shiny).ThenByDescending(p => p.Level).ToList(); } }

    /// <summary>A menor vaga livre do time (0 a 4) ou null se o time está cheio — a mesma regra do jogo (MAX_EQUIPE = 5).</summary>
    public int? VagaLivre
    {
        get
        {
            lock (_lock)
            {
                var usadas = _pokemons.Values.Where(p => p.Slot is not null).Select(p => p.Slot!.Value).ToHashSet();
                for (var i = 0; i < 5; i++) if (!usadas.Contains(i)) return i;
                return null;
            }
        }
    }

    /// <summary>
    /// O pedido "auto.set" com o objeto de automações INTEIRO mais a mudança (é assim que o jogo manda:
    /// `{ t: 'auto.set', ...estado.eu.automation, ...patch }`). null enquanto o servidor não mandou as automações.
    /// </summary>
    public Dictionary<string, object?>? PedidoAutomacao(IReadOnlyDictionary<string, object?> mudanca)
    {
        lock (_lock)
        {
            if (_autoBruta.Count == 0) return null;
            var pedido = new Dictionary<string, object?> { ["t"] = "auto.set" };
            foreach (var kv in _autoBruta) pedido[kv.Key] = kv.Value;
            foreach (var kv in mudanca) pedido[kv.Key] = kv.Value;
            return pedido;
        }
    }
    private static readonly ShopItemInfo[] ItensMarketPadrao =
    {
        new(200, "Small Potion", "potion", "Cura 60 HP", 50),
        new(201, "Great Potion", "potion", "Cura 150 HP", 150),
        new(202, "Ultra Potion", "potion", "Cura 400 HP", 400),
        new(203, "Hyper Potion", "potion", "Cura 1.000 HP", 800),
        new(204, "Ultimate Potion", "potion", "Cura 3.000 HP", 1500),
        new(205, "Revive", "revive", "Revive com 10% do HP", 600),
        new(206, "Max Revive", "revive", "Revive com 100% do HP", 2500),
        new(70091, "Mystery Egg P2", "egg", "Potência P2 · choca em 6h", 20_000_000),
        new(70092, "Mystery Egg P3", "egg", "Potência P3 · choca em 12h", 120_000_000),
        new(70093, "Mystery Egg P4", "egg", "Potência P4 · choca em 24h", 250_000_000),
    };

    private readonly Dictionary<int, long> _balls = new();
    private readonly Dictionary<int, long> _items = new();
    private List<HuntInfo> _hunts = new();
    private List<BallInfo> _catalogoBolas = new();
    private List<ShopItemInfo> _catalogoMarket = ItensMarketPadrao.ToList();
    private readonly Dictionary<long, PokemonInfo> _pokemons = new();

    /// <summary>Mudou alguma coisa (disparado na thread de quem leu a mensagem; a tela volta à UI).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<HuntInfo> Hunts { get { lock (_lock) return _hunts.ToList(); } }
    public IReadOnlyList<BallInfo> CatalogoBolas { get { lock (_lock) return _catalogoBolas.ToList(); } }
    public IReadOnlyList<ShopItemInfo> CatalogoMarket { get { lock (_lock) return _catalogoMarket.ToList(); } }
    public IReadOnlyList<PokemonInfo> Pokemons { get { lock (_lock) return _pokemons.Values.ToList(); } }
    public IReadOnlyList<int> BallIds { get { lock (_lock) return _ballIds.ToList(); } }
    public IReadOnlyList<int> PotionIds { get { lock (_lock) return _potionIds.ToList(); } }
    public IReadOnlyList<int> ReviveIds { get { lock (_lock) return _reviveIds.ToList(); } }
    public long Balls(int id) { lock (_lock) return _balls.GetValueOrDefault(id); }
    public long Items(int id) { lock (_lock) return _items.GetValueOrDefault(id); }
    public bool Auto(string chave) { lock (_lock) return _auto.GetValueOrDefault(chave); }
    public IReadOnlyDictionary<string, bool> AutoTudo { get { lock (_lock) return new Dictionary<string, bool>(_auto); } }

    public PokemonInfo? Ativo { get { lock (_lock) return ActiveId is { } id && _pokemons.TryGetValue(id, out var p) ? p : null; } }
    public HuntInfo? HuntAtual { get { lock (_lock) return HuntSlug is null ? null : _hunts.FirstOrDefault(h => h.Slug == HuntSlug); } }

    /// <summary>O time (os Pokémon com vaga), por vaga.</summary>
    public IReadOnlyList<PokemonInfo> Time { get { lock (_lock) return _pokemons.Values.Where(p => p.Slot is not null).OrderBy(p => p.Slot).ToList(); } }

    /// <summary>Lê um "welcome" ou um "estado" (a raiz da mensagem).</summary>
    public void Apply(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        lock (_lock)
        {
            foreach (var chave in ChavesDoWelcome)
                if (root.TryGetProperty(chave, out var wv) && wv.ValueKind != JsonValueKind.Undefined)
                {
                    var raw = wv.GetRawText();
                    if (raw.Length <= 2_000_000) _welcomeBruto[chave] = raw;
                }
            if (root.TryGetProperty("hunts", out var hs) && hs.ValueKind == JsonValueKind.Array) { LerHunts(hs); ValorDeVenda.HuntsDoWelcome(hs); }
            if (root.TryGetProperty("catalogoBolas", out var cb) && cb.ValueKind == JsonValueKind.Array) LerCatalogoBolas(cb);
            if (root.TryGetProperty("itensNossos", out var inn0) && inn0.ValueKind == JsonValueKind.Array) LerItensNossosDoMarket(inn0);
            if (root.TryGetProperty("itensCompraveis", out var ic) && ic.ValueKind == JsonValueKind.Array) LerItensCompraveis(ic, root.TryGetProperty("itensNossos", out var inn) && inn.ValueKind == JsonValueKind.Array ? inn : null);
            var est = root.TryGetProperty("estado", out var e) && e.ValueKind == JsonValueKind.Object ? e : root;
            LerEstado(est);
            Recebeu = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void LerHunts(JsonElement hs)
    {
        var lista = new List<HuntInfo>();
        foreach (var h in hs.EnumerateArray())
        {
            if (h.ValueKind != JsonValueKind.Object) continue;
            var slug = Str(h, "slug");
            if (string.IsNullOrEmpty(slug)) continue;
            lista.Add(new HuntInfo(slug, Str(h, "nome") ?? Str(h, "name") ?? slug, (int)(Num(h, "nivel") ?? Num(h, "level") ?? 1), Str(h, "regiao") ?? Str(h, "region")));
        }
        if (lista.Count > 0) _hunts = lista.OrderBy(h => h.Nivel).ThenBy(h => h.Nome, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private void LerCatalogoBolas(JsonElement cb)
    {
        var lista = new List<BallInfo>();
        foreach (var b in cb.EnumerateArray())
        {
            if (b.ValueKind != JsonValueKind.Object || Num(b, "id") is not { } id) continue;
            var preco = Num(b, "priceGold") ?? Num(b, "preco") ?? Num(b, "custo") ?? Num(b, "price");
            var compravel = !b.TryGetProperty("compravel", out var cp) || cp.ValueKind != JsonValueKind.False;
            double? catchRate = b.TryGetProperty("catchRate", out var cr) && cr.ValueKind == JsonValueKind.Number ? cr.GetDouble() : null;
            lista.Add(new BallInfo((int)id, Str(b, "nome") ?? $"Bola #{id}", preco, compravel, catchRate));
        }
        if (lista.Count > 0) _catalogoBolas = lista.OrderBy(b => b.Id).ToList();
    }

    /// <summary>
    /// Poções e revives próprios do servidor (`itensNossos`, como a Golden Potion): o Market do jogo lista todo item com
    /// categoria "heal" ou "revive" e preço de NPC, do items.json e dos itensNossos. Os do welcome valem mais (preço e nome).
    /// </summary>
    private void LerItensNossosDoMarket(JsonElement itens)
    {
        var novos = new List<ShopItemInfo>();
        foreach (var i in itens.EnumerateArray())
        {
            if (i.ValueKind != JsonValueKind.Object || Num(i, "id") is not { } id) continue;
            var cat = Str(i, "category") ?? Str(i, "categoria") ?? "";
            var nossa = cat switch { "heal" or "potion" => "potion", "revive" => "revive", _ => null };
            if (nossa is null) continue;
            var preco = Num(i, "npcPrice") ?? Num(i, "preco") ?? 0;
            if (preco <= 0) continue;
            var nome = Str(i, "nome") ?? Str(i, "name");
            if (string.IsNullOrEmpty(nome)) continue;
            string desc;
            if (nossa == "potion" && Dbl(i, "healPct") is { } hp and > 0) desc = $"Cura {Math.Round(hp * 100)}% do HP";
            else if (nossa == "potion" && Num(i, "healAmount") is { } ha and > 0) desc = $"Cura {ha:N0} HP";
            else if (nossa == "revive" && Dbl(i, "revivePct") is { } rp) desc = $"Revive com {Math.Round(rp * 100)}% do HP";
            else desc = "";
            novos.Add(new ShopItemInfo((int)id, nome, nossa, desc, preco));
        }
        if (novos.Count == 0) return;
        var ids = novos.Select(n => n.Id).ToHashSet();
        _catalogoMarket = _catalogoMarket.Where(m => !ids.Contains(m.Id)).Concat(novos).ToList();
        LoggingService.Log.Info("Market: itens do servidor: " + string.Join(", ", novos.Select(n => $"{n.Nome} (id {n.Id}, {n.Categoria}, {n.Preco})")));
    }

    private static double? Dbl(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private void LerItensCompraveis(JsonElement ic, JsonElement? itensNossos = null)
    {
        // Nomes dos itens próprios do servidor (o Golden Potion e afins não estão no items.json público).
        var nomes = new Dictionary<int, (string Nome, string Cat)>();
        if (itensNossos is { } inn)
            foreach (var i in inn.EnumerateArray())
                if (i.ValueKind == JsonValueKind.Object && Num(i, "id") is { } iid && (Str(i, "name") ?? Str(i, "nome")) is { Length: > 0 } nm)
                    nomes[(int)iid] = (nm, Str(i, "category") ?? Str(i, "categoria") ?? "");
        var precos = new Dictionary<int, long>();
        var novos = new List<ShopItemInfo>();
        foreach (var x in ic.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object || Num(x, "itemId") is not { } id || Num(x, "preco") is not { } p || p <= 0) continue;
            precos[(int)id] = p;
            if (_catalogoMarket.Any(m => m.Id == (int)id)) continue;
            var nome = Str(x, "nome") ?? Str(x, "name") ?? (nomes.TryGetValue((int)id, out var n) ? n.Nome : null);
            if (nome is null) { LoggingService.Log.Info($"Market: item sem nome no welcome (itemId {id}, preço {p})."); continue; }
            var cat = Str(x, "categoria") ?? Str(x, "category") ?? (nomes.TryGetValue((int)id, out var n2) ? n2.Cat : "");
            if (cat.Length == 0) cat = nome.Contains("revive", StringComparison.OrdinalIgnoreCase) ? "revive" : nome.Contains("potion", StringComparison.OrdinalIgnoreCase) ? "potion" : "item";
            novos.Add(new ShopItemInfo((int)id, nome, cat, Str(x, "descricao") ?? "", p));
        }
        if (precos.Count == 0) return;
        _catalogoMarket = _catalogoMarket.Select(m => precos.TryGetValue(m.Id, out var p) ? m with { Preco = p } : m).Concat(novos).ToList();
        if (novos.Count > 0) LoggingService.Log.Info("Market: itens novos lidos do jogo: " + string.Join(", ", novos.Select(n => $"{n.Nome} (id {n.Id}, {n.Categoria}, {n.Preco})")));
    }

    private void LerEstado(JsonElement est)
    {
        if (Str(est, "nick") is { } nick) Nick = nick;
        if (Num(est, "level") is { } lv) Level = lv;
        if (Num(est, "outlandTier") is { } otier && otier > 0) OutlandTierEscolhido = (int)otier;
        if (Num(est, "gold") is { } g) Gold = g;
        if (Num(est, "diamonds") is { } d) Diamonds = d;
        if (Num(est, "orbs") is { } o) Orbs = o;
        if (Num(est, "xp") is { } xp) Xp = xp;
        if (Num(est, "xpNivel") is { } xn) XpNivel = xn;
        if (Num(est, "xpProximo") is { } xpp) XpProximo = xpp;
        var huntAntes = HuntSlug;
        if (est.TryGetProperty("huntSlug", out var hs)) HuntSlug = hs.ValueKind == JsonValueKind.String ? hs.GetString() : null;
        // Uma troca de hunt que o app viu (a nossa ou a do jogo) arma a guarda de 4 s — menos a primeira leitura, ao conectar.
        if (Recebeu && HuntSlug != huntAntes) _guardaAte = DateTime.UtcNow.AddMilliseconds(GuardaTrocaMs);
        if (Num(est, "activeId") is { } aid) ActiveId = aid;
        if (est.TryGetProperty("noCentro", out var nc)) NoCentro = nc.ValueKind == JsonValueKind.True;
        // O relógio do servidor e o carimbo dos 3 s: só valem dentro de uma hunt; fora dela zeram.
        if (Num(est, "servidorAgora") is { } sa) _offsetMs = sa - AgoraLocalMs;
        if (HuntSlug is null || NoCentro) _centroLivreEm = 0;
        else if (Num(est, "centroLivreEm") is { } cl) _centroLivreEm = Math.Max(_centroLivreEm, cl);
        if (est.TryGetProperty("safari", out var sf)) NoSafari = sf.ValueKind == JsonValueKind.Object && sf.TryGetProperty("dentro", out var dn) && dn.ValueKind == JsonValueKind.True;

        if (est.TryGetProperty("loja", out var lj))
        {
            var ok = lj.ValueKind == JsonValueKind.Object;
            Vip = ok && lj.TryGetProperty("vip", out var vp) && vp.ValueKind == JsonValueKind.True;
            _vipAte = ok ? Num(lj, "vipAte") ?? 0 : 0;
            _boostXp = _boostPokexp = _atrasadosAlvo = 0;
            if (ok && lj.TryGetProperty("boosts", out var bs) && bs.ValueKind == JsonValueKind.Object) { _boostXp = Num(bs, "xp") ?? 0; _boostPokexp = Num(bs, "pokexp") ?? 0; }
            if (ok && lj.TryGetProperty("atrasados", out var at) && at.ValueKind == JsonValueKind.Object) _atrasadosAlvo = Num(at, "alvo") ?? 0;
        }
        if (est.TryGetProperty("guildBonusPct", out var gp)) _guildPct = gp.ValueKind == JsonValueKind.Number ? gp.GetDouble() : 0;
        if (Num(est, "guildBonusRank") is { } gr) _guildRank = gr;
        if (est.TryGetProperty("guild", out var gd)) _guildBoostAte = gd.ValueKind == JsonValueKind.Object ? Num(gd, "boostAte") ?? 0 : 0;
        if (est.TryGetProperty("twitch", out var tw)) _twitchXp = tw.ValueKind == JsonValueKind.Object && tw.TryGetProperty("bonus", out var tb) && tb.ValueKind == JsonValueKind.Object ? Num(tb, "xp") ?? 0 : 0;
        if (est.TryGetProperty("kick", out var kk)) _kickXp = kk.ValueKind == JsonValueKind.Object && kk.TryGetProperty("bonus", out var kb) && kb.ValueKind == JsonValueKind.Object ? Num(kb, "xp") ?? 0 : 0;
        if (est.TryGetProperty("evento", out var ev))
        {
            var ok = ev.ValueKind == JsonValueKind.Object;
            _eventoTermina = ok ? Num(ev, "terminaEm") ?? 0 : 0;
            _eventoPct = ok ? (ev.TryGetProperty("xpTreinadorPct", out var xt) && xt.ValueKind == JsonValueKind.Number ? xt.GetDouble() : ev.TryGetProperty("xpPokemonPct", out var xq) && xq.ValueKind == JsonValueKind.Number ? xq.GetDouble() : 0) : 0;
        }

        // O objeto "automation" chega INTEIRO quando muda (a remontagem do jogo troca a chave, não mescla).
        // O pedido "auto.set" do jogo manda tudo isso de volta com a mudança, então guardamos o objeto todo.
        if (est.TryGetProperty("automation", out var au) && au.ValueKind == JsonValueKind.Object)
        {
            _auto.Clear();
            _autoBruta.Clear();
            _ballIds = new();
            _potionIds = new();
            _reviveIds = new();
            _travados = new();
            foreach (var p in au.EnumerateObject())
            {
                _autoBruta[p.Name] = p.Value.Clone();
                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) _auto[p.Name] = p.Value.GetBoolean();
                else if (p.NameEquals("pokemonTravado") && p.Value.ValueKind == JsonValueKind.Array)
                    _travados = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (long)x.GetDouble()).ToHashSet();
                else if (p.NameEquals("ballIds") && p.Value.ValueKind == JsonValueKind.Array)
                    _ballIds = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble()).ToList();
                else if (p.NameEquals("potionIds") && p.Value.ValueKind == JsonValueKind.Array)
                    _potionIds = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble()).ToList();
                else if (p.NameEquals("reviveIds") && p.Value.ValueKind == JsonValueKind.Array)
                    _reviveIds = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble()).ToList();
            }
        }
        if (est.TryGetProperty("balls", out var bl) && bl.ValueKind == JsonValueKind.Object)
            foreach (var p in bl.EnumerateObject())
                if (int.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.Number) _balls[id] = (long)p.Value.GetDouble();
        if (est.TryGetProperty("items", out var it) && it.ValueKind == JsonValueKind.Object)
            foreach (var p in it.EnumerateObject())
                if (int.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.Number) _items[id] = (long)p.Value.GetDouble();

        var cheio = est.TryGetProperty("cheio", out var ch) && ch.ValueKind == JsonValueKind.True;
        if (cheio) { _pokemons.Clear(); _camposBrutos.Clear(); _pokBrutos.Clear(); }
        foreach (var prop in est.EnumerateObject())
        {
            if (FORA_DOS_CAMPOS.Contains(prop.Name)) continue;
            var raw = prop.Value.GetRawText();
            if (raw.Length <= MaxCampoBruto) _camposBrutos[prop.Name] = raw;
        }
        if (est.TryGetProperty("pokemons", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            _pokemons.Clear(); // uma lista completa SUBSTITUI a coleção
            _pokBrutos.Clear();
            foreach (var p in lista.EnumerateArray()) Guardar(p);
        }
        if (est.TryGetProperty("pkMud", out var mud) && mud.ValueKind == JsonValueKind.Array)
            foreach (var p in mud.EnumerateArray()) Guardar(p);
        if (est.TryGetProperty("pkFora", out var fora) && fora.ValueKind == JsonValueKind.Array)
            foreach (var id in fora.EnumerateArray())
                if (id.ValueKind == JsonValueKind.Number) { _pokemons.Remove((long)id.GetDouble()); _pokBrutos.Remove((long)id.GetDouble()); }
    }

    private void Guardar(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || Num(p, "id") is not { } id) return;
        _pokBrutos[id] = p.GetRawText();
        var especie = Str(p, "nome") ?? "?";
        var nome = Str(p, "apelido") is { Length: > 0 } ap ? ap : especie;
        double? qual = p.TryGetProperty("quality", out var qv) && qv.ValueKind == JsonValueKind.Number ? qv.GetDouble()
                     : p.TryGetProperty("qualidade", out qv) && qv.ValueKind == JsonValueKind.Number ? qv.GetDouble() : null;
        int? slot = p.TryGetProperty("slot", out var s) && s.ValueKind == JsonValueKind.Number ? (int)s.GetDouble() : null;
        _pokemons[id] = new PokemonInfo(id, nome, Num(p, "level") ?? 0, slot, Num(p, "hp"), Num(p, "maxHp"),
            p.TryGetProperty("shiny", out var sh) && sh.ValueKind == JsonValueKind.True,
            (int)(Num(p, "potencia") ?? 1), (int)(Num(p, "speciesId") ?? 0),
            p.TryGetProperty("starter", out var st) && st.ValueKind == JsonValueKind.True, especie, qual);
    }

    private static string? Str(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static long? Num(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (v.TryGetInt64(out var n) ? n : (long)v.GetDouble()) : null;
}
