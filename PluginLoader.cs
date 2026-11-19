using System.IO;
using VstCore;

namespace Koromogae;

public static class PluginLoader
{
    internal static string HostPath(PluginBitness bitness) => Path.Combine(
        AppContext.BaseDirectory, bitness == PluginBitness.X86 ? "PluginHost32" : "PluginHost64", "VstPluginHost.exe");

    /// <summary>
    /// プラグインを、そのビット数に合ったプラグインホストプロセスにロードする。
    /// 32bit / 64bit を問わず、プラグイン 1 つにつき 1 プロセスなので、1 つがクラッシュしても、このアプリや他のプラグインは落ちない。
    /// </summary>
    public static IVstEffect Load(string path, int sampleRate)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new FileNotFoundException(Loc.T("Err.FileNotFound"), path);

        var bitness = PeInfo.GetBitness(path);
        if (bitness == PluginBitness.Unknown)
            throw new BadImageFormatException(Loc.T("Err.NotPluginDll"));

        return RemotePlugin.Start(HostPath(bitness), path, bitness, sampleRate);
    }
}
