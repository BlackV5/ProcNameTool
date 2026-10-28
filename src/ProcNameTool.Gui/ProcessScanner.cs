using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcNameTool.Gui;

/// <summary>进程列表中的一行。</summary>
internal sealed record ProcEntry(int Pid, string Name, string? Path, string Description)
{
    public bool Operable => Path != null;
}

internal static class ProcessScanner
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    private static readonly Dictionary<string, string> DescCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    /// <summary>
    /// 取进程的完整映像路径。
    /// 用 QueryFullProcessImageName + PROCESS_QUERY_LIMITED_INFORMATION，
    /// 比 Process.MainModule 权限要求低得多，普通用户可以读到绝大多数进程。
    /// </summary>
    public static string? GetImagePath(int pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;

        try
        {
            int cap = 1024;
            var sb = new StringBuilder(cap);
            if (!QueryFullProcessImageName(h, 0, sb, ref cap)) return null;

            string path = sb.ToString();
            // 只保留 .exe，跳过 conhost/系统伪进程等
            return path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? path : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>读文件版本信息里的 FileDescription，任务管理器“名称”列显示的就是它。</summary>
    public static string GetDescription(string? path)
    {
        if (string.IsNullOrEmpty(path)) return string.Empty;

        lock (CacheLock)
        {
            if (DescCache.TryGetValue(path, out var cached)) return cached;
        }

        string desc = string.Empty;
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            desc = fvi.FileDescription ?? string.Empty;
        }
        catch
        {
            // 权限不足或文件被占用，保持空
        }

        lock (CacheLock)
        {
            DescCache[path] = desc;
        }
        return desc;
    }

    public static void ForgetCached(string path)
    {
        lock (CacheLock)
        {
            DescCache.Remove(path);
        }
    }

    public static List<ProcEntry> Scan()
    {
        var list = new List<ProcEntry>();

        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                int pid;
                string name;
                try
                {
                    pid = p.Id;
                    name = p.ProcessName;
                }
                catch
                {
                    continue;   // 进程在枚举后已退出
                }

                string? path = null;
                try { path = GetImagePath(pid); }
                catch { }

                list.Add(new ProcEntry(pid, name, path, GetDescription(path)));
            }
        }

        return list
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Pid)
            .ToList();
    }
}
