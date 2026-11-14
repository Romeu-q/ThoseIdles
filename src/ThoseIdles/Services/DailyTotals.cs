using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// O total de HOJE de cada conta (ouro, XP, abates, capturas, shinies), que sobrevive a fechar e abrir o
/// app. Zera na virada do dia (hora local). Grava a cada 30 s quando mudou, e ao sair.
/// Guarda só números, em %LOCALAPPDATA%\ThoseIdles\hoje.json — nada de nick, token ou Pokémon.
/// </summary>
public sealed class DailyTotals
{
    public sealed class Dia
    {
        public long Ouro { get; set; }
        public long Xp { get; set; }
        public int Abates { get; set; }
        public int Capturas { get; set; }
        public int ShiniesVistos { get; set; }
        public int ShiniesCapturados { get; set; }
    }

    private sealed class Arquivo
    {
        public string Data { get; set; } = "";
        public Dictionary<string, Dia> Contas { get; set; } = new();
    }

    public static DailyTotals Instance { get; } = new();

    private readonly object _lock = new();
    private Arquivo _a = new();
    private bool _carregado, _sujo;
    private Timer? _timer;

    private static string Caminho => Path.Combine(AppPaths.Root, "hoje.json");
    private static string Hoje => DateTime.Now.ToString("yyyy-MM-dd");

    private void Garantir()
    {
        if (!_carregado)
        {
            _carregado = true;
            try
            {
                if (File.Exists(Caminho) && JsonSerializer.Deserialize<Arquivo>(File.ReadAllText(Caminho)) is { } lido) _a = lido;
            }
            catch (Exception ex) { LoggingService.Log.Warn($"Total do dia ilegível ({ex.Message}); começando do zero."); }
        }
        if (_a.Data != Hoje) { _a = new Arquivo { Data = Hoje }; _sujo = true; }
    }

    /// <summary>Soma o que um lote de eventos rendeu à conta do slot.</summary>
    public void Somar(int slot, EconomySessionStats.Soma s)
    {
        lock (_lock)
        {
            Garantir();
            var d = _a.Contas.TryGetValue(slot.ToString(), out var x) ? x : _a.Contas[slot.ToString()] = new Dia();
            d.Ouro += s.Ouro; d.Xp += s.Xp; d.Abates += s.Abates; d.Capturas += s.Capturas;
            d.ShiniesVistos += s.ShiniesVistos; d.ShiniesCapturados += s.ShiniesCapturados;
            _sujo = true;
            _timer ??= new Timer(_ => Gravar(), null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }
    }

    /// <summary>Uma cópia do dia da conta (zeros se ainda não houve nada).</summary>
    public Dia Da(int slot)
    {
        lock (_lock)
        {
            Garantir();
            return _a.Contas.TryGetValue(slot.ToString(), out var d)
                ? new Dia { Ouro = d.Ouro, Xp = d.Xp, Abates = d.Abates, Capturas = d.Capturas, ShiniesVistos = d.ShiniesVistos, ShiniesCapturados = d.ShiniesCapturados }
                : new Dia();
        }
    }

    public string Data { get { lock (_lock) { Garantir(); return _a.Data; } } }

    public void Gravar()
    {
        string json;
        lock (_lock)
        {
            if (!_sujo) return;
            _sujo = false;
            json = JsonSerializer.Serialize(_a);
        }
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var tmp = Caminho + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, Caminho, true);
        }
        catch (Exception ex) { LoggingService.Log.Warn($"Não deu para gravar o total do dia: {ex.Message}"); }
    }

    /// <summary>Só para o autoteste: recomeça do zero (outra pasta de dados).</summary>
    internal void Reiniciar() { lock (_lock) { _a = new Arquivo { Data = Hoje }; _carregado = true; _sujo = false; } }
}
