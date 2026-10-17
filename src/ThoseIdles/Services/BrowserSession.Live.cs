using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using ThoseIdles.Mvvm;

namespace ThoseIdles.Services;

/// <summary>Uma live da Twitch aberta numa conta (uma "aba"). Todas usam o perfil da conta.</summary>
public sealed class LiveTab : ObservableObject
{
    private WebView2? _view;
    private bool _booting, _selected, _panelVisible, _muted = true;
    private string _status = "";

    public LiveTab(string channel) => Channel = channel;

    public string Channel { get; }

    public WebView2? View
    {
        get => _view;
        internal set => Set(ref _view, value);
    }

    public CoreWebView2? Core { get; internal set; }

    /// <summary>Iniciando: fica numa faixa mínima na tela para depois virar "hidden" de verdade.</summary>
    public bool IsBooting
    {
        get => _booting;
        internal set { if (Set(ref _booting, value)) OnPropertyChanged(nameof(IsShown)); }
    }

    /// <summary>A aba escolhida no painel (a que aparece quando o painel está visível).</summary>
    public bool IsSelected
    {
        get => _selected;
        internal set { if (Set(ref _selected, value)) OnPropertyChanged(nameof(IsShown)); }
    }

    internal bool PanelVisible
    {
        get => _panelVisible;
        set { if (Set(ref _panelVisible, value)) OnPropertyChanged(nameof(IsShown)); }
    }

    /// <summary>O controle aparece? Só a aba escolhida, com o painel aberto (ou durante a inicialização).</summary>
    public bool IsShown => _booting || (_selected && _panelVisible);

    public bool Muted
    {
        get => _muted;
        internal set => Set(ref _muted, value);
    }

    public string Status
    {
        get => _status;
        internal set => Set(ref _status, value);
    }

    public uint MainFrameId { get; internal set; }

    /// <summary>iframes da live (usado só pelas verificações do autoteste).</summary>
    public List<CoreWebView2Frame> Frames { get; } = new();

    internal int Generation;
}

/// <summary>
/// Lives da Twitch desta conta. Cada live é um WebView2 no MESMO perfil da conta: o login da Twitch
/// é o da conta (o mesmo do "Vincular Twitch" do jogo), e outra conta nunca vê.
///
/// O bônus do jogo é acumulativo (+15% de XP, +2,5% por live oficial a mais aberta ao mesmo tempo) e
/// exige a ABA da live aberta e logada: o servidor procura o login no chat de cada live. Por isso cada
/// live é a página real do canal, aberta em segundo plano (escondida, como aba do Chrome), muda e com
/// padrão de 160p. Abrir só o chat, sem a live, enganaria a regra — o app não faz isso.
/// </summary>
public sealed partial class BrowserSession
{
    /// <summary>Limite de lives por conta (cada uma custa ~0,5 GB de RAM).</summary>
    public const int MaxLivesPerAccount = 10;

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

    /// <summary>Hosts onde o script de qualidade padrão pode rodar. O autoteste acrescenta 127.0.0.1.</summary>
    public static string[] LiveQualityHosts { get; set; } = { "twitch.tv" };

    private static readonly HashSet<string> ReservedTwitchPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "directory", "videos", "settings", "login", "signup", "p", "downloads", "jobs", "popout", "subscriptions",
        "inventory", "wallet", "search", "turbo", "prime", "moderator", "friends", "messages", "drops", "store",
        "broadcast", "dashboard", "following", "privacy", "legal", "security", "embed", "team", "u", "user",
    };

    [GeneratedRegex("^[A-Za-z0-9_]{2,25}$")]
    private static partial Regex TwitchLoginPattern();

    public static bool IsValidTwitchLogin(string? s) => s is not null && TwitchLoginPattern().IsMatch(s);

    private LiveTab? _selectedLive;
    private double _liveZoom = 1.0;

    /// <summary>As lives abertas nesta conta, na ordem em que foram abertas.</summary>
    public ObservableCollection<LiveTab> Lives { get; } = new();

    /// <summary>A live mostrada quando o painel está visível.</summary>
    public LiveTab? SelectedLive
    {
        get => _selectedLive;
        private set
        {
            if (_selectedLive == value) return;
            if (_selectedLive is not null) _selectedLive.IsSelected = false;
            _selectedLive = value;
            if (value is not null) value.IsSelected = true;
            OnPropertyChanged();
            OnPropertyChanged(nameof(LiveChannel));
            OnPropertyChanged(nameof(LiveMuted));
            ApplyLiveMemoryTargets();
        }
    }

    public bool IsLiveOpen => Lives.Count > 0;
    public int LiveCount => Lives.Count;

    /// <summary>Canais guardados desta conta (reabrem com ela).</summary>
    public IReadOnlyList<string> SavedLiveChannels => Account.LiveChannels;

    // Atalhos para a live escolhida (usados pela barra do painel e pelo autoteste).
    public CoreWebView2? LiveCore => SelectedLive?.Core;
    public WebView2? LiveView => SelectedLive?.View;
    public string? LiveChannel => SelectedLive?.Channel ?? Account.LiveChannels.FirstOrDefault();
    public bool LiveMuted => SelectedLive?.Muted ?? true;
    public List<CoreWebView2Frame> LiveFrames => SelectedLive?.Frames ?? new();

    /// <summary>Frames principais de todas as lives (o MemoryMonitor separa a memória delas).</summary>
    public IEnumerable<uint> LiveMainFrameIds => Lives.Select(l => l.MainFrameId).Where(id => id != 0);

    public LiveTab? FindLive(string channel) => Lives.FirstOrDefault(l => l.Channel == channel);

    /// <summary>
    /// O painel aparece na tela? Escondido, todas as lives continuam abertas e logadas — como abas do
    /// Chrome em segundo plano, que é o que o jogo exige.
    /// </summary>
    public bool IsLiveVisible => Account.LiveVisible;

    /// <summary>Alguma live ainda iniciando (o painel ocupa uma faixa mínima enquanto isso).</summary>
    public bool IsLiveBooting => Lives.Any(l => l.IsBooting);

    /// <summary>Mostra/esconde o painel sem fechar nenhuma live.</summary>
    public void SetLiveVisible(bool visible)
    {
        if (Account.LiveVisible == visible) return;
        Account.LiveVisible = visible;
        foreach (var l in Lives) l.PanelVisible = visible;
        OnPropertyChanged(nameof(IsLiveVisible));
        SettingsChanged?.Invoke(this, EventArgs.Empty);
        ApplyLiveMemoryTargets();
        LoggingService.Log.Info($"[conta{Slot}] Painel de lives {(visible ? "visível" : "escondido (lives em segundo plano)")}.");
    }

    public void SelectLive(LiveTab tab)
    {
        if (Lives.Contains(tab)) SelectedLive = tab;
    }

    /// <summary>Escondidas: meta de memória baixa. Só a live mostrada na tela fica normal.</summary>
    private void ApplyLiveMemoryTargets()
    {
        foreach (var l in Lives)
        {
            if (l.Core is null) continue;
            try
            {
                var shown = Account.LiveVisible && l == SelectedLive;
                l.Core.MemoryUsageTargetLevel = !shown && _settings.LiveHiddenLowMemory
                    ? CoreWebView2MemoryUsageTargetLevel.Low
                    : CoreWebView2MemoryUsageTargetLevel.Normal;
            }
            catch { }
        }
    }

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

    /// <summary>
    /// Abre mais uma live nesta conta (se o canal já estiver aberto, só a seleciona). A conta precisa
    /// estar aberta. As lives se somam: o bônus do jogo cresce com cada live oficial aberta.
    /// </summary>
    public async Task OpenLiveAsync(string channel)
    {
        if ((_core is null && _eco is null) || !TwitchLoginPattern().IsMatch(channel)) return; // conta aberta (página ou modo econômico)
        channel = channel.ToLowerInvariant();

        if (FindLive(channel) is { } existing)
        {
            SelectedLive = existing;
            return;
        }
        if (Lives.Count >= MaxLivesPerAccount)
        {
            LoggingService.Log.Warn($"[conta{Slot}] Limite de {MaxLivesPerAccount} lives por conta; {channel} não foi aberta.");
            LiveLimitReached?.Invoke(this, channel);
            return;
        }

        if (!Account.LiveChannels.Contains(channel))
        {
            Account.LiveChannels.Add(channel);
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }

        var tab = new LiveTab(channel) { PanelVisible = Account.LiveVisible, Status = $"Abrindo {channel}…" };
        var gen = ++tab.Generation;
        var wv = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 14, 14, 16),
            ZoomFactor = _liveZoom,
        };
        // Nasce visível numa faixa mínima e é recolhida logo depois de iniciar: assim a página fica
        // "hidden" de verdade (o controle WPF só avisa o navegador quando a visibilidade MUDA).
        tab.IsBooting = true;
        tab.View = wv;
        Lives.Add(tab);
        OnLivesChanged();
        SelectedLive = tab;

        try
        {
            var env = await _profiles.GetEnvironmentAsync();
            if (gen != tab.Generation) return;
            // Mesmo perfil da conta: mesmos cookies da Twitch.
            await wv.EnsureCoreWebView2Async(env, _profiles.CreateControllerOptions(env, Slot));
            if (gen != tab.Generation) return;
            tab.IsBooting = false;
            OnPropertyChanged(nameof(IsLiveBooting));

            var core = wv.CoreWebView2;
            tab.Core = core;
            var s = core.Settings;
            s.AreDevToolsEnabled = _settings.DeveloperMode;
            s.IsStatusBarEnabled = false;
            s.AreHostObjectsAllowed = false;
            s.IsWebMessageEnabled = false;
            s.IsSwipeNavigationEnabled = false;

            core.IsMuted = _settings.LiveMutedByDefault;
            tab.Muted = core.IsMuted;
            if (_settings.LiveLowestQuality)
                await core.AddScriptToExecuteOnDocumentCreatedAsync(LowestQualityScript());
            if (_settings.LiveLightMode)
                core.SetVirtualHostNameToFolderMapping(LightHost, EnsureLightPageFolder(), CoreWebView2HostResourceAccessKind.DenyCors);

            // Os handlers capturam a aba; somem junto com o controller quando a live é fechada.
            core.NavigationStarting += OnLiveNavigationStarting;
            core.NewWindowRequested += OnLiveNewWindowRequested;
            core.ContentLoading += (_, _) => { try { tab.MainFrameId = core.FrameId; } catch { } };
            core.NavigationCompleted += (_, e) => tab.Status = e.IsSuccess || e.WebErrorStatus == CoreWebView2WebErrorStatus.OperationCanceled
                ? "" : $"não carregou ({e.WebErrorStatus})";
            core.ProcessFailed += (_, e) => OnLiveProcessFailed(tab, e);
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
            core.FrameCreated += (_, e) =>
            {
                var frame = e.Frame;
                tab.Frames.Add(frame);
                frame.Destroyed += (_, _) => tab.Frames.Remove(frame);
            };

            ApplyLiveZoom();
            ApplyLiveMemoryTargets();
            LoggingService.Log.Info($"[conta{Slot}] Live aberta: {channel} ({Lives.Count} na conta; mudo={core.IsMuted}, {(Account.LiveVisible ? "painel visível" : "em segundo plano")}).");
            core.Navigate(LiveAddress(channel));
        }
        catch (Exception ex) when (gen == tab.Generation)
        {
            LoggingService.Log.Error($"[conta{Slot}] Falha ao abrir a live {channel}", ex);
            tab.Status = $"falha ao abrir: {ex.Message}";
            DisposeTab(tab);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Info($"[conta{Slot}] Abertura da live {channel} descartada ({ex.GetType().Name}).");
        }
    }

    /// <summary>Reabre todas as lives guardadas desta conta.</summary>
    public async Task OpenSavedLivesAsync()
    {
        foreach (var ch in Account.LiveChannels.ToList()) await OpenLiveAsync(ch);
    }

    /// <summary>Fecha uma live. <paramref name="forget"/> = tirar o canal da lista (não reabre com a conta).</summary>
    public void CloseLive(LiveTab tab, bool forget = true)
    {
        DisposeTab(tab);
        if (forget && Account.LiveChannels.Remove(tab.Channel)) SettingsChanged?.Invoke(this, EventArgs.Empty);
        LoggingService.Log.Info($"[conta{Slot}] Live {tab.Channel} fechada{(forget ? "" : " (será reaberta com a conta)")}.");
    }

    /// <summary>Fecha todas as lives. <paramref name="forget"/> = esquecer também os canais guardados.</summary>
    public void CloseLive(bool forget = true)
    {
        foreach (var tab in Lives.ToList()) DisposeTab(tab);
        if (forget && Account.LiveChannels.Count > 0)
        {
            Account.LiveChannels.Clear();
            SettingsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetLiveMuted(bool muted)
    {
        if (SelectedLive is not { Core: { } core } tab) return;
        core.IsMuted = muted;
        tab.Muted = muted;
        OnPropertyChanged(nameof(LiveMuted));
    }

    /// <summary>Zoom das lives para caber no painel mantendo o layout desktop (vídeo + chat ao lado).</summary>
    public void SetLiveZoom(double zoom)
    {
        _liveZoom = Math.Clamp(zoom, 0.25, 1.0);
        ApplyLiveZoom();
    }

    private void ApplyLiveZoom()
    {
        foreach (var l in Lives)
        {
            if (l.View is not { } v) continue;
            try { if (Math.Abs(v.ZoomFactor - _liveZoom) > 0.001) v.ZoomFactor = _liveZoom; }
            catch { }
        }
    }

    /// <summary>
    /// Recarrega as lives desta conta, uma de cada vez (intervalo entre elas), para soltar a memória
    /// que o chat acumula. Não mexe na visibilidade nem no jogo. Retorna quantas recarregou.
    /// </summary>
    public async Task<int> ReloadLivesAsync(TimeSpan gap)
    {
        var count = 0;
        foreach (var tab in Lives.ToList())
        {
            if (tab.Core is not { } core || !Lives.Contains(tab)) continue;
            try
            {
                core.Reload();
                count++;
                LoggingService.Log.Info($"[conta{Slot}] Live {tab.Channel} recarregada para liberar memória.");
            }
            catch (Exception ex)
            {
                LoggingService.Log.Warn($"[conta{Slot}] Recarga da live {tab.Channel}: {ex.Message}");
            }
            if (gap > TimeSpan.Zero) await Task.Delay(gap);
        }
        return count;
    }

    /// <summary>A conta tentou abrir mais lives que o limite.</summary>
    public event EventHandler<string>? LiveLimitReached;

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

    private void DisposeTab(LiveTab tab)
    {
        tab.Generation++;
        var core = tab.Core;
        tab.Core = null;
        if (core is not null)
        {
            try
            {
                core.NavigationStarting -= OnLiveNavigationStarting;
                core.NewWindowRequested -= OnLiveNewWindowRequested;
                core.PermissionRequested -= OnPermissionRequested;
                core.DownloadStarting -= OnDownloadStarting;
                core.LaunchingExternalUriScheme -= OnLaunchingExternalUriScheme;
            }
            catch { }
        }
        tab.Frames.Clear();
        tab.MainFrameId = 0;
        tab.IsBooting = false;
        var wv = tab.View;
        var index = Lives.IndexOf(tab);
        Lives.Remove(tab); // sai da árvore visual antes de ser descartada
        tab.View = null;
        if (wv is not null)
        {
            try { wv.Dispose(); }
            catch (Exception ex) { LoggingService.Log.Warn($"[conta{Slot}] Dispose da live {tab.Channel}: {ex.Message}"); }
        }
        if (SelectedLive == tab)
            SelectedLive = Lives.Count == 0 ? null : Lives[Math.Clamp(index, 0, Lives.Count - 1)];
        OnLivesChanged();
    }

    private void OnLivesChanged()
    {
        OnPropertyChanged(nameof(IsLiveOpen));
        OnPropertyChanged(nameof(LiveCount));
        OnPropertyChanged(nameof(IsLiveBooting));
        OnPropertyChanged(nameof(LiveChannel));
    }

    // ------------------------------------------------------------------ eventos das lives

    private void OnLiveNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Dentro das lives só a Twitch (e o login dela) ou a página do modo leve. O resto vai para o navegador padrão.
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
        // Link para outro canal dentro de uma live: abre como mais uma live da conta.
        if (TryParseTwitchChannel(e.Uri, out var channel))
        {
            _ = OpenLiveAsync(channel);
            return;
        }
        if (NavigationPolicy.IsSafeExternalUrl(e.Uri) && DateTime.UtcNow - _lastExternalOpenUtc > TimeSpan.FromSeconds(2))
            OpenExternal(e.Uri);
    }

    private void OnLiveProcessFailed(LiveTab tab, CoreWebView2ProcessFailedEventArgs e)
    {
        LoggingService.Log.Warn($"[conta{Slot}] Live {tab.Channel}: ProcessFailed {e.ProcessFailedKind} ({e.Reason})");
        if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
        {
            // Recria só esta live; o jogo e as outras lives não são tocados.
            DisposeTab(tab);
            _ = OpenLiveAsync(tab.Channel);
        }
    }
}
