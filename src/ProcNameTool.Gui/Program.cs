namespace ProcNameTool.Gui;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 无界面自检模式：--selftest <结果文件>
        if (args.Length >= 2 && args[0] == "--selftest")
        {
            SelfTest.Run(args[1]);
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}
