using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace ThoseIdles.Services;

/// <summary>
/// Os números da conta para o RESUMO de todas as contas, nos dois modos:
///  · modo econômico: vêm do cliente leve (<see cref="EconomyClient"/>), que já lê tudo do servidor;
///  · com a página do jogo aberta: um script na página repassa ao app só as mensagens "welcome",
///    "estado" e "batalha" que o servidor já manda para ela (nada é enviado ao servidor), e o app as lê
///    com a MESMA lógica do modo econômico.
/// </summary>
public sealed partial class BrowserSession
{
    private readonly EconomySessionStats _pageStats = new();
    private readonly AccountGameState _pageGame = new();
    private bool _resumoLigado;

    /// <summary>As estatísticas da sessão da conta (do cliente leve no modo econômico; senão, da página).</summary>
    public EconomySessionStats Stats => _eco?.Stats ?? _pageStats;

    /// <summary>O estado da conta (nível, moedas, hunt, time…).</summary>
    public AccountGameState Game => _eco?.Game ?? _pageGame;

    private void LigarTotalDoDia(EconomySessionStats stats) => stats.Somou += soma => DailyTotals.Instance.Somar(Slot, soma);

    private static readonly Lazy<string> ContaRelayScript = new(() => ReadResource("ContaRelay.js"));

    private async Task EnableResumoRelayAsync(CoreWebView2 core)
    {
        if (ContaRelayScript.Value is not { Length: > 0 } script) return;
        if (!_resumoLigado) { _resumoLigado = true; LigarTotalDoDia(_pageStats); }
        await core.AddScriptToExecuteOnDocumentCreatedAsync(script);
    }

    /// <summary>Uma mensagem do servidor repassada pela página ("welcome", "estado" ou "batalha").</summary>
    internal void FeedPageMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("t", out var tv)) return;
            switch (tv.GetString())
            {
                case "welcome":
                    _pageStats.LerWelcome(root);
                    _pageGame.Apply(root);
                    _ = SpeciesCatalog.EnsureLoadedAsync(); // para a nota das capturas
                    break;
                case "estado":
                    _pageStats.LerEstado(root);
                    _pageStats.LerBolas(root);
                    _pageGame.Apply(root);
                    break;
                case "batalha":
                    _pageStats.Somar(root);
                    _pageGame.Evento(root);
                    break;
            }
        }
        catch { }
    }
}
