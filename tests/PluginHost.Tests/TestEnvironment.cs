using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using System.Reflection;
using VstCore;

namespace PluginHost.Tests;

internal static class TestEnvironment
{
    /// <summary>本体のビルドが出力したプラグインホスト (ビット数別)。場所は csproj の AssemblyMetadata 経由で受け取る。</summary>
    public static string HostExe(PluginBitness bitness)
    {
        string dir = typeof(TestEnvironment).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .First(a => a.Key == "HostDir").Value!;
        string exe = Path.Combine(dir, bitness == PluginBitness.X86 ? "PluginHost32" : "PluginHost64", "VstPluginHost.exe");
        return File.Exists(exe)
            ? exe
            : throw new FileNotFoundException($"プラグインホストが見つかりません。先に `dotnet build Koromogae.csproj` を実行してください: {exe}");
    }
    /// <summary>テスト用の偽プラグインホスト (FakeHost プロジェクトの出力)。</summary>
    public static string FakeHostExe => Path.Combine(AppContext.BaseDirectory, "FakeHost.exe");

    // 実プラグインを使うテストは、手元にあるものだけで実行する (無ければスキップ)。
    public static readonly string? Vst2x86 = FirstExisting(
        @"C:\Program Files (x86)\Steinberg\VSTPlugins\BitShiftGain32.dll");

    public static readonly string? Vst2x86WithPresets = FirstExisting(
        @"C:\Program Files (x86)\Yamaha\Vstplugins\YAMAHA\Final Master.dll");

    public static readonly string? Vst3x86 = FirstExisting(
        @"C:\Program Files (x86)\Common Files\VST3\ConvologyXT.vst3");

    public static readonly string? Vst2x64 = FirstExisting(
        @"C:\Program Files\Steinberg\VSTPlugins\airwindows\BitShiftGain64.dll");

    public static readonly string? Vst3x64 = FirstExisting(
        @"C:\Program Files\Common Files\VST3\ConvologyXT_64.vst3");

    static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
}

/// <summary>本物のプラグインホストを起動し、RemotePlugin を介さず生のパイプで話すためのセッション (不正なデータを送るテスト用)。</summary>
internal sealed class RawHostSession : IDisposable
{
    // HostProtocol.MemorySize と同じ: ヘッダ 64B + float 4 チャンネル x 4096 サンプル
    const long MemorySize = 64 + 4L * 4096 * sizeof(float);

    readonly MemoryMappedFile _mmf;
    readonly EventWaitHandle _req, _ack;
    readonly Process _process;
    NamedPipeClientStream _pipe;

    public BinaryWriter Writer { get; }
    public BinaryReader Reader { get; }

    public RawHostSession(PluginBitness hostBitness = PluginBitness.X86)
    {
        string id = Guid.NewGuid().ToString("N");
        _mmf = MemoryMappedFile.CreateNew($"VstHost_{id}_mem", MemorySize);
        _req = new EventWaitHandle(false, EventResetMode.AutoReset, $"VstHost_{id}_req");
        _ack = new EventWaitHandle(false, EventResetMode.AutoReset, $"VstHost_{id}_ack");
        var startInfo = new ProcessStartInfo(TestEnvironment.HostExe(hostBitness), id) { UseShellExecute = false, CreateNoWindow = true };
        // `dotnet test` が渡す x64 用の DOTNET_ROOT を 32bit ホストが拾うと起動できない (本体の RemotePlugin と同じ対策)
        if (hostBitness == PluginBitness.X86)
        {
            startInfo.Environment.Remove("DOTNET_ROOT");
            startInfo.Environment.Remove("DOTNET_ROOT_X64");
        }
        _process = Process.Start(startInfo)!;

        _pipe = new NamedPipeClientStream(".", $"VstHost_{id}_pipe", PipeDirection.InOut);
        _pipe.Connect(15000);
        Writer = new BinaryWriter(_pipe);
        Reader = new BinaryReader(_pipe);
    }

    /// <summary>送信する。プラグインホストが先に切断した場合の IOException は無視する (それが期待される結果のため)。</summary>
    public void Send(Action<BinaryWriter> send)
    {
        try
        {
            send(Writer);
            Writer.Flush();
        }
        catch (IOException)
        {
        }
    }

    public void ClosePipe() => _pipe.Dispose();

    /// <summary>プラグインをロードする (成功時 true)。</summary>
    public bool Load(string path, int sampleRate = 48000)
    {
        Writer.Write(1);
        Writer.Write(path);
        Writer.Write(sampleRate);
        Writer.Flush();
        bool ok = Reader.ReadBoolean();
        if (ok) { Reader.ReadString(); Reader.ReadBoolean(); }
        else Reader.ReadString();
        return ok;
    }

    public long PeakWorkingSet { get; private set; }

    /// <summary>プロセスの終了を待ち、終了コードを返す。時間内に終了しなければ kill して -1。</summary>
    public int WaitForExit(int timeoutMs = 15000)
    {
        var sw = Stopwatch.StartNew();
        while (!_process.HasExited && sw.ElapsedMilliseconds < timeoutMs)
        {
            try
            {
                _process.Refresh();
                PeakWorkingSet = Math.Max(PeakWorkingSet, _process.PeakWorkingSet64);
            }
            catch (InvalidOperationException)
            {
                // 直前に終了した
            }
            Thread.Sleep(20);
        }

        if (!_process.HasExited)
        {
            _process.Kill(true);
            return -1;
        }
        return _process.ExitCode;
    }

    public void Dispose()
    {
        try { if (!_process.HasExited) _process.Kill(true); } catch { }
        _pipe.Dispose();
        _mmf.Dispose();
        _req.Dispose();
        _ack.Dispose();
        _process.Dispose();
    }
}
