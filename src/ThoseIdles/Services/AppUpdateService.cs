using System.Net.Http;
using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// Confere se há uma Release mais nova no GitHub. Só AVISA: não baixa nem troca o exe.
/// A consulta é pública (sem login) e não envia nenhum dado da conta.
/// </summary>
public static class AppUpdateService
{
    /// <summary>API da última Release (o autoteste aponta para o servidor local).</summary>
    public static string LatestReleaseApi { get; set; } = "https://api.github.com/repos/Romeu-q/ThoseIdles/releases/latest";

    /// <summary>Página onde ficam as Releases (só links desse endereço são abertos).</summary>
    public const string ReleasesPage = "https://github.com/Romeu-q/ThoseIdles/releases";

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(AppUpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(v.Build, 0));
        }
    }

    public sealed record Release(Version Version, string Tag, string Url);

    /// <summary>A Release mais nova que a versão em uso, ou null (sem novidade ou sem internet).</summary>
    public static async Task<Release?> CheckAsync(CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ThoseIdles/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi, ct));
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var d) && d.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var p) && p.GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
            latest = new Version(latest.Major, latest.Minor, Math.Max(latest.Build, 0));
            if (latest <= CurrentVersion) return null;
            var url = root.TryGetProperty("html_url", out var h) ? h.GetString() : null;
            // Abre só páginas do repositório; qualquer outra coisa cai na lista de Releases.
            if (url is null || !url.StartsWith(ReleasesPage + "/", StringComparison.OrdinalIgnoreCase)) url = ReleasesPage;
            return new Release(latest, tag, url);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            LoggingService.Log.Info($"Verificação de atualização do app falhou: {ex.Message}");
            return null;
        }
    }
}
