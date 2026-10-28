using System.Diagnostics;
using System.Text;

namespace ProcNameTool.Gui;

/// <summary>
/// 无界面自检：走一遍和大按钮完全相同的流程（复制 → 改写版本信息 → 回读校验）。
/// 用于交付前验证，或部署到目标机后确认环境可用：
///   进程名修改工具.exe --selftest out.txt
/// </summary>
internal static class SelfTest
{
    public static void Run(string outFile)
    {
        var sb = new StringBuilder();
        string tempDir = Path.Combine(Path.GetTempPath(), "ProcNameToolSelfTest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            string self = Environment.ProcessPath ?? throw new InvalidOperationException("拿不到自身路径");
            string src = Path.Combine(tempDir, Path.GetFileNameWithoutExtension(self) + ".exe");
            File.Copy(self, src);

            var before = FileVersionInfo.GetVersionInfo(src);
            sb.AppendLine($"self            = {self}");
            sb.AppendLine($"copy            = {src}");
            sb.AppendLine($"before.desc     = {before.FileDescription}");
            sb.AppendLine($"before.version  = {before.FileVersion}");

            string dest = Path.Combine(tempDir, "GuiSelfTestProbe.exe");
            File.Copy(src, dest, overwrite: false);
            sb.AppendLine($"renamed copy    = {dest}");

            const string desc = "GUI 自检目标 A1";
            var changes = VersionResource.Patch(
                dest,
                new Dictionary<string, string> { ["FileDescription"] = desc },
                dryRun: false);

            foreach (var c in changes)
                sb.AppendLine($"change          = [{c.Table}] {c.Key}: \"{c.Old}\" -> \"{c.New}\"");

            var after = FileVersionInfo.GetVersionInfo(dest);
            sb.AppendLine($"after.desc      = {after.FileDescription}");
            sb.AppendLine($"after.version   = {after.FileVersion}");
            sb.AppendLine(after.FileDescription == desc ? "RESULT: OK" : $"RESULT: FAIL (期望 {desc})");
        }
        catch (Exception ex)
        {
            sb.AppendLine("RESULT: EXCEPTION");
            sb.AppendLine(ex.ToString());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }

        try { File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8); } catch { }
    }
}
