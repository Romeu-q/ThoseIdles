using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>Credenciais que o próprio jogo guardou no navegador da conta (localStorage "sessao" e "id-dispositivo").</summary>
public sealed record EconomySession(string Nick, string Token, string? DeviceId);

public enum EconomyState { Stopped, Connecting, Connected, Reconnecting, Failed }

/// <summary>
/// Modo econômico: mantém a conta conectada ao servidor do PokeIdle SEM a página do jogo.
///
/// Os termos do jogo (seção 5) permitem automação, dentro dos limites: até 4 contas por pessoa,
/// não explorar falhas e não sobrecarregar o servidor. Este cliente faz só o que o cliente oficial
/// faz para a conta continuar caçando — e nada além:
///  - ao conectar, o mesmo "hello" (nick, token, delta: 1, id do aparelho do próprio navegador);
///  - "ping" a cada 25 s; derruba e reconecta se ficar 30 s sem mensagem do jogo (como o oficial);
///  - em 4001 (sessão aberta em outro lugar), 4003 (IP bloqueado) ou "erro" antes de entrar
///    (token inválido, rede cheia, banimento) PARA e avisa, sem insistir;
///  - outras quedas: reconecta com espera crescente (2 s → 60 s).
/// Não envia nenhuma ação de jogo. Depois de entrar, manda só o aviso "cliente.economia" — o mesmo
/// do Modo Economia do próprio jogo — para o servidor parar de mandar o mapa, que ninguém desenha.
/// As mensagens recebidas são lidas só para o tipo, a bola automática do "welcome" e as capturas.
///
/// CAPTURA: a caça roda no servidor, mas a bola só é lançada sozinha com a automação do jogo
/// ("bola até capturar" / "sem parar", do VIP). Sem ela, capturar é clicar nos Pokémon caídos na
/// página — e o cliente leve não faz isso por você.
/// </summary>
public sealed class EconomyClient : IAsyncDisposable
{
    private static readonly int[] BackoffSeconds = { 2, 5, 10, 20, 30, 60 };
    /// <summary>Intervalo do "ping" (o mesmo do cliente oficial). O autoteste encurta.</summary>
    public static TimeSpan PingEvery { get; set; } = TimeSpan.FromSeconds(25);
    private static readonly TimeSpan SilenceLimit = TimeSpan.FromSeconds(30);

    private readonly Uri _endpoint;
    private readonly string _origin;
    private readonly string _userAgent;
    private readonly int _slot;
    private EconomySession _session;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public EconomyClient(int slot, Uri endpoint, string origin, string userAgent, EconomySession session)
    {
        _slot = slot;
        _endpoint = endpoint;
        _origin = origin;
        _userAgent = userAgent;
        _session = session;
    }

    public EconomyState State { get; private set; } = EconomyState.Stopped;
    public string Detail { get; private set; } = "";
    public DateTime? ConnectedSinceUtc { get; private set; }
    public long MessagesReceived { get; private set; }

    /// <summary>Bola automática ligada no jogo (null = o "welcome" não disse).</summary>
    public bool? AutoBall { get; private set; }
    public bool? IsVip { get; private set; }

    /// <summary>A prancheta da sessão (abates, shinies, drops raros, pokébolas) desde que o modo entrou.</summary>
    public EconomySessionStats Stats { get; } = new();

    /// <summary>Capturas anunciadas pelo servidor desde que o modo econômico entrou.</summary>
    public int Captures => Stats.Capturas;

    private DateTime _ultimoResumo = DateTime.MinValue;

    private string ConnectedText()
    {
        var bola = AutoBall switch { true => "bola automática ligada", false => "bola automática DESLIGADA (não captura)", _ => "bola automática: ?" };
        return $"Conectada como {_session.Nick} · {bola}\n{Stats.Resumo()}";
    }

    /// <summary>Atualiza o quadro no máximo a cada 2 s (a caça manda muitos eventos por segundo).</summary>
    private void AtualizarResumo(bool agora = false)
    {
        if (!agora && DateTime.UtcNow - _ultimoResumo < TimeSpan.FromSeconds(2)) return;
        _ultimoResumo = DateTime.UtcNow;
        SetState(EconomyState.Connected, ConnectedText());
    }

    /// <summary>Mudou o estado (disparado na thread do loop; quem ouve deve voltar à UI).</summary>
    public event EventHandler? StateChanged;

    /// <summary>O servidor renovou o token: precisa ser guardado para a próxima conexão/página.</summary>
    public event EventHandler<string>? TokenRenewed;

    public EconomySession Session => _session;

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts is null) return;
        _cts.Cancel();
        try { if (_loop is not null) await _loop; } catch { }
        _cts.Dispose();
        _cts = null;
        SetState(EconomyState.Stopped, "Parado");
    }

    private async Task RunAsync(CancellationToken stop)
    {
        var attempt = 0;
        while (!stop.IsCancellationRequested)
        {
            SetState(attempt == 0 ? EconomyState.Connecting : EconomyState.Reconnecting,
                attempt == 0 ? "Conectando…" : $"Reconectando (tentativa {attempt})…");
            var outcome = await ConnectOnceAsync(stop);
            if (stop.IsCancellationRequested) return;

            if (outcome.Fatal)
            {
                SetState(EconomyState.Failed, outcome.Reason);
                LoggingService.Log.Warn($"[conta{_slot}] Modo econômico parou: {outcome.Reason}");
                return;
            }
            attempt = outcome.WasWelcomed ? 1 : attempt + 1;
            var wait = BackoffSeconds[Math.Min(attempt - 1, BackoffSeconds.Length - 1)];
            SetState(EconomyState.Reconnecting, $"{outcome.Reason} — reconectando em {wait}s");
            LoggingService.Log.Info($"[conta{_slot}] Modo econômico: {outcome.Reason}; nova tentativa em {wait}s.");
            try { await Task.Delay(TimeSpan.FromSeconds(wait + Random.Shared.NextDouble()), stop); }
            catch (OperationCanceledException) { return; }
        }
    }

    private readonly record struct Outcome(bool Fatal, bool WasWelcomed, string Reason);

    private async Task<Outcome> ConnectOnceAsync(CancellationToken stop)
    {
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", _origin);
        ws.Options.SetRequestHeader("User-Agent", _userAgent);
        ws.Options.KeepAliveInterval = TimeSpan.Zero; // o keepalive é o "ping" do próprio jogo
        ws.Options.Cookies = new CookieContainer();

        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await ws.ConnectAsync(_endpoint, connectTimeout.Token);
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new Outcome(false, false, $"Falha ao conectar ({Short(ex)})");
        }

        await SendAsync(ws, new { t = "hello", nick = _session.Nick, token = _session.Token, delta = 1, dispositivo = _session.DeviceId }, stop);

        var welcomed = false;
        var lastGameMessage = DateTime.UtcNow;
        var nextPing = DateTime.UtcNow + PingEvery;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        Task<WebSocketReceiveResult>? receive = null;

        try
        {
            while (!stop.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                receive ??= ws.ReceiveAsync(buffer, stop);
                var timer = Task.Delay(TimeSpan.FromSeconds(1), stop);
                if (await Task.WhenAny(receive, timer) == receive)
                {
                    var r = await receive;
                    receive = null;
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    // Mensagens grandes (o estado completo no "welcome") chegam em pedaços.
                    if (message.Length < 4 * 1024 * 1024) message.Write(buffer, 0, r.Count);
                    if (!r.EndOfMessage) continue;

                    var head = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)Math.Min(message.Length, 200));
                    var type = MessageType(head);
                    MessagesReceived++;
                    if (type != "pong") lastGameMessage = DateTime.UtcNow;

                    if (type == "welcome" && !welcomed)
                    {
                        welcomed = true;
                        ReadAutomation(message);
                        Parse(message, root => { Stats.LerWelcome(root); Game.Apply(root); });
                        _wsAtual = ws;
                        _ = SpeciesCatalog.EnsureLoadedAsync(); // para a nota da "melhor captura"
                        ConnectedSinceUtc = DateTime.UtcNow;
                        SetState(EconomyState.Connected, ConnectedText());
                        LoggingService.Log.Info($"[conta{_slot}] Modo econômico conectado (bola automática: {AutoBall?.ToString() ?? "?"}, VIP: {IsVip?.ToString() ?? "?"}).");
                        // O mesmo aviso do Modo Economia do jogo: sem tela, o servidor não precisa mandar o mapa.
                        await SendAsync(ws, new { t = "cliente.economia", ativo = true }, stop);
                        await VerificarAutoCompraAsync();
                    }
                    else if (type == "batalha")
                    {
                        var shiniesAntes = Stats.ShiniesCapturados;
                        var mudou = false;
                        Parse(message, root => { mudou = Stats.Somar(root); Game.Evento(root); });
                        if (Stats.ShiniesCapturados > shiniesAntes)
                            LoggingService.Log.Info($"[conta{_slot}] Modo econômico: SHINY capturado — {Stats.UltimosShinies.FirstOrDefault()}.");
                        if (mudou) AtualizarResumo(Stats.ShiniesCapturados > shiniesAntes);
                        await VerificarAutoCompraAsync();
                    }
                    else if (welcomed && type == "estado")
                    {
                        // O estado da conta mudou (nível, Pokémon ativo, pokébolas…): só vêm os campos alterados.
                        var leu = false;
                        Parse(message, root => { leu = Stats.LerEstado(root) | Stats.LerBolas(root); Game.Apply(root); });
                        if (leu) AtualizarResumo();
                        await VerificarAutoCompraAsync();
                    }
                    else if (welcomed && type == "erro")
                    {
                        // Recusas do servidor depois de entrar (troca de hunt travada, sem bola, nível baixo…): vão para o registro.
                        var key = ReadString(message, "chave") ?? ReadString(message, "msg") ?? "erro";
                        Stats.Anotar($"⚠ o jogo recusou: {key}");
                        UltimoErro = key;
                        UltimoErroEm = DateTime.UtcNow;
                        ErroRecebido?.Invoke(this, key);
                    }
                    else if (welcomed && (Contains(message, "\"balls\"") || Contains(message, "\"items\"")))
                    {
                        var leu = false;
                        Parse(message, root => { leu = Stats.LerBolas(root); Game.Apply(root); });
                        if (leu) AtualizarResumo();
                        await VerificarAutoCompraAsync();
                    }
                    else if (type == "sessao.renovada")
                    {
                        if (ReadString(message, "token") is { Length: > 0 } token)
                        {
                            _session = _session with { Token = token };
                            TokenRenewed?.Invoke(this, token);
                        }
                    }
                    else if (type == "erro" && !welcomed)
                    {
                        // Recusado ao entrar (token, rede cheia, banimento...): não insiste.
                        var key = ReadString(message, "chave") ?? ReadString(message, "msg") ?? "erro";
                        await CloseQuietly(ws);
                        return new Outcome(true, false, $"O jogo recusou a entrada ({key}). Abra o jogo desta conta para entrar de novo.");
                    }
                    message.SetLength(0);
                }

                var now = DateTime.UtcNow;
                if (now >= nextPing)
                {
                    await SendAsync(ws, new { t = "ping" }, stop);
                    nextPing = now + PingEvery;
                }
                if (now - lastGameMessage > SilenceLimit)
                {
                    await CloseQuietly(ws);
                    return new Outcome(false, welcomed, "30 s sem resposta do jogo");
                }
            }
        }
        catch (Exception ex) when (!stop.IsCancellationRequested)
        {
            return new Outcome(false, welcomed, $"Conexão caiu ({Short(ex)})");
        }

        if (stop.IsCancellationRequested)
        {
            await CloseQuietly(ws);
            return new Outcome(false, welcomed, "Parado");
        }

        ConnectedSinceUtc = null;
        return ws.CloseStatus switch
        {
            (WebSocketCloseStatus)4001 => new Outcome(true, welcomed, "A conta foi aberta em outro lugar (sessão substituída)."),
            (WebSocketCloseStatus)4003 => new Outcome(true, welcomed, "O servidor bloqueou este IP."),
            _ => new Outcome(false, welcomed, $"Servidor fechou a conexão ({(int?)ws.CloseStatus ?? 0})"),
        };
    }

    /// <summary>{"t":"tipo",...} → "tipo", olhando só o começo da mensagem.</summary>
    internal static string? MessageType(string head)
    {
        var i = head.IndexOf("\"t\":\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i += 5;
        var j = head.IndexOf('"', i);
        return j > i ? head[i..j] : null;
    }

    private static string? ReadString(MemoryStream message, string property)
    {
        try
        {
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            return doc.RootElement.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
        catch { return null; }
    }


    private static void Parse(MemoryStream message, Action<JsonElement> use)
    {
        try
        {
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            use(doc.RootElement);
        }
        catch { }
    }

    private static bool Contains(MemoryStream message, string text) =>
        Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length).Contains(text, StringComparison.Ordinal);

    /// <summary>Do "welcome": a automação da bola (autoBallAteCapturar / autoBallSemParar) e o VIP.</summary>
    private void ReadAutomation(MemoryStream message)
    {
        try
        {
            using var doc = JsonDocument.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
            if (Find(doc.RootElement, "automation", 0) is { ValueKind: JsonValueKind.Object } auto)
            {
                bool On(string k) => auto.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True;
                AutoBall = On("autoBallAteCapturar") || On("autoBallSemParar");
            }
            if (Find(doc.RootElement, "loja", 0) is { ValueKind: JsonValueKind.Object } loja && loja.TryGetProperty("vip", out var vip))
                IsVip = vip.ValueKind is not (JsonValueKind.False or JsonValueKind.Null or JsonValueKind.Undefined);
        }
        catch { }
    }

    /// <summary>Primeira propriedade com esse nome, em qualquer nível (até 6 de profundidade).</summary>
    private static JsonElement? Find(JsonElement e, string name, int depth)
    {
        if (depth > 6) return null;
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.NameEquals(name)) return p.Value;
                if (p.Value.ValueKind is JsonValueKind.Object && Find(p.Value, name, depth + 1) is { } f) return f;
            }
        }
        return null;
    }

    private async Task SendAsync(ClientWebSocket ws, object payload, CancellationToken stop)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        // Um envio por vez: o ping do laço e um pedido da tela (ir caçar) podem coincidir.
        await _envio.WaitAsync(stop);
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, stop); }
        finally { _envio.Release(); }
    }

    private readonly SemaphoreSlim _envio = new(1, 1);
    private ClientWebSocket? _wsAtual;

    /// <summary>A hunt em que a conta está agora (do estado do jogo), ou null.</summary>
    public string? HuntAtual => Game.HuntSlug;

    /// <summary>O estado da conta (nível, moedas, hunts, time, automações, bolas) remontado das mensagens do servidor.</summary>
    public AccountGameState Game { get; } = new();

    /// <summary>Configurações persistentes da conta (para a compra automática no Market quando o estoque estiver acabando).</summary>
    public AccountSettings? Account { get; set; }

    private readonly Dictionary<int, DateTime> _ultimaAutoCompraUtc = new();
    private DateTime _ultimaAutoCompraGeralUtc = DateTime.MinValue;

    /// <summary>A auto-compra só repõe Ultra Ball, Max Revive e Golden Potion (e sempre na quantidade máxima).</summary>
    public static bool PermitidoNaAutoCompra(string? nome)
    {
        var n = new string((nome ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return n is "ultraball" or "maxrevive" or "goldenpotion";
    }

    /// <summary>As poções que o app mostra (Market e Resumo): só a Golden Potion e a Ultimate Potion.</summary>
    public static bool PocaoVisivel(string? nome)
    {
        var n = new string((nome ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        return n is "goldenpotion" or "ultimatepotion";
    }

    /// <summary>A pokébola está marcada para reposição automática quando acabar?</summary>
    public bool EstaEmAutoCompraBola(int id)
    {
        if (Account is not { AutoBuyBalls: true } acc) return false;
        if (!PermitidoNaAutoCompra(Game.CatalogoBolas.FirstOrDefault(x => x.Id == id)?.Nome)) return false;
        if (acc.AutoBuyDisabledIds.Contains(id)) return false;
        var b = Game.CatalogoBolas.FirstOrDefault(x => x.Id == id);
        if (b is null || !b.Compravel || b.Preco is null or <= 0) return false;
        return acc.AutoBuyExtraIds.Contains(id) || Game.BallIds.Contains(id);
    }

    /// <summary>O item (poção ou revive) está marcado para reposição automática quando acabar?</summary>
    public bool EstaEmAutoCompraItem(int id)
    {
        if (Account is not { } acc || acc.AutoBuyDisabledIds.Contains(id)) return false;
        var m = Game.CatalogoMarket.FirstOrDefault(x => x.Id == id);
        if (m is null || m.Preco <= 0 || !PermitidoNaAutoCompra(m.Nome)) return false;
        return m.Categoria switch
        {
            "potion" => acc.AutoBuyPotions && (acc.AutoBuyExtraIds.Contains(id) || Game.PotionIds.Contains(id)),
            "revive" => acc.AutoBuyRevives && (acc.AutoBuyExtraIds.Contains(id) || Game.ReviveIds.Contains(id)),
            _ => false,
        };
    }

    /// <summary>Liga ou desliga a reposição automática de uma bola, poção ou revive específico.</summary>
    public void AlternarAutoCompra(int id, bool ehBola)
    {
        if (Account is not { } acc) return;
        var nome = ehBola ? Game.CatalogoBolas.FirstOrDefault(x => x.Id == id)?.Nome : Game.CatalogoMarket.FirstOrDefault(x => x.Id == id)?.Nome;
        if (!PermitidoNaAutoCompra(nome)) return;
        var ativo = ehBola ? EstaEmAutoCompraBola(id) : EstaEmAutoCompraItem(id);
        if (ativo)
        {
            acc.AutoBuyExtraIds.Remove(id);
            if (!acc.AutoBuyDisabledIds.Contains(id)) acc.AutoBuyDisabledIds.Add(id);
        }
        else
        {
            acc.AutoBuyDisabledIds.Remove(id);
            if (!acc.AutoBuyExtraIds.Contains(id)) acc.AutoBuyExtraIds.Add(id);
            if (ehBola) acc.AutoBuyBalls = true;
            else
            {
                var cat = Game.CatalogoMarket.FirstOrDefault(x => x.Id == id)?.Categoria;
                if (cat == "potion") acc.AutoBuyPotions = true;
                else if (cat == "revive") acc.AutoBuyRevives = true;
            }
        }
    }

    /// <summary>
    /// Confere se alguma pokébola, poção ou revive marcado para auto-compra caiu abaixo do estoque mínimo e compra no NPC em ouro.
    /// Tem guarda de 15 s por item para não repetir o pedido enquanto o servidor processa.
    /// </summary>
    public async Task VerificarAutoCompraAsync()
    {
        if (_wsAtual is not { State: WebSocketState.Open } ws || !Game.Recebeu || Account is not { } acc) return;
        var agora = DateTime.UtcNow;
        if ((agora - _ultimaAutoCompraGeralUtc).TotalSeconds < 1.2) return;

        var ouro = Game.Gold;
        if (ouro <= 0) return;

        if (acc.AutoBuyBalls)
        {
            var min = Math.Max(1, acc.AutoBuyBallsMin);
            var lote = Math.Max(10, acc.AutoBuyBallsQty);
            foreach (var b in Game.CatalogoBolas)
            {
                if (!EstaEmAutoCompraBola(b.Id) || b.Preco is not { } preco || preco <= 0) continue;
                var tem = Game.Balls(b.Id);
                if (tem >= min) { _ultimaAutoCompraUtc.Remove(b.Id); continue; }
                if (_ultimaAutoCompraUtc.TryGetValue(b.Id, out var ult) && (agora - ult).TotalSeconds < 15) continue;

                var qtd = CalcularQtdCompra(lote, ouro, preco);
                if (qtd <= 0) continue;

                _ultimaAutoCompraUtc[b.Id] = agora;
                _ultimaAutoCompraGeralUtc = agora;
                await SendAsync(ws, new { t = "shop.buy", kind = "ball", id = b.Id, qty = qtd }, CancellationToken.None);
                Stats.Anotar($"🔄 auto-compra: +{qtd:N0} × {b.Nome} (estoque estava em {tem:N0} < {min:N0})");
                LoggingService.Log.Info($"[conta{_slot}] Auto-compra: +{qtd} × {b.Nome} (estoque={tem}, mín={min}).");
                return;
            }
        }

        if (acc.AutoBuyPotions || acc.AutoBuyRevives)
        {
            var min = Math.Max(1, acc.AutoBuyItemsMin);
            var lote = Math.Max(10, acc.AutoBuyItemsQty);
            foreach (var m in Game.CatalogoMarket)
            {
                if (m.Categoria is not ("potion" or "revive") || !EstaEmAutoCompraItem(m.Id) || m.Preco <= 0) continue;
                var tem = Game.Items(m.Id);
                if (tem >= min) { _ultimaAutoCompraUtc.Remove(m.Id); continue; }
                if (_ultimaAutoCompraUtc.TryGetValue(m.Id, out var ult) && (agora - ult).TotalSeconds < 15) continue;

                var qtd = CalcularQtdCompra(lote, ouro, m.Preco);
                if (qtd <= 0) continue;

                _ultimaAutoCompraUtc[m.Id] = agora;
                _ultimaAutoCompraGeralUtc = agora;
                await SendAsync(ws, new { t = "shop.buy", kind = "item", id = m.Id, qty = qtd }, CancellationToken.None);
                Stats.Anotar($"🔄 auto-compra: +{qtd:N0} × {m.Nome} (estoque estava em {tem:N0} < {min:N0})");
                LoggingService.Log.Info($"[conta{_slot}] Auto-compra: +{qtd} × {m.Nome} (estoque={tem}, mín={min}).");
                return;
            }
        }
    }

    private static int CalcularQtdCompra(int loteDesejado, long ouroDisponivel, long precoUnitario)
    {
        // Sempre a quantidade MÁXIMA (o "Máx" da loja: até 9.999, o que o ouro paga).
        if (precoUnitario <= 0) return 0;
        return (int)Math.Min(9999, ouroDisponivel / precoUnitario);
    }

    /// <summary>A última recusa do servidor depois de entrar ("erro" com a chave) e o aviso dela.</summary>
    public string? UltimoErro { get; private set; }
    public DateTime UltimoErroEm { get; private set; }
    public event EventHandler<string>? ErroRecebido;

    /// <summary>
    /// Manda uma ação de jogo ao servidor (a mesma mensagem que o cliente oficial manda ao clicar: "hunt.select",
    /// "team.active", "auto.set"…). false se a conta não está conectada agora.
    /// </summary>
    public async Task<bool> EnviarAsync(object payload)
    {
        if (_wsAtual is not { State: WebSocketState.Open } ws) return false;
        try { await SendAsync(ws, payload, CancellationToken.None); return true; }
        catch { return false; }
    }

    // ---- trocas de hunt, Centro e Pokémon em campo: respeitam as travas do jogo ----

    private CancellationTokenSource? _espera;
    private DateTime _ultimaTrocaAtivoUtc = DateTime.MinValue;

    /// <summary>Espera o tempo das travas (até ~15 s), avisando o que falta. false = outro pedido tomou o lugar deste.</summary>
    private async Task<bool> EsperarAsync(Func<double> faltaMs, Action<string>? aviso, string oQue, CancellationToken ct)
    {
        for (var i = 0; i < 70; i++)
        {
            var falta = faltaMs();
            if (falta <= 0) return true;
            aviso?.Invoke($"Aguardando {falta / 1000:0.0} s para {oQue} (trava do jogo)…");
            try { await Task.Delay((int)Math.Clamp(falta, 50, 250), ct); }
            catch (OperationCanceledException) { return false; }
        }
        return true; // passou do teto: manda, e quem decide é o servidor
    }

    private CancellationToken NovoPedido()
    {
        _espera?.Cancel();
        _espera = new CancellationTokenSource();
        return _espera.Token;
    }

    /// <summary>
    /// Vai caçar na hunt pedida: a MESMA mensagem do clique no mapa do jogo ("hunt.select"). Antes, ESPERA as
    /// travas — os 3 s sem dano (carimbo do servidor), a porta de 10 s desde a última entrada e a guarda de 4 s
    /// entre trocas — e só então manda; um pedido novo cancela o que ainda esperava. O servidor confere o resto
    /// (nível, Safari) e a resposta é a hunt do estado mudar.
    /// </summary>
    public async Task<(bool Ok, string Mensagem)> IrCacarAsync(string slug, Action<string>? aviso = null)
    {
        if (_wsAtual is not { State: WebSocketState.Open }) return (false, "O modo econômico não está conectado agora.");
        if (HuntAtual == slug) return (true, "A conta já está caçando nessa hunt.");
        var ct = NovoPedido();
        if (!await EsperarAsync(() => Math.Max(Game.EsperaCombateMs(), Math.Max(Game.EsperaPortaMs(), Game.EsperaGuardaMs())), aviso, "trocar de hunt", ct))
            return (false, "Pedido substituído por outro.");
        if (_wsAtual is not { State: WebSocketState.Open } ws) return (false, "O modo econômico não está conectado agora.");
        Game.ArmarGuarda();
        await SendAsync(ws, new { t = "hunt.select", slug }, CancellationToken.None);
        for (var i = 0; i < 20 && HuntAtual != slug; i++) await Task.Delay(200);
        return HuntAtual == slug
            ? (true, "Pronto: a conta foi caçar lá.")
            : (false, "O jogo não trocou de hunt (você pode estar em luta/boss, ou a hunt não está liberada para o seu nível).");
    }

    /// <summary>Volta ao Centro Pokémon ("centro.ir"), esperando os 3 s sem dano e a guarda de 4 s entre trocas.</summary>
    public async Task<(bool Ok, string Mensagem)> IrAoCentroAsync(Action<string>? aviso = null)
    {
        if (_wsAtual is not { State: WebSocketState.Open }) return (false, "O modo econômico não está conectado agora.");
        if (Game.NoSafari) return (false, "A conta está no Safari: sair é abandoná-lo, e o jogo pede confirmação. Abra o jogo completo para isso.");
        var ct = NovoPedido();
        if (!await EsperarAsync(() => Math.Max(Game.EsperaCombateMs(), Game.EsperaGuardaMs()), aviso, "voltar ao Centro", ct))
            return (false, "Pedido substituído por outro.");
        if (_wsAtual is not { State: WebSocketState.Open } ws) return (false, "O modo econômico não está conectado agora.");
        Game.ArmarGuarda();
        await SendAsync(ws, new { t = "centro.ir" }, CancellationToken.None);
        for (var i = 0; i < 20 && Game.HuntSlug is not null; i++) await Task.Delay(200);
        return Game.HuntSlug is null ? (true, "Pronto: a conta voltou ao Centro Pokémon.") : (false, "O jogo não deixou sair da hunt agora (luta em andamento); tente de novo.");
    }

    /// <summary>Troca o Pokémon em campo ("team.active"), com a espera de 3 s do jogo entre duas trocas.</summary>
    public async Task<(bool Ok, string Mensagem)> TrocarAtivoAsync(long id, Action<string>? aviso = null)
    {
        if (_wsAtual is not { State: WebSocketState.Open }) return (false, "O modo econômico não está conectado agora.");
        var ct = NovoPedido();
        if (!await EsperarAsync(() => Math.Max(0, 3250 - (DateTime.UtcNow - _ultimaTrocaAtivoUtc).TotalMilliseconds), aviso, "trocar o Pokémon em campo", ct))
            return (false, "Pedido substituído por outro.");
        if (_wsAtual is not { State: WebSocketState.Open } ws) return (false, "O modo econômico não está conectado agora.");
        _ultimaTrocaAtivoUtc = DateTime.UtcNow;
        await SendAsync(ws, new { t = "team.active", pokemonId = id }, CancellationToken.None);
        return (true, "Pedido de troca enviado.");
    }

    private static async Task CloseQuietly(ClientWebSocket ws)
    {
        try
        {
            using var t = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", t.Token);
        }
        catch { }
    }

    private static string Short(Exception ex) => ex.InnerException?.Message ?? ex.Message;

    private void SetState(EconomyState state, string detail)
    {
        State = state;
        Detail = detail;
        if (state != EconomyState.Connected) ConnectedSinceUtc = null;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
