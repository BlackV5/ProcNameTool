using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace ProcNameTool;

/// <summary>
/// VS_VERSIONINFO 资源节点。
/// 结构：wLength(2) + wValueLength(2) + wType(2) + szKey(UTF-16Z) + Value + Children[]
/// 所有子节点的起始位置按 32 位（4 字节）边界对齐，父节点负责补齐子节点之后的空隙。
/// </summary>
internal sealed class VersionNode
{
    public ushort WValueLength;
    public ushort WType;                 // 1 = 文本(UTF-16)，0 = 二进制
    public string Key = string.Empty;
    public byte[] BinaryValue = Array.Empty<byte>();
    public string? TextValue;
    public readonly List<VersionNode> Children = new();

    public VersionNode? Find(string key)
    {
        foreach (var c in Children)
            if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
                return c;
        return null;
    }

    public IEnumerable<VersionNode> FindAll(string key)
    {
        foreach (var c in Children)
            if (string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase))
                yield return c;
    }

    /// <summary>深度优先查找第一个同名节点。</summary>
    public VersionNode? FindDeep(string key)
    {
        var hit = Find(key);
        if (hit != null) return hit;
        foreach (var c in Children)
        {
            hit = c.FindDeep(key);
            if (hit != null) return hit;
        }
        return null;
    }
}

internal static class VersionResource
{
    private const uint RT_VERSION = 16;
    private const uint LOAD_LIBRARY_AS_DATAFILE = 0x00000002;
    private const uint LOAD_LIBRARY_AS_IMAGE_RESOURCE = 0x00000020;

    #region Win32

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "LoadLibraryExW")]
    private static extern IntPtr LoadLibraryEx(string lpLibFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool FreeLibrary(IntPtr hModule);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "FindResourceW")]
    private static extern IntPtr FindResource(IntPtr hModule, IntPtr lpName, IntPtr lpType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LoadResource(IntPtr hModule, IntPtr hResInfo);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LockResource(IntPtr hResData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SizeofResource(IntPtr hModule, IntPtr hResInfo);

    private delegate bool EnumResLangProc(IntPtr hModule, IntPtr lpszType, IntPtr lpszName, ushort wIDLanguage, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "EnumResourceLanguagesW")]
    private static extern bool EnumResourceLanguages(IntPtr hModule, IntPtr lpType, IntPtr lpName, EnumResLangProc lpEnumFunc, IntPtr lParam);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "BeginUpdateResourceW")]
    private static extern IntPtr BeginUpdateResource(string pFileName, bool bDeleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateResource(IntPtr hUpdate, IntPtr lpType, IntPtr lpName, ushort wLanguage, byte[] lpData, uint cbData);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr hUpdate, bool fDiscard);

    #endregion

    #region 读取

    public static byte[] ReadRaw(string file, out ushort language)
        => ReadRaw(file, out language, out _);

    /// <summary>读取 exe/dll 中 RT_VERSION 资源的原始字节，同时返回其语言 ID 与语言条目数。</summary>
    public static byte[] ReadRaw(string file, out ushort language, out int languageCount)
    {
        string full = Path.GetFullPath(file);
        IntPtr hModule = LoadLibraryEx(full, IntPtr.Zero, LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
        if (hModule == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"无法加载文件作为资源容器：{full}");

        try
        {
            language = FirstLanguage(hModule, out languageCount);

            IntPtr hRes = FindResource(hModule, (IntPtr)1, (IntPtr)RT_VERSION);
            if (hRes == IntPtr.Zero)
                throw new InvalidOperationException("该文件不含版本信息资源（RT_VERSION/1），无法读取。");

            uint size = SizeofResource(hModule, hRes);
            IntPtr hData = LoadResource(hModule, hRes);
            if (hData == IntPtr.Zero || size == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "加载版本资源失败。");

            IntPtr p = LockResource(hData);
            if (p == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "锁定版本资源失败。");

            var buf = new byte[size];
            Marshal.Copy(p, buf, 0, (int)size);
            return buf;
        }
        finally
        {
            FreeLibrary(hModule);
        }
    }

    private static ushort FirstLanguage(IntPtr hModule, out int count)
    {
        ushort found = 0;
        int n = 0;

        EnumResourceLanguages(hModule, (IntPtr)RT_VERSION, (IntPtr)1,
            (m, t, name, lang, lp) =>
            {
                if (n == 0) found = lang;
                n++;
                return true;
            }, IntPtr.Zero);

        count = n;
        return found;
    }

    public static VersionNode Parse(byte[] buf)
    {
        if (buf.Length < 6) throw new InvalidDataException("版本资源数据过短。");
        return ParseNode(buf, 0, buf.Length);
    }

    private static VersionNode ParseNode(byte[] buf, int offset, int limit)
    {
        if (offset + 6 > limit) throw new InvalidDataException("版本资源结构越界。");

        ushort wLength = BitConverter.ToUInt16(buf, offset);
        ushort wValueLength = BitConverter.ToUInt16(buf, offset + 2);
        ushort wType = BitConverter.ToUInt16(buf, offset + 4);

        int end = offset + wLength;
        if (wLength == 0 || end > limit) end = limit;

        int pos = offset + 6;
        string key = ReadUtf16Z(buf, ref pos, end);

        var node = new VersionNode { WValueLength = wValueLength, WType = wType, Key = key };

        pos = Align4(pos);

        if (wValueLength > 0)
        {
            int valueBytes = wType == 1 ? wValueLength * 2 : wValueLength;
            if (pos + valueBytes <= end)
            {
                if (wType == 1)
                    node.TextValue = Encoding.Unicode.GetString(buf, pos, valueBytes).TrimEnd('\0');
                else
                {
                    node.BinaryValue = new byte[valueBytes];
                    Array.Copy(buf, pos, node.BinaryValue, 0, valueBytes);
                }
                pos += valueBytes;
            }
        }

        pos = Align4(pos);

        while (pos + 6 <= end)
        {
            ushort childLen = BitConverter.ToUInt16(buf, pos);
            if (childLen == 0) break;
            node.Children.Add(ParseNode(buf, pos, end));
            pos = Align4(pos + childLen);
        }

        return node;
    }

    private static string ReadUtf16Z(byte[] buf, ref int pos, int limit)
    {
        var sb = new StringBuilder();
        while (pos + 1 < limit)
        {
            char ch = (char)(buf[pos] | (buf[pos + 1] << 8));
            pos += 2;
            if (ch == '\0') return sb.ToString();
            sb.Append(ch);
        }
        return sb.ToString();
    }

    #endregion

    #region 序列化

    public static byte[] Serialize(VersionNode root)
    {
        using var ms = new MemoryStream();
        WriteNode(ms, root);
        return ms.ToArray();   // 长度必须等于根节点的 wLength，不能再补字节
    }

    private static void WriteNode(Stream s, VersionNode n)
    {
        using var body = new MemoryStream();

        // szKey + 结尾 0x0000
        byte[] keyBytes = Encoding.Unicode.GetBytes(n.Key);
        body.Write(keyBytes, 0, keyBytes.Length);
        body.WriteByte(0);
        body.WriteByte(0);

        // Value
        byte[] value;
        ushort valueLength;
        if (n.WType == 1 && n.TextValue != null)
        {
            value = Encoding.Unicode.GetBytes(n.TextValue + "\0");
            valueLength = (ushort)(n.TextValue.Length + 1);  // 文本型 wValueLength 单位是字符数（含结尾 0）
        }
        else if (n.BinaryValue.Length > 0)
        {
            value = n.BinaryValue;
            valueLength = (ushort)value.Length;              // 二进制型单位是字节数
        }
        else
        {
            value = Array.Empty<byte>();                     // 容器节点（StringFileInfo/StringTable）没有 Value
            valueLength = 0;
        }

        bool hasValue = value.Length > 0;
        bool hasChildren = n.Children.Count > 0;

        if (hasValue || hasChildren) Pad4(body);
        if (hasValue) body.Write(value, 0, value.Length);

        if (hasChildren)
        {
            if (hasValue) Pad4(body);
            for (int i = 0; i < n.Children.Count; i++)
            {
                WriteNode(body, n.Children[i]);
                if (i < n.Children.Count - 1) Pad4(body);   // padding 只为对齐后续兄弟节点，不计入本节点 wLength
            }
        }

        ushort totalLength = (ushort)(6 + body.Length);

        Span<byte> header = stackalloc byte[6];
        BitConverter.TryWriteBytes(header, totalLength);
        BitConverter.TryWriteBytes(header[2..], valueLength);
        BitConverter.TryWriteBytes(header[4..], n.WType);

        s.Write(header);
        body.Position = 0;
        body.CopyTo(s);
    }

    /// <summary>按“整个资源块”的绝对偏移补齐到 4 字节边界（必须算上 6 字节节点头）。</summary>
    private static void Pad4(Stream s)
    {
        while ((6 + s.Length) % 4 != 0) s.WriteByte(0);
    }

    private static int Align4(int v) => (v + 3) & ~3;

    #endregion

    #region 修改

    /// <summary>
    /// 修改版本信息中的字符串键，例如 FileDescription / ProductName / OriginalFilename。
    /// 返回实际变更的键值对（用于日志与 --dry-run）。
    /// </summary>
    public static List<(string Table, string Key, string Old, string New)> Patch(
        string file,
        IReadOnlyDictionary<string, string> updates,
        bool dryRun)
    {
        string full = Path.GetFullPath(file);
        byte[] raw = ReadRaw(full, out ushort language, out int languageCount);
        VersionNode root = Parse(raw);

        VersionNode stringFileInfo = root.Find("StringFileInfo")
            ?? throw new InvalidOperationException("版本资源中缺少 StringFileInfo 节点。");

        List<VersionNode> tables = stringFileInfo.Children.ToList();

        if (tables.Count == 0)
            throw new InvalidOperationException("版本资源中缺少 StringTable 节点。");

        var changes = new List<(string, string, string, string)>();
        bool dirty = false;

        foreach (var kv in updates)
        {
            bool applied = false;

            foreach (var table in tables)
            {
                var node = table.Find(kv.Key);
                if (node == null) continue;

                string old = node.TextValue ?? string.Empty;
                if (old != kv.Value)
                {
                    node.TextValue = kv.Value;
                    node.WType = 1;
                    node.WValueLength = (ushort)(kv.Value.Length + 1);
                    changes.Add((table.Key, kv.Key, old, kv.Value));
                    dirty = true;
                }
                applied = true;
            }

            if (!applied)
            {
                // 原文件没有这个键：加到第一个字符串表末尾
                VersionNode target = tables[0];
                target.Children.Add(new VersionNode
                {
                    Key = kv.Key,
                    WType = 1,
                    TextValue = kv.Value,
                    WValueLength = (ushort)(kv.Value.Length + 1)
                });
                changes.Add((target.Key, kv.Key, "", kv.Value));
                dirty = true;
            }
        }

        if (!dirty || dryRun) return changes;

        if (languageCount == 0)
            throw new InvalidOperationException("该文件未枚举到任何 RT_VERSION 语言，已中止写入。");

        byte[] blob = Serialize(root);
        WriteRaw(full, blob, language);
        return changes;
    }

    private static void WriteRaw(string file, byte[] blob, ushort language)
    {
        FileAttributes attrs = File.GetAttributes(file);
        DateTime lastWrite = File.GetLastWriteTimeUtc(file);
        if ((attrs & FileAttributes.ReadOnly) != 0)
            File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);

        try
        {
            IntPtr hUpdate = BeginUpdateResource(file, false);
            if (hUpdate == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "BeginUpdateResource 失败（文件被占用 / 无写权限 / 受系统保护）。");

            bool discarded = true;
            try
            {
                if (!UpdateResource(hUpdate, (IntPtr)RT_VERSION, (IntPtr)1, language, blob, (uint)blob.Length))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateResource 写入失败。");

                if (!EndUpdateResource(hUpdate, false))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "EndUpdateResource 提交失败。");

                discarded = false;
            }
            finally
            {
                if (discarded) EndUpdateResource(hUpdate, true);
            }
        }
        finally
        {
            File.SetAttributes(file, attrs);
            File.SetLastWriteTimeUtc(file, lastWrite);
        }

        PeChecksum.Update(file);
    }

    #endregion

    #region 便捷读取（给 info 命令用）

    public sealed record InfoItem(string Table, string Key, string Value);

    public static List<InfoItem> Describe(string file)
    {
        byte[] raw = ReadRaw(file, out _);
        VersionNode root = Parse(raw);

        var list = new List<InfoItem>();
        VersionNode? sfi = root.Find("StringFileInfo");
        if (sfi == null) return list;

        foreach (var table in sfi.Children)
        {
            foreach (var item in table.Children)
                list.Add(new InfoItem(table.Key, item.Key, item.TextValue ?? ""));
        }

        return list;
    }

    #endregion
}
