using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ThoseIdles.Services;

/// <summary>Uma hunt do ranking de XP (os textos já como o Hub os mostra: "4,5M", "×2"…).</summary>
public sealed record MelhorHunt(int Posicao, string Slug, string Nome, string Detalhe, string XpH, string XpPokemonH, string OuroH, string Vantagem);

/// <summary>
/// "A melhor hunt de XP agora" para o Pokémon em campo. O cálculo é o da Calculadora de XP do PokéIdle Hub
/// (a mesma conta do jogo: XP por abate × abates/h × bônus ligados, e a vantagem elemental dos golpes do
/// Pokémon): o app abre o Hub numa janela escondida e SEM LOGIN (perfil temporário, que não guarda nada),
/// preenche nível, Pokémon e bônus da conta, lê a tabela "Hunt fixa" (o ranking de agora) e fecha tudo.
/// Só roda quando a aba "Caçar" está à vista, no máximo a cada 5 min (ou quando o Pokémon ou os bônus mudam);
/// leva alguns segundos e usa memória só enquanto calcula.
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

    /// <summary>Para quem o último ranking foi calculado ("Charizard Nv 900") e quando.</summary>
    public string RankingPara { get; private set; } = "";
    public DateTime? RankingQuando => _rankingEm == DateTime.MinValue ? null : _rankingEm.ToLocalTime();
    public bool RankingCalculando => _rankingCalculando;

    /// <summary>Onde o app procura o ranking (a página do Hub); o autoteste troca por uma página local.</summary>
    internal static string RankingUrl { get; set; } = HubOrigin + "/#xp";
    internal static int RankingTimeoutSegundos { get; set; } = 40;

    /// <summary>O autoteste desliga o cálculo automático (o teste do ranking chama à mão).</summary>
    internal static bool RankingAutomatico { get; set; } = true;

    private string ChaveDoRanking(AccountGameState g)
    {
        var at = g.Ativo;
        // Muda quando troca o Pokémon em campo ou algum bônus liga/desliga (os níveis só entram na renovação de 5 min).
        return $"{at?.Id}|{at?.Especie}|{JsonSerializer.Serialize(g.EscolhasXp())}";
    }

    /// <summary>Precisa calcular (ou recalcular) agora?</summary>
    public bool PrecisaDoRanking()
    {
        var g = Game;
        if (!RankingAutomatico || _rankingCalculando || g.Ativo is null || g.Level <= 0 || DateTime.UtcNow < _rankingProximaTentativa) return false;
        if (MelhoresHunts.Count == 0 && _rankingEm == DateTime.MinValue) return true;
        return ChaveDoRanking(g) != _rankingChave || DateTime.UtcNow - _rankingEm > TimeSpan.FromMinutes(RankingValidadeMin);
    }

    /// <summary>A "ficha" do Pokémon em campo no formato que o Hub lê (espécie, nível, Potência, Qualidade).</summary>
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
        RankingStatus = "Calculando com o motor do PokéIdle Hub…";
        OnPropertyChanged(nameof(RankingStatus));
        Window? win = null;
        WebView2? wv = null;
        try
        {
            // As abates/h que a conta de fato faz (depois de uns minutos na hunt) valem mais que a cadência padrão do Hub.
            int? kph = null;
            var st = Stats;
            var horas = (DateTime.UtcNow - st.HuntDesde).TotalHours;
            if (horas >= 3 / 60d && st.AbatesHunt >= 60) kph = (int)Math.Round(st.AbatesHunt / horas);

            var seed = JsonSerializer.Serialize(new
            {
                nivelTreinador = g.Level,
                nivelPokemon = ativo.Level,
                escolhas = g.EscolhasXp(),
                ficha = FichaParaHub(ativo),
                kph,
            });

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
            core.NavigationStarting += (_, ev) => { if (!IsHubUrl(ev.Uri) && !(_policy.AllowLocalhostHttp && Uri.TryCreate(ev.Uri, UriKind.Absolute, out var u) && u.IsLoopback)) ev.Cancel = true; };
            core.NewWindowRequested += (_, ev) => ev.Handled = true;
            core.PermissionRequested += (_, ev) => ev.State = CoreWebView2PermissionState.Deny;
            core.DownloadStarting += (_, ev) => ev.Handled = true;

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
            List<MelhorHunt>? lista = null;
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
                if (erro.Length > 0) break;
                if (root.TryGetProperty("linhas", out var ls) && ls.GetArrayLength() > 0)
                {
                    lista = new();
                    var pos = 0;
                    foreach (var l in ls.EnumerateArray())
                    {
                        pos++;
                        var c = l.GetProperty("c").EnumerateArray().Select(x => x.GetString() ?? "").ToList();
                        string C(int i) => i < c.Count ? c[i] : "";
                        lista.Add(new MelhorHunt(pos, l.GetProperty("slug").GetString() ?? "", l.GetProperty("nome").GetString() ?? "",
                            l.GetProperty("det").GetString() ?? "", C(2), C(3), C(4), C(5)));
                        if (lista.Count >= RankingTopo) break;
                    }
                    break;
                }
            }

            if (lista is { Count: > 0 })
            {
                MelhoresHunts = lista;
                _rankingChave = chave;
                _rankingEm = DateTime.UtcNow;
                RankingPara = $"{ativo.Especie ?? ativo.Nome} Nv {ativo.Level:N0}";
                RankingStatus = "";
                _rankingProximaTentativa = DateTime.MinValue;
                LoggingService.Log.Info($"[conta{Slot}] Melhor hunt de XP para {RankingPara}: {lista[0].Nome} ({lista[0].XpH} XP/h).");
            }
            else
            {
                RankingStatus = erro.Length > 0 ? $"O Hub não leu o Pokémon: {erro}" : "Não deu para calcular agora (o Hub não respondeu a tempo).";
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
}
