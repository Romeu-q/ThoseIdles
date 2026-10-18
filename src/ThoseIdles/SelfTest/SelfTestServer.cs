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
        <div><span id="shl-conta">5 shinies</span></div>
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
            window.__ws = ws;
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
    /// <summary>O servidor do autoteste em execução (para os testes conferirem o que ele recebeu).</summary>
    public static SelfTestServer? Current { get; private set; }

    public SelfTestServer(int port = 0)
    {
        Current = this;
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

    /// <summary>"hello" recebidos (JSON bruto), na ordem — o autoteste confere o que o cliente leve mandou.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> Hellos { get; } = new();

    /// <summary>Pedidos "shiny.listar" recebidos (JSON bruto), na ordem.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> ShinyRequests { get; } = new();

    /// <summary>Tag que a "API do GitHub" falsa devolve como última Release.</summary>
    public string LatestReleaseTag { get; set; } = "v0.0.1";

    /// <summary>Pedidos "market.comprar" recebidos (JSON bruto), na ordem.</summary>
    public System.Collections.Concurrent.ConcurrentQueue<string> BuyRequests { get; } = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, bool> _sold = new();
    private int _marketScans, _sentCooldown;
    public int MarketScans => _marketScans;

    private int _pings;
    public int Pings => _pings;

    /// <summary>
    /// Responde como o servidor do jogo: "hello" → "welcome" (e uma renovação de token); "ping" → "pong".
    /// Tokens especiais simulam recusas: "tomada" fecha com 4001; "recusa" manda "erro" antes de entrar.
    /// Retorna true quando a conexão deve terminar.
    /// </summary>
    private async Task<bool> AnswerLikeGameAsync(WebSocket ws, string text)
    {
        string? type = null, token = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            type = doc.RootElement.TryGetProperty("t", out var t) ? t.GetString() : null;
            token = doc.RootElement.TryGetProperty("token", out var k) ? k.GetString() : null;
        }
        catch { }

        async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, _cts.Token);

        if (type == "teste.eco")
        {
            // O autoteste pede ao "servidor" para mandar uma mensagem do jogo (batalha, mercado).
            using var d = System.Text.Json.JsonDocument.Parse(text);
            await Send(d.RootElement.GetProperty("msg").GetRawText());
            return false;
        }
        if (type == "market.listar")
        {
            Interlocked.Increment(ref _marketScans);
            // 1: compra já · 2: caro · 3: em Gemas (regra é de ouro) · 4: sem shiny · 6: libera daqui a 1,5 s (só na 1ª listagem).
            var libera = Interlocked.Exchange(ref _sentCooldown, 1) == 0 ? DateTimeOffset.Now.ToUnixTimeMilliseconds() + 1500 : 0;
            await Send($$"""{"t":"market","aba":"pokemon","linhas":[{"id":1,"pokemonNome":"Charizard","shiny":true,"potencia":4,"preco":1500,"moeda":"gold"},{"id":2,"pokemonNome":"Charizard","shiny":true,"potencia":4,"preco":9000,"moeda":"gold"},{"id":3,"pokemonNome":"Charizard","shiny":true,"potencia":4,"preco":10,"moeda":"orb"},{"id":4,"pokemonNome":"Charmander","potencia":5,"preco":10,"moeda":"gold"},{"id":6,"pokemonNome":"Charmeleon","shiny":true,"potencia":3,"preco":800,"moeda":"gold","compravelEm":{{libera}}}]}""");
            return false;
        }
        if (type == "market.comprar")
        {
            BuyRequests.Enqueue(text);
            using var d = System.Text.Json.JsonDocument.Parse(text);
            var id = d.RootElement.GetProperty("id").GetInt32();
            // Como o jogo: só a primeira tentativa por anúncio dá certo; as repetidas são recusadas.
            if (_sold.TryAdd(id, true)) await Send($$"""{"t":"batalha","ev":[{"k":"marketComprado","descricao":"anúncio {{id}} comprado"}]}""");
            else await Send("""{"t":"erro","chave":"market.indisponivel"}""");
            return false;
        }
        if (type == "shiny.listar")
        {
            ShinyRequests.Enqueue(text);
            int pagina = 0;
            try
            {
                using var d = System.Text.Json.JsonDocument.Parse(text);
                pagina = d.RootElement.TryGetProperty("pagina", out var p) ? p.GetInt32() : 0;
            }
            catch { }
            var linhas = pagina switch
            {
                0 => """[{"nome":"Charizard","potencia":3,"quality":87,"ivs":{"hp":20,"atk":31,"def":10},"anuncio":{"preco":1500,"moeda":"orb"}},{"nome":"Gengar","potencia":1,"iv":42,"anuncio":{"preco":90000,"moeda":"gold"}}]""",
                1 => """[{"nome":"Pikachu, o raro","potencia":5,"qualidade":100,"ivTotal":186},{"nome":"Eevee","potencia":2}]""",
                2 => """[{"nome":"Mewtwo","potencia":4,"quality":55,"ivs":{"hp":31,"atk":31},"anuncio":{"preco":250.5,"moeda":"orb"}}]""",
                _ => "[]",
            };
            await Send($"{{\"t\":\"shiny.lista\",\"pagina\":{pagina},\"total\":5,\"linhas\":{linhas}}}");
            return false;
        }
        if (type == "ping")
        {
            Interlocked.Increment(ref _pings);
            await Send("{\"t\":\"pong\"}");
            return false;
        }
        if (type != "hello") return false;
        Hellos.Enqueue(text);
        if (token == "tomada")
        {
            await ws.CloseAsync((WebSocketCloseStatus)4001, "substituido", _cts.Token);
            return true;
        }
        if (token == "recusa")
        {
            await Send("{\"t\":\"erro\",\"chave\":\"auth.tokenInvalido\"}");
            return false;
        }
        await Send("{\"t\":\"welcome\",\"estado\":{\"cheio\":true}}");
        if (token is not null && !token.EndsWith("-renovado", StringComparison.Ordinal))
            await Send($"{{\"t\":\"sessao.renovada\",\"token\":\"{token}-renovado\"}}");
        return false;
    }

    private async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            if (ctx.Request.IsWebSocketRequest)
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                var ws = wsCtx.WebSocket;
                var buffer = new byte[4096];
                var receive = ws.ReceiveAsync(buffer, _cts.Token);
                while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
                {
                    var tick = Task.Delay(2000, _cts.Token);
                    if (await Task.WhenAny(receive, tick) == receive)
                    {
                        var r = receive.Result;
                        if (r.MessageType == WebSocketMessageType.Close) break;
                        if (await AnswerLikeGameAsync(ws, Encoding.UTF8.GetString(buffer, 0, r.Count))) break;
                        receive = ws.ReceiveAsync(buffer, _cts.Token);
                        continue;
                    }
                    await ws.SendAsync(Encoding.UTF8.GetBytes("{\"t\":\"tick\"}"), WebSocketMessageType.Text, true, _cts.Token);
                }
                if (ws.State == WebSocketState.Open) ws.Abort();
                return;
            }

            if (ctx.Request.Url?.AbsolutePath == "/releases/latest")
            {
                // Imita a API do GitHub; html_url fora do repositório para conferir que o app não o abre.
                var json = Encoding.UTF8.GetBytes($$"""{"tag_name":"{{LatestReleaseTag}}","draft":false,"prerelease":false,"html_url":"https://exemplo.invalid/x"}""");
                ctx.Response.ContentType = "application/json";
                ctx.Response.ContentLength64 = json.Length;
                await ctx.Response.OutputStream.WriteAsync(json);
                ctx.Response.Close();
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
