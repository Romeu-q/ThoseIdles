using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ThoseIdles.Services;

/// <summary>Uma hunt do ranking de XP (os textos já como o painel "Onde caçar?" os mostra: "42,9 mi", "21,1 s", "✓ 24%").</summary>
public sealed record MelhorHunt(int Posicao, string Slug, string Nome, string Regiao, int Nivel, string XpH, string XpTreinadorH,
    string OuroH, string AbatesH, string MataEm, string Risco, string Golpe)
{
    public bool Outland => string.Equals(Regiao, "outland", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// "A melhor hunt de XP agora" para o Pokémon em campo — a MESMA conta do /hunt ("Onde caçar?") que roda dentro do jogo.
///
/// 1) Com a extensão /hunt instalada, o app roda o motor DELA (engine.js, o mesmo que desenha o painel no jogo) numa janela
///    escondida e sem login (perfil temporário, nada fica guardado): carrega os mesmos módulos do jogo, entrega a ele o
///    welcome, o estado e o pokémon em campo como o servidor mandou, e lê o ranking. Assim os números batem com os do painel
///    do jogo: abates/h por hunt (o ritmo de cada mapa), "mata em", risco, bônus de XP do servidor e a vantagem dos golpes.
/// 2) Sem a extensão, cai para a Calculadora de XP do PokéIdle Hub (conta mais simples: abates/h iguais em todas as hunts).
///
/// Só roda com a aba "Caçar" à vista, no máximo a cada 5 min (ou quando o Pokémon, o nível ou os bônus mudam); leva alguns
/// segundos e usa memória só enquanto calcula.
/// </summary>
public sealed partial class BrowserSession
{
    private const int RankingValidadeMin = 5;
    private const int RankingTopo = 5;

    private bool _rankingCalculando;
    private string? _rankingChave;
    private DateTime _rankingEm = DateTime.MinValue;
    private DateTime _rankingProximaTentativa = DateTime.MinValue;

    /// <summary>O ranking mais recente (o melhor primeiro); vazio enquanto não houver.</summary>
    public IReadOnlyList<MelhorHunt> MelhoresHunts { get; private set; } = Array.Empty<MelhorHunt>();

    /// <summary>O que o ranking mostra de estado: "" (pronto), "Calculando…" ou o motivo de não ter calculado.</summary>
    public string RankingStatus { get; private set; } = "";

    /// <summary>Para quem o último ranking foi calculado ("Charizard Nv 900") e de onde veio a conta.</summary>
    public string RankingPara { get; private set; } = "";
    public string RankingOrigem { get; private set; } = "";
    public DateTime? RankingQuando => _rankingEm == DateTime.MinValue ? null : _rankingEm.ToLocalTime();
    public bool RankingCalculando => _rankingCalculando;

    /// <summary>Onde o app procura o ranking pelo Hub (plano B); o autoteste pode trocar.</summary>
    internal static string RankingUrl { get; set; } = HubOrigin + "/#xp";
    internal static int RankingTimeoutSegundos { get; set; } = 45;

    /// <summary>O autoteste desliga o cálculo automático (o teste do ranking chama à mão).</summary>
    internal static bool RankingAutomatico { get; set; } = true;

    /// <summary>O autoteste aponta para a pasta real da extensão /hunt (ele não a instala).</summary>
    internal static string? RankingExtensaoPasta { get; set; }

    /// <summary>O autoteste força o plano B (Hub) ou o motor do /hunt.</summary>
    internal static bool RankingSoHub { get; set; }

    private string? PastaDaExtensaoHunt()
    {
        if (RankingSoHub) return null;
        var pasta = RankingExtensaoPasta ?? _hunt?.InstalledFolder;
        return pasta is not null && File.Exists(Path.Combine(pasta, "manifest.json")) && Directory.Exists(Path.Combine(pasta, "src")) ? pasta : null;
    }

    private string ChaveDoRanking(AccountGameState g) =>
        $"{g.ChaveDoMotor()}|{JsonSerializer.Serialize(g.EscolhasXp())}|{(PastaDaExtensaoHunt() is null ? "hub" : "hunt")}";

    /// <summary>Precisa calcular (ou recalcular) agora?</summary>
    public bool PrecisaDoRanking()
    {
        var g = Game;
        if (!RankingAutomatico || _rankingCalculando || g.Ativo is null || g.Level <= 0 || DateTime.UtcNow < _rankingProximaTentativa) return false;
        if (MelhoresHunts.Count == 0 && _rankingEm == DateTime.MinValue) return true;
        return ChaveDoRanking(g) != _rankingChave || DateTime.UtcNow - _rankingEm > TimeSpan.FromMinutes(RankingValidadeMin);
    }

    /// <summary>A "ficha" do Pokémon em campo no formato que o Hub lê (espécie, nível, Potência, Qualidade) — plano B.</summary>
    internal static string FichaParaHub(PokemonInfo p)
    {
        var l = new List<string>
        {
            "Ficha do PokeIdle (ThoseIdles)",
            $"- Espécie: {p.Especie ?? p.Nome}",
            $"- Shiny: {(p.Shiny ? "sim" : "não")}",
            $"- Nível: {p.Level}",
        };
        if (p.Potencia is >= 1 and <= 5) l.Add($"- Potência: P{p.Potencia}");
        if (p.Qualidade is { } q and > 0) l.Add($"- Qualidade: {(q > 10 ? q / 1000 : q).ToString("0.000", System.Globalization.CultureInfo.InvariantCulture)}");
        return string.Join("\n", l);
    }

    /// <summary>Calcula o ranking agora (um por vez). Atualiza <see cref="MelhoresHunts"/> e <see cref="RankingStatus"/>.</summary>
    public async Task AtualizarRankingAsync(bool forcar = false)
    {
        var g = Game;
        if (_rankingCalculando) return;
        if (g.Ativo is not { } ativo || g.Level <= 0) { RankingStatus = "Sem Pokémon em campo para calcular."; OnPropertyChanged(nameof(RankingStatus)); return; }
        if (!forcar && !PrecisaDoRanking()) return;

        _rankingCalculando = true;
        var chave = ChaveDoRanking(g);
        RankingStatus = "Calculando…";
        OnPropertyChanged(nameof(RankingStatus));
        Window? win = null;
        WebView2? wv = null;
        try
        {
            var env = await _profiles.GetEnvironmentAsync();
            wv = new WebView2();
            win = new Window
            {
                Width = 1100, Height = 900, Left = -20000, Top = -20000, WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false, ShowActivated = false, Content = wv,
            };
            win.Show();
            CoreWebView2ControllerOptions opts;
            try { opts = env.CreateCoreWebView2ControllerOptions(); opts.IsInPrivateModeEnabled = true; opts.ScriptLocale = "pt-BR"; }
            catch { opts = _profiles.CreateControllerOptions(env, Slot); }
            await wv.EnsureCoreWebView2Async(env, opts);
            var core = wv.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            core.IsMuted = true;
            core.NewWindowRequested += (_, ev) => ev.Handled = true;
            core.PermissionRequested += (_, ev) => ev.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, ev) => ev.Handled = true;

            var pasta = PastaDaExtensaoHunt();
            var entrada = pasta is not null ? g.EntradaDoMotor() : null;
            (List<MelhorHunt>? lista, string erro, string origem) r;
            if (pasta is not null && entrada is not null) r = await CalcularPeloMotorHuntAsync(core, pasta, entrada);
            else r = await CalcularPeloHubAsync(core, g, ativo);

            if (r.lista is { Count: > 0 })
            {
                MelhoresHunts = r.lista;
                _rankingChave = chave;
                _rankingEm = DateTime.UtcNow;
                RankingPara = $"{ativo.Especie ?? ativo.Nome} Nv {ativo.Level:N0}";
                RankingOrigem = r.origem;
                RankingStatus = "";
                _rankingProximaTentativa = DateTime.MinValue;
                LoggingService.Log.Info($"[conta{Slot}] Melhor hunt de XP para {RankingPara} ({r.origem}): {r.lista[0].Nome} ({r.lista[0].XpH} XP/h).");
            }
            else
            {
                RankingStatus = r.erro.Length > 0 ? r.erro : "Não deu para calcular agora (o motor não respondeu a tempo).";
                _rankingProximaTentativa = DateTime.UtcNow.AddSeconds(60);
            }
        }
        catch (Exception ex)
        {
            RankingStatus = $"Não deu para calcular agora: {ex.Message}";
            _rankingProximaTentativa = DateTime.UtcNow.AddSeconds(60);
            LoggingService.Log.Warn($"[conta{Slot}] Ranking de hunts falhou: {ex.Message}");
        }
        finally
        {
            try { win?.Close(); } catch { }
            try { wv?.Dispose(); } catch { }
            _rankingCalculando = false;
            OnPropertyChanged(nameof(MelhoresHunts));
            OnPropertyChanged(nameof(RankingStatus));
        }
    }

    // ------------------------------------------------------------------ 1) o motor do /hunt

    private Uri OrigemDoJogoParaOMotor()
    {
        var o = new Uri(_settings.StartUrl);
        return o.IsLoopback ? new Uri("https://pokeidle.io") : new Uri(o.GetLeftPart(UriPartial.Authority));
    }

    private async Task<(List<MelhorHunt>? lista, string erro, string origem)> CalcularPeloMotorHuntAsync(CoreWebView2 core, string pasta, string entrada)
    {
        var origem = OrigemDoJogoParaOMotor();
        core.NavigationStarting += (_, ev) =>
        {
            if (!Uri.TryCreate(ev.Uri, UriKind.Absolute, out var u) || !(string.Equals(u.Host, origem.Host, StringComparison.OrdinalIgnoreCase) || ev.Uri == "about:blank"))
                ev.Cancel = true;
        };

        // Os scripts da extensão, na ORDEM do manifesto (menos a tela, ui.js): rodam antes da página, como na loja.
        var versao = Path.GetFileName(pasta);
        var scripts = new List<string>();
        using (var man = JsonDocument.Parse(File.ReadAllText(Path.Combine(pasta, "manifest.json"))))
        {
            foreach (var cs in man.RootElement.GetProperty("content_scripts").EnumerateArray())
            {
                if (!cs.TryGetProperty("js", out var js)) continue;
                foreach (var f in js.EnumerateArray())
                {
                    var rel = f.GetString() ?? "";
                    var nome = Path.GetFileName(rel);
                    if (nome.Equals("ui.js", StringComparison.OrdinalIgnoreCase) || nome.Contains("wishlist", StringComparison.OrdinalIgnoreCase)) continue;
                    var caminho = Path.GetFullPath(Path.Combine(pasta, rel.Replace('/', Path.DirectorySeparatorChar)));
                    if (!caminho.StartsWith(Path.GetFullPath(pasta), StringComparison.OrdinalIgnoreCase) || !File.Exists(caminho)) continue;
                    scripts.Add(caminho);
                }
            }
        }
        foreach (var s in scripts) await core.AddScriptToExecuteOnDocumentCreatedAsync(File.ReadAllText(s));

        var carregou = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        core.NavigationCompleted += (_, ev) => carregou.TrySetResult(ev.IsSuccess || ev.HttpStatusCode is > 0);
        core.Navigate(new Uri(origem, "/robots.txt").ToString());
        if (await Task.WhenAny(carregou.Task, Task.Delay(TimeSpan.FromSeconds(20))) != carregou.Task)
            return (null, "O jogo não respondeu a tempo para o cálculo.", "");

        var script = $$"""
            (() => {
              window.__tiRes = null;
              (async () => {
                try {
                  const d = {{entrada}};
                  const PIH = globalThis.__pokeidleHunt;
                  if (!PIH?.engine) throw new Error('o motor do /hunt não carregou');
                  const jogo = await PIH.carregarJogo();
                  const w = d.welcome, eu = d.eu, pk = d.pk;
                  const ctx = {
                    mod: jogo.mod, especies: jogo.especies, hunts: w.hunts ?? [],
                    consts: { tabelaTipos: w.tabelaTipos, ampliacaoHunt: w.ampliacaoHunt, multHpSelvagem: w.multHpSelvagem, multDanoSelvagem: w.multDanoSelvagem,
                      tiposBoost: w.tiposBoost, catalogoBolas: w.catalogoBolas, captura: w.captura, chanceShiny: w.chanceShiny, shinyLooks: w.shinyLooks },
                    eu, ritmo: PIH.ritmo ?? null,
                    mapaVisto: (s) => PIH.geo?.mapaDa?.(s) ?? null, ritmoMedido: () => null,
                  };
                  const TIPOS = ['NORMAL','FIRE','WATER','GRASS','ELECTRIC','ICE','FIGHTING','POISON','GROUND','FLYING','PSYCHIC','BUG','ROCK','GHOST','DRAGON','DARK','STEEL','FAIRY'];
                  const dia = TIPOS.includes(eu?.diaSazonal?.tipo) ? eu.diaSazonal.tipo : null;
                  const r = PIH.engine.analisar(pk, ctx, { ordem: 'xp', soLiberadas: true, tipoDoDia: dia });
                  // A formatação é a do painel do /hunt (ui.js): compacto em pt-BR, segundos, risco.
                  const nf = new Intl.NumberFormat('pt-BR'), nfc = new Intl.NumberFormat('pt-BR', { notation: 'compact', maximumFractionDigits: 1 });
                  const num = (v) => nf.format(Math.round(v));
                  const curto = (v) => (Math.abs(v) >= 10000 ? nfc.format(v) : num(v));
                  const seg = (s) => (s < 1 ? '<1 s' : s < 60 ? `${s.toFixed(1).replace('.', ',')} s` : `${Math.floor(s / 60)} min ${Math.round(s % 60)} s`);
                  const risco = (l) => l.umHit ? '☠ 1 hit' : l.mortesH >= 0.5 ? `☠ ${l.mortesH.toFixed(1).replace('.', ',')}/h`
                    : `${Math.round(l.hitPct * 100) >= 50 ? '⚠' : '✓'} ${Math.round(l.hitPct * 100)}%`;
                  // As hunts da Outland (a área "outland" do jogo): a melhor delas vai junto, mesmo fora do top.
                  const daOutland = new Set((w.hunts ?? []).filter(h => String(h.area ?? h.regiao ?? '').toLowerCase() === 'outland').map(h => h.slug));
                  const viaveis = r.linhas.filter(l => !l.inviavel);
                  const escolhidas = viaveis.slice(0, 8);
                  const melhorOut = viaveis.find(l => daOutland.has(l.slug) || String(l.regiao ?? '').toLowerCase() === 'outland');
                  if (melhorOut && !escolhidas.includes(melhorOut)) escolhidas.push(melhorOut);
                  const linhas = escolhidas.map((l) => ({
                    pos: viaveis.indexOf(l) + 1,
                    slug: l.slug, nome: l.nome, regiao: daOutland.has(l.slug) ? 'outland' : (l.regiao ?? ''), nivel: l.nivel,
                    xp: curto(l.xpPokemonH), xpTr: curto(l.xpTreinadorH), ouro: curto(l.ouroH), kph: num(l.kph), ttk: seg(l.ttk), risco: risco(l),
                    golpe: l.melhorGolpe ? `${l.melhorGolpe.nome} ×${l.efetividade.toFixed(2).replace('.', ',')}` : '',
                  }));
                  window.__tiRes = { ok: true, linhas, total: r.total, ms: r.ms };
                } catch (e) { window.__tiRes = { ok: false, erro: String(e?.message ?? e) }; }
              })();
              return true;
            })()
            """;
        await core.ExecuteScriptAsync(script);

        var limite = DateTime.UtcNow.AddSeconds(RankingTimeoutSegundos);
        while (DateTime.UtcNow < limite)
        {
            await Task.Delay(300);
            string raw;
            try { raw = await core.ExecuteScriptAsync("JSON.stringify(window.__tiRes)"); } catch { continue; }
            if (raw is null or "null" or "\"null\"") continue;
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw) ?? "null");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) continue;
            if (!(root.TryGetProperty("ok", out var ok) && ok.GetBoolean()))
                return (null, $"O motor do /hunt não calculou: {(root.TryGetProperty("erro", out var e) ? e.GetString() : "erro")}", "");
            var lista = new List<MelhorHunt>();
            var pos = 0;
            foreach (var l in root.GetProperty("linhas").EnumerateArray())
            {
                pos++;
                string S(string k) => l.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
                var posReal = l.TryGetProperty("pos", out var pv) && pv.TryGetInt32(out var pr) ? pr : pos;
                var h = new MelhorHunt(posReal, S("slug"), S("nome"), S("regiao"), l.TryGetProperty("nivel", out var nv) && nv.TryGetInt32(out var n) ? n : 0,
                    S("xp"), S("xpTr"), S("ouro"), S("kph"), S("ttk"), S("risco"), S("golpe"));
                // O top de XP e, além dele, a melhor da Outland (se ela não entrou no top).
                if (lista.Count < RankingTopo) lista.Add(h);
                else if (h.Outland && !lista.Any(x => x.Outland)) { lista.Add(h); break; }
            }
            return (lista, lista.Count == 0 ? "O motor do /hunt não achou hunt liberada para este Pokémon." : "", $"motor do /hunt v{versao}");
        }
        return (null, "O motor do /hunt não respondeu a tempo.", "");
    }

    // ------------------------------------------------------------------ 2) plano B: a Calculadora do Hub

    private async Task<(List<MelhorHunt>? lista, string erro, string origem)> CalcularPeloHubAsync(CoreWebView2 core, AccountGameState g, PokemonInfo ativo)
    {
        core.NavigationStarting += (_, ev) => { if (!IsHubUrl(ev.Uri) && !(_policy.AllowLocalhostHttp && Uri.TryCreate(ev.Uri, UriKind.Absolute, out var u) && u.IsLoopback)) ev.Cancel = true; };

        // As abates/h que a conta de fato faz (depois de uns minutos na hunt) valem mais que a cadência padrão do Hub.
        int? kph = null;
        var st = Stats;
        var horas = (DateTime.UtcNow - st.HuntDesde).TotalHours;
        if (horas >= 3 / 60d && st.AbatesHunt >= 60) kph = (int)Math.Round(st.AbatesHunt / horas);

        var seed = JsonSerializer.Serialize(new { nivelTreinador = g.Level, nivelPokemon = ativo.Level, escolhas = g.EscolhasXp(), ficha = FichaParaHub(ativo), kph });
        // O estado da Calculadora entra ANTES de a página carregar: ela lê o localStorage ao montar.
        await core.AddScriptToExecuteOnDocumentCreatedAsync($$"""
            (() => {
              try {
                const d = {{seed}};
                const s = { nivelTreinador: d.nivelTreinador, speciesId: null, nivelPokemon: d.nivelPokemon,
                  cadencia: d.kph ? 'personalizado' : 'padrao', kphPersonalizado: d.kph ?? null, metaEscolhida: null,
                  escolhas: d.escolhas, aba: 'fixa', huntFixa: null, fichaAberta: true, fichaTexto: d.ficha,
                  tutorialVisto: true, tutorialFichaVisto: true };
                localStorage.setItem('pokeidle-painel.calculadora-xp.v2', JSON.stringify(s));
                window.__tiFicha = d.ficha;
              } catch {}
            })();
            """);
        core.Navigate(RankingUrl);

        const string ler = """
            (() => {
              const campo = document.querySelector('#xpc-ficha');
              if (campo && window.__tiFicha && !window.__tiColou) {
                window.__tiColou = true;
                campo.value = window.__tiFicha;
                campo.dispatchEvent(new Event('input', { bubbles: true }));
              }
              const erro = document.querySelector('.xpc-ficha-retorno.erro')?.innerText ?? '';
              const linhas = [...document.querySelectorAll('.xpc-tabela tr.xpc-linha')].slice(0, 6).map(tr => ({
                slug: tr.dataset.slug || '',
                nome: tr.querySelector('.xpc-hunt-texto b')?.textContent ?? '',
                det: (tr.querySelector('.xpc-hunt-texto small')?.innerText ?? '').replace(/\s+/g, ' ').trim(),
                c: [...tr.querySelectorAll('td')].map(td => (td.innerText ?? '').replace(/\s+/g, ' ').trim()),
              }));
              return JSON.stringify({ erro, linhas });
            })()
            """;
        var limite = DateTime.UtcNow.AddSeconds(RankingTimeoutSegundos);
        string erro = "";
        while (DateTime.UtcNow < limite)
        {
            await Task.Delay(400);
            string raw;
            try { raw = await core.ExecuteScriptAsync(ler); } catch { continue; }
            if (raw is null or "null") continue;
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw) ?? "{}");
            var root = doc.RootElement;
            erro = root.TryGetProperty("erro", out var e) ? e.GetString() ?? "" : "";
            if (erro.Length > 0) return (null, $"O Hub não leu o Pokémon: {erro}", "");
            if (root.TryGetProperty("linhas", out var ls) && ls.GetArrayLength() > 0)
            {
                var lista = new List<MelhorHunt>();
                var pos = 0;
                foreach (var l in ls.EnumerateArray())
                {
                    pos++;
                    var c = l.GetProperty("c").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                    string C(int i) => i < c.Count ? c[i] : "";
                    var det = l.GetProperty("det").GetString() ?? "";
                    var nvTxt = System.Text.RegularExpressions.Regex.Match(det, @"Nv\s*([\d\.]+)");
                    var nivel = nvTxt.Success && int.TryParse(nvTxt.Groups[1].Value.Replace(".", ""), out var nv) ? nv : 0;
                    lista.Add(new MelhorHunt(pos, l.GetProperty("slug").GetString() ?? "", l.GetProperty("nome").GetString() ?? "",
                        det.Contains("Outland", StringComparison.OrdinalIgnoreCase) ? "outland" : "", nivel, C(3), C(2), C(4), "—", "—", C(5), ""));
                    if (lista.Count >= RankingTopo) break;
                }
                return (lista, "", "Calculadora do Hub (aproximado; instale a extensão /hunt para a conta exata)");
            }
        }
        return (null, "A Calculadora do Hub não respondeu a tempo.", "");
    }
}
