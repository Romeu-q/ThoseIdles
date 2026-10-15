using System.Text.Json;
using System.Text.Json.Serialization;

namespace ThoseIdles.Services;

public sealed class AccountSettings
{
    /// <summary>1 a 4. Determina o nome do perfil WebView2 (conta1…conta4), que nunca muda.</summary>
    public int Slot { get; set; }

    /// <summary>Nome exibido. Renomear não toca no perfil nem na sessão.</summary>
    public string DisplayName { get; set; } = "";

    /// <summary>Se a conta abre junto com "Abrir todas" / na inicialização.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Extensão /hunt ligada nesta conta (só vale com <see cref="AppSettings.EnableHuntExtension"/>).</summary>
    public bool HuntEnabled { get; set; } = true;

    /// <summary>Zoom da página desta conta (1.0 = 100%). Ctrl + roda do mouse na página ajusta e salva.</summary>
    public double Zoom { get; set; }

    /// <summary>Canal da Twitch aberto no painel de live desta conta (null = sem live).</summary>
    public string? LiveChannel { get; set; }

    /// <summary>Se a live desta conta aparece na tela. Escondida ela continua aberta, como uma aba em segundo plano.</summary>
    public bool LiveVisible { get; set; }

    /// <summary>Altura da live em relação ao painel da conta (0.15–0.85).</summary>
    public double LiveSplit { get; set; } = 0.4;

    /// <summary>Id e pasta da extensão instalada neste perfil (o id de uma extensão desempacotada depende da pasta).</summary>
    public string? HuntExtensionId { get; set; }
    public string? HuntExtensionFolder { get; set; }

    [JsonIgnore]
    public string ProfileName => ProfileManager.ProfileNameForSlot(Slot);
}

/// <summary>Posições das divisórias (frações 0–1 da área útil). Ajustadas arrastando na tela.</summary>
public sealed class LayoutSplits
{
    /// <summary>Altura da linha de cima na grade.</summary>
    public double Row { get; set; } = 0.5;
    /// <summary>Largura da conta 1 dentro da linha de cima (o resto é da conta 2).</summary>
    public double Top { get; set; } = 0.5;
    /// <summary>Largura da conta 3 dentro da linha de baixo (o resto é da conta 4).</summary>
    public double Bottom { get; set; } = 0.5;
    /// <summary>No modo "uma conta": largura da conta em foco (o resto é a coluna de miniaturas).</summary>
    public double Focus { get; set; } = 0.78;

    public void Normalize()
    {
        Row = Clamp(Row);
        Top = Clamp(Top);
        Bottom = Clamp(Bottom);
        Focus = Math.Clamp(double.IsFinite(Focus) ? Focus : 0.78, 0.3, 0.95);
    }

    private static double Clamp(double v) => Math.Clamp(double.IsFinite(v) ? v : 0.5, 0.1, 0.9);
}

public sealed class WindowPlacement
{
    /// <summary>null = ainda não posicionada (centraliza).</summary>
    public double? Left { get; set; }
    public double? Top { get; set; }
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; } = true;
}

public sealed class AppSettings
{
    public const int AccountCount = 4;

    public string StartUrl { get; set; } = "https://pokeidle.io/app";
    public List<AccountSettings> Accounts { get; set; } = new();

    public bool StartAllOnLaunch { get; set; } = true;

    /// <summary>Habilita DevTools (F12 / botão). Desligado por padrão.</summary>
    public bool DeveloperMode { get; set; }

    /// <summary>Observa o WebSocket do jogo (via DevTools Protocol, só leitura) para o indicador "Conectado".</summary>
    public bool MonitorGameConnection { get; set; } = true;

    public int MemoryPollSeconds { get; set; } = 5;

    /// <summary>Zoom das páginas na grade 2x2 (1.0 = 100%).</summary>
    public double GridZoom { get; set; } = 1.0;

    /// <summary>No modo foco, as outras contas viram miniaturas reduzidas por zoom (o jogo segue rodando).</summary>
    public bool ScaleThumbnails { get; set; } = true;

    /// <summary>Renderiza a UI WPF por software: o anfitrião não carrega Direct3D. Os jogos não são afetados.</summary>
    public bool SoftwareRenderingForUi { get; set; } = true;

    /// <summary>
    /// Opcional e desligado por padrão: pede ao WebView2 MemoryUsageTargetLevel.Low nas miniaturas.
    /// Não pausa scripts, mas pode descartar caches e deixar a página mais lenta quando voltar ao foco.
    /// </summary>
    public bool LowMemoryTargetForThumbnails { get; set; }

    /// <summary>
    /// Carrega a extensão "/hunt for PokeIdle" (guia de hunts, só leitura) nos perfis.
    /// Desligado por padrão: o ambiente WebView2 só habilita extensões quando isto está ligado.
    /// </summary>
    public bool EnableHuntExtension { get; set; }

    /// <summary>Consulta a loja ao abrir o app e a cada 12 h; versão nova é baixada e validada sozinha.</summary>
    public bool AutoUpdateHuntExtension { get; set; } = true;

    /// <summary>Live da Twitch: abre muda (o áudio fica desligado no WebView2, não só no player).</summary>
    public bool LiveMutedByDefault { get; set; } = true;

    /// <summary>Live da Twitch: qualidade padrão 160p (gravada uma vez; trocar no player continua valendo).</summary>
    public bool LiveLowestQuality { get; set; } = true;

    /// <summary>Reabre a live de cada conta quando a conta abre.</summary>
    public bool ReopenLives { get; set; } = true;

    /// <summary>
    /// Modo leve: em vez do site inteiro da Twitch, uma página mínima do app com o PLAYER e o CHAT
    /// oficiais incorporados (embed). Desligado por padrão: o chat incorporado precisa reconhecer o
    /// login da Twitch para o jogo detectar o jogador — confira na sua conta antes de usar.
    /// </summary>
    public bool LiveLightMode { get; set; }

    /// <summary>Live escondida: pede ao WebView2 meta de memória baixa para aquela aba (o jogo não é afetado).</summary>
    public bool LiveHiddenLowMemory { get; set; } = true;

    /// <summary>Grava amostras de memória em CSV na pasta benchmark.</summary>
    public bool RecordBenchmarkCsv { get; set; }

    /// <summary>Hosts aceitos na navegação principal. "*.dominio" aceita subdomínios.</summary>
    public List<string> AllowedHosts { get; set; } = new();

    public bool FocusMode { get; set; }
    public int FocusedSlot { get; set; } = 1;

    public WindowPlacement Window { get; set; } = new();

    public LayoutSplits Splits { get; set; } = new();

    public static readonly string[] DefaultAllowedHosts =
    {
        "pokeidle.io", "*.pokeidle.io",
        // Login por Google / Discord / Twitch (o jogo navega na MESMA aba: /auth/<provedor>).
        "accounts.google.com", "*.google.com", "accounts.youtube.com",
        "discord.com", "*.discord.com",
        "id.twitch.tv", "*.twitch.tv",
        "challenges.cloudflare.com",
    };

    public AccountSettings Account(int slot) => Accounts.First(a => a.Slot == slot);

    /// <summary>Corrige qualquer valor ausente ou fora da faixa depois de ler o arquivo.</summary>
    public void Normalize()
    {
        var fixedAccounts = new List<AccountSettings>(AccountCount);
        for (var slot = 1; slot <= AccountCount; slot++)
        {
            var a = Accounts?.FirstOrDefault(x => x.Slot == slot) ?? new AccountSettings { Slot = slot };
            a.Slot = slot;
            a.DisplayName = string.IsNullOrWhiteSpace(a.DisplayName) ? $"Conta {slot}" : a.DisplayName.Trim();
            if (a.DisplayName.Length > 40) a.DisplayName = a.DisplayName[..40];
            fixedAccounts.Add(a);
        }
        Accounts = fixedAccounts;

        if (!Uri.TryCreate(StartUrl, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps)
            StartUrl = "https://pokeidle.io/app";
        MemoryPollSeconds = Math.Clamp(MemoryPollSeconds, 2, 60);
        GridZoom = Math.Clamp(GridZoom, 0.25, 2.0);
        FocusedSlot = Math.Clamp(FocusedSlot, 1, AccountCount);
        AllowedHosts = (AllowedHosts is { Count: > 0 } ? AllowedHosts : DefaultAllowedHosts.ToList())
            .Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0).Distinct().ToList();
        Window ??= new WindowPlacement();
        Splits ??= new LayoutSplits();
        Splits.Normalize();
        foreach (var a in Accounts)
        {
            a.Zoom = Math.Clamp(double.IsFinite(a.Zoom) && a.Zoom > 0 ? a.Zoom : GridZoom, 0.25, 3.0);
            a.LiveSplit = Math.Clamp(double.IsFinite(a.LiveSplit) && a.LiveSplit > 0 ? a.LiveSplit : 0.4, 0.15, 0.85);
            if (a.LiveChannel is { } ch && !System.Text.RegularExpressions.Regex.IsMatch(ch, "^[A-Za-z0-9_]{2,25}$")) a.LiveChannel = null;
        }
    }
}

/// <summary>Lê e grava settings.json com escrita atômica (arquivo temporário + substituição).</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    private readonly string _path;
    private readonly bool _persist;
    private readonly object _gate = new();

    public AppSettings Current { get; }

    public SettingsService(string path, bool persist = true)
    {
        _path = path;
        _persist = persist;
        Current = Load();
    }

    private AppSettings Load()
    {
        AppSettings? s = null;
        try
        {
            if (File.Exists(_path))
                s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
        }
        catch (Exception ex)
        {
            LoggingService.Log.Error("settings.json inválido; usando padrões (o arquivo antigo foi copiado para settings.bad.json)", ex);
            try { File.Copy(_path, Path.ChangeExtension(_path, ".bad.json"), overwrite: true); } catch { }
        }
        s ??= new AppSettings();
        s.Normalize();
        return s;
    }

    public void Save()
    {
        if (!_persist) return;
        lock (_gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, _path, overwrite: true);
            }
            catch (Exception ex)
            {
                LoggingService.Log.Error("Falha ao salvar settings.json", ex);
            }
        }
    }
}
