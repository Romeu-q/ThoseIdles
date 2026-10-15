using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ThoseIdles.Shared;

/// <summary>Memória de um processo, em bytes. PrivateWorkingSet = -1 quando o Windows não informa.</summary>
public readonly record struct ProcessMemory(
    int Pid,
    long WorkingSet,
    long PrivateBytes,
    long PrivateWorkingSet)
{
    public static readonly ProcessMemory Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Leitura de memória por processo via GetProcessMemoryInfo, sem PerformanceCounter (que é lento e
/// aloca bastante). Usado tanto pelo app quanto pelo MemBench, para que os dois cenários do benchmark
/// sejam medidos exatamente com o mesmo método.
///
/// Métricas:
///  - WorkingSet:        páginas físicas mapeadas (inclui DLLs compartilhadas; somar entre processos superestima).
///  - PrivateBytes:      commit privado (PrivateUsage). É a métrica mais honesta para "quanto este processo custa".
///  - PrivateWorkingSet: parte privada residente em RAM. É o que o Gerenciador de Tarefas mostra na coluna "Memória".
/// </summary>
public static class ProcessMemoryReader
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX2
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
        // Campos abaixo só existem em PROCESS_MEMORY_COUNTERS_EX2 (Windows 10 21H2+ e 11).
        public nuint PrivateWorkingSetSize;
        public ulong SharedCommitUsage;
    }

    private static readonly int SizeEx2 = Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX2>();
    private static readonly int SizeEx = SizeEx2 - IntPtr.Size - sizeof(ulong);
    private static bool _ex2Supported = true;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
    private static extern bool GetProcessMemoryInfo(IntPtr process, ref PROCESS_MEMORY_COUNTERS_EX2 counters, uint cb);

    public static bool TryRead(int pid, out ProcessMemory memory)
    {
        memory = ProcessMemory.Empty;
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            // Algumas versões antigas exigem PROCESS_VM_READ para GetProcessMemoryInfo.
            handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
            if (handle == IntPtr.Zero) return false;
        }

        try
        {
            var c = new PROCESS_MEMORY_COUNTERS_EX2();
            if (_ex2Supported)
            {
                c.cb = (uint)SizeEx2;
                if (GetProcessMemoryInfo(handle, ref c, c.cb))
                {
                    memory = new ProcessMemory(pid, (long)c.WorkingSetSize, (long)c.PrivateUsage, (long)c.PrivateWorkingSetSize);
                    return true;
                }
                _ex2Supported = false;
            }

            c = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)SizeEx };
            if (!GetProcessMemoryInfo(handle, ref c, c.cb)) return false;
            memory = new ProcessMemory(pid, (long)c.WorkingSetSize, (long)c.PrivateUsage, -1);
            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    // ---------------------------------------------------------------- memória do sistema

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>RAM física total, disponível e commit total do sistema (bytes).</summary>
    public static (long TotalPhys, long AvailPhys, long CommitUsed) ReadSystem()
    {
        var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref s)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return ((long)s.ullTotalPhys, (long)s.ullAvailPhys, (long)(s.ullTotalPageFile - s.ullAvailPageFile));
    }
}
