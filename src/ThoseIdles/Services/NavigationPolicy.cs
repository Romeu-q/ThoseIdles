namespace ThoseIdles.Services;

public enum NavigationDecision
{
    Allow,
    /// <summary>Cancelar dentro do app e abrir no navegador padrão (link externo clicado pelo jogador).</summary>
    OpenExternally,
    Block,
}

/// <summary>
/// Validação das navegações do frame principal. iframes (Turnstile, analytics) não passam por aqui;
/// eles seguem o CSP do próprio site.
/// </summary>
public sealed class NavigationPolicy
{
    private readonly Func<IReadOnlyList<string>> _allowedHosts;

    /// <summary>Somente para o autoteste: aceita http://localhost.</summary>
    public bool AllowLocalhostHttp { get; init; }

    public NavigationPolicy(Func<IReadOnlyList<string>> allowedHosts) => _allowedHosts = allowedHosts;

    public NavigationDecision Evaluate(string url, bool isRedirected, bool isUserInitiated)
    {
        if (url is "about:blank") return NavigationDecision.Allow;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return NavigationDecision.Block;

        var isHttps = uri.Scheme == Uri.UriSchemeHttps;
        var isLocalTest = AllowLocalhostHttp && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback;
        if (!isHttps && !isLocalTest)
            return NavigationDecision.Block;

        if (isLocalTest || IsHostAllowed(uri.Host))
            return NavigationDecision.Allow;

        // Redirecionamentos de servidor (ex.: OAuth que passa por um host intermediário) fazem parte
        // de um fluxo iniciado num host permitido. O primeiro salto, porém, precisa ter passado aqui.
        if (isRedirected)
            return NavigationDecision.Allow;

        return isUserInitiated ? NavigationDecision.OpenExternally : NavigationDecision.Block;
    }

    public bool IsHostAllowed(string host)
    {
        host = host.ToLowerInvariant();
        foreach (var rule in _allowedHosts())
        {
            if (rule.StartsWith("*.", StringComparison.Ordinal))
            {
                var suffix = rule[1..]; // ".dominio"
                if (host.EndsWith(suffix, StringComparison.Ordinal) || host == rule[2..]) return true;
            }
            else if (host == rule)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Só http/https vão para o navegador padrão. Nenhum outro esquema é executado.</summary>
    public static bool IsSafeExternalUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);
}
