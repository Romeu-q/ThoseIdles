using System.Diagnostics;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using ThoseIdles.Shared;

namespace ThoseIdles.Services;

/// <summary>Soma de memória de um grupo de processos (bytes).</summary>
public struct MemoryGroup
{
    public long PrivateBytes;
    public long WorkingSet;
    public long PrivateWorkingSet;
    public int Processes;

    public void Add(in ProcessMemory m)
    {
        PrivateBytes += m.PrivateBytes;
        WorkingSet += m.WorkingSet;
        if (m.PrivateWorkingSet > 0) PrivateWorkingSet += m.PrivateWorkingSet;
        Processes++;
    }
}

public sealed record ProcessDetail(int Pid, string Kind, int? Slot, ProcessMemory Memory);

public sealed record MemorySnapshot(
    DateTime TimeUtc,
    MemoryGroup Total,
    MemoryGroup Host,
    MemoryGroup Shared,
    MemoryGroup[] Accounts, // índice 0..3 = conta1..conta4 (o jogo)
    MemoryGroup[] Lives,    // índice 0..3 = renderers da live da Twitch de cada conta
    IReadOnlyList<ProcessDetail> Processes);

/// <summary>
/// Mede periodicamente o processo do app e TODOS os processos auxiliares do WebView2
/// (browser, GPU, rede, utilitários, renderers e crashpad), por árvore de processos.
/// A classificação por conta usa GetProcessExtendedInfosAsync: o renderer cujo frame raiz é o
/// frame principal de uma conta é atribuído a ela; o resto é "compartilhado".
/// </summary>
public sealed class MemoryMonitor : IDisposable
{
    private readonly BrowserSessionManager _sessions;
    private readonly DispatcherTimer _timer;
    private readonly int _hostPid = Environment.ProcessId;
    private bool _busy;

    public event EventHandler<MemorySnapshot>? Sampled;
    public MemorySnapshot? Last { get; private set; }

    public MemoryMonitor(BrowserSessionManager sessions, int intervalSeconds)
    {
        _sessions = sessions;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(intervalSeconds) };
        _timer.Tick += OnTick;
    }

    public void Start() => _timer.Start();
    public void Stop() => _timer.Stop();
    public void SetInterval(int seconds) => _timer.Interval = TimeSpan.FromSeconds(Math.Clamp(seconds, 2, 60));

    private async void OnTick(object? sender, EventArgs e)
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var snap = await SampleAsync();
            Last = snap;
            Sampled?.Invoke(this, snap);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"Amostra de memória falhou: {ex.Message}");
        }
        finally
        {
            _busy = false;
        }
    }

    public async Task<MemorySnapshot> SampleAsync()
    {
        // 1) Classificação pelo WebView2.
        var kindByPid = new Dictionary<int, (string Kind, int? Slot)>();
        var livePids = new HashSet<int>();
        var env = _sessions.Profiles.Environment;
        if (env is not null)
        {
            var slotByFrame = new Dictionary<uint, int>();
            var liveSlotByFrame = new Dictionary<uint, int>();
            foreach (var s in _sessions.Sessions)
            {
                if (s.MainFrameId != 0) slotByFrame[s.MainFrameId] = s.Slot;
                foreach (var id in s.LiveMainFrameIds) liveSlotByFrame[id] = s.Slot;
            }

            try
            {
                var infos = await env.GetProcessExtendedInfosAsync();
                foreach (var info in infos)
                {
                    var p = info.ProcessInfo;
                    int? slot = null;
                    if (p.Kind == CoreWebView2ProcessKind.Renderer)
                    {
                        foreach (var frame in info.AssociatedFrameInfos)
                        {
                            var root = frame;
                            for (var depth = 0; root.ParentFrameInfo is { } parent && depth < 32; depth++) root = parent;
                            if (slotByFrame.TryGetValue(root.FrameId, out var found)) { slot = found; break; }
                            if (liveSlotByFrame.TryGetValue(root.FrameId, out var liveSlot)) { slot = liveSlot; livePids.Add(p.ProcessId); break; }
                        }
                    }
                    kindByPid[p.ProcessId] = (KindName(p.Kind), slot);
                }
            }
            catch (Exception ex)
            {
                LoggingService.Log.Warn($"GetProcessExtendedInfosAsync falhou: {ex.Message}");
            }
        }

        // 2) Leitura de memória da árvore inteira (inclui processos que a API não lista, ex. crashpad).
        var tree = ProcessTree.Descendants(ProcessTree.Snapshot(), _hostPid);
        var total = new MemoryGroup();
        var host = new MemoryGroup();
        var shared = new MemoryGroup();
        var accounts = new MemoryGroup[AppSettings.AccountCount];
        var lives = new MemoryGroup[AppSettings.AccountCount];
        var details = new List<ProcessDetail>(tree.Count);

        foreach (var entry in tree)
        {
            if (!ProcessMemoryReader.TryRead(entry.Pid, out var mem)) continue;
            total.Add(mem);
            if (entry.Pid == _hostPid)
            {
                host.Add(mem);
                details.Add(new ProcessDetail(entry.Pid, "app (WPF)", null, mem));
                continue;
            }

            if (kindByPid.TryGetValue(entry.Pid, out var k) && k.Slot is int slot && slot >= 1 && slot <= accounts.Length)
            {
                var isLive = livePids.Contains(entry.Pid);
                if (isLive) lives[slot - 1].Add(mem);
                else accounts[slot - 1].Add(mem);
                details.Add(new ProcessDetail(entry.Pid, isLive ? "renderer (live)" : k.Kind, slot, mem));
            }
            else
            {
                shared.Add(mem);
                details.Add(new ProcessDetail(entry.Pid, kindByPid.TryGetValue(entry.Pid, out var k2) ? k2.Kind : entry.ExeName, null, mem));
            }
        }

        return new MemorySnapshot(DateTime.UtcNow, total, host, shared, accounts, lives, details);
    }

    private static string KindName(CoreWebView2ProcessKind kind) => kind switch
    {
        CoreWebView2ProcessKind.Browser => "navegador",
        CoreWebView2ProcessKind.Renderer => "renderer",
        CoreWebView2ProcessKind.Gpu => "gpu",
        CoreWebView2ProcessKind.Utility => "utilitário",
        CoreWebView2ProcessKind.PpapiPlugin => "plugin",
        CoreWebView2ProcessKind.PpapiBroker => "plugin-broker",
        CoreWebView2ProcessKind.SandboxHelper => "sandbox-helper",
        _ => kind.ToString(),
    };

    public static string Format(long bytes) => bytes switch
    {
        <= 0 => "—",
        < 1L << 30 => $"{bytes / 1048576.0:0} MB",
        _ => $"{bytes / 1073741824.0:0.00} GB",
    };

    public void Dispose()
    {
        _timer.Stop();
        _timer.Tick -= OnTick;
    }
}
