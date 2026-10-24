using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace ThoseIdles.Services;

/// <summary>
/// Janelinhas de LOGIN. Sites como a Kick abrem o "Entrar com Google" numa janela pop-up que, no
/// fim, avisa a página de origem (window.opener) e se fecha. Mandar esse pop-up para o navegador
/// padrão quebra o login: ele acontece fora do perfil da conta e a página nunca recebe a resposta.
/// Por isso os pop-ups de login (Google, Apple, Kick, Twitch, Discord) abrem aqui, numa janela do
/// app, no MESMO perfil da conta — como no Chrome. Qualquer outro pop-up continua indo para o
/// navegador padrão.
/// </summary>
public sealed partial class BrowserSession
{
    private static readonly string[] LoginPopupHosts =
    {
        "accounts.google.com", "google.com",
        "appleid.apple.com",
        "kick.com",
        "id.twitch.tv",
        "discord.com",
    };

    /// <summary>Janelas de login abertas desta conta (o autoteste confere).</summary>
    public List<Window> LoginPopups { get; } = new();

    /// <summary>É um pop-up de login que deve abrir dentro do app (no perfil da conta)?</summary>
    public bool IsLoginPopupUrl(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        if (_policy.AllowLocalhostHttp && u.IsLoopback && u.AbsolutePath.StartsWith("/popup", StringComparison.Ordinal)) return true; // autoteste
        return IsLoginPage(url);
    }

    /// <summary>
    /// Página de login (Google, Apple, Kick, Twitch, Discord), em https. A Kick, por exemplo, leva a
    /// PRÓPRIA aba para o "Continuar com Google" (sem pop-up): a aba da live precisa poder ir até lá e voltar.
    /// </summary>
    public static bool IsLoginPage(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps) return false;
        var host = u.Host.ToLowerInvariant();
        return LoginPopupHosts.Any(h => host == h || host.EndsWith("." + h, StringComparison.Ordinal));
    }

    /// <summary>
    /// Abre o pop-up pedido pela página numa janela do app, no perfil desta conta, ligado à página de
    /// origem (o login consegue responder e fechar a janela sozinho).
    /// </summary>
    private async void OpenLoginPopup(CoreWebView2NewWindowRequestedEventArgs e)
    {
        var deferral = e.GetDeferral();
        Window? win = null;
        try
        {
            var host = Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) ? u.Host : "login";
            var wv = new WebView2 { DefaultBackgroundColor = System.Drawing.Color.White };
            win = new Window
            {
                Title = $"{Account.DisplayName} — entrar ({host})",
                Width = 520,
                Height = 720,
                Content = wv,
                ShowInTaskbar = true,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Owner = Application.Current?.MainWindow,
            };
            var janela = win;
            janela.Closed += (_, _) =>
            {
                LoginPopups.Remove(janela);
                try { wv.Dispose(); } catch { }
            };
            LoginPopups.Add(janela);
            janela.Show();

            var env = await _profiles.GetEnvironmentAsync();
            await wv.EnsureCoreWebView2Async(env, _profiles.CreateControllerOptions(env, Slot));
            var core = wv.CoreWebView2;
            core.Settings.AreDevToolsEnabled = _settings.DeveloperMode;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = false;
            // O login fecha a janela sozinho (window.close) quando termina.
            core.WindowCloseRequested += (_, _) => janela.Close();
            core.DocumentTitleChanged += (_, _) => { try { janela.Title = $"{Account.DisplayName} — {core.DocumentTitle}"; } catch { } };
            core.NavigationStarting += (_, ev) =>
            {
                // Só https (o autoteste usa a página local).
                if (Uri.TryCreate(ev.Uri, UriKind.Absolute, out var nu) && (nu.Scheme == Uri.UriSchemeHttps || (_policy.AllowLocalhostHttp && nu.IsLoopback) || ev.Uri == "about:blank")) return;
                ev.Cancel = true;
            };
            core.NewWindowRequested += (_, ev) =>
            {
                if (IsLoginPopupUrl(ev.Uri)) { OpenLoginPopup(ev); return; }
                ev.Handled = true;
                if (NavigationPolicy.IsSafeExternalUrl(ev.Uri)) OpenExternal(ev.Uri);
            };
            core.PermissionRequested += OnPermissionRequested;
            core.DownloadStarting += OnDownloadStarting;
            core.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;

            e.NewWindow = core; // a página de origem segue como "opener" desta janela
            e.Handled = true;
            LoggingService.Log.Info($"[conta{Slot}] Janela de login aberta no perfil da conta: {host}");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"[conta{Slot}] Janela de login falhou ({ex.Message}); abrindo no navegador padrão.");
            e.Handled = true;
            try { win?.Close(); } catch { }
            if (NavigationPolicy.IsSafeExternalUrl(e.Uri)) OpenExternal(e.Uri);
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>Fecha as janelas de login desta conta (a conta fechou ou foi limpa).</summary>
    private void CloseLoginPopups()
    {
        foreach (var w in LoginPopups.ToList())
        {
            try { w.Close(); } catch { }
        }
    }
}
