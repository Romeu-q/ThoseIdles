using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// Baixa, valida e guarda a extensão "/hunt for PokeIdle" (guia de hunts do jogo).
///
/// - A extensão NÃO é distribuída com o app: é baixada do servidor de atualização do Google
///   (o mesmo que o Chrome usa) quando o usuário pede.
/// - Antes de instalar, o manifesto é validado: só são aceitos content scripts que rodem nos
///   endereços permitidos (pokeidle.io), sem permissões, sem background e sem páginas próprias.
///   Se uma versão futura pedir mais do que isso, ela é recusada e o motivo aparece na tela.
/// - Cada versão fica numa pasta própria (extensions\hunt\&lt;versão&gt;); o WebView2 carrega a
///   extensão "desempacotada" a partir dessa pasta, em cada perfil.
/// </summary>
public sealed class HuntExtensionService
{
    public const string StoreId = "mhnplloljgchpfpcpbhmcokmnefodjnh";
    public const string ExtensionName = "/hunt for PokeIdle";
    private const long MaxDownloadBytes = 5 * 1024 * 1024;

    /// <summary>Chaves de manifest aceitas. Qualquer outra (permissions, background, action…) recusa a versão.</summary>
    private static readonly HashSet<string> AllowedManifestKeys = new(StringComparer.Ordinal)
    {
        "manifest_version", "name", "short_name", "version", "version_name", "description", "icons",
        "content_scripts", "update_url", "default_locale", "author", "homepage_url", "minimum_chrome_version", "key",
    };

    private readonly string _root;
    private readonly IReadOnlyCollection<string> _allowedMatches;

    public HuntExtensionService(string root, IReadOnlyCollection<string> allowedMatches)
    {
        _root = root;
        _allowedMatches = allowedMatches;
    }

    private string CurrentPointer => Path.Combine(_root, "current.txt");

    /// <summary>Pasta da versão em uso, ou null se nenhuma foi baixada.</summary>
    public string? InstalledFolder
    {
        get
        {
            try
            {
                if (!File.Exists(CurrentPointer)) return null;
                var folder = Path.Combine(_root, File.ReadAllText(CurrentPointer).Trim());
                return File.Exists(Path.Combine(folder, "manifest.json")) ? folder : null;
            }
            catch { return null; }
        }
    }

    public string? InstalledVersion => InstalledFolder is { } f ? Path.GetFileName(f) : null;

    /// <summary>Resposta do servidor de atualização: versão disponível, endereço do .crx e hash.</summary>
    public sealed record UpdateInfo(string Version, string Codebase, string Sha256, long Size);

    private static string ProdVersion(string browserVersion)
    {
        var prod = string.Concat(browserVersion.TakeWhile(c => char.IsDigit(c) || c == '.'));
        return prod.Length == 0 ? "150.0.0.0" : prod;
    }

    /// <summary>
    /// Pergunta à loja (protocolo de atualização do Chrome) se há versão mais nova que a instalada.
    /// Retorna null quando já está atualizada. Lança em erro de rede/formato.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(string browserVersion, CancellationToken ct = default)
    {
        var current = InstalledVersion ?? "0.0.0";
        var url = $"https://clients2.google.com/service/update2/crx?response=updatecheck&acceptformat=crx3" +
                  $"&prodversion={ProdVersion(browserVersion)}&x=id%3D{StoreId}%26v%3D{current}%26uc";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var xml = await http.GetStringAsync(url, ct);
        var doc = System.Xml.Linq.XDocument.Parse(xml);
        var check = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "updatecheck")
                    ?? throw new InvalidDataException("resposta sem <updatecheck>");
        var status = (string?)check.Attribute("status");
        if (status == "noupdate") return null;
        if (status != "ok") throw new InvalidDataException($"status '{status}'");

        var version = (string?)check.Attribute("version") ?? throw new InvalidDataException("sem versão");
        var codebase = (string?)check.Attribute("codebase") ?? throw new InvalidDataException("sem codebase");
        var sha = ((string?)check.Attribute("hash_sha256") ?? "").ToLowerInvariant();
        long.TryParse((string?)check.Attribute("size"), out var size);
        if (InstalledVersion is { } installed && Version.TryParse(installed, out var a) && Version.TryParse(version, out var b) && b <= a)
            return null;
        return new UpdateInfo(version, codebase, sha, size);
    }

    /// <summary>Baixa o .crx indicado pela loja, confere tamanho e SHA-256 e instala.</summary>
    public async Task<(bool Ok, string Message)> InstallUpdateAsync(UpdateInfo info, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(info.Codebase, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".google.com", StringComparison.OrdinalIgnoreCase)))
            return (false, "A loja indicou um endereço de download inesperado; atualização ignorada.");
        if (info.Size > MaxDownloadBytes) return (false, "Arquivo grande demais para esta extensão.");
        try
        {
            var bytes = await DownloadBytesAsync(uri.AbsoluteUri, ct);
            if (bytes is null) return (false, "Arquivo grande demais para esta extensão.");
            if (info.Size > 0 && bytes.Length != info.Size) return (false, "Tamanho do arquivo diferente do informado pela loja.");
            if (info.Sha256.Length == 64)
            {
                var actual = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
                if (actual != info.Sha256) return (false, "O arquivo baixado não confere com o hash SHA-256 da loja.");
            }
            return InstallFromCrx(bytes);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            LoggingService.Log.Warn($"Download da extensão falhou: {ex.Message}");
            return (false, $"Não foi possível baixar: {ex.Message}");
        }
    }

    /// <summary>Baixa (ou reinstala) a versão mais recente da loja. Retorna (ok, mensagem para a tela).</summary>
    public async Task<(bool Ok, string Message)> DownloadLatestAsync(string browserVersion, CancellationToken ct = default)
    {
        try
        {
            var info = await CheckForUpdateAsync(browserVersion, ct);
            if (info is null)
                return InstalledVersion is { } v ? (true, $"{ExtensionName} v{v} já é a versão mais recente.") : (false, "A loja não ofereceu nenhuma versão.");
            return await InstallUpdateAsync(info, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException or System.Xml.XmlException)
        {
            LoggingService.Log.Warn($"Consulta à loja falhou: {ex.Message}");
            return (false, $"Não foi possível consultar a loja: {ex.Message}");
        }
    }

    private static async Task<byte[]?> DownloadBytesAsync(string url, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength > MaxDownloadBytes) return null;
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxDownloadBytes) return null;
        }
        return buffer.ToArray();
    }

    /// <summary>Instala a partir dos bytes de um .crx (formato CRX3: "Cr24", versão, cabeçalho, zip).</summary>
    public (bool Ok, string Message) InstallFromCrx(byte[] crx)
    {
        if (crx.Length < 16 || crx[0] != 'C' || crx[1] != 'r' || crx[2] != '2' || crx[3] != '4')
            return (false, "O arquivo baixado não é uma extensão (.crx).");
        var format = BitConverter.ToUInt32(crx, 4);
        if (format != 3) return (false, $"Formato CRX {format} não suportado.");
        var headerLength = BitConverter.ToUInt32(crx, 8);
        var zipStart = 12L + headerLength;
        if (zipStart >= crx.Length) return (false, "Arquivo .crx corrompido.");

        var staging = Path.Combine(_root, "_novo");
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
            Directory.CreateDirectory(staging);
            using (var zip = new ZipArchive(new MemoryStream(crx, (int)zipStart, crx.Length - (int)zipStart), ZipArchiveMode.Read))
            {
                // ExtractToDirectory recusa entradas com "..": sem zip-slip.
                zip.ExtractToDirectory(staging);
            }
            return InstallFromFolder(staging, moveSource: true);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (false, $"Não foi possível abrir a extensão: {ex.Message}");
        }
        finally
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
        }
    }

    /// <summary>Valida o manifesto da pasta e a torna a versão em uso.</summary>
    public (bool Ok, string Message) InstallFromFolder(string source, bool moveSource = false)
    {
        var manifestPath = Path.Combine(source, "manifest.json");
        if (!File.Exists(manifestPath)) return (false, "manifest.json ausente.");

        string version, name;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath), new JsonDocumentOptions { AllowTrailingCommas = true });
            var problem = ValidateManifest(doc.RootElement, _allowedMatches);
            if (problem is not null)
            {
                LoggingService.Log.Warn($"Extensão recusada: {problem}");
                return (false, $"Versão recusada por segurança: {problem}");
            }
            version = doc.RootElement.GetProperty("version").GetString()!;
            name = doc.RootElement.GetProperty("name").GetString()!;
        }
        catch (JsonException ex)
        {
            return (false, $"manifest.json inválido: {ex.Message}");
        }

        if (version.Any(c => !(char.IsDigit(c) || c == '.')) || version.Length is 0 or > 24)
            return (false, "Versão com formato inesperado.");

        var target = Path.Combine(_root, version);
        if (!Directory.Exists(target))
        {
            Directory.CreateDirectory(_root);
            if (moveSource) Directory.Move(source, target);
            else CopyDirectory(source, target);
        }
        File.WriteAllText(CurrentPointer, version);
        LoggingService.Log.Info($"Extensão '{name}' v{version} pronta em {target}");
        return (true, $"{name} v{version} pronta.");
    }

    /// <summary>Apaga versões antigas que não estão mais em uso.</summary>
    public void CleanupOldVersions()
    {
        var current = InstalledVersion;
        if (current is null || !Directory.Exists(_root)) return;
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var n = Path.GetFileName(dir);
            if (n == current || n.StartsWith('_')) continue;
            try { Directory.Delete(dir, true); } catch { /* ainda carregada por algum perfil; fica para a próxima */ }
        }
    }

    /// <summary>null se o manifesto é aceitável; senão, o motivo.</summary>
    public static string? ValidateManifest(JsonElement m, IReadOnlyCollection<string> allowedMatches)
    {
        if (m.ValueKind != JsonValueKind.Object) return "manifest não é um objeto";
        var extra = m.EnumerateObject().Select(p => p.Name).Where(k => !AllowedManifestKeys.Contains(k)).ToList();
        if (extra.Count > 0) return "pede recursos além de content scripts: " + string.Join(", ", extra);
        if (!m.TryGetProperty("manifest_version", out var mv) || mv.GetInt32() != 3) return "manifest_version diferente de 3";
        if (!m.TryGetProperty("version", out var v) || v.ValueKind != JsonValueKind.String) return "sem versão";
        if (!m.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String) return "sem nome";
        if (!m.TryGetProperty("content_scripts", out var cs) || cs.ValueKind != JsonValueKind.Array || cs.GetArrayLength() == 0)
            return "sem content scripts";

        foreach (var entry in cs.EnumerateArray())
        {
            if (!entry.TryGetProperty("matches", out var matches) || matches.ValueKind != JsonValueKind.Array)
                return "content script sem 'matches'";
            foreach (var p in matches.EnumerateArray())
            {
                var pattern = p.GetString() ?? "";
                if (!allowedMatches.Contains(pattern, StringComparer.OrdinalIgnoreCase))
                    return $"roda em endereço não permitido: {pattern}";
            }
            foreach (var key in new[] { "js", "css" })
            {
                if (!entry.TryGetProperty(key, out var files)) continue;
                foreach (var f in files.EnumerateArray())
                {
                    var path = f.GetString() ?? "";
                    if (path.Contains("..") || Path.IsPathRooted(path) || path.Contains(':')) return $"caminho de arquivo inválido: {path}";
                }
            }
        }
        return null;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(source, file);
            var dest = Path.Combine(target, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }
}
