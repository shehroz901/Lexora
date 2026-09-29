namespace Lexora;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--export-icon")
        {
            Theme.ExportIco(args[1]);
            return;
        }

        if (args.Contains("--selftest-diff"))
        {
            DiffSelfTest.Run();
            return;
        }

        using var mutex = new Mutex(true, "Lexora.SingleInstance", out bool isFirstInstance);
        if (!isFirstInstance) return;

        if (args.FirstOrDefault(a => a.StartsWith("--selftest-web=")) is { } web)
            TextWatcher.SelfTestAutomationId = web["--selftest-web=".Length..];

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(selfTest: args.Any(a => a.StartsWith("--selftest"))));
    }
}
