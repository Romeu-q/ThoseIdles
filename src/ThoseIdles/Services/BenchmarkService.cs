using System.Diagnostics;
using System.Globalization;
using System.Text;
using ThoseIdles.Shared;

namespace ThoseIdles.Services;

/// <summary>
/// Diagnóstico interno: linha do tempo de inicialização e série de memória em CSV.
/// A comparação com o Chrome é feita pelo MemBench (tools/MemBench), que mede os dois cenários
/// com o mesmo método; os CSVs daqui detalham a divisão por conta, que só o app conhece.
/// </summary>
public sealed class BenchmarkService
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private readonly string _folder;
    private readonly AppSettings _settings;
    private readonly DateTime _processStartUtc;
    private readonly List<(string Event, double Seconds)> _timeline = new();
    private readonly HashSet<int> _loaded = new(), _connected = new();
    private string? _memoryCsv;
    private bool _timelineWritten;

    public BenchmarkService(string folder, AppSettings settings)
    {
        _folder = folder;
        _settings = settings;
        using var self = Process.GetCurrentProcess();
        _processStartUtc = self.StartTime.ToUniversalTime();
    }

    public double SecondsSinceProcessStart => (DateTime.UtcNow - _processStartUtc).TotalSeconds;

    public void Mark(string evt)
    {
        var s = SecondsSinceProcessStart;
        _timeline.Add((evt, s));
        LoggingService.Log.Info($"[inicialização] {evt}: {s:0.00}s desde o início do processo");
    }

    public void Attach(BrowserSessionManager manager)
    {
        foreach (var session in manager.Sessions)
        {
            session.Milestone += (sender, m) =>
            {
                var slot = ((BrowserSession)sender!).Slot;
                var set = m.Milestone == "carregada" ? _loaded : _connected;
                if (!set.Add(slot))
                {
                    // Reinício posterior: não faz parte da inicialização.
                    LoggingService.Log.Info($"[conta{slot}] {m.Milestone} em {m.Elapsed.TotalSeconds:0.00}s após (re)abrir.");
                    return;
                }
                Mark($"conta{slot} {m.Milestone} ({m.Elapsed.TotalSeconds:0.00}s após abrir a conta)");
                if (set.Count == AppSettings.AccountCount)
                {
                    Mark($"todas as 4 contas {m.Milestone}s");
                    if (set == _connected || !_settings.MonitorGameConnection) WriteTimeline();
                }
            };
        }
    }

    /// <summary>Grava a linha do tempo (chamado quando as 4 conectam ou ao sair).</summary>
    public void WriteTimeline()
    {
        if (_timelineWritten || _timeline.Count == 0) return;
        _timelineWritten = true;
        try
        {
            Directory.CreateDirectory(_folder);
            var path = Path.Combine(_folder, $"startup-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
            var sb = new StringBuilder("evento;segundos_desde_inicio_processo\n");
            foreach (var (evt, sec) in _timeline) sb.Append(Csv(evt)).Append(';').Append(sec.ToString("0.000", Inv)).Append('\n');
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"Falha ao gravar linha do tempo: {ex.Message}");
        }
    }

    public void OnMemorySample(MemorySnapshot s)
    {
        if (!_settings.RecordBenchmarkCsv) return;
        try
        {
            Directory.CreateDirectory(_folder);
            if (_memoryCsv is null)
            {
                _memoryCsv = Path.Combine(_folder, $"memoria-app-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
                File.WriteAllText(_memoryCsv,
                    "timestamp_utc;segundos;processos;total_privada_mb;total_ws_mb;total_privada_ws_mb;app_privada_mb;compartilhado_privada_mb;" +
                    "conta1_privada_mb;conta2_privada_mb;conta3_privada_mb;conta4_privada_mb;sistema_ram_disponivel_mb;contas_conectadas\n", Encoding.UTF8);
            }
            var sys = ProcessMemoryReader.ReadSystem();
            var line = string.Join(';',
                s.TimeUtc.ToString("o", Inv),
                SecondsSinceProcessStart.ToString("0", Inv),
                s.Total.Processes.ToString(Inv),
                Mb(s.Total.PrivateBytes), Mb(s.Total.WorkingSet), Mb(s.Total.PrivateWorkingSet),
                Mb(s.Host.PrivateBytes), Mb(s.Shared.PrivateBytes),
                Mb(s.Accounts[0].PrivateBytes), Mb(s.Accounts[1].PrivateBytes), Mb(s.Accounts[2].PrivateBytes), Mb(s.Accounts[3].PrivateBytes),
                Mb(sys.AvailPhys),
                _connectedNow.ToString(Inv));
            File.AppendAllText(_memoryCsv, line + "\n");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"Falha ao gravar CSV de memória: {ex.Message}");
        }
    }

    private int _connectedNow;
    public void SetConnectedCount(int n) => _connectedNow = n;

    /// <summary>Exporta uma foto detalhada (um processo por linha). Retorna o caminho.</summary>
    public string ExportSnapshot(MemorySnapshot s)
    {
        Directory.CreateDirectory(_folder);
        var path = Path.Combine(_folder, $"snapshot-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        var sb = new StringBuilder("pid;tipo;conta;privada_mb;working_set_mb;privada_ws_mb\n");
        foreach (var p in s.Processes.OrderBy(p => p.Slot ?? 0).ThenBy(p => p.Kind))
        {
            sb.Append(p.Pid).Append(';').Append(Csv(p.Kind)).Append(';').Append(p.Slot?.ToString(Inv) ?? "compartilhado").Append(';')
              .Append(Mb(p.Memory.PrivateBytes)).Append(';').Append(Mb(p.Memory.WorkingSet)).Append(';').Append(Mb(p.Memory.PrivateWorkingSet)).Append('\n');
        }
        sb.Append($"TOTAL;;;{Mb(s.Total.PrivateBytes)};{Mb(s.Total.WorkingSet)};{Mb(s.Total.PrivateWorkingSet)}\n");
        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        return path;
    }

    private static string Mb(long bytes) => bytes < 0 ? "" : (bytes / 1048576.0).ToString("0.0", Inv);
    private static string Csv(string s) => s.Contains(';') || s.Contains('"') ? "\"" + s.Replace("\"", "\"\"") + "\"" : s;
}
