using Microsoft.Web.WebView2.Core;

namespace ThoseIdles.Services;

/// <summary>
/// Lives da Twitch AUTOMÁTICAS: o jogo diz quais lives oficiais estão no ar e quais valem bônus
/// (bloco "twitch" do estado). Com a opção ligada, a conta abre as que estão no ar e valem bônus, e
/// fecha as lives OFICIAIS que saíram do ar. Canais que você abriu e não são oficiais não são tocados,
/// nem as lives da Kick.
///  · no modo econômico a informação vem do cliente leve;
///  · com a página do jogo aberta, um script na página repassa só esse bloco (postMessage).
/// </summary>
public sealed partial class BrowserSession
{
    private string _ultimaTwitch = "";
    private DateTime _ultimaTwitchUtc = DateTime.MinValue;

    /// <summary>O último bloco "twitch" recebido do jogo (o autoteste e a tela consultam).</summary>
    public TwitchInfo? UltimaTwitchInfo { get; private set; }

    private void OnEconomyTwitchInfo(object? sender, TwitchInfo info) =>
        _dispatcher.BeginInvoke(() => ApplyTwitchInfo(info));

    /// <summary>Abre as lives oficiais no ar com bônus e fecha as oficiais offline (no máx. 1×/min, ou quando muda).</summary>
    public void ApplyTwitchInfo(TwitchInfo info, bool forcar = false)
    {
        UltimaTwitchInfo = info;
        if (!_settings.AutoTwitchLives || string.IsNullOrEmpty(info.Login)) return;
        var mudou = info.Assinatura != _ultimaTwitch;
        if (!forcar && !mudou && DateTime.UtcNow - _ultimaTwitchUtc < TimeSpan.FromMinutes(1)) return;
        _ultimaTwitch = info.Assinatura;
        _ultimaTwitchUtc = DateTime.UtcNow;

        // Fecha as oficiais que saíram do ar (e esquece: quando voltarem ao ar, reabrem sozinhas).
        foreach (var tab in Lives.ToList())
        {
            if (tab.IsKick) continue;
            var canal = tab.Channel.ToLowerInvariant();
            if (info.Oficiais.Contains(canal) && !info.NoAr.Contains(canal))
            {
                LoggingService.Log.Info($"[conta{Slot}] Lives automáticas: {canal} saiu do ar; fechando.");
                CloseLive(tab, forget: true);
            }
        }
        // Abre as que estão no ar e valem bônus.
        foreach (var canal in info.QueValemAbrir())
        {
            if (FindLive(canal) is not null || !IsValidTwitchLogin(canal)) continue;
            if (Lives.Count >= MaxLivesPerAccount) break;
            LoggingService.Log.Info($"[conta{Slot}] Lives automáticas: {canal} está no ar e vale bônus; abrindo.");
            _ = OpenLiveAsync(canal);
        }
    }

    /// <summary>
    /// Script da página do jogo: repassa ao app SÓ o bloco "twitch" do estado quando o servidor o manda.
    /// Não lê nem envia mais nada.
    /// </summary>
    private const string TwitchRelayScript = """
        (() => {
          if (window.top !== window || window.__tiTwitchRelay || !window.chrome?.webview) return;
          window.__tiTwitchRelay = true;
          const Original = window.WebSocket;
          window.WebSocket = new Proxy(Original, {
            construct(target, args) {
              const ws = Reflect.construct(target, args);
              try {
                if (new URL(String(args[0]), location.href).host === location.host)
                  ws.addEventListener('message', (ev) => {
                    if (typeof ev.data !== 'string' || !ev.data.includes('"twitch"')) return;
                    try {
                      const m = JSON.parse(ev.data);
                      const tw = m?.estado?.twitch;
                      if (tw && typeof tw === 'object') window.chrome.webview.postMessage({ ti: 'twitch', tw });
                    } catch {}
                  });
              } catch {}
              return ws;
            },
          });
        })();
        """;

    private async Task EnableTwitchRelayAsync(CoreWebView2 core)
    {
        core.Settings.IsWebMessageEnabled = true;
        core.WebMessageReceived += OnGameWebMessage;
        await core.AddScriptToExecuteOnDocumentCreatedAsync(TwitchRelayScript);
    }

    private void OnGameWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            // Só da página do jogo (a mesma origem do StartUrl), e só a mensagem esperada.
            var origem = new Uri(_settings.StartUrl);
            if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var src) || !string.Equals(src.Host, origem.Host, StringComparison.OrdinalIgnoreCase)) return;
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            var r = doc.RootElement;
            if (!r.TryGetProperty("ti", out var ti) || ti.GetString() != "twitch" || !r.TryGetProperty("tw", out var tw)) return;
            if (TwitchInfo.From(tw) is { } info) ApplyTwitchInfo(info);
        }
        catch { }
    }
}
