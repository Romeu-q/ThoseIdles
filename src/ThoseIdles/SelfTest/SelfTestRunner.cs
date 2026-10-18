using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using ThoseIdles.Services;
using ThoseIdles.ViewModels;

namespace ThoseIdles.SelfTest;

public sealed record TestResult(string Name, bool Passed, string Detail, double Seconds);

/// <summary>
/// Autoteste de ponta a ponta com WebView2 real, numa pasta de dados separada.
/// Fase "run": isolamento, ciclo de vida, limpeza isolada, execução em segundo plano, memória.
/// Fase "verify": nova execução do app — confirma que as sessões persistiram.
/// </summary>
public sealed class SelfTestRunner
{
    private readonly BrowserSessionManager _manager;
    private readonly MainViewModel _vm;
    private readonly MemoryMonitor _memory;
    private readonly Window _window;
    private readonly string _resultFile;
    private readonly List<TestResult> _results = new();

    public SelfTestRunner(BrowserSessionManager manager, MainViewModel vm, MemoryMonitor memory, Window window, string resultFile)
    {
        _manager = manager;
        _vm = vm;
        _memory = memory;
        _window = window;
        _resultFile = resultFile;
    }

    public async Task<bool> RunAsync(bool verifyPhase)
    {
        LoggingService.Log.Info($"=== Autoteste, fase {(verifyPhase ? "verify" : "run")} ===");
        try
        {
            if (verifyPhase) await VerifyPhaseAsync();
            else
            {
                await RunPhaseAsync();
                await LeaveAccountInEconomyForVerifyAsync();
            }
        }
        catch (Exception ex)
        {
            _results.Add(new TestResult("exceção inesperada", false, ex.ToString(), 0));
        }

        var ok = _results.Count > 0 && _results.All(r => r.Passed);
        var json = JsonSerializer.Serialize(new { phase = verifyPhase ? "verify" : "run", passed = ok, results = _results },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        File.WriteAllText(_resultFile, json);
        foreach (var r in _results) LoggingService.Log.Info($"[autoteste] {(r.Passed ? "OK  " : "FALHOU")} {r.Name} — {r.Detail}");
        return ok;
    }

    // ------------------------------------------------------------------ medição on-line de lives da Twitch

    /// <summary>
    /// Mede o custo real de lives da Twitch: 4 contas numa página neutra (sem live), depois live numa
    /// conta, depois live nas 4. Cada fase espera a página assentar e mede memória (total, jogo,
    /// live, compartilhado) e a CPU da árvore inteira do app por 10 s.
    /// </summary>
    public async Task<bool> RunTwitchMeasureAsync(string channel)
    {
        LoggingService.Log.Info($"=== Medição de lives da Twitch (canal {channel}) ===");
        var phases = new List<(string Name, MemorySnapshot Mem, double Cpu)>();
        try
        {
            await _manager.StartAllAsync();
            await WaitUntil(() => _manager.Sessions.All(s => s.Status is SessionStatus.AwaitingConnection or SessionStatus.Loaded), TimeSpan.FromSeconds(60));
            await Task.Delay(20000);
            phases.Add(("4 contas, sem live", await _memory.SampleAsync(), await TreeCpuAsync(TimeSpan.FromSeconds(10))));

            // "--live-channel a,b,c": lives ACUMULADAS na conta 1 (o bônus soma por live). Um canal só:
            // compara 1 live x a mesma live nas 4 contas.
            var channels = channel.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            (bool Ok, string Detail) check = default;
            if (channels.Length > 1)
            {
                for (var i = 0; i < channels.Length; i++)
                {
                    await _manager[1].OpenLiveAsync(channels[i]);
                    await Task.Delay(40000);
                    phases.Add(($"conta 1 com {i + 1} live(s) ({string.Join(", ", channels.Take(i + 1))})",
                        await _memory.SampleAsync(), await TreeCpuAsync(TimeSpan.FromSeconds(10))));
                    if (i == 0) check = await LiveCheckAsync(_manager[1]);
                }
            }
            else
            {
                await _manager[1].OpenLiveAsync(channel);
                await Task.Delay(45000);
                phases.Add(("4 contas, live em 1", await _memory.SampleAsync(), await TreeCpuAsync(TimeSpan.FromSeconds(10))));
                check = await LiveCheckAsync(_manager[1]);

                foreach (var slot in new[] { 2, 3, 4 }) await _manager[slot].OpenLiveAsync(channel);
                await Task.Delay(45000);
                phases.Add(("4 contas, live nas 4", await _memory.SampleAsync(), await TreeCpuAsync(TimeSpan.FromSeconds(10))));
            }

            foreach (var (name, m, cpu) in phases)
            {
                var lives = m.Lives.Where(l => l.Processes > 0).Select(l => MemoryMonitor.Format(l.PrivateWorkingSet)).ToList();
                _results.Add(new TestResult(name, true,
                    $"total {MemoryMonitor.Format(m.Total.PrivateWorkingSet)} em {m.Total.Processes} processos (commit {MemoryMonitor.Format(m.Total.PrivateBytes)}); " +
                    $"compartilhado {MemoryMonitor.Format(m.Shared.PrivateWorkingSet)}; jogos {MemoryMonitor.Format(m.Accounts.Sum(a => a.PrivateWorkingSet))}; " +
                    $"renderers de live: {(lives.Count == 0 ? "—" : string.Join(", ", lives))}; CPU da máquina {cpu:0.0}%", 0));
            }
            var a = phases[0].Mem.Total.PrivateWorkingSet;
            var steps = phases.Skip(1).Select((p, i) => $"{i + 1} live(s): +{MemoryMonitor.Format(p.Mem.Total.PrivateWorkingSet - a)}").ToList();
            var n = phases.Count - 1;
            var lastLives = channels.Length > 1 ? n : 4;
            _results.Add(new TestResult("custo das lives", true,
                $"{string.Join("; ", steps)} (≈ {MemoryMonitor.Format((phases[^1].Mem.Total.PrivateWorkingSet - a) / lastLives)} por live); " +
                $"CPU: {string.Join(" → ", phases.Select(p => $"{p.Cpu:0.0}%"))} da máquina", 0));
            _results.Add(new TestResult("página real da live (conta 1)", check.Ok, check.Detail ?? "não verificada", 0));
        }
        catch (Exception ex)
        {
            _results.Add(new TestResult("exceção inesperada", false, ex.ToString(), 0));
        }

        var passed = _results.Count > 0 && _results.All(r => r.Passed);
        File.WriteAllText(_resultFile, JsonSerializer.Serialize(new { phase = "twitch", channel, passed, results = _results },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        foreach (var r in _results) LoggingService.Log.Info($"[medição-twitch] {r.Name} — {r.Detail}");
        return passed;
    }

    /// <summary>Confere na página real: vídeo tocando em 160p, áudio mudo no WebView2 e chat conectado.</summary>
    private static async Task<(bool Ok, string Detail)> LiveCheckAsync(BrowserSession s)
    {
        var live = s.LiveCore;
        if (live is null) return (false, "live não abriu");
        const string probe = """
            JSON.stringify((() => {
              const v = document.querySelector('video');
              const chat = !!document.querySelector('[data-a-target="chat-input"], .chat-scrollable-area__message-container, [data-test-selector="chat-scrollable-area__message-container"]');
              return { h: v ? v.videoHeight : 0, paused: v ? v.paused : null, chat, q: localStorage.getItem('video-quality'), w: innerWidth, host: location.hostname, vis: document.visibilityState };
            })())
            """;
        // Página principal + iframes (no modo leve o player e o chat são iframes da Twitch).
        var results = new List<string>();
        try { results.Add(Unquote(await live.ExecuteScriptAsync(probe)) ?? "{}"); } catch { }
        foreach (var f in s.LiveFrames.ToList())
        {
            try { results.Add(Unquote(await f.ExecuteScriptAsync(probe)) ?? "{}"); } catch { }
        }
        int h = 0; var playing = false; var chat = false; string? q = null; var w = 0.0; var hosts = new List<string>(); string? pageVis = null;
        foreach (var json in results)
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.TryGetProperty("host", out var hostEl)) hosts.Add(hostEl.GetString() ?? "");
            pageVis ??= r.TryGetProperty("vis", out var visEl) ? visEl.GetString() : null;
            var fh = r.TryGetProperty("h", out var hv) && hv.ValueKind == JsonValueKind.Number ? hv.GetInt32() : 0;
            if (fh > 0)
            {
                h = fh;
                playing = r.TryGetProperty("paused", out var pv) && pv.ValueKind == JsonValueKind.False;
                q = r.TryGetProperty("q", out var qv) && qv.ValueKind == JsonValueKind.String ? qv.GetString() : q;
                w = r.TryGetProperty("w", out var wv) ? wv.GetDouble() : w;
            }
            chat |= r.TryGetProperty("chat", out var cv) && cv.ValueKind == JsonValueKind.True;
        }
        var detail = $"{(s.IsLiveVisible ? "visível" : "em segundo plano")} (página: {pageVis}): vídeo {(h > 0 ? $"{h}p" : "sem quadro decodificado")}, {(playing ? "tocando" : "parado")}, " +
                     $"áudio mudo={live.IsMuted}, chat {(chat ? "conectado" : "NÃO encontrado")}, largura CSS do player {w:0} (zoom {s.LiveView?.ZoomFactor:0.00}), " +
                     $"qualidade salva {q ?? "—"}, meta de memória {live.MemoryUsageTargetLevel}; frames: {string.Join(", ", hosts.Distinct())}";
        // O que o bônus exige é a aba aberta, logada, com o chat conectado. Escondido, o Chromium pode
        // parar de decodificar o vídeo (como numa aba em segundo plano) — isso é esperado.
        var videoOk = !s.IsLiveVisible || (h > 0 && h <= 360 && playing);
        return (videoOk && live.IsMuted && chat, detail);
    }

    /// <summary>CPU de todos os processos do app (anfitrião + WebView2) em % da máquina.</summary>
    private static async Task<double> TreeCpuAsync(TimeSpan window)
    {
        static Dictionary<int, TimeSpan> Read()
        {
            var d = new Dictionary<int, TimeSpan>();
            foreach (var e in ThoseIdles.Shared.ProcessTree.Descendants(ThoseIdles.Shared.ProcessTree.Snapshot(), Environment.ProcessId))
            {
                try { using var p = Process.GetProcessById(e.Pid); d[e.Pid] = p.TotalProcessorTime; } catch { }
            }
            return d;
        }
        var a = Read();
        var sw = Stopwatch.StartNew();
        await Task.Delay(window);
        var b = Read();
        var used = b.Where(kv => a.ContainsKey(kv.Key)).Sum(kv => (kv.Value - a[kv.Key]).TotalMilliseconds);
        return used / (sw.Elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
    }

    // ------------------------------------------------------------------ teste on-line da /hunt

    /// <summary>
    /// Baixa a extensão real pelo mesmo caminho do botão do app, abre as 4 contas no pokeidle.io
    /// (sem login) sem e com a extensão, e confere que ela está ativa e quanto custa de memória.
    /// </summary>
    public async Task<bool> RunHuntOnlineAsync(HuntExtensionService hunt)
    {
        LoggingService.Log.Info("=== Autoteste on-line da extensão /hunt ===");
        MemorySnapshot? without = null, with = null;
        try
        {
            await Test("baixar e validar a /hunt da Chrome Web Store", async () =>
            {
                var (ok, message) = await hunt.DownloadLatestAsync(ProfileManager.GetRuntimeVersion() ?? "");
                return ok ? Pass($"{message} (hash SHA-256 conferido com a loja) Pasta: {hunt.InstalledFolder}") : Fail(message);
            });

            await Test("verificação de atualização na loja", async () =>
            {
                var info = await hunt.CheckForUpdateAsync(ProfileManager.GetRuntimeVersion() ?? "");
                return info is null
                    ? Pass($"loja respondeu 'noupdate' para a v{hunt.InstalledVersion} instalada")
                    : Fail($"loja ofereceu v{info.Version} logo após instalar v{hunt.InstalledVersion}");
            });

            await Test("4 contas no pokeidle.io SEM extensão", async () =>
            {
                await _manager.StartAllAsync();
                var ok = await WaitUntil(() => _manager.Sessions.All(s => s.Status == SessionStatus.AwaitingConnection), TimeSpan.FromSeconds(60));
                await Task.Delay(15000);
                without = await _memory.SampleAsync();
                var marks = await HuntRealMarks();
                return ok && marks.Values.All(v => !v)
                    ? Pass($"extensão ausente nas 4; {DescribeMemory(without)}")
                    : Fail($"estados: {string.Join(",", _manager.Sessions.Select(s => s.Status))}; {Marks(marks)}");
            });

            await Test("4 contas no pokeidle.io COM a /hunt real", async () =>
            {
                _vm.EnableHuntExtension = true;
                await WaitUntil(() => _manager.Profiles.ExtensionsEnabled &&
                                      _manager.Sessions.All(s => s.Status == SessionStatus.AwaitingConnection), TimeSpan.FromSeconds(60));
                await Task.Delay(15000);
                with = await _memory.SampleAsync();
                var marks = await HuntRealMarks();
                var wrapped = new List<string>();
                foreach (var s in _manager.Sessions)
                    wrapped.Add(Unquote(await s.Core!.ExecuteScriptAsync("WebSocket.name")) ?? "");
                return marks.Values.All(v => v) && _manager.Sessions.All(s => s.HuntActive)
                    ? Pass($"hook da /hunt instalado nas 4 (WebSocket = {string.Join("/", wrapped.Distinct())}); {DescribeMemory(with)}")
                    : Fail($"{Marks(marks)}; WebSocket = {string.Join("/", wrapped)}");
            });

            if (without is not null && with is not null)
            {
                var diff = with.Total.PrivateWorkingSet - without.Total.PrivateWorkingSet;
                var perAccount = Enumerable.Range(0, 4).Select(i => with.Accounts[i].PrivateWorkingSet - without.Accounts[i].PrivateWorkingSet).ToList();
                _results.Add(new TestResult("custo de memória da /hunt (tela de login, 15 s após abrir)", true,
                    $"total {Signed(diff)} ({MemoryMonitor.Format(without.Total.PrivateWorkingSet)} → {MemoryMonitor.Format(with.Total.PrivateWorkingSet)}); " +
                    $"processos {without.Total.Processes} → {with.Total.Processes}; por conta: {string.Join(", ", perAccount.Select((d, i) => $"conta{i + 1} {Signed(d)}"))}. " +
                    "Sem login a extensão só instala o hook; logada, ela carrega o catálogo do jogo e o custo deve ser maior.", 0));
            }
        }
        catch (Exception ex)
        {
            _results.Add(new TestResult("exceção inesperada", false, ex.ToString(), 0));
        }

        var passed = _results.Count > 0 && _results.All(r => r.Passed);
        File.WriteAllText(_resultFile, JsonSerializer.Serialize(new { phase = "hunt-online", passed, results = _results },
            new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        foreach (var r in _results) LoggingService.Log.Info($"[autoteste-hunt] {(r.Passed ? "OK  " : "FALHOU")} {r.Name} — {r.Detail}");
        return passed;
    }

    private async Task<Dictionary<int, bool>> HuntRealMarks()
    {
        var d = new Dictionary<int, bool>();
        foreach (var s in _manager.Sessions.Where(s => s.Core is not null))
            d[s.Slot] = await s.Core!.ExecuteScriptAsync("!!(window.__pokeidleHunt && window.__pokeidleHunt.hookInstalado)") == "true";
        return d;
    }

    private static string DescribeMemory(MemorySnapshot s) =>
        $"privada em RAM {MemoryMonitor.Format(s.Total.PrivateWorkingSet)} em {s.Total.Processes} processos";

    private static string Signed(long bytes) => (bytes >= 0 ? "+" : "−") + MemoryMonitor.Format(Math.Abs(bytes)).Replace("—", "0 MB");

    // ------------------------------------------------------------------ fase 1

    private async Task RunPhaseAsync()
    {
        await Test("política de navegação", () =>
        {
            var p = new NavigationPolicy(() => AppSettings.DefaultAllowedHosts);
            var checks = new (string Url, bool Redirect, bool User, NavigationDecision Expected)[]
            {
                ("https://pokeidle.io/app", false, false, NavigationDecision.Allow),
                ("https://accounts.google.com/o/oauth2/auth", false, false, NavigationDecision.Allow),
                ("http://pokeidle.io/app", false, false, NavigationDecision.Block),
                ("https://evil.example/", false, false, NavigationDecision.Block),
                ("https://discord.gg/pokeidle", false, true, NavigationDecision.OpenExternally),
                ("https://cdn.oauth-intermediario.example/x", true, false, NavigationDecision.Allow),
                ("file:///C:/Windows/win.ini", false, true, NavigationDecision.Block),
                ("javascript:alert(1)", false, true, NavigationDecision.Block),
            };
            var bad = checks.Where(c => p.Evaluate(c.Url, c.Redirect, c.User) != c.Expected).Select(c => c.Url).ToList();
            var sanitized = LoggingService.Sanitize("GET https://pokeidle.io/app?sessao=SEGREDO#x ok");
            if (sanitized.Contains("SEGREDO")) bad.Add("log não removeu query string");
            return Task.FromResult(bad.Count == 0 ? Pass("8 URLs avaliadas corretamente; query string removida do log") : Fail("divergências: " + string.Join(", ", bad)));
        });

        await Test("4 contas abrem e conectam (WebSocket detectado)", async () =>
        {
            var sw = Stopwatch.StartNew();
            await _manager.StartAllAsync();
            var ok = await WaitUntil(() => _manager.Sessions.All(s => s.Status == SessionStatus.Connected), TimeSpan.FromSeconds(60));
            var states = string.Join(", ", _manager.Sessions.Select(s => $"{s.Slot}:{s.Status}"));
            return ok ? Pass($"todas conectadas em {sw.Elapsed.TotalSeconds:0.0}s ({states})") : Fail(states);
        });

        await Test("um único processo de navegador compartilhado", async () =>
        {
            var pids = _manager.Sessions.Select(s => s.Core!.BrowserProcessId).Distinct().ToList();
            var snap = await _memory.SampleAsync();
            var browsers = snap.Processes.Count(p => p.Kind == "navegador");
            return pids.Count == 1 && browsers == 1
                ? Pass($"BrowserProcessId {pids[0]} nas 4 contas; {snap.Total.Processes} processos no total")
                : Fail($"pids distintos: {string.Join(",", pids)}; processos 'navegador': {browsers}");
        });

        await Test("isolamento: cada conta grava seus dados", async () =>
        {
            foreach (var s in _manager.Sessions) await WriteAccountData(s);
            return Pass("localStorage, cookie e IndexedDB gravados nas 4 contas");
        });

        await Test("isolamento: nenhuma conta vê dados de outra", async () =>
        {
            var problems = new List<string>();
            foreach (var s in _manager.Sessions)
            {
                var d = await ReadAccountData(s);
                var expected = s.Slot.ToString();
                if (d.Local != expected || d.Cookie != expected || d.Idb != expected || d.CookieCount != 1)
                    problems.Add($"conta{s.Slot}: local={d.Local} cookie={d.Cookie} idb={d.Idb} cookies(acct)={d.CookieCount}");
            }
            return problems.Count == 0 ? Pass("cada conta lê somente o próprio valor (localStorage, cookie via JS e CookieManager, IndexedDB)") : Fail(string.Join("; ", problems));
        });

        await Test("perfis em pastas separadas no disco", () =>
        {
            var paths = _manager.Sessions.Select(s => s.Core!.Profile.ProfilePath).ToList();
            var names = _manager.Sessions.Select(s => s.Core!.Profile.ProfileName).ToList();
            var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4;
            var exist = paths.All(Directory.Exists);
            var underRoot = paths.All(p => p.StartsWith(AppPaths.UserDataFolder, StringComparison.OrdinalIgnoreCase));
            var detail = string.Join(", ", names.Zip(paths, (n, p) => $"{n} → {Path.GetFileName(p)}"));
            return Task.FromResult(distinct && exist && underRoot ? Pass(detail) : Fail(detail));
        });

        await Test("reiniciar a conta 3 não afeta as outras", async () =>
        {
            var before = await BootIds();
            await _manager[3].RestartAsync();
            await WaitUntil(() => _manager[3].Status == SessionStatus.Connected, TimeSpan.FromSeconds(30));
            var after = await BootIds();
            var acct3 = (await ReadAccountData(_manager[3])).Local;
            var othersSame = new[] { 1, 2, 4 }.All(i => before[i] == after[i]);
            var restarted = before[3] != after[3];
            return othersSame && restarted && acct3 == "3"
                ? Pass("contas 1, 2 e 4 mantiveram a mesma página (bootId); a 3 recarregou e manteve seus dados")
                : Fail($"outras iguais={othersSame}, 3 reiniciou={restarted}, acct3={acct3}");
        });

        await Test("fechar a conta 2 não afeta as outras", async () =>
        {
            var before = await BootIds();
            var ticksBefore = await Ticks();
            _manager[2].Close();
            await Task.Delay(2000);
            var after = await BootIds(skip: 2);
            var ticksAfter = await Ticks(skip: 2);
            var othersSame = new[] { 1, 3, 4 }.All(i => before[i] == after[i]);
            var advancing = new[] { 1, 3, 4 }.All(i => ticksAfter[i] > ticksBefore[i] + 10);
            await _manager[2].StartAsync();
            await WaitUntil(() => _manager[2].Status == SessionStatus.Connected, TimeSpan.FromSeconds(30));
            var acct2 = (await ReadAccountData(_manager[2])).Local;
            return othersSame && advancing && acct2 == "2"
                ? Pass("1, 3 e 4 seguiram executando sem recarregar; a 2 reabriu com a sessão preservada")
                : Fail($"outras iguais={othersSame}, timers avançando={advancing}, acct2 após reabrir={acct2}");
        });

        await Test("limpar dados da conta 4 é isolado", async () =>
        {
            await _manager[4].ClearDataAsync();
            await WaitUntil(() => _manager[4].Status == SessionStatus.Connected, TimeSpan.FromSeconds(30));
            var d4 = await ReadAccountData(_manager[4]);
            var others = new List<string>();
            foreach (var i in new[] { 1, 2, 3 })
            {
                var d = await ReadAccountData(_manager[i]);
                if (d.Local != i.ToString() || d.Cookie != i.ToString() || d.Idb != i.ToString()) others.Add($"conta{i} perdeu dados");
            }
            var cleared = d4.Local is null or "null" && d4.Cookie is "" && d4.Idb is "" or "null" && d4.CookieCount == 0;
            return cleared && others.Count == 0
                ? Pass("conta 4 sem localStorage/cookie/IndexedDB; contas 1–3 intactas")
                : Fail($"conta4 local={d4.Local} cookie='{d4.Cookie}' idb={d4.Idb} cookies={d4.CookieCount}; {string.Join(", ", others)}");
        });

        await Test("modo foco: miniaturas continuam executando", async () =>
        {
            _vm.ToggleFocus(1);
            await Task.Delay(1500);
            var r = await MeasureActivity(TimeSpan.FromSeconds(5));
            _vm.IsFocusMode = false;
            var thumbs = new[] { 2, 3, 4 };
            var ok = thumbs.All(i => r[i].Ticks >= 35 && r[i].Rafs > 30 && r[i].Visibility == "visible" && r[i].WsOpen);
            return ok ? Pass(Describe(r)) : Fail(Describe(r));
        });

        await Test("janela minimizada: as 4 contas continuam executando", async () =>
        {
            _window.WindowState = WindowState.Minimized;
            await Task.Delay(3000);
            var minimized = IsIconic(new System.Windows.Interop.WindowInteropHelper(_window).Handle);
            var r = await MeasureActivity(TimeSpan.FromSeconds(6));
            _window.WindowState = WindowState.Normal;
            _window.Activate();
            // Critério: timers JS sem limitação (~10/s) e WebSocket aberto. requestAnimationFrame é
            // reportado mas não é critério: o jogo depende de timers + WebSocket, não de quadros.
            var ok = minimized && r.Values.All(x => x.Ticks >= 40 && x.WsOpen);
            return ok ? Pass($"janela minimizada (IsIconic={minimized}); " + Describe(r)) : Fail($"IsIconic={minimized}; " + Describe(r));
        });

        await RunLayoutTestsAsync();
        _vm.SaveSettings();
        await RunLiveTestsAsync();
        await RunExtensionTestsAsync();
        await RunShinyExportTestsAsync();
        await RunAppUpdateTestsAsync();
        await RunRmtAlertTestsAsync();
        await RunEconomyTestsAsync();

        await Test("memória: renderers atribuídos por conta", async () =>
        {
            var s = await _memory.SampleAsync();
            var perAccount = s.Accounts.Select((g, i) => $"conta{i + 1}={MemoryMonitor.Format(g.PrivateWorkingSet)}/{g.Processes}p").ToList();
            var renderers = s.Processes.Where(p => p.Slot is not null).GroupBy(p => p.Slot).ToDictionary(g => g.Key!.Value, g => g.Select(p => p.Pid).ToList());
            var distinct = renderers.Values.SelectMany(x => x).Distinct().Count() == renderers.Values.Sum(x => x.Count);
            var detail = $"total privada-em-RAM {MemoryMonitor.Format(s.Total.PrivateWorkingSet)}, commit {MemoryMonitor.Format(s.Total.PrivateBytes)}, " +
                         $"app {MemoryMonitor.Format(s.Host.PrivateWorkingSet)}, compartilhado {MemoryMonitor.Format(s.Shared.PrivateWorkingSet)} ({s.Shared.Processes}p), " +
                         string.Join(", ", perAccount);
            return renderers.Count == 4 && distinct ? Pass(detail) : Fail(detail);
        });
    }

    // ------------------------------------------------------------------ layout e zoom

    private sealed record Viewport(double W, double H);

    private async Task<Dictionary<int, Viewport>> Viewports()
    {
        var d = new Dictionary<int, Viewport>();
        foreach (var s in _manager.Sessions)
        {
            var json = Unquote(await s.Core!.ExecuteScriptAsync("JSON.stringify([innerWidth, innerHeight])"))!;
            var a = JsonSerializer.Deserialize<double[]>(json)!;
            d[s.Slot] = new Viewport(a[0], a[1]);
        }
        return d;
    }

    private static string Describe(Dictionary<int, Viewport> v) =>
        string.Join(", ", v.OrderBy(x => x.Key).Select(x => $"conta{x.Key} {x.Value.W:0}×{x.Value.H:0}"));

    private async Task RunLayoutTestsAsync()
    {
        await Test("divisórias: cada conta muda de tamanho de forma independente", async () =>
        {
            _vm.IsFocusMode = false;
            await Task.Delay(800);
            var boot = await BootIds();
            var before = await Viewports();
            // Mesmo efeito de arrastar: as divisórias estão ligadas a estas propriedades.
            _vm.RowSplit = 0.65;
            _vm.TopSplit = 0.30;
            _vm.BottomSplit = 0.70;
            await Task.Delay(1200);
            var after = await Viewports();
            var boot2 = await BootIds();
            var ok = after[1].W < before[1].W && after[2].W > before[2].W   // linha de cima: 1 encolhe, 2 cresce
                     && after[3].W > before[3].W && after[4].W < before[4].W // linha de baixo: o contrário
                     && after[1].H > before[1].H && after[3].H < before[3].H // linha de cima mais alta
                     && Math.Abs(after[1].W - after[3].W) > 50               // colunas independentes por linha
                     && boot.All(b => boot2[b.Key] == b.Value);              // ninguém recarregou
            return ok
                ? Pass($"antes: {Describe(before)} → depois: {Describe(after)}; nenhuma conta recarregou")
                : Fail($"antes: {Describe(before)} → depois: {Describe(after)}");
        });

        await Test("divisória do modo \"uma conta\"", async () =>
        {
            _vm.ToggleFocus(1);
            _vm.FocusSplit = 0.78;
            await Task.Delay(1000);
            var a = (await Viewports())[1];
            _vm.FocusSplit = 0.60;
            await Task.Delay(1000);
            var b = (await Viewports())[1];
            _vm.IsFocusMode = false;
            return b.W < a.W - 50
                ? Pass($"conta em foco: {a.W:0} px → {b.W:0} px de largura útil ao arrastar a divisória")
                : Fail($"{a.W:0} → {b.W:0}");
        });

        await Test("zoom por conta (independente)", async () =>
        {
            await Task.Delay(800);
            var before = await Viewports();
            _vm.Accounts[1].SetZoom(1.5);                     // conta 2
            await Task.Delay(800);
            var after = await Viewports();
            var ratio = before[2].W / after[2].W;
            var othersSame = new[] { 1, 3, 4 }.All(i => Math.Abs(after[i].W - before[i].W) < 2);
            return Math.Abs(ratio - 1.5) < 0.05 && othersSame
                ? Pass($"conta 2 a 150%: largura CSS {before[2].W:0} → {after[2].W:0}; contas 1, 3 e 4 inalteradas")
                : Fail($"razão {ratio:0.00}; {Describe(before)} → {Describe(after)}");
        });

        await Test("botões −/+ de zoom ajustam só a conta e salvam", async () =>
        {
            var before = await Viewports();
            _vm.Accounts[2].ZoomInCommand.Execute(null);   // conta 3: 100% → 110%
            _vm.Accounts[2].ZoomInCommand.Execute(null);   // → 120%
            await Task.Delay(800);
            var after = await Viewports();
            var saved = _manager[3].Account.Zoom;
            _vm.Accounts[2].ZoomOutCommand.Execute(null);  // → 110%
            var down = _manager[3].Account.Zoom;
            _vm.Accounts[2].SetZoom(1.0);
            var ratio = before[3].W / after[3].W;
            var othersSame = new[] { 1, 4 }.All(i => Math.Abs(after[i].W - before[i].W) < 2);
            return Math.Abs(saved - 1.2) < 0.001 && Math.Abs(down - 1.1) < 0.001 && Math.Abs(ratio - 1.2) < 0.05 && othersSame
                ? Pass($"conta 3: + + → 120% (largura CSS {before[3].W:0} → {after[3].W:0}), − → 110%; contas 1 e 4 inalteradas")
                : Fail($"salvo {saved}, depois do − {down}, razão {ratio:0.00}, outras inalteradas={othersSame}");
        });
    }

    // ------------------------------------------------------------------ live da Twitch (local)

    private static async Task<bool> WaitLivePage(BrowserSession s, string channel, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (s.LiveCore is { } live)
            {
                try
                {
                    if (Unquote(await live.ExecuteScriptAsync("window.__livePage || null")) == channel) return true;
                }
                catch { }
            }
            await Task.Delay(200);
        }
        return false;
    }

    private async Task RunLiveTestsAsync()
    {
        await Test("live: canal da Twitch clicado no jogo abre no painel da conta", async () =>
        {
            var boot = await BootIds();
            await _manager[2].Core!.ExecuteScriptAsync("document.getElementById('twlink').click()");
            var loaded = await WaitLivePage(_manager[2], "canalteste", TimeSpan.FromSeconds(15));
            var boot2 = await BootIds();
            var s2 = _manager[2];
            var othersClosed = new[] { 1, 3, 4 }.All(i => !_manager[i].IsLiveOpen);
            var gameStayed = boot.All(b => boot2[b.Key] == b.Value);
            var hidden = !s2.IsLiveVisible;
            var ok = loaded && hidden && s2.Account.LiveChannels.SequenceEqual(new[] { "canalteste" }) && othersClosed && gameStayed;
            return ok
                ? Pass("o link (target=_blank) abriu a live da conta 2 em segundo plano (escondida, carregada); o jogo dela não saiu da tela; contas 1, 3 e 4 sem live")
                : Fail($"carregou={loaded}, escondida={hidden}, canais salvos={string.Join(",", s2.Account.LiveChannels)}, outras sem live={othersClosed}, jogo ficou={gameStayed}");
        });

        await Test("live: mesmo perfil da conta, muda e 160p por padrão", async () =>
        {
            var s2 = _manager[2];
            var live = s2.LiveCore!;
            var sameProfile = string.Equals(live.Profile.ProfilePath, s2.Core!.Profile.ProfilePath, StringComparison.OrdinalIgnoreCase);
            var quality = Unquote(await live.ExecuteScriptAsync("localStorage.getItem('video-quality')"));
            var ok = sameProfile && live.IsMuted && quality is not null && quality.Contains("160p30");
            return ok
                ? Pass($"perfil {live.Profile.ProfileName} (o mesmo do jogo); IsMuted=true; video-quality={quality}")
                : Fail($"mesmo perfil={sameProfile}, mudo={live.IsMuted}, qualidade={quality}");
        });

        await Test("live: memória separada da do jogo", async () =>
        {
            await Task.Delay(800);
            var s = await _memory.SampleAsync();
            var livePids = s.Processes.Where(p => p.Kind == "renderer (live)" && p.Slot == 2).Select(p => p.Pid).ToList();
            var gamePids = s.Processes.Where(p => p.Kind == "renderer" && p.Slot == 2).Select(p => p.Pid).ToList();
            var ok = s.Lives[1].Processes >= 1 && livePids.Count > 0 && !livePids.Intersect(gamePids).Any()
                     && s.Lives[0].Processes == 0 && s.Lives[2].Processes == 0 && s.Lives[3].Processes == 0;
            return ok
                ? Pass($"conta 2: jogo {MemoryMonitor.Format(s.Accounts[1].PrivateWorkingSet)} (pid {string.Join(",", gamePids)}), live {MemoryMonitor.Format(s.Lives[1].PrivateWorkingSet)} (pid {string.Join(",", livePids)})")
                : Fail($"lives por conta: {string.Join("/", s.Lives.Select(l => l.Processes))}; pids live {string.Join(",", livePids)}; jogo {string.Join(",", gamePids)}");
        });

        await Test("live escondida continua rodando (como aba em segundo plano)", async () =>
        {
            var s2 = _manager[2];
            var live = s2.LiveCore!;
            var t0 = long.Parse(await live.ExecuteScriptAsync("window.liveTicks"));
            await Task.Delay(5000);
            var t1 = long.Parse(await live.ExecuteScriptAsync("window.liveTicks"));
            var vis = Unquote(await live.ExecuteScriptAsync("document.visibilityState"));
            var target = live.MemoryUsageTargetLevel;
            return t1 - t0 >= 40 && vis == "hidden" && !s2.IsLiveVisible && target == CoreWebView2MemoryUsageTargetLevel.Low
                ? Pass($"página da live 'hidden' (como aba em segundo plano) e mesmo assim {t1 - t0} ticks/50 em 5 s; meta de memória da aba: Low")
                : Fail($"{t1 - t0} ticks em 5 s, {vis}, meta {target}, visível={s2.IsLiveVisible}");
        });

        await Test("live: mostrar, esconder e fechar", async () =>
        {
            var s2 = _manager[2];
            var boot = (await BootIds())[2];
            var liveBoot = Unquote(await s2.LiveCore!.ExecuteScriptAsync("String(performance.timeOrigin)"));
            s2.SetLiveVisible(true);                           // 📺 do cabeçalho: mostrar
            await Task.Delay(1000);
            var visShown = Unquote(await s2.LiveCore!.ExecuteScriptAsync("document.visibilityState"));
            var normal = s2.LiveCore.MemoryUsageTargetLevel == CoreWebView2MemoryUsageTargetLevel.Normal;
            s2.SetLiveVisible(false);                          // esconder: continua aberta
            await Task.Delay(500);
            var visHidden = Unquote(await s2.LiveCore!.ExecuteScriptAsync("document.visibilityState"));
            LoggingService.Log.Info($"[diag live] depois de mostrar e esconder: {visHidden}");
            var sameLive = Unquote(await s2.LiveCore!.ExecuteScriptAsync("String(performance.timeOrigin)")) == liveBoot;
            var sameGame = (await BootIds())[2] == boot;
            var reopened = s2.IsLiveOpen && !s2.IsLiveVisible && sameLive;
            // Fecha e esquece na conta 3 (abre lá só para isso); a conta 2 fica com a live para a fase 2.
            await _manager[3].OpenLiveAsync("outrocanal");
            var opened3 = await WaitLivePage(_manager[3], "outrocanal", TimeSpan.FromSeconds(15));
            _manager[3].CloseLive(forget: true);
            var forgot = !_manager[3].IsLiveOpen && _manager[3].Account.LiveChannels.Count == 0;
            _vm.SaveSettings();
            return visShown == "visible" && normal && reopened && sameGame && opened3 && forgot
                ? Pass("mostrar: página visível e memória normal; esconder: a mesma live seguiu aberta (sem recarregar), jogo intacto; fechar (conta 3) esqueceu o canal")
                : Fail($"mostrada={visShown}, memória normal={normal}, escondida sem recarregar={reopened}, jogo intacto={sameGame}, abriu3={opened3}, esqueceu={forgot}");
        });

        await Test("live: reabrir pelo canal guardado", async () =>
        {
            var s2 = _manager[2];
            s2.CloseLive(forget: false);                       // fecha (ex.: conta reiniciada), canal fica guardado
            var kept = !s2.IsLiveOpen && s2.Account.LiveChannels.SequenceEqual(new[] { "canalteste" });
            await s2.OpenSavedLivesAsync();               // 📺 do cabeçalho reabre
            var reopened = await WaitLivePage(s2, "canalteste", TimeSpan.FromSeconds(15));
            _vm.SaveSettings();
            return kept && reopened
                ? Pass("fechada, o canal ficou guardado; o botão reabriu a mesma live em segundo plano")
                : Fail($"guardou={kept}, reabriu={reopened}");
        });

        await Test("várias lives acumuladas na mesma conta", async () =>
        {
            var s2 = _manager[2];
            var boot = (await BootIds())[2];
            await s2.OpenLiveAsync("canal2");
            await s2.OpenLiveAsync("canal3");
            await s2.OpenLiveAsync("canal2"); // repetido: não abre de novo
            var loaded = true;
            foreach (var ch in new[] { "canalteste", "canal2", "canal3" })
                loaded &= s2.FindLive(ch) is { } tab && await WaitTabPage(tab, ch, TimeSpan.FromSeconds(15));
            // As três rodando escondidas ao mesmo tempo.
            var ticks0 = new List<long>();
            foreach (var t in s2.Lives) ticks0.Add(long.Parse(await t.Core!.ExecuteScriptAsync("window.liveTicks")));
            await Task.Delay(3000);
            var allRunning = true; var allHidden = true; var i = 0;
            foreach (var t in s2.Lives)
            {
                allRunning &= long.Parse(await t.Core!.ExecuteScriptAsync("window.liveTicks")) - ticks0[i++] >= 20;
                allHidden &= Unquote(await t.Core.ExecuteScriptAsync("document.visibilityState")) == "hidden";
            }
            var saved = string.Join(",", s2.Account.LiveChannels);
            var mem = await _memory.SampleAsync();
            // Fechar uma não mexe nas outras.
            s2.CloseLive(s2.FindLive("canal3")!, forget: true);
            var afterClose = string.Join(",", s2.Lives.Select(l => l.Channel));
            var gameSame = (await BootIds())[2] == boot;
            var ok = loaded && s2.LiveCount == 2 && allRunning && allHidden && saved == "canalteste,canal2,canal3"
                     && afterClose == "canalteste,canal2" && string.Join(",", s2.Account.LiveChannels) == "canalteste,canal2" && gameSame;
            return ok
                ? Pass($"3 lives abertas na conta 2 (canal repetido ignorado), todas 'hidden' e rodando; memória das lives {MemoryMonitor.Format(mem.Lives[1].PrivateWorkingSet)}; fechar canal3 manteve as outras e o jogo")
                : Fail($"carregaram={loaded}, rodando={allRunning}, escondidas={allHidden}, salvos={saved}, depois de fechar={afterClose}, jogo intacto={gameSame}");
        });
        await Test("recarga das lives (libera a memória do chat) sem tocar no jogo", async () =>
        {
            var s2 = _manager[2];
            var boot = (await BootIds())[2];
            var before = new Dictionary<string, string?>();
            foreach (var t in s2.Lives) before[t.Channel] = Unquote(await t.Core!.ExecuteScriptAsync("String(performance.timeOrigin)"));
            var n = await _manager.ReloadAllLivesAsync(TimeSpan.Zero);
            await Task.Delay(1500);
            var reloaded = true; var hidden = true;
            foreach (var t in s2.Lives)
            {
                reloaded &= await WaitTabPage(t, t.Channel == "canalteste" ? "canalteste" : t.Channel, TimeSpan.FromSeconds(10))
                            && Unquote(await t.Core!.ExecuteScriptAsync("String(performance.timeOrigin)")) != before[t.Channel];
                hidden &= Unquote(await t.Core!.ExecuteScriptAsync("document.visibilityState")) == "hidden";
            }
            var gameSame = (await BootIds())[2] == boot;
            return n == s2.LiveCount && reloaded && hidden && gameSame
                ? Pass($"{n} live(s) recarregada(s) (página nova em cada uma), continuam 'hidden'; o jogo da conta não recarregou")
                : Fail($"recarregadas={n}/{s2.LiveCount}, páginas novas={reloaded}, escondidas={hidden}, jogo intacto={gameSame}");
        });
        _vm.SaveSettings();
    }

    private static async Task<bool> WaitTabPage(LiveTab tab, string channel, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (tab.Core is { } c)
            {
                try { if (Unquote(await c.ExecuteScriptAsync("window.__livePage || null")) == channel) return true; }
                catch { }
            }
            await Task.Delay(200);
        }
        return false;
    }

    // ------------------------------------------------------------------ extensão

    /// <summary>Manifesto real da /hunt for PokeIdle v1.0.2 (vetor de teste da validação).</summary>
    private const string RealHuntManifest = """
        {"update_url":"https://clients2.google.com/service/update2/crx","manifest_version":3,"name":"/hunt for PokeIdle",
         "version":"1.0.2","description":"x","icons":{"16":"icons/icon-16.png"},
         "content_scripts":[{"matches":["https://pokeidle.io/*"],"js":["src/hook.js","src/ui.js"],"run_at":"document_start","world":"MAIN"},
                            {"matches":["https://pokeidle.io/*"],"css":["src/ui.css"],"run_at":"document_start"}]}
        """;

    /// <summary>
    /// Cria e instala uma extensão de teste com o MESMO formato da /hunt (content script em world MAIN,
    /// document_start), só que para http://localhost. Ela marca window.__huntTeste na página.
    /// </summary>
    public static void PrepareTestExtension(HuntExtensionService hunt, AppSettings settings)
    {
        var src = Path.Combine(Path.GetTempPath(), "ThoseIdles-selftest-ext-" + Environment.ProcessId);
        Directory.CreateDirectory(Path.Combine(src, "src"));
        File.WriteAllText(Path.Combine(src, "manifest.json"), """
            {"manifest_version":3,"name":"/hunt for PokeIdle","version":"0.0.1","description":"extensão de autoteste",
             "content_scripts":[{"matches":["http://localhost/*"],"js":["src/marca.js"],"run_at":"document_start","world":"MAIN"}]}
            """);
        File.WriteAllText(Path.Combine(src, "src", "marca.js"), "window.__huntTeste = 'ok';");
        var (ok, message) = hunt.InstallFromFolder(src);
        Directory.Delete(src, true);
        if (!ok) throw new InvalidOperationException("extensão de teste: " + message);
        settings.EnableHuntExtension = true;
    }

    private async Task<Dictionary<int, bool>> HuntMarks()
    {
        var d = new Dictionary<int, bool>();
        foreach (var s in _manager.Sessions.Where(s => s.Core is not null))
            d[s.Slot] = await s.Core!.ExecuteScriptAsync("window.__huntTeste === 'ok'") == "true";
        return d;
    }

    private static string Marks(Dictionary<int, bool> m) =>
        string.Join(", ", m.OrderBy(x => x.Key).Select(x => $"conta{x.Key}={(x.Value ? "ativa" : "ausente")}"));

    private async Task RunExtensionTestsAsync()
    {
        await Test("extensão: validação do manifesto", () =>
        {
            var allowed = new[] { "https://pokeidle.io/*" };
            using var real = JsonDocument.Parse(RealHuntManifest);
            var realOk = HuntExtensionService.ValidateManifest(real.RootElement, allowed) is null;
            var bad = new[]
            {
                """{"manifest_version":3,"name":"x","version":"1","permissions":["cookies"],"content_scripts":[{"matches":["https://pokeidle.io/*"],"js":["a.js"]}]}""",
                """{"manifest_version":3,"name":"x","version":"1","background":{"service_worker":"b.js"},"content_scripts":[{"matches":["https://pokeidle.io/*"],"js":["a.js"]}]}""",
                """{"manifest_version":3,"name":"x","version":"1","content_scripts":[{"matches":["<all_urls>"],"js":["a.js"]}]}""",
                """{"manifest_version":3,"name":"x","version":"1","host_permissions":["*://*/*"],"content_scripts":[{"matches":["https://pokeidle.io/*"],"js":["a.js"]}]}""",
                """{"manifest_version":3,"name":"x","version":"1","content_scripts":[{"matches":["https://pokeidle.io/*"],"js":["../fora.js"]}]}""",
            };
            var rejected = bad.Count(j =>
            {
                using var d = JsonDocument.Parse(j);
                return HuntExtensionService.ValidateManifest(d.RootElement, allowed) is not null;
            });
            return Task.FromResult(realOk && rejected == bad.Length
                ? Pass($"manifesto real da /hunt v1.0.2 aceito; {rejected}/{bad.Length} manifestos perigosos recusados (permissions, background, <all_urls>, host_permissions, caminho '..')")
                : Fail($"real aceito={realOk}; recusados {rejected}/{bad.Length}"));
        });

        await Test("extensão: carregada em cada perfil", async () =>
        {
            var m = await HuntMarks();
            var ids = _manager.Sessions.Select(s => s.Account.HuntExtensionId).ToList();
            return m.Values.All(v => v) && _manager.Sessions.All(s => s.HuntActive) && ids.All(id => !string.IsNullOrEmpty(id))
                ? Pass($"{Marks(m)}; content script em world MAIN rodou antes da página")
                : Fail(Marks(m));
        });

        await Test("extensão: desligar só na conta 2 não afeta as outras", async () =>
        {
            var before = await BootIds();
            await _manager[2].SetHuntEnabledAsync(false);
            await WaitUntil(() => _manager[2].Status == SessionStatus.Connected, TimeSpan.FromSeconds(30));
            await Task.Delay(500);
            var m = await HuntMarks();
            var after = await BootIds();
            var othersSame = new[] { 1, 3, 4 }.All(i => before[i] == after[i]);
            await _manager[2].SetHuntEnabledAsync(true);
            await WaitUntil(() => _manager[2].Status == SessionStatus.Connected, TimeSpan.FromSeconds(30));
            await Task.Delay(500);
            var back = (await HuntMarks())[2];
            var ok = !m[2] && m[1] && m[3] && m[4] && othersSame && back;
            return ok
                ? Pass($"desligada: {Marks(m)}; 1, 3 e 4 não recarregaram; religada na conta 2: {(back ? "ativa" : "ausente")}")
                : Fail($"{Marks(m)}; outras sem recarregar={othersSame}; religada={back}");
        });

        await Test("extensão: desligar/ligar no app recria o ambiente", async () =>
        {
            _vm.EnableHuntExtension = false;
            await WaitUntil(() => _manager.Sessions.All(s => s.Status == SessionStatus.Connected) && !_manager.Profiles.ExtensionsEnabled, TimeSpan.FromSeconds(40));
            var off = await HuntMarks();
            var offOk = _manager.Sessions.All(s => s.Status == SessionStatus.Connected) && off.Count == 4 && off.Values.All(v => !v);
            _vm.EnableHuntExtension = true;
            await WaitUntil(() => _manager.Sessions.All(s => s.Status == SessionStatus.Connected) && _manager.Profiles.ExtensionsEnabled, TimeSpan.FromSeconds(40));
            var on = await HuntMarks();
            var onOk = on.Count == 4 && on.Values.All(v => v);
            var data = (await ReadAccountData(_manager[1])).Local;
            return offOk && onOk && data == "1"
                ? Pass($"sem extensões: {Marks(off)}; religado: {Marks(on)}; sessões preservadas")
                : Fail($"off: {Marks(off)}; on: {Marks(on)}; acct1={data}");
        });
    }

    // ------------------------------------------------------------------ fase 2

    // ------------------------------------------------------------------ exportar shinies

    private async Task RunShinyExportTestsAsync()
    {
        var server = SelfTestServer.Current!;
        var folder = BrowserSession.ExportFolder;

        await Test("exportar shinies: todas as páginas num .txt, com os filtros da tela", async () =>
        {
            var s1 = _manager[1];
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            var hasButton = await s1.Core!.ExecuteScriptAsync("!!document.getElementById('ti-shl-exportar')") == "true";
            // A "página" do jogo pede a lista com um filtro (o script guarda esses filtros).
            await s1.Core.ExecuteScriptAsync("window.__ws.send(JSON.stringify({ t: 'shiny.listar', busca: '', tipo: 'fogo', soAVenda: false, ordem: 'recentes', pagina: 1 }))");
            await Task.Delay(500);
            var before = server.ShinyRequests.Count;
            await s1.Core.ExecuteScriptAsync("document.getElementById('ti-shl-exportar').click()");
            string? file = null;
            await WaitUntil(() => (file = Directory.Exists(folder) ? Directory.GetFiles(folder, "shinies-pokeidle-*.txt").FirstOrDefault() : null) is not null, TimeSpan.FromSeconds(20));
            if (file is null) return Fail($"nenhum arquivo salvo (botão presente={hasButton})");
            await Task.Delay(300);
            var lines = (await File.ReadAllLinesAsync(file)).Select(l => l.TrimStart('﻿')).ToList();
            var rows = lines.Where(l => !l.StartsWith('#') && l.Length > 0).ToList();
            var requests = server.ShinyRequests.Skip(before).ToList();
            var pages = requests.Select(r => JsonDocument.Parse(r).RootElement.GetProperty("pagina").GetInt32()).ToList();
            var allFogo = requests.All(r => r.Contains("\"tipo\":\"fogo\""));
            var expected = new[]
            {
                "ESPECIE, POTENCIA, QUALIDADE, IVS, VALOR NO RMT",
                "Charizard, P3, 87, 61 (hp 20 / atk 31 / def 10), 1500 Gemas",
                "Gengar, P1, -, 42, - (à venda por 90000 ouro, não é RMT)",
                "Pikachu  o raro, P5, 100, 186, -",
                "Eevee, P2, -, -, -",
                "Mewtwo, P4, 55, 62 (hp 31 / atk 31), 250.5 Gemas",
            };
            var missing = expected.Where(e => !rows.Contains(e)).ToList();
            var ok = hasButton && missing.Count == 0 && rows.Count == 6
                     && pages.SequenceEqual(new[] { 0, 1, 2, 1 }) && allFogo
                     && lines.Any(l => l.StartsWith("# Filtros: tipo fogo"));
            return ok
                ? Pass($"{Path.GetFileName(file)}: 5 shinies de 3 páginas (pedidos 0, 1, 2 e a volta para a página 1), filtro \"fogo\" mantido, colunas e RMT corretos")
                : Fail($"botão={hasButton}; páginas pedidas={string.Join(",", pages)}; filtro mantido={allFogo}; faltando: {string.Join(" | ", missing)}; linhas: {string.Join(" | ", rows)}");
        });

        await Test("exportar shinies: qualquer outro download continua bloqueado", async () =>
        {
            var s1 = _manager[1];
            var countBefore = Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;
            await s1.Core!.ExecuteScriptAsync("""
                (() => {
                  for (const nome of ['malicioso.exe', 'shinies-pokeidle-x.txt.exe', 'outra-coisa.txt']) {
                    const a = document.createElement('a');
                    a.href = URL.createObjectURL(new Blob(['x']));
                    a.download = nome;
                    document.body.appendChild(a); a.click(); a.remove();
                  }
                })();
                """);
            await Task.Delay(2500);
            var countAfter = Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var leaked = Directory.Exists(downloads) && Directory.GetFiles(downloads, "malicioso*").Any(f => File.GetCreationTimeUtc(f) > DateTime.UtcNow.AddMinutes(-1));
            return countAfter == countBefore && !leaked
                ? Pass("3 downloads com outros nomes (incluindo .exe disfarçado) foram bloqueados; nada foi salvo")
                : Fail($"arquivos antes={countBefore}, depois={countAfter}, vazou para Downloads={leaked}");
        });
    }

    // ------------------------------------------------------------------ painel de farm + alerta de mercado

    private async Task RunAccountCountLayoutTestAsync()
    {
        await Test("quantidade de contas: grade para 1, 2, 3 e 4 contas", () =>
        {
            var panel = new Views.SessionLayoutPanel();
            for (var i = 0; i < 4; i++) panel.Children.Add(new System.Windows.Controls.Border());
            var size = new Size(1000, 600);
            string Show(int n, bool focus)
            {
                panel.VisibleCount = n;
                panel.IsFocusMode = focus;
                return string.Join(" ", panel.PanelRects(size).Select(r => r.Width * r.Height > 0 ? $"{r.X:0},{r.Y:0},{r.Width:0}x{r.Height:0}" : "-"));
            }
            var got = new[] { Show(1, false), Show(2, false), Show(3, false), Show(4, false), Show(2, true), Show(1, true) };
            var expected = new[]
            {
                "0,0,1000x600 - - -",
                "0,0,497x600 503,0,497x600 - -",
                "0,0,497x297 503,0,497x297 0,303,1000x297 -",
                "0,0,497x297 503,0,497x297 0,303,497x297 503,303,497x297",
                "0,0,775x600 781,0,219x600 - -",
                "0,0,1000x600 - - -",
            };
            var ok = got.SequenceEqual(expected);
            return Task.FromResult(ok
                ? Pass("1: tela cheia · 2: lado a lado · 3: duas em cima e uma larga embaixo · 4: 2x2 · destaque com 2: grande + 1 miniatura; contas a mais ficam sem espaço")
                : Fail(string.Join(" || ", got)));
        });
    }

    private async Task RunSniperTestsAsync()
    {
        var server = SelfTestServer.Current!;
        await Test("sniper: desligado não consulta nem compra; ligado compra só o que bate, espera a carência e respeita o limite", async () =>
        {
            var core = _manager[1].Core!;
            // Regra: Pokémon shiny com "char", P3+, até 2000 de ouro, no máximo 2 compras.
            await core.ExecuteScriptAsync("""localStorage.setItem('ti-sniper', JSON.stringify({ intervalo: 3, regras: [{ ativa: true, tipo: 'pokemon', texto: 'char', soShiny: true, potMin: 3, precoMax: 2000, moeda: 'gold', maxCompras: 2, compradas: 0 }] }))""");
            await core.ExecuteScriptAsync("location.reload()");
            for (var i = 0; i < 60 && await core.ExecuteScriptAsync("window.wsOpen === true && !!window.__tiSniper?.estado") != "true"; i++)
                await Task.Delay(250);
            var hasButton = await core.ExecuteScriptAsync("!!document.getElementById('ti-snp-btn')") == "true";

            // Desligado (como sempre abre): nenhuma consulta nem compra, mesmo recebendo anúncios.
            var scans0 = server.MarketScans; var buys0 = server.BuyRequests.Count;
            await core.ExecuteScriptAsync("window.__ws.send(JSON.stringify({ t: 'teste.eco', msg: { t: 'market', aba: 'pokemon', linhas: [{ id: 99, pokemonNome: 'Charizard', shiny: true, potencia: 5, preco: 1, moeda: 'gold' }] } }))");
            await Task.Delay(4000);
            var offQuiet = server.MarketScans == scans0 && server.BuyRequests.Count == buys0;

            await core.ExecuteScriptAsync("window.__tiSniper.ligar(true)");
            await Task.Delay(9000); // ~3 varreduras e a liberação do anúncio 6
            await core.ExecuteScriptAsync("window.__tiSniper.ligar(false)");
            var buys = server.BuyRequests.Skip(buys0).Select(b => JsonDocument.Parse(b).RootElement).ToList();
            var ids = buys.Select(b => b.GetProperty("id").GetInt32()).ToList();
            var est = JsonDocument.Parse(JsonDocument.Parse(await core.ExecuteScriptAsync("JSON.stringify(window.__tiSniper.estado())")).RootElement.GetString()!).RootElement;
            var compradas = est.GetProperty("regras")[0].GetProperty("compradas").GetInt32();
            var scans = server.MarketScans - scans0;
            var ok = hasButton && offQuiet && ids.Count(i => i == 1) == 1 && ids.Count(i => i == 6) >= 1 && ids.All(i => i is 1 or 6)
                     && compradas == 2 && scans is >= 2 and <= 5 && buys.All(b => b.GetProperty("moeda").GetString() == "gold");
            return ok
                ? Pass($"desligado: 0 consultas; ligado: {scans} consultas em 9 s, comprou o anúncio 1 na hora e o 6 depois da carência ({ids.Count(i => i == 6)} disparo(s)); ignorou o caro, o em Gemas e o sem shiny; parou em 2/2 compras")
                : Fail($"botão={hasButton} desligadoQuieto={offQuiet} consultas={scans} compras=[{string.Join(",", ids)}] compradas={compradas}");
        });
    }

    private async Task RunPriceCalcTestsAsync()
    {
        await Test("calculadora de preço: IVs úteis pelo perfil + Qualidade + Potência; RMT só com nota 3,5+", async () =>
        {
            var core = _manager[1].Core!;
            await core.ExecuteScriptAsync("localStorage.removeItem('ti-preco-anuncios'); location.reload()");
            for (var i = 0; i < 60 && await core.ExecuteScriptAsync("window.wsOpen === true && !!window.__tiPreco?.avaliar") != "true"; i++)
                await Task.Delay(250);
            var hasButton = await core.ExecuteScriptAsync("!!document.querySelector('#ti-dock #ti-prc-btn')") == "true";
            await core.ExecuteScriptAsync("window.__tiPreco.definirPerfil('Charizard', 'especial')");
            // Meus Pokémon com IVs em português (o classificador tem que reconhecer).
            const string estado = """{"t":"estado","pokemons":[{"nome":"Rattata","potencia":1,"qualidade":20,"ivs":{"ps":10,"ataque":10,"defesa":10,"atq_esp":10,"def_esp":10,"velocidade":10}}]}""";
            // Charizard especial: A/C/D parecidos; B tem IV alto só no Ataque (inútil para especial); E é P5.
            var mercado = """
              {"t":"market","aba":"pokemon","linhas":[
              {"id":601,"pokemonNome":"Charizard","shiny":true,"potencia":4,"qualidade":90,"ivs":{"hp":31,"atk":0,"def":20,"spa":31,"spd":20,"spe":31},"preco":2000,"moeda":"orb"},
              {"id":602,"pokemonNome":"Charizard","shiny":true,"potencia":4,"qualidade":90,"ivs":{"hp":31,"atk":31,"def":20,"spa":0,"spd":20,"spe":31},"preco":600,"moeda":"orb"},
              {"id":603,"pokemonNome":"Charizard","shiny":true,"potencia":4,"qualidade":88,"ivs":{"hp":31,"atk":0,"def":20,"spa":29,"spd":20,"spe":30},"preco":1800,"moeda":"orb"},
              {"id":604,"ficha":{"nome":"Charizard","shiny":true,"potencia":4,"qualidade":95,"ivs":{"hp":31,"atk":0,"def":20,"spa":31,"spd":20,"spe":31}},"preco":2200,"moeda":"orb"},
              {"id":605,"pokemonNome":"Charizard","shiny":true,"potencia":5,"qualidade":90,"ivs":{"hp":31,"atk":0,"def":20,"spa":31,"spd":20,"spe":31},"preco":9000,"moeda":"orb"},
              {"id":606,"pokemonNome":"Charizard","shiny":true,"potencia":4,"qualidade":90,"ivs":{"hp":31,"atk":0,"def":20,"spa":31,"spd":20,"spe":31},"preco":80000,"moeda":"gold"},
              {"id":607,"itemId":7,"itemNome":"Charizardite","preco":5,"moeda":"orb"}]}
              """.Replace("\r", "").Replace("\n", "");
            foreach (var msg in new[] { estado, mercado })
                await core.ExecuteScriptAsync($"window.__ws.send(JSON.stringify({{ t: 'teste.eco', msg: {msg} }}))");
            await Task.Delay(1500);
            async Task<JsonElement> Eval(string js) =>
                JsonDocument.Parse(JsonDocument.Parse(await core.ExecuteScriptAsync($"JSON.stringify({js})")).RootElement.GetString()!).RootElement;
            const string alvo = "{ nome: 'Charizard', shiny: true, pot: 4, qual: 90, nota: 4.2, ivs: { hp: 31, atk: 0, def: 20, spa: 31, spd: 20, spe: 31 } }";
            var esp = await Eval($"window.__tiPreco.avaliar({alvo})");
            await core.ExecuteScriptAsync("window.__tiPreco.definirPerfil('Charizard', 'fisico')");
            var fis = await Eval($"window.__tiPreco.avaliar({alvo})");
            await core.ExecuteScriptAsync("window.__tiPreco.definirPerfil('Charizard', 'especial')");
            var baixa = await Eval("window.__tiPreco.avaliar({ nome: 'Charizard', shiny: true, pot: 4, qual: 90, nota: 3.4, ivs: { spa: 31 } })");
            var limite = await Eval("window.__tiPreco.avaliar({ nome: 'Charizard', shiny: true, pot: 4, qual: 90, nota: 3500, ivs: { spa: 31 } })");
            var meus = await Eval("window.__tiPreco.meus().map(p => window.__tiPreco.avaliar(p))");
            var gemas = esp.GetProperty("gemas").GetProperty("preco").GetDouble();
            var ouro = esp.GetProperty("ouro").GetProperty("preco").GetDouble();
            var ivEsp = esp.GetProperty("ivUtil").GetInt32();
            var ivFis = fis.GetProperty("ivUtil").GetInt32();
            var ivRattata = meus[0].GetProperty("ivUtil").GetInt32();
            var ok = hasButton && gemas == 2000 && ouro == 80000 && ivEsp == 92 && ivFis == 56 && ivRattata == 32
                     && !baixa.GetProperty("podeRmt").GetBoolean() && limite.GetProperty("podeRmt").GetBoolean();
            return ok
                ? Pass($"Charizard especial: IVs úteis {ivEsp}% (como físico seriam {ivFis}%), valor {esp.GetProperty("valor")}/10 → {gemas} Gemas e {ouro} ouro; o anúncio com IV só em Ataque e o P5 pesaram pouco; nota 3,4 bloqueada, 3500 (=3,5) liberada; IVs em português reconhecidos ({ivRattata}%)")
                : Fail($"botão={hasButton} esp={esp} fis={fis} baixa={baixa} limite={limite} meus={meus}");
        });
    }

    private async Task RunRmtAlertTestsAsync()
    {
        await RunSniperTestsAsync();
        await RunPriceCalcTestsAsync();
        await RunAccountCountLayoutTestAsync();
        await Test("alerta de RMT: avisa só anúncio em Gemas que bate com a regra; não envia nada", async () =>
        {
            var core = _manager[1].Core!;
            var hasButton = await core.ExecuteScriptAsync("!!document.getElementById('ti-rmt-btn')") == "true";
            // Regra: shiny com "char" no nome, P3+, até 2000 Gemas.
            await core.ExecuteScriptAsync("localStorage.setItem('ti-rmt-regras', JSON.stringify([{ ativa: true, texto: 'char', soShiny: true, potMin: 3, precoMax: 2000 }]))");
            await core.ExecuteScriptAsync("location.reload()"); // recarrega para o script ler as regras salvas
            for (var i = 0; i < 60 && await core.ExecuteScriptAsync("window.wsOpen === true && !!window.__tiRmtAlertState") != "true"; i++)
                await Task.Delay(250);
            // Conta o que a página manda ao servidor a partir daqui (o alerta não pode mandar nada).
            await core.ExecuteScriptAsync("""
                window.__enviados = [];
                const s = WebSocket.prototype.send;
                WebSocket.prototype.send = function (d) { window.__enviados.push(String(d)); return s.apply(this, arguments); };
                """);
            const string mercado = """{"t":"market","aba":"pokemon","linhas":[{"id":1,"pokemonNome":"Charizard","shiny":true,"potencia":4,"preco":1500,"moeda":"orb","vendedor":"fulano"},{"id":2,"pokemonNome":"Charizard","shiny":true,"potencia":4,"preco":2500,"moeda":"orb"},{"id":3,"pokemonNome":"Charmander","potencia":5,"preco":10,"moeda":"orb"},{"id":4,"pokemonNome":"Charmeleon","shiny":true,"potencia":3,"preco":100,"moeda":"gold"},{"id":5,"pokemonNome":"Charizard","shiny":true,"potencia":5,"preco":900,"moeda":"orb","comprador":"x"}]}""";
            foreach (var msg in new[] { mercado, mercado })
                await core.ExecuteScriptAsync($"window.__ws.send(JSON.stringify({{ t: 'teste.eco', msg: {msg} }}))");
            await Task.Delay(1500);
            var state = JsonDocument.Parse(JsonDocument.Parse(await core.ExecuteScriptAsync("JSON.stringify(window.__tiRmtAlertState())")).RootElement.GetString()!).RootElement;
            var alertas = state.GetProperty("alertas").EnumerateArray().Select(a => a.GetProperty("desc").GetString()!).ToList();
            var badge = await core.ExecuteScriptAsync("document.getElementById('ti-rmt-btn').textContent");
            var enviados = JsonDocument.Parse(await core.ExecuteScriptAsync("JSON.stringify(window.__enviados)")).RootElement.GetString()!;
            var proprios = JsonDocument.Parse(enviados).RootElement.EnumerateArray().Select(e => e.GetString()!).Where(s => !s.Contains("teste.eco")).ToList();
            var ok = hasButton && alertas.Count == 1 && alertas[0].StartsWith("Charizard ✨ P4 por 1500 Gemas") && badge.Contains("RMT1") && proprios.Count == 0;
            return ok
                ? Pass($"de 5 anúncios (repetidos 2×; 1 em ouro, 1 caro, 1 sem shiny, 1 vendido) só 1 alertou: \"{alertas[0]}\"; botão {badge}; nenhuma mensagem enviada")
                : Fail($"botão={hasButton} badge={badge} alertas=[{string.Join(" | ", alertas)}] enviados=[{string.Join(" | ", proprios)}]");
        });
    }

    // ------------------------------------------------------------------ aviso de versão nova

    private async Task RunAppUpdateTestsAsync()
    {
        var server = SelfTestServer.Current!;
        await Test("aviso de versão nova: só aparece para versão maior, some ao dispensar", async () =>
        {
            server.LatestReleaseTag = "v0.0.1";
            await _vm.CheckAppUpdateAsync();
            var older = _vm.AppUpdateText;
            server.LatestReleaseTag = $"v{AppUpdateService.CurrentVersion}";
            await _vm.CheckAppUpdateAsync();
            var same = _vm.AppUpdateText;
            server.LatestReleaseTag = "v99.0.0";
            await _vm.CheckAppUpdateAsync();
            var newer = _vm.AppUpdateText;
            var release = await AppUpdateService.CheckAsync(CancellationToken.None);
            _vm.DismissAppUpdateCommand.Execute(null);
            var afterDismiss = _vm.AppUpdateText;
            await _vm.CheckAppUpdateAsync();
            var again = _vm.AppUpdateText;
            var ok = older == "" && same == "" && newer.Contains("v99.0.0") && afterDismiss == "" && again == ""
                     && release?.Url == AppUpdateService.ReleasesPage;
            return ok
                ? Pass($"v0.0.1 e a própria versão: sem aviso; v99.0.0: \"{newer}\"; link estranho trocado pela página de Releases; dispensado não volta")
                : Fail($"antiga=\"{older}\" igual=\"{same}\" nova=\"{newer}\" link={release?.Url} dispensado=\"{afterDismiss}\" de novo=\"{again}\"");
        });
    }

    // ------------------------------------------------------------------ modo econômico

    private static string? LastHelloField(string field)
    {
        var last = SelfTestServer.Current?.Hellos.LastOrDefault();
        if (last is null) return null;
        using var doc = JsonDocument.Parse(last);
        return doc.RootElement.TryGetProperty(field, out var v) ? v.ToString() : null;
    }

    private static async Task SetGameSession(BrowserSession s, string nick, string token, string device) =>
        await s.Core!.ExecuteScriptAsync(
            $"localStorage.setItem('sessao', JSON.stringify({{ nick: '{nick}', token: '{token}' }})); localStorage.setItem('id-dispositivo', '{device}');");

    private async Task RunEconomyTestsAsync()
    {
        var server = SelfTestServer.Current!;

        await Test("modo econômico: fecha a página e conecta com o mesmo hello do navegador", async () =>
        {
            var s3 = _manager[3];
            var bootOthers = await BootIds(skip: 3);
            await SetGameSession(s3, "conta3teste", "tok3", "disp-3333-abcd");
            var problem = await s3.EnterEconomyAsync();
            var connected = await WaitUntil(() => s3.IsEconomy && s3.Status == SessionStatus.Connected, TimeSpan.FromSeconds(15));
            var helloOk = LastHelloField("nick") == "conta3teste" && LastHelloField("token") == "tok3"
                          && LastHelloField("dispositivo") == "disp-3333-abcd" && LastHelloField("delta") == "1";
            var pageClosed = s3.WebView is null && s3.IsRunning;
            var othersSame = (await BootIds(skip: 3)).All(b => bootOthers[b.Key] == b.Value);
            var mem = await _memory.SampleAsync();
            var noRenderer = mem.Accounts[2].Processes == 0;
            return problem is null && connected && helloOk && pageClosed && othersSame && noRenderer
                ? Pass($"conta 3 conectada pelo cliente leve ({s3.StatusDetail}); hello com o nick, token e id do aparelho do navegador; página fechada (0 renderers da conta 3); outras contas intactas")
                : Fail($"problema={problem}, conectada={connected} ({s3.StatusDetail}), hello={helloOk} ({server.Hellos.LastOrDefault()}), página fechada={pageClosed}, outras={othersSame}, renderers conta3={mem.Accounts[2].Processes}");
        });

        await Test("modo econômico: ping periódico e token renovado guardado criptografado", async () =>
        {
            var s3 = _manager[3];
            var pings0 = server.Pings;
            await Task.Delay(5000);
            var pings = server.Pings - pings0;
            var renewed = s3.Economy?.Session.Token == "tok3-renovado";
            _vm.SaveSettings();
            var settingsText = await File.ReadAllTextAsync(AppPaths.SettingsFile);
            var encrypted = s3.Account.EconomySessionProtected is { Length: > 40 } && !settingsText.Contains("tok3") && !settingsText.Contains("conta3teste");
            var stillConnected = s3.Status == SessionStatus.Connected;
            return pings >= 2 && renewed && encrypted && stillConnected
                ? Pass($"{pings} pings em 5 s (intervalo de teste 2 s; no jogo, 25 s como o oficial); token renovado pelo servidor e guardado; settings.json não contém nick nem token em texto")
                : Fail($"pings={pings}, renovado={renewed}, criptografado={encrypted}, conectada={stillConnected}");
        });

        await Test("modo econômico: sessão tomada (4001) e recusa na entrada param sem insistir", async () =>
        {
            var endpoint = new Uri(server.Url.Replace("http://", "ws://"));
            var before = server.Hellos.Count;
            await using var taken = new EconomyClient(9, endpoint, server.Url.TrimEnd('/'), "teste", new EconomySession("x", "tomada", "d"));
            await using var refused = new EconomyClient(9, endpoint, server.Url.TrimEnd('/'), "teste", new EconomySession("y", "recusa", "d"));
            taken.Start();
            refused.Start();
            await WaitUntil(() => taken.State == EconomyState.Failed && refused.State == EconomyState.Failed, TimeSpan.FromSeconds(10));
            await Task.Delay(6000); // tempo de sobra para uma reconexão indevida aparecer
            var attempts = server.Hellos.Count - before;
            return taken.State == EconomyState.Failed && refused.State == EconomyState.Failed && attempts == 2
                ? Pass($"4001 → \"{taken.Detail}\"; erro na entrada → \"{refused.Detail}\"; só 1 tentativa de cada (sem reconectar)")
                : Fail($"tomada={taken.State} ({taken.Detail}), recusa={refused.State} ({refused.Detail}), tentativas={attempts}");
        });

        await Test("modo econômico: \"Abrir jogo\" volta para a página logada com o token novo", async () =>
        {
            var s3 = _manager[3];
            await s3.ExitEconomyAsync();
            var loaded = await WaitUntil(() => s3.Core is not null && s3.Status == SessionStatus.Connected, TimeSpan.FromSeconds(20));
            var token = loaded ? Unquote(await s3.Core!.ExecuteScriptAsync("JSON.parse(localStorage.getItem('sessao')).token")) : null;
            var data = loaded ? (await ReadAccountData(s3)).Local : null;
            return loaded && !s3.IsEconomy && !s3.Account.EconomyMode && token == "tok3-renovado" && data == "3"
                ? Pass("página do jogo reaberta e conectada; o localStorage dela recebeu o token renovado durante o modo econômico; dados da conta intactos")
                : Fail($"carregou={loaded}, econômico={s3.IsEconomy}, token na página={token}, dados={data}");
        });
    }

    /// <summary>Fim da fase 1: deixa a conta 3 em modo econômico para a fase 2 conferir que ela reabre assim.</summary>
    private async Task LeaveAccountInEconomyForVerifyAsync()
    {
        try
        {
            var s3 = _manager[3];
            if (s3.Core is null) return;
            await s3.EnterEconomyAsync();
            await WaitUntil(() => s3.Status == SessionStatus.Connected, TimeSpan.FromSeconds(10));
            _vm.SaveSettings();
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[autoteste] preparar modo econômico para a fase 2: {ex.Message}");
        }
    }

    private async Task VerifyPhaseAsync()
    {
        await Test("conta em modo econômico reabre conectada sem a página", async () =>
        {
            await _manager.StartAllAsync();
            var s3 = _manager[3];
            var ok = await WaitUntil(() => s3.IsEconomy && s3.Status == SessionStatus.Connected, TimeSpan.FromSeconds(20));
            var helloToken = LastHelloField("token");
            var noPage = s3.WebView is null;
            // Volta para a página para os demais testes da fase 2.
            await s3.ExitEconomyAsync();
            await WaitUntil(() => s3.Core is not null && s3.Status == SessionStatus.Connected, TimeSpan.FromSeconds(20));
            return ok && noPage && helloToken == "tok3-renovado"
                ? Pass("a conta 3 voltou em modo econômico ao reabrir o app, com as credenciais guardadas (token renovado), sem página")
                : Fail($"conectada={ok} ({s3.StatusDetail}), sem página={noPage}, token no hello={helloToken}");
        });

        await Test("persistência após reiniciar o aplicativo", async () =>
        {
            await _manager.StartAllAsync();
            await WaitUntil(() => _manager.Sessions.All(s => s.Status == SessionStatus.Connected), TimeSpan.FromSeconds(60));
            var problems = new List<string>();
            foreach (var s in _manager.Sessions)
            {
                var d = await ReadAccountData(s);
                var expected = s.Slot == 4 ? null : s.Slot.ToString(); // a 4 foi limpa na fase 1
                if (expected is null)
                {
                    if (d.Local is not (null or "null") || d.CookieCount != 0) problems.Add($"conta4 deveria estar vazia: local={d.Local} cookies={d.CookieCount}");
                }
                else if (d.Local != expected || d.Cookie != expected || d.Idb != expected)
                {
                    problems.Add($"conta{s.Slot}: local={d.Local} cookie={d.Cookie} idb={d.Idb}");
                }
            }
            return problems.Count == 0
                ? Pass("contas 1–3 mantiveram localStorage, cookie e IndexedDB; conta 4 continua limpa")
                : Fail(string.Join("; ", problems));
        });

        await Test("extensão persiste após reiniciar o aplicativo", async () =>
        {
            var m = await HuntMarks();
            return m.Count == 4 && m.Values.All(v => v) ? Pass(Marks(m)) : Fail(Marks(m));
        });

        await Test("live reabre com a conta após reiniciar o aplicativo", async () =>
        {
            var s2 = _manager[2];
            await WaitUntil(() => s2.LiveCount == 2, TimeSpan.FromSeconds(15));
            var reopened = s2.LiveCount == 2;
            foreach (var ch in new[] { "canalteste", "canal2" })
                reopened &= s2.FindLive(ch) is { } tab && await WaitTabPage(tab, ch, TimeSpan.FromSeconds(15));
            var muted = s2.Lives.All(l => l.Core?.IsMuted == true);
            var hidden = !s2.IsLiveVisible;
            var others = new[] { 1, 3, 4 }.All(i => !_manager[i].IsLiveOpen);
            return reopened && muted && hidden && others
                ? Pass("conta 2 voltou com as 2 lives guardadas (canalteste, canal2), mudas e em segundo plano; as outras sem live")
                : Fail($"reabriu={reopened}, muda={muted}, escondida={hidden}, outras sem live={others}");
        });

        await Test("divisórias e zoom persistem após reiniciar o aplicativo", () =>
        {
            var z2 = _manager[2].WebView!.ZoomFactor;
            var splitsOk = Math.Abs(_vm.RowSplit - 0.65) < 0.001 && Math.Abs(_vm.TopSplit - 0.30) < 0.001 && Math.Abs(_vm.BottomSplit - 0.70) < 0.001;
            var detail = $"divisórias linha={_vm.RowSplit:0.00} cima={_vm.TopSplit:0.00} baixo={_vm.BottomSplit:0.00}; zoom conta 2 = {z2 * 100:0}%";
            return Task.FromResult(splitsOk && Math.Abs(z2 - 1.5) < 0.01 ? Pass(detail) : Fail(detail));
        });
    }

    // ------------------------------------------------------------------ utilitários

    private sealed record AccountData(string? Local, string? Cookie, string? Idb, int CookieCount);
    private sealed record Activity(long Ticks, long Rafs, string Visibility, bool WsOpen);

    private static async Task WriteAccountData(BrowserSession s)
    {
        var n = s.Slot;
        // IIFE: scripts executados no escopo global não podem redeclarar const entre chamadas.
        await s.Core!.ExecuteScriptAsync($$"""
            (() => {
              localStorage.setItem('acct', '{{n}}');
              document.cookie = 'acct={{n}}; max-age=31536000; path=/';
              window.__idbDone = false;
              const rq = indexedDB.open('autoteste', 1);
              rq.onupgradeneeded = () => rq.result.createObjectStore('kv');
              rq.onsuccess = () => {
                const tx = rq.result.transaction('kv', 'readwrite');
                tx.objectStore('kv').put('{{n}}', 'acct');
                tx.oncomplete = () => { rq.result.close(); window.__idbDone = true; };
              };
            })();
            """);
        await WaitScriptTrue(s, "window.__idbDone === true", TimeSpan.FromSeconds(10));
    }

    private static async Task<AccountData> ReadAccountData(BrowserSession s)
    {
        var core = s.Core!;
        await core.ExecuteScriptAsync("""
            (() => {
              window.__idb = undefined;
              const rq = indexedDB.open('autoteste', 1);
              rq.onupgradeneeded = () => rq.result.createObjectStore('kv');
              rq.onsuccess = () => {
                const g = rq.result.transaction('kv', 'readonly').objectStore('kv').get('acct');
                g.onsuccess = () => { window.__idb = g.result === undefined ? '' : String(g.result); rq.result.close(); };
                g.onerror = () => { window.__idb = ''; };
              };
              rq.onerror = () => { window.__idb = ''; };
            })();
            """);
        await WaitScriptTrue(s, "window.__idb !== undefined", TimeSpan.FromSeconds(10));
        var local = Unquote(await core.ExecuteScriptAsync("localStorage.getItem('acct')"));
        var cookie = Unquote(await core.ExecuteScriptAsync("(document.cookie.split('; ').find(c => c.startsWith('acct=')) || '').slice(5)"));
        var idb = Unquote(await core.ExecuteScriptAsync("window.__idb"));
        var cookies = await core.CookieManager.GetCookiesAsync(core.Source);
        var count = cookies.Count(c => c.Name == "acct");
        return new AccountData(local, cookie, idb, count);
    }

    private async Task<Dictionary<int, string>> BootIds(int skip = 0)
    {
        var d = new Dictionary<int, string>();
        foreach (var s in _manager.Sessions.Where(s => s.Slot != skip))
            d[s.Slot] = Unquote(await s.Core!.ExecuteScriptAsync("window.bootId")) ?? "";
        return d;
    }

    private async Task<Dictionary<int, long>> Ticks(int skip = 0)
    {
        var d = new Dictionary<int, long>();
        foreach (var s in _manager.Sessions.Where(s => s.Slot != skip))
            d[s.Slot] = long.Parse(await s.Core!.ExecuteScriptAsync("window.ticks"));
        return d;
    }

    /// <summary>
    /// Mede ticks de setInterval(100ms) e quadros de requestAnimationFrame dentro de cada página.
    /// Os valores são lidos no começo e no fim, mas contados pela própria página: a medição não
    /// depende da janela estar visível.
    /// </summary>
    private async Task<Dictionary<int, Activity>> MeasureActivity(TimeSpan window)
    {
        var start = new Dictionary<int, (long T, long R)>();
        foreach (var s in _manager.Sessions)
        {
            var json = await s.Core!.ExecuteScriptAsync("JSON.stringify([window.ticks, window.rafs])");
            var arr = JsonSerializer.Deserialize<long[]>(JsonSerializer.Deserialize<string>(json)!)!;
            start[s.Slot] = (arr[0], arr[1]);
        }
        await Task.Delay(window);
        var result = new Dictionary<int, Activity>();
        foreach (var s in _manager.Sessions)
        {
            var json = await s.Core!.ExecuteScriptAsync("JSON.stringify([window.ticks, window.rafs, document.visibilityState, window.wsOpen])");
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(json)!);
            var a = doc.RootElement;
            var scale = 5.0 / window.TotalSeconds; // normaliza para 5 s
            result[s.Slot] = new Activity(
                (long)((a[0].GetInt64() - start[s.Slot].T) * scale),
                (long)((a[1].GetInt64() - start[s.Slot].R) * scale),
                a[2].GetString() ?? "",
                a[3].GetBoolean());
        }
        return result;
    }

    private static string Describe(Dictionary<int, Activity> r) =>
        "por 5 s → " + string.Join("; ", r.OrderBy(x => x.Key).Select(x =>
            $"conta{x.Key}: {x.Value.Ticks} ticks/50 esperados, {x.Value.Rafs} rAF, {x.Value.Visibility}, ws {(x.Value.WsOpen ? "aberto" : "FECHADO")}"));

    private static async Task WaitScriptTrue(BrowserSession s, string expr, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await s.Core!.ExecuteScriptAsync(expr) == "true") return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"conta{s.Slot}: '{expr}' não ficou verdadeiro");
    }

    private static async Task<bool> WaitUntil(Func<bool> condition, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (condition()) return true;
            await Task.Delay(200);
        }
        return condition();
    }

    private static string? Unquote(string json)
    {
        if (json == "null") return null;
        try { return JsonSerializer.Deserialize<string>(json); }
        catch { return json; }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    private static (bool, string) Pass(string d) => (true, d);
    private static (bool, string) Fail(string d) => (false, d);

    private async Task Test(string name, Func<Task<(bool Passed, string Detail)>> body)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var (passed, detail) = await body();
            _results.Add(new TestResult(name, passed, detail, Math.Round(sw.Elapsed.TotalSeconds, 2)));
        }
        catch (Exception ex)
        {
            _results.Add(new TestResult(name, false, $"{ex.GetType().Name}: {ex.Message}", Math.Round(sw.Elapsed.TotalSeconds, 2)));
        }
    }
}
