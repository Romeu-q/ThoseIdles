using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ThoseIdles.Services;

/// <summary>
/// Atualização do app pelas Releases do GitHub:
///  1. consulta a última Release (pública, sem login, sem enviar dados da conta);
///  2. baixa o .exe dela só do endereço de downloads deste repositório;
///  3. confere o arquivo: SHA-256 publicado pelo GitHub + versão do .exe igual à da Release;
///  4. troca o .exe em uso (renomeia o atual para .old) e reabre o app.
/// Se qualquer passo falhar, nada é trocado e o usuário pode baixar pelo site.
/// </summary>
public static class AppUpdateService
{
    /// <summary>API da última Release (o autoteste aponta para o servidor local).</summary>
    public static string LatestReleaseApi { get; set; } = "https://api.github.com/repos/Romeu-q/ThoseIdles/releases/latest";

    /// <summary>Página onde ficam as Releases (só links desse endereço são abertos).</summary>
    public const string ReleasesPage = "https://github.com/Romeu-q/ThoseIdles/releases";

    /// <summary>Só baixa .exe que venha deste endereço (o autoteste troca pelo servidor local).</summary>
    public static string AllowedAssetPrefix { get; set; } = "https://github.com/Romeu-q/ThoseIdles/releases/download/";

    private static readonly Regex AssetName = new(@"^ThoseIdles[^/\\]*\.exe$", RegexOptions.IgnoreCase);

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return Normalize(v);
        }
    }

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <param name="AssetUrl">Endereço do .exe da Release (null = Release sem .exe).</param>
    /// <param name="Sha256">Hash publicado pelo GitHub, em hexadecimal (null se não houver).</param>
    public sealed record Release(Version Version, string Tag, string Url, string? AssetUrl = null, string? Sha256 = null, long Size = 0);

    /// <summary>A Release mais nova que a versão em uso, ou null (sem novidade ou sem internet).</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct)
    {
        var r = await GetLatestAsync(ct);
        return r is not null && r.Version > CurrentVersion ? r : null;
    }

    /// <summary>A última Release publicada (seja ou não mais nova), ou null.</summary>
    public static async Task<Release?> GetLatestAsync(CancellationToken ct)
    {
        try
        {
            using var http = NewClient(TimeSpan.FromSeconds(20));
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            // Abre só páginas do repositório; qualquer outra coisa cai na lista de Releases.
            if (url is null || !url.StartsWith(ReleasesPage + "/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;

            string? assetUrl = null, sha = null;
            long size = 0;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    var name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var dl = a.TryGetProperty("browser_download_url", out var b) ? b.GetString() : null;
                    if (!AssetName.IsMatch(name) || dl is null) continue;
                    if (!dl.StartsWith(AllowedAssetPrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    assetUrl = dl;
                    size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var sz) ? sz : 0;
                    var digest = a.TryGetProperty("digest", out var dg) ? dg.GetString() : null;
                    if (digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest["sha256:".Length..].ToLowerInvariant();
                    break;
                }
            }
            return new Release(Normalize(latest), tag, url, assetUrl, sha, size);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LoggingService.Log.Info($"Verificação de atualização do app falhou: {ex.Message}");
            return null;
        }
    }

    private static HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ThoseIdles/{CurrentVersion}");
        return http;
    }

    /// <summary>
    /// Baixa o .exe da Release para <paramref name="destination"/> e confere hash e versão.
    /// Em qualquer falha apaga o arquivo e devolve o motivo.
    /// </summary>
    public static async Task<(bool Ok, string Message)> DownloadAsync(Release r, string destination, IProgress<double>? progress, CancellationToken ct)
    {
        if (r.AssetUrl is null) return (false, "esta versão não tem o .exe para baixar");
        if (!r.AssetUrl.StartsWith(AllowedAssetPrefix, StringComparison.OrdinalIgnoreCase)) return (false, "endereço de download não confiável");
        var part = destination + ".part";
        try
        {
            using var http = NewClient(TimeSpan.FromMinutes(5));
            using var resp = await http.GetAsync(r.AssetUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? r.Size;
            using var sha = SHA256.Create();
            await using (var input = await resp.Content.ReadAsStreamAsync(ct))
            await using (var output = File.Create(part))
            {
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    done += read;
                    if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
                }
                sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            }

            var hash = Convert.ToHexString(sha.Hash!).ToLowerInvariant();
            if (r.Sha256 is not null && hash != r.Sha256)
                return Fail($"o arquivo baixado não confere com o hash publicado (SHA-256 {hash[..12]}… ≠ {r.Sha256[..12]}…)");

            var info = FileVersionInfo.GetVersionInfo(part);
            if (!Version.TryParse(info.FileVersion, out var fv) || Normalize(fv) != r.Version)
                return Fail($"a versão do arquivo ({info.FileVersion ?? "nenhuma"}) não é a da Release ({r.Tag})");
            if (!string.Equals(info.ProductName, "ThoseIdles", StringComparison.OrdinalIgnoreCase))
                return Fail("o arquivo baixado não é o ThoseIdles");

            File.Move(part, destination, overwrite: true);
            LoggingService.Log.Info($"Atualização {r.Tag} baixada e conferida (SHA-256 {hash}{(r.Sha256 is null ? ", sem hash publicado" : ", igual ao publicado")}).");
            return (true, "ok");
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Fail(ex.Message);
        }

        (bool, string) Fail(string why)
        {
            try { File.Delete(part); } catch { }
            LoggingService.Log.Info($"Atualização {r.Tag} recusada: {why}");
            return (false, why);
        }
    }

    /// <summary>
    /// Troca o .exe em uso pelo baixado e inicia a versão nova, que espera este processo sair.
    /// O Windows deixa renomear um .exe em execução (não apagar): o atual vira ".old" e é apagado
    /// na próxima abertura. Devolve false (e desfaz) se não der para trocar.
    /// </summary>
    public static bool InstallAndRestart(string downloadedExe, IEnumerable<string> keepArgs, out string message)
    {
        var current = Environment.ProcessPath;
        if (current is null || !File.Exists(current)) { message = "não encontrei o .exe em uso"; return false; }
        var old = current + ".old";
        try
        {
            if (File.Exists(old)) File.Delete(old);
            File.Move(current, old);
            try
            {
                File.Move(downloadedExe, current);
            }
            catch
            {
                File.Move(old, current); // desfaz
                throw;
            }
            // Fora do job do app: senão o Windows encerraria a versão nova junto com esta.
            var args = new List<string> { "--wait-pid", Environment.ProcessId.ToString() };
            args.AddRange(keepArgs);
            if (!ProcessJob.StartOutsideJob(current, args, Path.GetDirectoryName(current)!))
            {
                File.Move(current, downloadedExe, overwrite: true); // desfaz
                File.Move(old, current);
                throw new InvalidOperationException("o Windows não deixou abrir a versão nova");
            }
            LoggingService.Log.Info("Versão nova instalada; reabrindo o app.");
            message = "ok";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            LoggingService.Log.Info($"Não deu para trocar o .exe: {ex.Message}");
            return false;
        }
    }

    /// <summary>Argumentos a repetir quando o app reabre depois de atualizar (ex.: --data-root).</summary>
    public static IReadOnlyList<string> RestartArgs { get; set; } = Array.Empty<string>();

    /// <summary>Na abertura pós-atualização: espera a versão antiga fechar (ela segura os perfis).</summary>
    public static void WaitForPreviousInstance(string? pid)
    {
        if (!int.TryParse(pid, out var id)) return;
        try
        {
            using var p = Process.GetProcessById(id);
            p.WaitForExit(30_000);
        }
        catch { } // já saiu
    }

    /// <summary>Apaga o .exe antigo deixado pela última atualização (na abertura seguinte).</summary>
    public static void CleanupOld()
    {
        try
        {
            var old = Environment.ProcessPath + ".old";
            if (File.Exists(old)) File.Delete(old);
        }
        catch { }
    }

    /// <summary>Dá para gravar na pasta do .exe? (Senão a atualização é pelo site.)</summary>
    public static bool CanWriteNextToExe(out string folder)
    {
        folder = Path.GetDirectoryName(Environment.ProcessPath ?? "") ?? "";
        try
        {
            var probe = Path.Combine(folder, $".ti-write-test-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }
}
