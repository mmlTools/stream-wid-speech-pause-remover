using System.Runtime.InteropServices;
using System.Text;

namespace StreamWID.Services;

public static class ConsoleBridge
{
    private const int AttachParentProcess = -1;

    public static void AttachForCli()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return;

        if (Console.IsOutputRedirected || Console.IsErrorRedirected)
            return;

        AttachConsole(AttachParentProcess);
        ResetConsoleStreams();
    }

    private static void ResetConsoleStreams()
    {
        try
        {
            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            var output = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
            var error = new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true };
            Console.SetOut(output);
            Console.SetError(error);
        }
        catch
        {
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);
}
