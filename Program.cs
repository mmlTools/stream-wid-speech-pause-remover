using Avalonia;
using System;
using StreamWID.Services;

namespace StreamWID;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (CliRunner.ShouldRunCli(args))
        {
            ConsoleBridge.AttachForCli();
            return CliRunner.RunAsync(args, Console.Out, Console.Error).GetAwaiter().GetResult();
        }

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
