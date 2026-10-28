namespace ProcNameTool;

/// <summary>
/// PE 可选头校验和重算。
/// 改资源后校验和会失效，虽然 Windows 加载普通 exe 不校验，但部分部署/签名工具会报“映像损坏”，
/// 因此这里顺手重算一次。
/// </summary>
internal static class PeChecksum
{
    public static void Update(string path)
    {
        byte[] buf = File.ReadAllBytes(path);
        if (buf.Length < 0x40) return;

        int peOffset = BitConverter.ToInt32(buf, 0x3C);
        if (peOffset <= 0 || peOffset + 0x40 > buf.Length) return;
        if (buf[peOffset] != (byte)'P' || buf[peOffset + 1] != (byte)'E') return;

        int optHeader = peOffset + 0x18;              // COFF 头 20 字节后的可选头
        if (optHeader + 0x40 > buf.Length) return;

        int checksumOffset = optHeader + 0x40;
        if (checksumOffset + 4 > buf.Length) return;

        BitConverter.GetBytes(0).CopyTo(buf, checksumOffset);

        ulong sum = 0;
        int len = buf.Length;

        for (int i = 0; i < len; i += 2)
        {
            uint word = buf[i];
            if (i + 1 < len) word |= (uint)buf[i + 1] << 8;
            sum += word;
            sum = (sum & 0xFFFF) + (sum >> 16);
        }

        sum = (sum & 0xFFFF) + (sum >> 16);
        sum += (ulong)len;

        BitConverter.GetBytes((uint)sum).CopyTo(buf, checksumOffset);
        File.WriteAllBytes(path, buf);
    }
}
