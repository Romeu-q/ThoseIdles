namespace ThoseIdles;

/// <summary>Todos os caminhos em disco do aplicativo. Nada fica ao lado do .exe.</summary>
public static class AppPaths
{
    public static string Root { get; private set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ThoseIdles");

    /// <summary>Usado pelo autoteste para não tocar nos perfis reais.</summary>
    public static void UseRoot(string root) => Root = Path.GetFullPath(root);

    /// <summary>
    /// O app se chamava "PokeIdle Multi" e guardava tudo em %LOCALAPPDATA%\PokeIdleMulti. Na primeira
    /// abertura com o nome novo, a pasta antiga é movida para cá, com os logins, configurações e a
    /// extensão. Se não der para mover (ex.: a versão antiga ainda aberta), segue usando a antiga.
    /// Retorna uma mensagem para o log, ou null quando não havia nada a migrar.
    /// </summary>
    public static string? MigrateLegacyRoot()
    {
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PokeIdleMulti");
        if (!Directory.Exists(legacy) || Directory.Exists(Root)) return null;
        try
        {
            Directory.Move(legacy, Root);
            return $"Pasta de dados migrada de {legacy} para {Root} (nome antigo do app).";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Root = legacy;
            return $"Não foi possível mover {legacy} ({ex.Message}); usando a pasta antiga nesta execução.";
        }
    }

    /// <summary>
    /// Pasta de dados do WebView2 (uma só). Os quatro perfis ficam dentro dela, em
    /// <c>EBWebView\WV2Profile_conta1 … _conta4</c>, cada um com cookies/Local Storage/IndexedDB próprios.
    /// </summary>
    public static string UserDataFolder => Path.Combine(Root, "WebView2");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
    public static string LogsFolder => Path.Combine(Root, "logs");
    public static string BenchmarkFolder => Path.Combine(Root, "benchmark");

    /// <summary>Extensão /hunt baixada da loja, uma subpasta por versão.</summary>
    public static string HuntExtensionFolder => Path.Combine(Root, "extensions", "hunt");
}
