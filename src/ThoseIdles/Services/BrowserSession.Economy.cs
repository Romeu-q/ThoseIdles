using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ThoseIdles.Services;

/// <summary>
/// Modo econômico da conta: a página do jogo é fechada e um <see cref="EconomyClient"/> mantém a
/// conta conectada (e caçando) com poucos MB. As lives da Twitch da conta continuam. "Abrir jogo"
/// volta para a página normal, já com o token mais recente.
/// </summary>
public sealed partial class BrowserSession
{
    private EconomyClient? _eco;
    private string? _pendingPageToken;

    public bool IsEconomy => _eco is not null;
    public EconomyClient? Economy => _eco;

    /// <summary>A conta tem credenciais guardadas para o modo econômico.</summary>
    public bool HasEconomySession => Account.EconomySessionProtected is not null;

    /// <summary>
    /// Entra no modo econômico. A página do jogo precisa estar aberta e logada: é dela que vêm o
    /// nick, o token e o id do aparelho (os mesmos que o jogo usa). Retorna null se deu certo, ou o motivo.
    /// </summary>
    public async Task<string?> EnterEconomyAsync()
    {
        if (_eco is not null) return null;
        if (_core is null) return "Abra a conta (e faça login) antes de ligar o modo econômico.";
        if (!SameHost(_core.Source, _settings.StartUrl)) return "A página do jogo não está aberta nesta conta.";

        EconomySession? session;
        string userAgent;
        try
        {
            var json = await _core.ExecuteScriptAsync(
                "JSON.stringify({ s: localStorage.getItem('sessao'), d: localStorage.getItem('id-dispositivo') })");
            using var outer = JsonDocument.Parse(JsonSerializer.Deserialize<string>(json) ?? "{}");
            var raw = outer.RootElement.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            var device = outer.RootElement.TryGetProperty("d", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            session = null;
            if (raw is not null)
            {
                using var inner = JsonDocument.Parse(raw);
                var nick = inner.RootElement.TryGetProperty("nick", out var n) ? n.GetString() : null;
                var token = inner.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                if (!string.IsNullOrEmpty(nick) && !string.IsNullOrEmpty(token)) session = new EconomySession(nick, token, device);
            }
            userAgent = _core.Settings.UserAgent;
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[conta{Slot}] Não foi possível ler a sessão do jogo: {ex.Message}");
            return "Não foi possível ler a sessão do jogo nesta conta.";
        }
        if (session is null) return "Esta conta não está logada no jogo. Faça login e tente de novo.";

        Account.EconomySessionProtected = Protect(session, userAgent);
        Account.EconomyMode = true;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        LoggingService.Log.Info($"[conta{Slot}] Entrando no modo econômico (a página do jogo será fechada; lives continuam).");

        // Fecha só a página do jogo; as lives (mesmo perfil) seguem abertas.
        _generation++;
        CancelTimers();
        DetachAndDisposeGame();
        StartEconomyClient(session, userAgent);
        return null;
    }

    /// <summary>Sai do modo econômico e abre a página do jogo, com o token mais recente.</summary>
    public async Task ExitEconomyAsync()
    {
        if (_eco is { } eco)
        {
            _pendingPageToken = eco.Session.Token;
            await StopEconomyClientAsync();
        }
        if (Account.EconomyMode)
        {
            Account.EconomyMode = false;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        LoggingService.Log.Info($"[conta{Slot}] Saindo do modo econômico: abrindo a página do jogo.");
        await StartAsync();
    }

    /// <summary>Chamado pelo StartAsync quando a conta está em modo econômico. false = não deu (abre a página normal).</summary>
    private bool TryStartEconomyFromSaved()
    {
        if (!Account.EconomyMode || Account.EconomySessionProtected is null) return false;
        var saved = Unprotect(Account.EconomySessionProtected);
        if (saved is null)
        {
            LoggingService.Log.Warn($"[conta{Slot}] Credenciais do modo econômico ilegíveis; abrindo a página do jogo.");
            Account.EconomyMode = false;
            Account.EconomySessionProtected = null;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            return false;
        }
        StartEconomyClient(saved.Value.Session, saved.Value.UserAgent);
        return true;
    }

    private void StartEconomyClient(EconomySession session, string userAgent)
    {
        var start = new Uri(_settings.StartUrl);
        var endpoint = new UriBuilder(start.Scheme == Uri.UriSchemeHttps ? "wss" : "ws", start.Host, start.Port, "/").Uri;
        var eco = new EconomyClient(Slot, endpoint, $"{start.Scheme}://{start.Authority}", userAgent, session);
        eco.StateChanged += OnEconomyStateChanged;
        eco.TokenRenewed += OnEconomyTokenRenewed;
        _eco = eco;
        OnPropertyChanged(nameof(IsEconomy));
        OnPropertyChanged(nameof(IsRunning));
        SetStatus(SessionStatus.Loading, "Modo econômico: conectando…");
        eco.Start();
    }

    private async Task StopEconomyClientAsync()
    {
        var eco = _eco;
        if (eco is null) return;
        _eco = null;
        eco.StateChanged -= OnEconomyStateChanged;
        eco.TokenRenewed -= OnEconomyTokenRenewed;
        await eco.StopAsync();
        OnPropertyChanged(nameof(IsEconomy));
        OnPropertyChanged(nameof(IsRunning));
    }

    private void OnEconomyStateChanged(object? sender, EventArgs e)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (sender != _eco || _eco is not { } eco) return;
            switch (eco.State)
            {
                case EconomyState.Connected:
                    _wasConnected = true;
                    SetStatus(SessionStatus.Connected, eco.Detail);
                    if (!_reportedConnected)
                    {
                        _reportedConnected = true;
                        Milestone?.Invoke(this, ("conectada", _startWatch?.Elapsed ?? TimeSpan.Zero));
                    }
                    break;
                case EconomyState.Connecting:
                    SetStatus(SessionStatus.Loading, $"Modo econômico: {eco.Detail}");
                    break;
                case EconomyState.Reconnecting:
                    SetStatus(SessionStatus.Disconnected, $"Modo econômico: {eco.Detail}");
                    break;
                case EconomyState.Failed:
                    SetStatus(SessionStatus.Error, $"Modo econômico: {eco.Detail}");
                    break;
            }
        });
    }

    private void OnEconomyTokenRenewed(object? sender, string token)
    {
        _dispatcher.BeginInvoke(() =>
        {
            if (sender != _eco || _eco is not { } eco || Account.EconomySessionProtected is null) return;
            var saved = Unprotect(Account.EconomySessionProtected);
            Account.EconomySessionProtected = Protect(eco.Session, saved?.UserAgent ?? "");
            _pendingPageToken = token;
            SettingsChanged?.Invoke(this, EventArgs.Empty);
            LoggingService.Log.Info($"[conta{Slot}] Modo econômico: token renovado pelo servidor e guardado.");
        });
    }

    /// <summary>
    /// Antes de a página do jogo carregar, entrega a ela o token renovado durante o modo econômico
    /// (só na primeira página; depois o próprio jogo cuida da sessão).
    /// </summary>
    private async Task ApplyPendingTokenAsync(CoreWebView2 core)
    {
        if (_pendingPageToken is not { } token) return;
        _pendingPageToken = null;
        var host = new Uri(_settings.StartUrl).Host;
        var script = $$"""
            (() => {
              try {
                if (location.hostname !== {{JsonSerializer.Serialize(host)}}) return;
                const s = JSON.parse(localStorage.getItem('sessao') || 'null');
                if (s && s.token !== {{JsonSerializer.Serialize(token)}}) {
                  s.token = {{JsonSerializer.Serialize(token)}};
                  localStorage.setItem('sessao', JSON.stringify(s));
                }
              } catch {}
            })();
            """;
        var id = await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
        void Remove(object? s, CoreWebView2ContentLoadingEventArgs e)
        {
            core.ContentLoading -= Remove;
            try { core.RemoveScriptToExecuteOnDocumentCreated(id); } catch { }
        }
        core.ContentLoading += Remove;
    }

    private static bool SameHost(string? a, string b) =>
        Uri.TryCreate(a, UriKind.Absolute, out var ua) && Uri.TryCreate(b, UriKind.Absolute, out var ub) &&
        ua.Host.Equals(ub.Host, StringComparison.OrdinalIgnoreCase) && ua.Port == ub.Port;

    // ------------------------------------------------------------------ credenciais (DPAPI)

    private sealed record StoredEconomy(string Nick, string Token, string? DeviceId, string UserAgent);

    private static string Protect(EconomySession s, string userAgent)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredEconomy(s.Nick, s.Token, s.DeviceId, userAgent));
        var sealedBytes = ProtectedData.Protect(json, Encoding.UTF8.GetBytes("ThoseIdles.economy"), DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(sealedBytes);
    }

    private static (EconomySession Session, string UserAgent)? Unprotect(string protectedBase64)
    {
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedBase64), Encoding.UTF8.GetBytes("ThoseIdles.economy"), DataProtectionScope.CurrentUser);
            var s = JsonSerializer.Deserialize<StoredEconomy>(bytes);
            return s is null ? null : (new EconomySession(s.Nick, s.Token, s.DeviceId), s.UserAgent);
        }
        catch
        {
            return null;
        }
    }
}
