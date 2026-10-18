using System.Runtime.InteropServices;

namespace ThoseIdles.Services;

/// <summary>
/// Coloca o próprio processo num Job Object com KILL_ON_JOB_CLOSE. Os processos do WebView2 nascem
/// dentro do job; se o app morrer de forma abrupta (crash, "Finalizar tarefa"), o Windows encerra
/// todos eles — nada de msedgewebview2.exe órfão segurando memória.
/// BREAKAWAY_OK é mantido para não impedir o sandbox do Chromium de criar seus próprios jobs.
/// </summary>
public static class ProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x0800;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateProcess(string? app, System.Text.StringBuilder cmdLine, IntPtr procAttr, IntPtr threadAttr,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    private const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000;

    /// <summary>
    /// Inicia um processo FORA do job (senão ele morreria junto com este app). Usado para reabrir o
    /// app depois de uma atualização. Se o Windows recusar o breakaway, tenta sem ele.
    /// </summary>
    public static bool StartOutsideJob(string exe, IEnumerable<string> args, string workingDir)
    {
        var cmd = new System.Text.StringBuilder(Quote(exe));
        foreach (var a in args) cmd.Append(' ').Append(Quote(a));
        foreach (var flags in new[] { CREATE_BREAKAWAY_FROM_JOB, 0u })
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            if (CreateProcess(exe, new System.Text.StringBuilder(cmd.ToString()), IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, workingDir, ref si, out var pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                if (flags == 0) LoggingService.Log.Info("Aviso: a versão nova foi aberta dentro do job (o Windows recusou sair dele).");
                return true;
            }
        }
        return false;

        static string Quote(string s) => s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? s : "\"" + s.Replace("\"", "\\\"") + "\"";
    }

    // Mantido vivo até o fim do processo: fechar o handle dispararia o KILL_ON_JOB_CLOSE.
    private static IntPtr _job;

    public static bool TryAttachCurrentProcess()
    {
        try
        {
            _job = CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return false;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
            {
                BasicLimitInformation = { LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK },
            };
            if (!SetInformationJobObject(_job, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                return false;
            return AssignProcessToJobObject(_job, GetCurrentProcess());
        }
        catch
        {
            return false;
        }
    }
}
