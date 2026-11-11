using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ThoseIdles.Services;

/// <summary>
/// PokéIdle Hub (https://pokeidleguide.io): Pokédex, Calculadora de XP, Vale a pena?, Guias…
/// Os botões que o script do jogo põe na ficha do Pokémon pedem (postMessage) para abrir o Hub numa
/// janela do app, no perfil desta conta, na página certa e já com a ficha colada no campo.
/// A página do jogo só pode pedir ISSO: uma das rotas conhecidas, com um texto curto.
/// </summary>
public sealed partial class BrowserSession
{
    public const string HubOrigin = "https://pokeidleguide.io";
    private static readonly string[] HubRoutes = { "pokedex", "xp", "hunt", "vale-a-pena", "mercado", "guias", "wishlist" };
    private const int HubMaxFicha = 6000;

    private Window? _hubWindow;
    private WebView2? _hubView;
    private string? _hubPending; // script de preencher, à espera do carregamento da página

    /// <summary>A janela do Hub desta conta (o autoteste confere).</summary>
    public Window? HubWindow => _hubWindow;
    public CoreWebView2? HubCore => _hubView?.CoreWebView2;

    public static bool IsHubUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
        && (string.Equals(u.Host, new Uri(HubOrigin).Host, StringComparison.OrdinalIgnoreCase)
            // O endereço antigo (até 11/2026): se ainda redirecionar para o novo, a janela acompanha.
            || string.Equals(u.Host, "pokeidle-gg.pages.dev", StringComparison.OrdinalIgnoreCase));

    private static readonly Lazy<string> HubScript = new(() => ReadResource("PokeIdleHub.js"));
    private static readonly Lazy<string> HubSiteScript = new(() => ReadResource("PokeIdleHubSite.js"));
    private static readonly System.Text.RegularExpressions.Regex SlugValido = new("^[a-z0-9][a-z0-9_-]{0,63}$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Da página do Hub: só { ti: 'cacar', slug } (o botão "▶ Caçar" da Calculadora de XP).</summary>
    private void OnHubWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (!IsHubUrl(e.Source)) return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "ti") != "cacar" || Str(r, "slug") is not { } slug || !SlugValido.IsMatch(slug)) return;
            _ = IrCacarAsync(slug);
        }
        catch { }
    }

    /// <summary>
    /// Leva a conta para a hunt: pela página do jogo (o script dela manda "hunt.select", como o clique
    /// no mapa) ou, no modo econômico, pelo cliente leve. A resposta aparece na janela do Hub.
    /// </summary>
    public async Task IrCacarAsync(string slug)
    {
        LoggingService.Log.Info($"[conta{Slot}] PokéIdle Hub: ir caçar em {slug}.");
        if (_eco is { } eco)
        {
            var (ok, msg) = await eco.IrCacarAsync(slug);
            AvisarHub(ok, msg);
            return;
        }
        if (_core is null) { AvisarHub(false, "A conta está fechada. Abra a conta para ir caçar."); return; }
        // A página responde com { ti: 'cacou' } (OnGameWebMessage → AvisarHub).
        var tem = await _core.ExecuteScriptAsync($"(window.__tiHub && typeof window.__tiHub.cacar === 'function') ? (window.__tiHub.cacar({JsonSerializer.Serialize(slug)}), true) : false");
        if (tem != "true") AvisarHub(false, "O jogo desta conta ainda não carregou.");
    }

    private void AvisarHub(bool ok, string msg)
    {
        LoggingService.Log.Info($"[conta{Slot}] Ir caçar: {(ok ? "ok" : "não")} — {msg}");
        if (HubCore is not { } core || !IsHubUrl(core.Source)) return;
        if (msg.Length > 300) msg = msg[..300];
        _ = core.ExecuteScriptAsync($"window.__tiCacou && window.__tiCacou({(ok ? "true" : "false")}, {JsonSerializer.Serialize(msg)})");
    }

    private async Task EnableHubButtonsAsync(CoreWebView2 core)
    {
        if (HubScript.Value is not { Length: > 0 } script) return;
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += OnGameWebMessage;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    private void OnGameWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Só da página do jogo (a mesma origem do StartUrl), e só a mensagem esperada.
            var origem = new Uri(_settings.StartUrl);
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var src) || !string.Equals(src.Host, origem.Host, StringComparison.OrdinalIgnoreCase)) return;
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return;
            // Resposta do "ir caçar": vai para a janela do Hub, como aviso.
            if (Str(r, "ti") == "cacou")
            {
                AvisarHub(r.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True, Str(r, "msg") ?? "");
                return;
            }
            if (Str(r, "ti") != "hub") return;
            var rota = Str(r, "rota");
            if (rota is null || !HubRoutes.Contains(rota)) return;
            var ficha = Str(r, "ficha");
            var busca = Str(r, "busca");
            if (ficha is { Length: > HubMaxFicha } || busca is { Length: > 80 }) return;
            var alvo = Str(r, "alvo") == "candidato" ? "candidato" : "atual";
            var xp = rota == "xp" && r.TryGetProperty("xp", out var x) ? DadosDeXp(x) : null;
            _ = OpenHubAsync(rota, ficha, busca, alvo, xp);
        }
        catch { }
    }

    private static string? Str(JsonElement o, string p) => o.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static readonly string[] EscolhasBool = { "vip", "xpBoost", "xpBoostPokemon", "boostGuild", "twitch", "kick", "atrasados" };

    /// <summary>
    /// Os dados da conta para a Calculadora de XP, refeitos campo a campo (nada além do que a
    /// Calculadora guarda): nível do treinador e as escolhas de bônus. JSON pronto, ou null.
    /// </summary>
    internal static string? DadosDeXp(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("nivelTreinador", out var nv) || !nv.TryGetInt32(out var nivel) || nivel is < 1 or > 1_000_000)
            return null;
        var escolhas = new Dictionary<string, object>();
        if (x.TryGetProperty("escolhas", out var es) && es.ValueKind == JsonValueKind.Object)
        {
            foreach (var k in EscolhasBool)
                if (es.TryGetProperty(k, out var b) && b.ValueKind is JsonValueKind.True or JsonValueKind.False) escolhas[k] = b.GetBoolean();
            if (es.TryGetProperty("guildPosicao", out var gp) && gp.TryGetInt32(out var g)) escolhas["guildPosicao"] = Math.Clamp(g, 0, 10);
            if (es.TryGetProperty("eventoPct", out var ep) && ep.TryGetDouble(out var ev)) escolhas["eventoPct"] = Math.Clamp(ev, 0, 10_000);
            if (es.TryGetProperty("atrasadosAlvo", out var aa) && aa.TryGetInt32(out var a) && a > 0) escolhas["atrasadosAlvo"] = a;
        }
        return JsonSerializer.Serialize(new { nivelTreinador = nivel, escolhas });
    }

    /// <summary>
    /// Script que roda NO HUB: vai para a rota e, quando o campo aparecer, cola a ficha (ou a busca)
    /// e avisa a página (evento "input"), como se você tivesse colado.
    /// </summary>
    internal static string HubFillScript(string rota, string? ficha, string? busca, string alvo, string? xp = null)
    {
        var dados = JsonSerializer.Serialize(new { rota, ficha = ficha ?? "", busca = busca ?? "", alvo });
        return $$"""
            (async (d, xp) => {
              if (xp && d.rota === 'xp') {
                // A Calculadora guarda o que foi preenchido no navegador: nível e bônus entram ali, e a
                // página é redesenhada para ler (o resto do que você escolheu, como a cadência, fica).
                const CHAVE = 'pokeidle-painel.calculadora-xp.v2';
                let s = {};
                try { s = JSON.parse(localStorage.getItem(CHAVE) || '{}') || {}; } catch { }
                s.nivelTreinador = xp.nivelTreinador;
                s.escolhas = { ...(s.escolhas || {}), ...xp.escolhas };
                s.metaEscolhida = null;
                s.tutorialVisto = true;
                if (d.ficha) { s.fichaAberta = true; s.tutorialFichaVisto = true; }
                try { localStorage.setItem(CHAVE, JSON.stringify(s)); } catch { }
                location.hash = location.hash === '#xp/ti' ? 'xp' : 'xp/ti';
                await new Promise(r => setTimeout(r, 50));
              } else if (location.hash.slice(1).split('/')[0] !== d.rota) location.hash = d.rota;
              if (!d.ficha && !d.busca) return 'rota';
              const esperar = async (sel) => {
                for (let i = 0; i < 150; i++) {
                  const el = document.querySelector(sel);
                  if (el) return el;
                  await new Promise(r => setTimeout(r, 100));
                }
                return null;
              };
              const por = (el, v) => {
                el.value = v;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
              };
              let campo = null;
              if (d.rota === 'pokedex' && d.busca) campo = await esperar('#dex-search');
              else if (d.rota === 'xp' && d.ficha) {
                campo = await esperar('#xpc-ficha');
                const det = campo?.closest('details');
                if (det) det.open = true;
              } else if (d.rota === 'vale-a-pena' && d.ficha) {
                if (d.alvo === 'candidato') {
                  (await esperar('[data-modo="ficha"]'))?.click();
                  campo = await esperar('#vp-ficha-cand');
                } else campo = await esperar('#vp-ficha-atual');
              }
              if (!campo) return 'sem campo';
              por(campo, d.rota === 'pokedex' ? d.busca : d.ficha);
              campo.scrollIntoView({ block: 'center' });
              return 'preenchido';
            })({{dados}}, {{xp ?? "null"}})
            """;
    }

    /// <summary>Abre (ou traz para a frente) a janela do Hub desta conta na rota pedida.</summary>
    public async Task OpenHubAsync(string rota, string? ficha = null, string? busca = null, string alvo = "atual", string? xp = null)
    {
        var fill = HubFillScript(rota, ficha, busca, alvo, xp);
        try
        {
            if (_hubWindow is not null && _hubView?.CoreWebView2 is { } atual)
            {
                if (_hubWindow.WindowState == WindowState.Minimized) _hubWindow.WindowState = WindowState.Normal;
                _hubWindow.Activate();
                if (IsHubUrl(atual.Source)) { await atual.ExecuteScriptAsync(fill); }
                else { _hubPending = fill; atual.Navigate($"{HubOrigin}/#{rota}"); }
                LoggingService.Log.Info($"[conta{Slot}] PokéIdle Hub: {rota}{(ficha is { Length: > 0 } ? " (com a ficha)" : "")}.");
                return;
            }
            if (_hubWindow is not null) { _hubPending = fill; _hubWindow.Activate(); return; } // ainda abrindo

            var wv = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x16, 0x18, 0x1d) };
            var win = new Window
            {
                Title = $"{Account.DisplayName} — PokéIdle Hub",
                Width = 1180,
                Height = 820,
                Content = wv,
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
            };
            _hubWindow = win;
            _hubView = wv;
            _hubPending = fill;
            win.Closed += (_, _) =>
            {
                if (_hubWindow == win) { _hubWindow = null; _hubView = null; _hubPending = null; }
                try { wv.Dispose(); } catch { }
            };
            win.Show();

            var env = await _profiles.GetEnvironmentAsync();
            await wv.EnsureCoreWebView2Async(env, _profiles.CreateControllerOptions(env, Slot));
            var core = wv.CoreWebView2;
            core.Settings.AreDevToolsEnabled = _settings.DeveloperMode;
            core.Settings.AreHostObjectsAllowed = false;
            // Mensagens só para o "▶ Caçar" (script do app na página do Hub; ver OnHubWebMessage).
            core.Settings.IsWebMessageEnabled = true;
            core.Settings.IsStatusBarEnabled = false;
            core.WebMessageReceived += OnHubWebMessage;
            if (HubSiteScript.Value is { Length: > 0 } site) await core.AddScriptToExecuteOnDocumentCreatedAsync(site);
            core.DocumentTitleChanged += (_, _) => { try { win.Title = $"{Account.DisplayName} — {core.DocumentTitle}"; } catch { } };
            core.NavigationStarting += (_, ev) =>
            {
                // O Hub, o login dos Guias (Discord) e as páginas de login conhecidas ficam aqui;
                // qualquer outro link sai para o navegador padrão.
                if (IsHubUrl(ev.Uri) || IsLoginPage(ev.Uri) || ev.Uri == "about:blank") return;
                ev.Cancel = true;
                if (NavigationPolicy.IsSafeExternalUrl(ev.Uri)) OpenExternal(ev.Uri);
            };
            core.NavigationCompleted += async (_, ev) =>
            {
                if (!ev.IsSuccess || _hubPending is not { } pend || !IsHubUrl(core.Source)) return;
                _hubPending = null;
                try { await core.ExecuteScriptAsync(pend); } catch { }
            };
            core.NewWindowRequested += (_, ev) =>
            {
                if (IsLoginPopupUrl(ev.Uri)) { OpenLoginPopup(ev); return; }
                ev.Handled = true;
                if (IsHubUrl(ev.Uri)) core.Navigate(ev.Uri);
                else if (NavigationPolicy.IsSafeExternalUrl(ev.Uri)) OpenExternal(ev.Uri);
            };
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.Navigate($"{HubOrigin}/#{rota}");
            LoggingService.Log.Info($"[conta{Slot}] PokéIdle Hub aberto: {rota}{(ficha is { Length: > 0 } ? " (com a ficha)" : "")}.");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[conta{Slot}] PokéIdle Hub não abriu ({ex.Message}); abrindo no navegador padrão.");
            try { _hubWindow?.Close(); } catch { }
            OpenExternal($"{HubOrigin}/#{rota}");
        }
    }

    /// <summary>Fecha a janela do Hub desta conta (a conta fechou ou foi limpa).</summary>
    private void CloseHubWindow()
    {
        try { _hubWindow?.Close(); } catch { }
    }
}
