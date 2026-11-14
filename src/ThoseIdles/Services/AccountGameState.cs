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
public sealed record BallInfo(int Id, string Nome, long? Preco);

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
                }
            }
        }
    }

    public string? Nick { get; private set; }
    public long Level { get; private set; }
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
    private readonly Dictionary<int, long> _balls = new();
    private List<HuntInfo> _hunts = new();
    private List<BallInfo> _catalogoBolas = new();
    private readonly Dictionary<long, PokemonInfo> _pokemons = new();

    /// <summary>Mudou alguma coisa (disparado na thread de quem leu a mensagem; a tela volta à UI).</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<HuntInfo> Hunts { get { lock (_lock) return _hunts.ToList(); } }
    public IReadOnlyList<BallInfo> CatalogoBolas { get { lock (_lock) return _catalogoBolas.ToList(); } }
    public IReadOnlyList<PokemonInfo> Pokemons { get { lock (_lock) return _pokemons.Values.ToList(); } }
    public IReadOnlyList<int> BallIds { get { lock (_lock) return _ballIds.ToList(); } }
    public long Balls(int id) { lock (_lock) return _balls.GetValueOrDefault(id); }
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
            if (root.TryGetProperty("hunts", out var hs) && hs.ValueKind == JsonValueKind.Array) LerHunts(hs);
            if (root.TryGetProperty("catalogoBolas", out var cb) && cb.ValueKind == JsonValueKind.Array) LerCatalogoBolas(cb);
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
            lista.Add(new BallInfo((int)id, Str(b, "nome") ?? $"Bola #{id}", Num(b, "preco") ?? Num(b, "custo") ?? Num(b, "price")));
        }
        if (lista.Count > 0) _catalogoBolas = lista.OrderBy(b => b.Id).ToList();
    }

    private void LerEstado(JsonElement est)
    {
        if (Str(est, "nick") is { } nick) Nick = nick;
        if (Num(est, "level") is { } lv) Level = lv;
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
            _travados = new();
            foreach (var p in au.EnumerateObject())
            {
                _autoBruta[p.Name] = p.Value.Clone();
                if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) _auto[p.Name] = p.Value.GetBoolean();
                else if (p.NameEquals("pokemonTravado") && p.Value.ValueKind == JsonValueKind.Array)
                    _travados = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (long)x.GetDouble()).ToHashSet();
                else if (p.NameEquals("ballIds") && p.Value.ValueKind == JsonValueKind.Array)
                    _ballIds = p.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Number).Select(x => (int)x.GetDouble()).ToList();
            }
        }
        if (est.TryGetProperty("balls", out var bl) && bl.ValueKind == JsonValueKind.Object)
            foreach (var p in bl.EnumerateObject())
                if (int.TryParse(p.Name, out var id) && p.Value.ValueKind == JsonValueKind.Number) _balls[id] = (long)p.Value.GetDouble();

        var cheio = est.TryGetProperty("cheio", out var ch) && ch.ValueKind == JsonValueKind.True;
        if (cheio) _pokemons.Clear();
        if (est.TryGetProperty("pokemons", out var lista) && lista.ValueKind == JsonValueKind.Array)
        {
            _pokemons.Clear(); // uma lista completa SUBSTITUI a coleção
            foreach (var p in lista.EnumerateArray()) Guardar(p);
        }
        if (est.TryGetProperty("pkMud", out var mud) && mud.ValueKind == JsonValueKind.Array)
            foreach (var p in mud.EnumerateArray()) Guardar(p);
        if (est.TryGetProperty("pkFora", out var fora) && fora.ValueKind == JsonValueKind.Array)
            foreach (var id in fora.EnumerateArray())
                if (id.ValueKind == JsonValueKind.Number) _pokemons.Remove((long)id.GetDouble());
    }

    private void Guardar(JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || Num(p, "id") is not { } id) return;
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
