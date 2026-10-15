using System.Diagnostics;
using System.Globalization;
using System.Text;
using ThoseIdles.Shared;

// MemBench — mede memória de um conjunto de processos (com todos os auxiliares) e grava CSV.
// O MESMO código mede o Cenário A (Chrome) e o Cenário B (ThoseIdles), para a comparação ser justa.

var inv = CultureInfo.InvariantCulture;
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
var opts = ParseOptions(args.Skip(1).ToArray());

try
{
    return command switch
    {
        "sample" => await Sample(opts, launch: false),
        "launch" => await Sample(opts, launch: true),
        "compare" => Compare(opts.Positional),
        "list" => ListTarget(opts),
        _ => Fail($"comando desconhecido: {command}"),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Erro: {ex.Message}");
    return 1;
}

// ---------------------------------------------------------------------------------------------

async Task<int> Sample(Options o, bool launch)
{
    var target = o.Get("target") ?? throw new ArgumentException("--target é obrigatório (chrome | app | exe:<nome.exe> | pid:<pid>)");
    var duration = TimeSpan.FromSeconds(double.Parse(o.Get("duration") ?? "600", inv));
    var interval = TimeSpan.FromSeconds(double.Parse(o.Get("interval") ?? "5", inv));
    var label = o.Get("label") ?? target;
    var outPath = o.Get("out") ?? $"membench-{Sanitize(label)}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
    var detailPath = o.Has("detail") ? Path.ChangeExtension(outPath, ".processos.csv") : null;

    double? settledAt = null;
    var launchWatch = Stopwatch.StartNew();
    if (launch)
    {
        var exe = o.Get("exe") ?? throw new ArgumentException("launch exige --exe <caminho>");
        var argSets = o.GetAll("arg");
        // --args-file: uma linha = argumentos de um processo, lidos literalmente (sem problemas de aspas do shell).
        if (o.Get("args-file") is { } argsFile)
            argSets.AddRange(File.ReadAllLines(argsFile).Where(l => l.Trim().Length > 0));
        if (argSets.Count == 0) argSets.Add("");
        // Segurança: um alvo "launched" sem argumentos válidos para o Chrome abriria o perfil PADRÃO do usuário.
        if (Path.GetFileName(exe).Equals("chrome.exe", StringComparison.OrdinalIgnoreCase) &&
            argSets.Any(a => !a.Contains("--user-data-dir=", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("para o Chrome, cada conjunto de argumentos precisa de --user-data-dir=<pasta de teste> (proteção para não abrir o seu perfil real)");
        Console.WriteLine($"Iniciando {argSets.Count} processo(s): {exe}");
        foreach (var a in argSets)
        {
            using var p = Process.Start(new ProcessStartInfo(exe, a) { UseShellExecute = false });
            if (p is not null) LaunchedRoots.Add(p.Id);
            // Pequeno intervalo: o Chrome com a mesma pasta de dados repassa a janela ao processo já aberto.
            await Task.Delay(300);
        }
        settledAt = await WaitSettled(target,
            cpuThreshold: double.Parse(o.Get("settle-cpu") ?? "0", inv),
            memThresholdPct: double.Parse(o.Get("settle-mem") ?? "2", inv),
            timeout: TimeSpan.FromSeconds(double.Parse(o.Get("settle-timeout") ?? "120", inv)),
            launchWatch);
        Console.WriteLine(settledAt is double s
            ? $"Carga estabilizada em {s:0.0}s (processos constantes e memória variando < limite por 5 s)."
            : "Não estabilizou dentro do tempo limite.");
    }

    Console.WriteLine($"Amostrando '{target}' a cada {interval.TotalSeconds}s por {duration.TotalSeconds}s → {outPath}");
    await using var csv = new StreamWriter(outPath, false, new UTF8Encoding(true));
    await csv.WriteLineAsync("timestamp_utc;segundos;rotulo;processos;privada_ws_mb;privada_commit_mb;working_set_mb;cpu_pct_maquina;sistema_ram_disponivel_mb;sistema_commit_mb;tempo_estabilizar_s");
    StreamWriter? detail = null;
    if (detailPath is not null)
    {
        detail = new StreamWriter(detailPath, false, new UTF8Encoding(true));
        await detail.WriteLineAsync("timestamp_utc;pid;exe;privada_ws_mb;privada_commit_mb;working_set_mb");
    }

    var watch = Stopwatch.StartNew();
    var cpu = new CpuTracker();
    var rows = new List<Row>();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    while (watch.Elapsed <= duration && !cts.IsCancellationRequested)
    {
        var procs = Resolve(target);
        long pws = 0, priv = 0, ws = 0;
        var now = DateTime.UtcNow;
        foreach (var p in procs)
        {
            if (!ProcessMemoryReader.TryRead(p.Pid, out var m)) continue;
            pws += Math.Max(0, m.PrivateWorkingSet);
            priv += m.PrivateBytes;
            ws += m.WorkingSet;
            if (detail is not null)
                await detail.WriteLineAsync($"{now:o};{p.Pid};{p.ExeName};{Mb(m.PrivateWorkingSet)};{Mb(m.PrivateBytes)};{Mb(m.WorkingSet)}");
        }
        var cpuPct = cpu.Sample(procs);
        var sys = ProcessMemoryReader.ReadSystem();
        var row = new Row(watch.Elapsed.TotalSeconds, procs.Count, pws, priv, ws, cpuPct);
        rows.Add(row);
        await csv.WriteLineAsync(string.Join(';',
            now.ToString("o", inv), row.Seconds.ToString("0", inv), label, procs.Count,
            Mb(pws), Mb(priv), Mb(ws), cpuPct.ToString("0.0", inv), Mb(sys.AvailPhys), Mb(sys.CommitUsed),
            settledAt?.ToString("0.0", inv) ?? ""));
        await csv.FlushAsync();
        Console.WriteLine($"[{row.Seconds,5:0}s] {procs.Count,3} proc | privada em RAM {Mb(pws),8} MB | commit {Mb(priv),8} MB | WS {Mb(ws),8} MB | CPU {cpuPct,5:0.0}%");
        try { await Task.Delay(interval, cts.Token); } catch (OperationCanceledException) { break; }
    }
    if (detail is not null) await detail.DisposeAsync();

    PrintSummary(label, rows, settledAt);
    return 0;
}

// "Carga estabilizada" = número de processos constante e memória privada variando menos de
// --settle-mem % por 5 leituras seguidas (1/s). Páginas com animação contínua nunca ficam com CPU
// baixa, então a memória é o critério principal; --settle-cpu (opcional) acrescenta um teto de CPU.
async Task<double?> WaitSettled(string target, double cpuThreshold, double memThresholdPct, TimeSpan timeout, Stopwatch since)
{
    var cpu = new CpuTracker();
    var calm = 0;
    long lastMem = -1;
    var lastCount = -1;
    while (since.Elapsed < timeout)
    {
        await Task.Delay(1000);
        var procs = Resolve(target);
        if (procs.Count == 0) { calm = 0; continue; }
        long mem = 0;
        foreach (var p in procs)
            if (ProcessMemoryReader.TryRead(p.Pid, out var m)) mem += m.PrivateBytes;
        var pct = cpu.Sample(procs);
        var memStable = lastMem > 0 && Math.Abs(mem - lastMem) * 100.0 / lastMem < memThresholdPct;
        var countStable = procs.Count == lastCount;
        var cpuOk = cpuThreshold <= 0 || pct < cpuThreshold;
        calm = memStable && countStable && cpuOk ? calm + 1 : 0;
        lastMem = mem;
        lastCount = procs.Count;
        if (calm >= 5) return since.Elapsed.TotalSeconds - 5;
    }
    return null;
}

int Compare(List<string> files)
{
    if (files.Count < 2) return Fail("compare exige dois CSVs: MemBench compare cenarioA.csv cenarioB.csv");
    var a = LoadCsv(files[0]);
    var b = LoadCsv(files[1]);
    Console.WriteLine();
    Console.WriteLine($"{"métrica",-36} {"A: " + a.Label,22} {"B: " + b.Label,22} {"B vs A",10}");
    Line("privada em RAM — média (MB)", a.Avg(r => r.Pws), b.Avg(r => r.Pws));
    Line("privada em RAM — 2ª metade (MB)", a.SecondHalf(r => r.Pws), b.SecondHalf(r => r.Pws));
    Line("privada em RAM — máxima (MB)", a.Max(r => r.Pws), b.Max(r => r.Pws));
    Line("commit privado — média (MB)", a.Avg(r => r.Priv), b.Avg(r => r.Priv));
    Line("working set — média (MB)", a.Avg(r => r.Ws), b.Avg(r => r.Ws));
    Line("processos — média", a.Avg(r => r.Count), b.Avg(r => r.Count));
    Line("CPU da máquina — média (%)", a.Avg(r => r.Cpu), b.Avg(r => r.Cpu));
    if (a.Settled is double sa && b.Settled is double sb) Line("tempo até estabilizar (s)", sa, sb);
    Console.WriteLine($"\nAmostras: A={a.Rows.Count}, B={b.Rows.Count}. A 'privada em RAM' é a coluna Memória do Gerenciador de Tarefas.");
    return 0;

    void Line(string name, double va, double vb)
    {
        var diff = va > 0 ? (vb - va) / va * 100 : double.NaN;
        Console.WriteLine($"{name,-36} {va,22:0.0} {vb,22:0.0} {(double.IsNaN(diff) ? "—" : diff.ToString("+0.0;-0.0", inv) + "%"),10}");
    }
}

int ListTarget(Options o)
{
    var target = o.Get("target") ?? throw new ArgumentException("--target é obrigatório");
    var procs = Resolve(target);
    long total = 0;
    foreach (var p in procs.OrderBy(p => p.ExeName).ThenBy(p => p.Pid))
    {
        if (!ProcessMemoryReader.TryRead(p.Pid, out var m)) continue;
        total += Math.Max(0, m.PrivateWorkingSet);
        Console.WriteLine($"{p.Pid,7} (pai {p.ParentPid,7}) {p.ExeName,-24} privada-RAM {Mb(m.PrivateWorkingSet),8} MB  commit {Mb(m.PrivateBytes),8} MB  WS {Mb(m.WorkingSet),8} MB");
    }
    Console.WriteLine($"TOTAL: {procs.Count} processos, privada em RAM {Mb(total)} MB");
    return 0;
}

// ---------------------------------------------------------------------------------------------

static List<ProcessEntry> Resolve(string target)
{
    var all = ProcessTree.Snapshot();
    if (target.Equals("launched", StringComparison.OrdinalIgnoreCase))
    {
        // Só os processos iniciados por este MemBench (e descendentes): não mistura com um Chrome já aberto.
        var result = new Dictionary<int, ProcessEntry>();
        foreach (var root in LaunchedRoots)
            foreach (var p in ProcessTree.Descendants(all, root))
                result.TryAdd(p.Pid, p);
        return result.Values.ToList();
    }
    if (target.Equals("chrome", StringComparison.OrdinalIgnoreCase))
        return ByExeWithTree(all, "chrome.exe");
    if (target.Equals("app", StringComparison.OrdinalIgnoreCase))
        return ByExeWithTree(all, "ThoseIdles.exe");
    if (target.StartsWith("exe:", StringComparison.OrdinalIgnoreCase))
        return ByExeWithTree(all, target[4..]);
    if (target.StartsWith("pid:", StringComparison.OrdinalIgnoreCase))
        return ProcessTree.Descendants(all, int.Parse(target[4..], CultureInfo.InvariantCulture));
    throw new ArgumentException($"alvo inválido: {target}");
}

// Todos os processos com aquele executável + seus descendentes (crashpad, msedgewebview2 etc.).
static List<ProcessEntry> ByExeWithTree(List<ProcessEntry> all, string exe)
{
    var result = new Dictionary<int, ProcessEntry>();
    foreach (var root in all.Where(p => p.ExeName.Equals(exe, StringComparison.OrdinalIgnoreCase)))
        foreach (var p in ProcessTree.Descendants(all, root.Pid))
            result.TryAdd(p.Pid, p);
    return result.Values.ToList();
}

static string Mb(long bytes) => bytes < 0 ? "" : (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);
static string Sanitize(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

static int Fail(string msg)
{
    Console.Error.WriteLine(msg);
    PrintHelp();
    return 2;
}

void PrintSummary(string label, List<Row> rows, double? settled)
{
    if (rows.Count == 0) return;
    var half = rows.Skip(rows.Count / 2).ToList();
    Console.WriteLine();
    Console.WriteLine($"Resumo '{label}' ({rows.Count} amostras)");
    Console.WriteLine($"  privada em RAM: média {Mb((long)rows.Average(r => r.Pws))} MB · 2ª metade {Mb((long)half.Average(r => r.Pws))} MB · máx {Mb(rows.Max(r => r.Pws))} MB");
    Console.WriteLine($"  commit privado: média {Mb((long)rows.Average(r => r.Priv))} MB · working set médio {Mb((long)rows.Average(r => r.Ws))} MB");
    Console.WriteLine($"  processos: {rows.Min(r => r.Count)}–{rows.Max(r => r.Count)} · CPU média {rows.Average(r => r.Cpu):0.0}%");
    if (settled is double s) Console.WriteLine($"  tempo até estabilizar: {s:0.0}s");
}

static CsvData LoadCsv(string path)
{
    var lines = File.ReadAllLines(path).Skip(1).Where(l => l.Length > 0).Select(l => l.Split(';')).ToList();
    var rows = lines.Select(c => new Row(
        double.Parse(c[1], CultureInfo.InvariantCulture), int.Parse(c[3], CultureInfo.InvariantCulture),
        (long)(double.Parse(c[4], CultureInfo.InvariantCulture) * 1048576), (long)(double.Parse(c[5], CultureInfo.InvariantCulture) * 1048576),
        (long)(double.Parse(c[6], CultureInfo.InvariantCulture) * 1048576), double.Parse(c[7], CultureInfo.InvariantCulture))).ToList();
    double? settled = lines.Select(c => c.Length > 10 ? c[10] : "").Where(v => v.Length > 0).Select(v => (double?)double.Parse(v, CultureInfo.InvariantCulture)).FirstOrDefault();
    return new CsvData(lines.FirstOrDefault()?[2] ?? Path.GetFileName(path), rows, settled);
}

static void PrintHelp()
{
    Console.WriteLine("""
        MemBench — comparação de memória: Chrome (cenário A) x ThoseIdles (cenário B)

          MemBench sample  --target <alvo> [--duration 600] [--interval 5] [--label nome] [--out arquivo.csv] [--detail]
          MemBench launch  --target <alvo> --exe <caminho> [--arg "<args>"]... [--args-file linhas.txt] [--settle-mem 2] [--settle-cpu 0] [--settle-timeout 120] [opções do sample]
          MemBench list    --target <alvo>
          MemBench compare cenarioA.csv cenarioB.csv

        Alvos:
          chrome            todos os chrome.exe e descendentes
          app               ThoseIdles.exe e TODOS os descendentes (msedgewebview2.exe: navegador, GPU, rede, renderers…)
          exe:<nome.exe>    qualquer executável e descendentes
          pid:<pid>         um processo e descendentes
          launched          (só com "launch") apenas os processos iniciados pelo MemBench e descendentes —
                            use para medir um Chrome de teste sem misturar com o Chrome que você já usa

        Colunas: privada em RAM (= coluna "Memória" do Gerenciador de Tarefas), commit privado, working set
        (inclui DLLs compartilhadas; somar entre processos superestima), CPU, RAM disponível no sistema.
        "launch" mede o tempo até a carga estabilizar: nº de processos constante e commit privado variando
        menos de --settle-mem % por 5 s seguidos (opcional: CPU abaixo de --settle-cpu %). É uma
        aproximação externa, igual para os dois cenários; o app registra também o tempo exato por conta.
        """);
}

static Options ParseOptions(string[] a)
{
    // Opções que SEMPRE levam valor: o valor pode começar com "--" (ex.: --arg "--user-data-dir=...").
    var valued = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        { "target", "exe", "arg", "args-file", "duration", "interval", "label", "out", "settle-cpu", "settle-mem", "settle-timeout" };
    var o = new Options();
    for (var i = 0; i < a.Length; i++)
    {
        if (a[i].StartsWith("--", StringComparison.Ordinal))
        {
            var key = a[i][2..];
            if (valued.Contains(key))
            {
                if (i + 1 >= a.Length) throw new ArgumentException($"--{key} exige um valor");
                o.Add(key, a[++i]);
            }
            else if (i + 1 < a.Length && !a[i + 1].StartsWith("--", StringComparison.Ordinal)) o.Add(key, a[++i]);
            else o.Add(key, "true");
        }
        else
        {
            o.Positional.Add(a[i]);
        }
    }
    return o;
}

// ---------------------------------------------------------------------------------------------

static partial class Program
{
    public static readonly List<int> LaunchedRoots = new();
}

sealed class Options
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Positional { get; } = new();
    public void Add(string k, string v) { if (!_values.TryGetValue(k, out var l)) _values[k] = l = new(); l.Add(v); }
    public string? Get(string k) => _values.TryGetValue(k, out var l) ? l[^1] : null;
    public List<string> GetAll(string k) => _values.TryGetValue(k, out var l) ? new List<string>(l) : new();
    public bool Has(string k) => _values.ContainsKey(k);
}

sealed record Row(double Seconds, int Count, long Pws, long Priv, long Ws, double Cpu);

sealed record CsvData(string Label, List<Row> Rows, double? Settled)
{
    public double Avg(Func<Row, double> f) => Rows.Count == 0 ? 0 : Rows.Average(r => f(r)) / Scale(f);
    public double Max(Func<Row, double> f) => Rows.Count == 0 ? 0 : Rows.Max(r => f(r)) / Scale(f);
    public double SecondHalf(Func<Row, double> f) => Rows.Count == 0 ? 0 : Rows.Skip(Rows.Count / 2).Average(r => f(r)) / Scale(f);
    // Valores de memória vêm em bytes; contagem e CPU não.
    private double Scale(Func<Row, double> f) => f(new Row(0, 0, 1048576, 1048576, 1048576, 0)) == 1048576 ? 1048576 : 1;
}

/// <summary>CPU agregada do alvo em % da máquina inteira (todos os núcleos).</summary>
sealed class CpuTracker
{
    private readonly Dictionary<int, TimeSpan> _last = new();
    private readonly Stopwatch _watch = new();

    public double Sample(List<ProcessEntry> procs)
    {
        var elapsed = _watch.Elapsed;
        _watch.Restart();
        double used = 0;
        var seen = new HashSet<int>();
        foreach (var p in procs)
        {
            try
            {
                using var proc = Process.GetProcessById(p.Pid);
                var t = proc.TotalProcessorTime;
                if (_last.TryGetValue(p.Pid, out var prev) && t >= prev) used += (t - prev).TotalMilliseconds;
                _last[p.Pid] = t;
                seen.Add(p.Pid);
            }
            catch { }
        }
        foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        if (elapsed.TotalMilliseconds <= 0) return 0;
        return used / (elapsed.TotalMilliseconds * Environment.ProcessorCount) * 100;
    }
}
