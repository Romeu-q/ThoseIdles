using System.Text.Json;

namespace ThoseIdles.Services;

/// <summary>
/// O bloco "twitch" que o servidor do jogo manda no estado da conta:
///  · login       — a conta da Twitch vinculada (sem vínculo, não há bônus a buscar);
///  · lives       — as lives OFICIAIS no ar agora;
///  · oficiais    — todos os canais oficiais, com bonus = true quando o vigia consegue ler o chat;
///  · assistindoEm — onde o vigia já achou o jogador.
/// </summary>
public sealed record TwitchInfo(string? Login, IReadOnlySet<string> NoAr, IReadOnlySet<string> Oficiais, IReadOnlySet<string> ComBonus, IReadOnlySet<string> AchadoEm)
{
    public static TwitchInfo? From(JsonElement tw)
    {
        if (tw.ValueKind != JsonValueKind.Object) return null;
        static string? Login(JsonElement x) => x.ValueKind switch
        {
            JsonValueKind.String => x.GetString(),
            JsonValueKind.Object when x.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String => l.GetString(),
            _ => null,
        };
        HashSet<string> Lista(string nome, Func<JsonElement, bool>? filtro = null)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tw.TryGetProperty(nome, out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var x in a.EnumerateArray())
                    if ((filtro is null || filtro(x)) && Login(x) is { Length: > 0 } l) s.Add(l.ToLowerInvariant());
            return s;
        }
        var login = tw.TryGetProperty("login", out var lg) && lg.ValueKind == JsonValueKind.String ? lg.GetString() : null;
        return new TwitchInfo(
            login,
            Lista("lives"),
            Lista("oficiais"),
            Lista("oficiais", x => x.ValueKind == JsonValueKind.Object && x.TryGetProperty("bonus", out var b) && b.ValueKind == JsonValueKind.True),
            Lista("assistindoEm"));
    }

    /// <summary>As lives que vale ter abertas: oficiais no ar com bônus (se o servidor não disse o bônus, todas no ar).</summary>
    public IEnumerable<string> QueValemAbrir() => NoAr.Where(c => ComBonus.Count == 0 || ComBonus.Contains(c));

    public string Assinatura => string.Join(",", NoAr.OrderBy(x => x)) + "|" + string.Join(",", ComBonus.OrderBy(x => x));
}
