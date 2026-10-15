using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ThoseIdles.Services;

/// <summary>
/// Painel de live da Twitch desta conta. É um segundo WebView2 no MESMO perfil da conta: o login
/// da Twitch é o da conta (o mesmo que o jogo usa no "Vincular Twitch"), e outra conta nunca vê.
///
/// O bônus do jogo (+15% de XP, +2,5% por live a mais) é para quem ASSISTE a uma live oficial
/// logado na Twitch vinculada: o servidor procura o login no chat da live. Por isso o painel abre a
/// página real do canal (vídeo + chat), só que mudo e na menor qualidade por padrão. Abrir só o
/// chat, sem a live, enganaria a regra — o app não faz isso.
/// </summary>
public sealed partial class BrowserSession
{
    /// <summary>Endereço da live de um canal. Substituível no autoteste (página local).</summary>
    public static Func<string, string> LiveUrlForChannel { get; set; } = c => $"https://www.twitch.tv/{c}";

    /// <summary>Host virtual (só existe dentro do app) da página do modo leve.</summary>
    public const string LightHost = "live.thoseidles.example";

    /// <summary>
    /// Página do modo leve: player e chat OFICIAIS da Twitch incorporados (o mesmo embed que outros
    /// sites usam), lado a lado. Fica numa pasta do app, servida pelo host virtual acima — o "parent"
    /// exigido pela Twitch é esse host.
    /// </summary>
    private const string LightPage = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>live</title>
        <style>
          html, body { margin: 0; height: 100%; background: #0e0e10; }
          body { display: flex; }
          #player { flex: 1; border: 0; min-width: 0; }
          #chat { width: 340px; border: 0; border-left: 1px solid #26262c; }
        </style></head>
        <body>
        <iframe id="player" allow="autoplay; fullscreen; encrypted-media"></iframe>
        <iframe id="chat"></iframe>
        <script>
          const q = new URLSearchParams(location.search);
          const ch = encodeURIComponent(q.get('c') || '');
          const parent = location.hostname;
          const muted = q.get('m') === '1';
          document.getElementById('player').src =
            `https://player.twitch.tv/?channel=${ch}&parent=${parent}&autoplay=true&muted=${muted}`;
          document.getElementById('chat').src =
            `https://www.twitch.tv/embed/${ch}/chat?parent=${parent}&darkpopout`;
        </script>
        </body></html>
        """;

    private string LiveAddress(string channel) => _settings.LiveLightMode && LiveUrlForChannel(channel).StartsWith("https://www.twitch.tv/", StringComparison.Ordinal)
        ? $"https://{LightHost}/live.html?c={Uri.EscapeDataString(channel)}&m={(_settings.LiveMutedByDefault ? 1 : 0)}"
        : LiveUrlForChannel(channel);

    private static string EnsureLightPageFolder()
    {
        var folder = Path.Combine(AppPaths.Root, "live-host");
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "live.html");
        if (!File.Exists(file) || File.ReadAllText(file) != LightPage) File.WriteAllText(file, LightPage);
        return folder;
    }

    /// <summary>Hosts onde o script de qualidade padrão pode rodar. O autoteste acrescenta localhost.</summary>
    public static string[] LiveQualityHosts { get; set; } = { "twitch.tv" };

    private static readonly HashSet<string> ReservedTwitchPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "directory", "videos", "settings", "login", "signup", "p", "downloads", "jobs", "popout", "subscriptions",
        "inventory", "wallet", "search", "turbo", "prime", "moderator", "friends", "messages", "drops", "store",
        "broadcast", "dashboard", "following", "privacy", "legal", "security", "embed", "team", "u", "user",
    };

    [GeneratedRegex("^[A-Za-z0-9_]{2,25}$")]
    private static partial Regex TwitchLoginPattern();

    private WebView2? _liveView;
    private CoreWebView2? _liveCore;
    private int _liveGeneration;
    private bool _liveMuted = true;
    private string _liveStatus = "";
    private double _liveZoom = 1.0;

    /// <summary>O controle da live (null quando fechada). A view o hospeda abaixo do jogo.</summary>
    public WebView2? LiveView
    {
        get => _liveView;
        private set
        {
            if (Set(ref _liveView, value)) OnPropertyChanged(nameof(IsLiveOpen));
        }
    }

    public CoreWebView2? LiveCore => _liveCore;
    public bool IsLiveOpen => _liveView is not null;
    public string? LiveChannel => Account.LiveChannel;

    /// <summary>
    /// A live aparece na tela? Escondida, ela continua aberta e logada — como uma aba do Chrome em
    /// segundo plano, que é o que o jogo exige. O controle fica na árvore visual, só recolhido.
    /// </summary>
    public bool IsLiveVisible => Account.LiveVisible;

    private bool _liveBooting;

    /// <summary>
    /// Durante a inicialização a live fica numa faixa mínima na tela. O controle WPF do WebView2 só
    /// avisa o navegador quando a visibilidade MUDA; nascendo já escondida, a página ficaria "visible"
    /// (desenhando e decodificando vídeo à toa). Ao recolher depois de iniciar, ela vira "hidden" de
    /// verdade — o mesmo estado de uma aba do Chrome em segundo plano.
    /// </summary>
    public bool IsLiveBooting
    {
        get => _liveBooting;
        private set => Set(ref _liveBooting, value);
    }

    /// <summary>Mostra/esconde a live sem fechá-la.</summary>
    public void SetLiveVisible(bool visible)
    {
        if (Account.LiveVisible == visible) return;
        Account.LiveVisible = visible;
        OnPropertyChanged(nameof(IsLiveVisible));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        ApplyLiveMemoryTarget();
        LoggingService.Log.Info($"[conta{Slot}] Live {(visible ? "visível" : "em segundo plano")}.");
    }

    /// <summary>Escondida: meta de memória baixa (só nesta aba de live). Visível: normal.</summary>
    private void ApplyLiveMemoryTarget()
    {
        if (_liveCore is null) return;
        try
        {
            _liveCore.MemoryUsageTargetLevel = !Account.LiveVisible && _settings.LiveHiddenLowMemory
                ? CoreWebView2MemoryUsageTargetLevel.Low
                : CoreWebView2MemoryUsageTargetLevel.Normal;
        }
        catch { }
    }

    public bool LiveMuted
    {
        get => _liveMuted;
        private set => Set(ref _liveMuted, value);
    }

    public string LiveStatus
    {
        get => _liveStatus;
        private set => Set(ref _liveStatus, value);
    }

    /// <summary>Frame principal da live (para o MemoryMonitor separar a memória da live).</summary>
    public uint LiveMainFrameId { get; private set; }

    /// <summary>"https://www.twitch.tv/canal" → "canal". Ignora páginas que não são de canal.</summary>
    public static bool TryParseTwitchChannel(string? url, out string channel)
    {
        channel = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host.ToLowerInvariant();
        if (host is not ("twitch.tv" or "www.twitch.tv" or "m.twitch.tv")) return false;
        var parts = u.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 1 || ReservedTwitchPaths.Contains(parts[0]) || !TwitchLoginPattern().IsMatch(parts[0])) return false;
        channel = parts[0].ToLowerInvariant();
        return true;
    }

    /// <summary>Abre (ou troca) a live desta conta. A conta precisa estar aberta.</summary>
    public async Task OpenLiveAsync(string channel)
    {
        if (_core is null || !TwitchLoginPattern().IsMatch(channel)) return;
        channel = channel.ToLowerInvariant();
        var changed = Account.LiveChannel != channel;
        Account.LiveChannel = channel;
        OnPropertyChanged(nameof(LiveChannel));
        if (changed) SettingsChanged?.Invoke(this, EventArgs.Empty);

        if (_liveCore is not null)
        {
            if (changed) _liveCore.Navigate(LiveAddress(channel));
            return;
        }
        if (_liveView is not null) return; // já está abrindo

        var gen = ++_liveGeneration;
        LiveStatus = $"Abrindo {channel}…";
        var wv = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 14, 16),
            ZoomFactor = _liveZoom,
        };
        IsLiveBooting = !Account.LiveVisible;
        LiveView = wv;
        try
        {
            var env = await _profiles.GetEnvironmentAsync();
            if (gen != _liveGeneration) return;
            // Mesmo perfil da conta: mesmos cookies da Twitch.
            await wv.EnsureCoreWebView2Async(env, _profiles.CreateControllerOptions(env, Slot));
            if (gen != _liveGeneration) return;
            IsLiveBooting = false; // recolhe: o controller passa a "hidden" se a live deve ficar escondida

            var core = wv.CoreWebView2;
            _liveCore = core;
            var s = core.Settings;
            s.AreDevToolsEnabled = _settings.DeveloperMode;
            s.IsStatusBarEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            core.IsMuted = _settings.LiveMutedByDefault;
            LiveMuted = core.IsMuted;
            if (_settings.LiveLowestQuality)
                await core.AddScriptToExecuteOnDocumentCreatedAsync(LowestQualityScript());

            if (_settings.LiveLightMode)
                core.SetVirtualHostNameToFolderMapping(LightHost, EnsureLightPageFolder(), CoreWebView2HostResourceAccessKind.DenyCors);

            core.NavigationStarting += OnLiveNavigationStarting;
            core.NewWindowRequested += OnLiveNewWindowRequested;
            core.ContentLoading += OnLiveContentLoading;
            core.NavigationCompleted += OnLiveNavigationCompleted;
            core.ProcessFailed += OnLiveProcessFailed;
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.FrameCreated += OnLiveFrameCreated;

            ApplyLiveZoom();
            ApplyLiveMemoryTarget();
            LoggingService.Log.Info($"[conta{Slot}] Live aberta: {channel} (mudo={core.IsMuted}, modo {(_settings.LiveLightMode ? "leve" : "completo")}, {(Account.LiveVisible ? "visível" : "em segundo plano")}).");
            core.Navigate(LiveAddress(channel));
        }
        catch (Exception ex) when (gen == _liveGeneration)
        {
            LoggingService.Log.Error($"[conta{Slot}] Falha ao abrir a live", ex);
            DisposeLive();
            LiveStatus = $"Falha ao abrir a live: {ex.Message}";
        }
        catch (Exception ex)
        {
            LoggingService.Log.Info($"[conta{Slot}] Abertura da live descartada ({ex.GetType().Name}).");
        }
    }

    /// <summary>Fecha a live. <paramref name="forget"/> = não reabrir na próxima vez que a conta abrir.</summary>
    public void CloseLive(bool forget = true)
    {
        DisposeLive();
        if (forget && Account.LiveChannel is not null)
        {
            Account.LiveChannel = null;
            OnPropertyChanged(nameof(LiveChannel));
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
        LoggingService.Log.Info($"[conta{Slot}] Live fechada{(forget ? "" : " (será reaberta com a conta)")}.");
    }

    public void SetLiveMuted(bool muted)
    {
        if (_liveCore is null) return;
        _liveCore.IsMuted = muted;
        LiveMuted = muted;
    }

    /// <summary>Zoom da live para caber no painel mantendo o layout desktop (vídeo + chat ao lado).</summary>
    public void SetLiveZoom(double zoom)
    {
        _liveZoom = Math.Clamp(zoom, 0.25, 1.0);
        ApplyLiveZoom();
    }

    private void ApplyLiveZoom()
    {
        if (_liveView is null) return;
        try
        {
            if (Math.Abs(_liveView.ZoomFactor - _liveZoom) > 0.001) _liveView.ZoomFactor = _liveZoom;
        }
        catch { }
    }

    /// <summary>Padrão de qualidade 160p, gravado UMA vez: se o jogador trocar no player, a escolha dele vale.</summary>
    private static string LowestQualityScript()
    {
        var hosts = string.Join(",", LiveQualityHosts.Select(h => $"'{h}'"));
        return $$"""
            (() => {
              try {
                const h = location.hostname;
                if (![{{hosts}}].some(x => h === x || h.endsWith('.' + x))) return;
                if (localStorage.getItem('__thoseidles_qualidade')) return;
                localStorage.setItem('video-quality', JSON.stringify({ default: '160p30' }));
                localStorage.setItem('__thoseidles_qualidade', '1');
              } catch {}
            })();
            """;
    }

    private void DisposeLive()
    {
        _liveGeneration++;
        var core = _liveCore;
        _liveCore = null;
        if (core is not null)
        {
            try
            {
                core.NavigationStarting -= OnLiveNavigationStarting;
                core.NewWindowRequested -= OnLiveNewWindowRequested;
                core.ContentLoading -= OnLiveContentLoading;
                core.NavigationCompleted -= OnLiveNavigationCompleted;
                core.ProcessFailed -= OnLiveProcessFailed;
                core.PermissionRequested -= OnPermissionRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
                core.FrameCreated -= OnLiveFrameCreated;
            }
            catch { }
        }
        LiveFrames.Clear();
        LiveMainFrameId = 0;
        IsLiveBooting = false;
        LiveStatus = "";
        var wv = _liveView;
        LiveView = null;
        if (wv is not null)
        {
            try { wv.Dispose(); }
            catch (Exception ex) { LoggingService.Log.Warn($"[conta{Slot}] Dispose da live: {ex.Message}"); }
        }
    }

    // ------------------------------------------------------------------ eventos da live

    /// <summary>iframes da live (no modo leve: player e chat). Usado só pelas verificações do autoteste.</summary>
    public List<CoreWebView2Frame> LiveFrames { get; } = new();

    private void OnLiveFrameCreated(object? sender, CoreWebView2FrameCreatedEventArgs e)
    {
        var frame = e.Frame;
        LiveFrames.Add(frame);
        frame.Destroyed += (_, _) => LiveFrames.Remove(frame);
    }

    private void OnLiveNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Dentro do painel só a Twitch (e o login dela) ou a página do modo leve. O resto vai para o navegador padrão.
        var isLight = Uri.TryCreate(e.Uri, UriKind.Absolute, out var lu) && lu.Scheme == Uri.UriSchemeHttps &&
                      lu.Host.Equals(LightHost, StringComparison.OrdinalIgnoreCase) && _settings.LiveLightMode;
        if (isLight) return;
        var decision = _policy.Evaluate(e.Uri, e.IsRedirected, e.IsUserInitiated);
        var isTwitch = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) &&
                       (u.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".twitch.tv", StringComparison.OrdinalIgnoreCase));
        var isLocalTest = _policy.AllowLocalhostHttp && u is not null && u.IsLoopback;
        if (decision == NavigationDecision.Allow && (isTwitch || isLocalTest || e.IsRedirected || e.Uri == "about:blank")) return;

        e.Cancel = true;
        if (e.IsUserInitiated && NavigationPolicy.IsSafeExternalUrl(e.Uri)) OpenExternal(e.Uri);
    }

    private void OnLiveNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;
        if (TryParseTwitchChannel(e.Uri, out var channel))
        {
            _ = OpenLiveAsync(channel);
            return;
        }
        if (NavigationPolicy.IsSafeExternalUrl(e.Uri) && DateTime.UtcNow - _lastExternalOpenUtc > TimeSpan.FromSeconds(2))
            OpenExternal(e.Uri);
    }

    private void OnLiveContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    {
        if (_liveCore is null) return;
        try { LiveMainFrameId = _liveCore.FrameId; }
        catch { }
    }

    private void OnLiveNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (e.IsSuccess)
        {
            LiveStatus = "";
            if (TryParseTwitchChannel(_liveCore?.Source, out var ch) && ch != Account.LiveChannel)
            {
                // O jogador navegou para outro canal dentro do painel: passa a ser o canal desta conta.
                Account.LiveChannel = ch;
                OnPropertyChanged(nameof(LiveChannel));
                SettingsChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        else if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
        {
            LiveStatus = $"A live não carregou ({e.WebErrorStatus})";
        }
    }

    private void OnLiveProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        LoggingService.Log.Warn($"[conta{Slot}] Live: ProcessFailed {e.ProcessFailedKind} ({e.Reason})");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive
            && Account.LiveChannel is { } ch)
        {
            // Recria só a live; o jogo da conta não é tocado.
            DisposeLive();
            _ = OpenLiveAsync(ch);
        }
    }
}
