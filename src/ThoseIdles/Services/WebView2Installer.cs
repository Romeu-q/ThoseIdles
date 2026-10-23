using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ThoseIdles.Services;

/// <summary>
/// Instala o Microsoft Edge WebView2 Runtime em PCs que não o têm (Windows 10 antigo, sistemas
/// "enxutos"). Usa o instalador oficial "Evergreen Bootstrapper" da Microsoft (~2 MB), que baixa o
/// resto e se mantém atualizado sozinho:
///  1. baixa do link oficial da Microsoft (go.microsoft.com);
///  2. confere a assinatura digital (Windows Authenticode) e que o assinante é a Microsoft;
///  3. roda em silêncio ("/silent /install"); sem permissão de administrador, instala só para o usuário.
/// Se qualquer passo falhar, nada é executado e o app mostra o link para instalar à mão.
/// </summary>
public static class WebView2Installer
{
    public const string BootstrapperUrl = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    public const string ManualPage = "https://developer.microsoft.com/microsoft-edge/webview2/";

    public static async Task<(bool Ok, string Message)> InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var file = Path.Combine(Path.GetTempPath(), $"ThoseIdles-WebView2Setup-{Environment.ProcessId}.exe");
        try
        {
            progress?.Report("Baixando o instalador da Microsoft…");
            using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("ThoseIdles");
                var bytes = await http.GetByteArrayAsync(BootstrapperUrl, ct);
                await File.WriteAllBytesAsync(file, bytes, ct);
            }

            progress?.Report("Conferindo a assinatura da Microsoft…");
            if (!IsSignedByMicrosoft(file, out var why))
                return (false, $"o instalador baixado não passou na conferência de assinatura ({why})");

            progress?.Report("Instalando o WebView2 (pode levar alguns minutos)…");
            using var p = Process.Start(new ProcessStartInfo(file, "/silent /install") { UseShellExecute = false, CreateNoWindow = true })
                ?? throw new InvalidOperationException("não foi possível iniciar o instalador");
            await p.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromMinutes(15), ct);
            LoggingService.Log.Info($"Instalador do WebView2 terminou (código {p.ExitCode}).");

            var version = ProfileManager.GetRuntimeVersion();
            return version is not null
                ? (true, $"WebView2 {version} instalado")
                : (false, $"o instalador terminou (código {p.ExitCode}), mas o WebView2 ainda não aparece");
        }
        catch (Exception ex)
        {
            LoggingService.Log.Warn($"Instalação do WebView2 falhou: {ex.Message}");
            return (false, ex.Message);
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }

    /// <summary>Assinatura Authenticode válida (WinVerifyTrust) E assinante "Microsoft Corporation".</summary>
    public static bool IsSignedByMicrosoft(string path, out string why)
    {
        if (!VerifyTrust(path, out var code)) { why = $"assinatura inválida (0x{code:X8})"; return false; }
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            if (!cert.Subject.Contains("O=Microsoft Corporation", StringComparison.Ordinal))
            {
                why = $"assinado por outra empresa ({cert.Subject})";
                return false;
            }
            why = "ok";
            return true;
        }
        catch (Exception ex) { why = ex.Message; return false; }
    }

    // ------------------------------------------------------------------ WinVerifyTrust

    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;          // 2 = WTD_UI_NONE
        public uint fdwRevocationChecks; // 0 = WTD_REVOKE_NONE
        public uint dwUnionChoice;       // 1 = WTD_CHOICE_FILE
        public IntPtr pFile;
        public uint dwStateAction;       // 0 = WTD_STATEACTION_IGNORE
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    private static bool VerifyTrust(string path, out int code)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        var pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x00000040, // WTD_SAFER_FLAG
            };
            code = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            return code == 0;
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFile);
            Marshal.FreeHGlobal(pFile);
        }
    }
}
