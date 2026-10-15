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
            else await RunPhaseAsync();
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
        await RunExtensionTestsAsync();

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

    private async Task VerifyPhaseAsync()
    {
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
