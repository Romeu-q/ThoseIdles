using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;

namespace ThoseIdles.SelfTest;

/// <summary>
/// Servidor HTTP + WebSocket local (http://localhost:porta) que imita o formato do jogo: uma página
/// que abre um WebSocket no mesmo host. Usado só pelo autoteste; nunca roda no modo normal.
/// </summary>
public sealed class SelfTestServer : IDisposable
{
    private const string Page = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>autoteste</title></head>
        <body style="background:#1b1b22;color:#ccc;font:13px Segoe UI,sans-serif;margin:8px">
        <b>ThoseIdles — página de autoteste</b>
        <a id="twlink" href="https://www.twitch.tv/canalteste" target="_blank" rel="noopener">canal da Twitch</a>
        <div id="t"></div>
        <script>
          window.bootId = Math.random().toString(36).slice(2);
          window.ticks = 0;
          setInterval(() => { window.ticks++; }, 100);
          window.rafs = 0;
          (function f() { window.rafs++; requestAnimationFrame(f); })();
          window.wsOpen = false;
          window.wsMsgs = 0;
          function connect() {
            const ws = new WebSocket('ws://' + location.host + '/ws');
            ws.onopen = () => { window.wsOpen = true; };
            ws.onmessage = () => { window.wsMsgs++; };
            ws.onclose = () => { window.wsOpen = false; setTimeout(connect, 1000); };
          }
          connect();
          setInterval(() => {
            document.getElementById('t').textContent =
              'boot ' + bootId + ' | ticks ' + ticks + ' | rAF ' + rafs + ' | ' + document.visibilityState +
              ' | ws ' + (wsOpen ? 'aberto' : 'fechado') + ' | msgs ' + wsMsgs +
              ' | acct ' + localStorage.getItem('acct');
          }, 250);
        </script>
        </body></html>
        """;

    /// <summary>"Live" de teste. Fica em 127.0.0.1 (outro site que o jogo em localhost), como twitch.tv × pokeidle.io.</summary>
    private const string LivePage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>live de teste</title></head>
        <body style="background:#0e0e10;color:#ddd;font:13px Segoe UI,sans-serif;margin:8px">
        <b>live de teste</b> <span id="c"></span>
        <script>
          window.__livePage = new URLSearchParams(location.search).get('c');
          document.getElementById('c').textContent = window.__livePage;
          window.liveTicks = 0;
          setInterval(() => { window.liveTicks++; }, 100);
        </script>
        </body></html>
        """;

    private readonly HttpListener _listener = new();

    /// <summary>Endereço da live de teste de um canal.</summary>
    public string LiveUrl(string channel) => $"http://127.0.0.1:{Port}/live?c={Uri.EscapeDataString(channel)}";
    private readonly CancellationTokenSource _cts = new();

    public int Port { get; }
    public string Url => $"http://localhost:{Port}/";

    /// <param name="port">Porta fixa entre as duas fases (Local Storage é por origem, e a origem inclui a porta).</param>
    public SelfTestServer(int port = 0)
    {
        Port = port > 0 ? port : FreePort();
        _listener.Prefixes.Add(Url);
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = Task.Run(AcceptLoop);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.IsWebSocketRequest && ctx.Request.Url?.AbsolutePath == "/ws")
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var ws = wsCtx.WebSocket;
                var buffer = new byte[1024];
                var receive = ws.ReceiveAsync(buffer, _cts.Token);
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var tick = Task.Delay(2000, _cts.Token);
                    if (await Task.WhenAny(receive, tick) == receive)
                    {
                        if (receive.Result.MessageType == WebSocketMessageType.Close) break;
                        receive = ws.ReceiveAsync(buffer, _cts.Token);
                        continue;
                    }
                    await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"tick\"}"), WebSocketMessageType.Text, true, _cts.Token);
                }
                ws.Abort();
                return;
            }

            var bytes = Encoding.UTF8.GetBytes(ctx.Request.Url?.AbsolutePath == "/live" ? LivePage : Page);
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _listener.Close();
    }
}
