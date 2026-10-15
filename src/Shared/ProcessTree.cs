using System.Runtime.InteropServices;

namespace ThoseIdles.Shared;

public readonly record struct ProcessEntry(int Pid, int ParentPid, string ExeName);

/// <summary>Snapshot leve da tabela de processos (Toolhelp32), sem WMI.</summary>
public static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref PROCESSENTRY32W entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    public static List<ProcessEntry> Snapshot()
    {
        var list = new List<ProcessEntry>(256);
        var snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == INVALID_HANDLE_VALUE) return list;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return list;
            do
            {
                list.Add(new ProcessEntry((int)e.th32ProcessID, (int)e.th32ParentProcessID, e.szExeFile));
            } while (Process32NextW(snap, ref e));
        }
        finally
        {
            CloseHandle(snap);
        }
        return list;
    }

    /// <summary>O processo raiz e todos os descendentes (recursivo).</summary>
    public static List<ProcessEntry> Descendants(IReadOnlyList<ProcessEntry> all, int rootPid, bool includeRoot = true)
    {
        var byParent = all.ToLookup(p => p.ParentPid);
        var result = new List<ProcessEntry>();
        var seen = new HashSet<int>();
        var stack = new Stack<int>();
        var root = all.FirstOrDefault(p => p.Pid == rootPid);
        if (includeRoot && root.Pid != 0) result.Add(root);
        seen.Add(rootPid);
        stack.Push(rootPid);
        while (stack.Count > 0)
        {
            var pid = stack.Pop();
            foreach (var child in byParent[pid])
            {
                // PIDs são reciclados: um "filho" que já estava lá antes de um pai morto não deve
                // criar ciclos. O HashSet evita loops infinitos.
                if (child.Pid == 0 || !seen.Add(child.Pid)) continue;
                result.Add(child);
                stack.Push(child.Pid);
            }
        }
        return result;
    }
}
