using VstCore;

namespace VstPluginHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) return 1;
        try
        {
            return PluginHostServer.Run(args[0]);
        }
        catch
        {
            return 2;
        }
    }
}
