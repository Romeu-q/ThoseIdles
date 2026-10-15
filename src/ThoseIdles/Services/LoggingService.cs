using System.Text;
using System.Text.RegularExpressions;

namespace ThoseIdles.Services;

/// <summary>
/// Log local em texto com rotação por tamanho (app.log → app.1.log … app.3.log).
/// Toda mensagem passa por <see cref="Sanitize"/>: query string e fragmento de URLs são removidos,
/// porque é ali que tokens de sessão/OAuth costumam aparecer (ex.: <c>?sessao=</c> no retorno do login).
/// Nunca registre cookies, cabeçalhos ou corpo de requisições.
/// </summary>
public sealed partial class LoggingService : IDisposable
{
    private const long MaxBytes = 1_000_000;
    private const int KeepFiles = 3;

    private static LoggingService? _instance;
    public static LoggingService Log => _instance ?? throw new InvalidOperationException("LoggingService não inicializado.");

    private readonly object _gate = new();
    private readonly string _dir;
    private StreamWriter? _writer;
    private bool _disposed;

    private LoggingService(string dir)
    {
        _dir = dir;
        Directory.CreateDirectory(dir);
    }

    public static void Initialize(string dir)
    {
        _instance?.Dispose();
        _instance = new LoggingService(dir);
    }

    public string CurrentFile => Path.Combine(_dir, "app.log");

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null) =>
        Write("ERRO", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {Sanitize(message)}";
        lock (_gate)
        {
            if (_disposed) return;
            try
            {
                _writer ??= new StreamWriter(new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), Encoding.UTF8) { AutoFlush = true };
                _writer.WriteLine(line);
                if (_writer.BaseStream.Length > MaxBytes) Rotate();
            }
            catch
            {
                // Log nunca pode derrubar o app.
            }
        }
#if DEBUG
        System.Diagnostics.Debug.WriteLine(line);
#endif
    }

    private void Rotate()
    {
        _writer?.Dispose();
        _writer = null;
        var oldest = Path.Combine(_dir, $"app.{KeepFiles}.log");
        if (File.Exists(oldest)) File.Delete(oldest);
        for (var i = KeepFiles - 1; i >= 1; i--)
        {
            var src = Path.Combine(_dir, $"app.{i}.log");
            if (File.Exists(src)) File.Move(src, Path.Combine(_dir, $"app.{i + 1}.log"));
        }
        File.Move(CurrentFile, Path.Combine(_dir, "app.1.log"));
    }

    [GeneratedRegex(@"\b((?:https?|wss?)://[^\s?#'""<>]+)[?#][^\s'""<>]*", RegexOptions.IgnoreCase)]
    private static partial Regex UrlWithQuery();

    /// <summary>Remove query/fragmento de qualquer URL contida no texto.</summary>
    public static string Sanitize(string text) => UrlWithQuery().Replace(text, "$1?…");

    /// <summary>Forma segura de exibir uma URL: só esquema, host e caminho.</summary>
    public static string SafeUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return "(vazio)";
        return Uri.TryCreate(url, UriKind.Absolute, out var u)
            ? $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{u.AbsolutePath}"
            : "(url inválida)";
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }
}
