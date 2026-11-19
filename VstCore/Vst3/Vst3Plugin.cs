using System.Collections.Concurrent;
using System.ComponentModel;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using VstCore.Vst3;

namespace VstCore;

/// <summary>
/// VST3 プラグインを同一プロセス内にロードして駆動するホスト実装 (オーディオエフェクト用)。
/// 非オーディオの操作 (ロード・エディタ・状態・破棄) は専用スレッドに集約し、process だけをオーディオスレッドから呼ぶ。
/// </summary>
public sealed unsafe partial class Vst3Plugin : ILocalPlugin, IEditorOwner
{
    const int MaxBlock = PluginLimits.MaxBlock;

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessSetup
    {
        public int processMode;
        public int symbolicSampleSize;
        public int maxSamplesPerBlock;
        public double sampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct AudioBusBuffers
    {
        public int numChannels;
        public ulong silenceFlags;
        public float** channelBuffers32;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessData
    {
        public int processMode, symbolicSampleSize, numSamples, numInputs, numOutputs;
        public AudioBusBuffers* inputs;
        public AudioBusBuffers* outputs;
        public IntPtr inputParameterChanges, outputParameterChanges, inputEvents, outputEvents, processContext;
    }

    readonly string _path;
    readonly MessageThread _thread;
    readonly ConcurrentQueue<(uint Id, double Value)> _pendingEdits = new();
    readonly HostApplication _hostApp = new();
    readonly ParamChanges _inChanges = new();
    readonly ParamChanges _outChanges = new();
    readonly EventList _events = new();
    ComponentHandler? _handler;
    PlugFrame? _frame;

    IntPtr _module, _factory;
    IntPtr _component, _processor, _controller;
    IntPtr _componentConn, _controllerConn;
    bool _controllerIsComponent;
    bool _initialized;

    int _inBusCount, _outBusCount, _inCh, _outCh, _inAlloc, _outAlloc;
    float** _inBufs, _outBufs;
    AudioBusBuffers* _inBus, _outBus;
    byte* _context;
    long _samplePos;
    int _sampleRate = 48000;

    volatile bool _processing;
    bool _disposed;

    public string Name { get; private set; } = "";
    public string Path => _path;
    public PluginBitness Bitness => PeInfo.Current;
    public bool HasEditor => _controller != IntPtr.Zero;
    public bool IsFaulted => false;

    /// <summary>IAudioProcessor::getLatencySamples。プラグインのスレッドで呼ぶ。</summary>
    public int LatencySamples
    {
        get
        {
            if (_disposed || _processor == IntPtr.Zero) return 0;
            try
            {
                return _thread.Invoke(() => (int)Math.Min(
                    ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(_processor, 6))(_processor), int.MaxValue));
            }
            catch
            {
                return 0;
            }
        }
    }

    Vst3Plugin(string path, MessageThread thread)
    {
        _path = path;
        _thread = thread;
        _hostApp.Owner = path;
    }

    public static Vst3Plugin Load(string path, int sampleRate)
    {
        var thread = new MessageThread("VST3: " + System.IO.Path.GetFileName(path.TrimEnd('\\', '/')));
        var plugin = new Vst3Plugin(path, thread);
        try
        {
            thread.Invoke(() => plugin.LoadOnThread(sampleRate), 30000);
            return plugin;
        }
        catch
        {
            try { thread.Invoke(plugin.TeardownOnThread, 5000); } catch { }
            thread.Stop();
            throw;
        }
    }

    // ================= COM 呼び出しヘルパー =================

    static IntPtr Fn(IntPtr obj, int slot) => ((IntPtr*)*(IntPtr*)obj)[slot];

    static int QueryInterface(IntPtr obj, Guid iid, out IntPtr result)
    {
        IntPtr r = IntPtr.Zero;
        int rc = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Fn(obj, 0))(obj, &iid, &r);
        result = rc == 0 ? r : IntPtr.Zero;
        return rc;
    }

    static void Release(ref IntPtr obj)
    {
        if (obj == IntPtr.Zero) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Fn(obj, 2))(obj);
        obj = IntPtr.Zero;
    }

    static int Initialize(IntPtr obj, IntPtr context)
        => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(obj, 3))(obj, context);

    static int Terminate(IntPtr obj)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(obj, 4))(obj);

    static string AsciiAt(byte* p, int max)
    {
        int len = 0;
        while (len < max && p[len] != 0) len++;
        return Encoding.ASCII.GetString(p, len);
    }

    // ================= ロード =================

    void LoadOnThread(int sampleRate)
    {
        _sampleRate = sampleRate;

        string? binary = PluginPaths.ResolveBinary(_path, PeInfo.Current);
        if (binary == null)
            throw new BadImageFormatException(CoreLoc.F("Core.Vst3.NoBinary", PeInfo.Current));

        _module = Win32.LoadLibraryExW(binary, IntPtr.Zero, 0x00000008 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
        if (_module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), CoreLoc.F("Core.Vst.DllLoadFailed", binary));

        IntPtr initDll = Win32.GetProcAddress(_module, "InitDll");
        if (initDll != IntPtr.Zero) ((delegate* unmanaged[Cdecl]<byte>)initDll)();

        IntPtr getFactory = Win32.GetProcAddress(_module, "GetPluginFactory");
        if (getFactory == IntPtr.Zero)
            throw new InvalidOperationException(CoreLoc.T("Core.Vst3.NoEntry"));
        _factory = ((delegate* unmanaged[Cdecl]<IntPtr>)getFactory)();
        if (_factory == IntPtr.Zero)
            throw new InvalidOperationException(CoreLoc.T("Core.Vst3.NoFactory"));

        CreateComponent();
        _initialized = true;
        SetupController();
        SetupBuses();
        AllocateBuffers();
        Activate();
    }

    void CreateComponent()
    {
        int count = ((delegate* unmanaged[Stdcall]<IntPtr, int>)Fn(_factory, 4))(_factory);
        byte* info = stackalloc byte[116]; // PClassInfo: cid[16] cardinality[4] category[32] name[64]
        for (int i = 0; i < count; i++)
        {
            new Span<byte>(info, 116).Clear();
            if (((delegate* unmanaged[Stdcall]<IntPtr, int, byte*, int>)Fn(_factory, 5))(_factory, i, info) != 0) continue;
            if (AsciiAt(info + 20, 32) != "Audio Module Class") continue;

            Guid cid = *(Guid*)info;
            Name = AsciiAt(info + 52, 64);

            Guid iid = Tuids.IComponent;
            IntPtr obj = IntPtr.Zero;
            int rc = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Fn(_factory, 6))(_factory, &cid, &iid, &obj);
            if (rc != 0 || obj == IntPtr.Zero)
                throw new InvalidOperationException(CoreLoc.F("Core.Vst3.CreateFailed", rc));

            _component = obj;
            if (string.IsNullOrWhiteSpace(Name))
                Name = System.IO.Path.GetFileNameWithoutExtension(_path.TrimEnd('\\', '/'));

            int init = Initialize(_component, _hostApp.Primary);
            if (init != 0) throw new InvalidOperationException(CoreLoc.F("Core.Vst3.InitFailed", init));
            return;
        }
        throw new InvalidOperationException(CoreLoc.T("Core.Vst3.NoAudioClass"));
    }

    void SetupController()
    {
        if (QueryInterface(_component, Tuids.IAudioProcessor, out _processor) != 0)
            throw new InvalidOperationException(CoreLoc.T("Core.Vst3.NoProcessor"));

        // 単一コンポーネント型: 同じオブジェクトが IEditController も実装している
        if (QueryInterface(_component, Tuids.IEditController, out _controller) == 0)
        {
            _controllerIsComponent = true;
        }
        else
        {
            Guid classId;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)Fn(_component, 5))(_component, &classId) == 0)
            {
                Guid iid = Tuids.IEditController;
                IntPtr obj = IntPtr.Zero;
                if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Fn(_factory, 6))(_factory, &classId, &iid, &obj) == 0
                    && obj != IntPtr.Zero)
                {
                    _controller = obj;
                    Initialize(_controller, _hostApp.Primary);
                }
            }
        }

        if (_controller != IntPtr.Zero)
        {
            _handler = new ComponentHandler((id, value) => _pendingEdits.Enqueue((id, value)));
            _frame = new PlugFrame(OnResizeView);
            ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controller, 16))(_controller, _handler.Primary);

            // コンポーネントとコントローラが別オブジェクトなら接続する
            if (!_controllerIsComponent
                && QueryInterface(_component, Tuids.IConnectionPoint, out _componentConn) == 0
                && QueryInterface(_controller, Tuids.IConnectionPoint, out _controllerConn) == 0)
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_componentConn, 3))(_componentConn, _controllerConn);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controllerConn, 3))(_controllerConn, _componentConn);
            }
        }
    }

    int BusCount(int direction)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int>)Fn(_component, 7))(_component, 0 /* kAudio */, direction);

    int GetArrangement(int direction, int index, ulong* arrangement)
        => ((delegate* unmanaged[Stdcall]<IntPtr, int, int, ulong*, int>)Fn(_processor, 4))(_processor, direction, index, arrangement);

    void SetupBuses()
    {
        _inBusCount = BusCount(0);
        _outBusCount = BusCount(1);

        const ulong Stereo = 3; // kSpeakerL | kSpeakerR
        ulong* inArr = stackalloc ulong[Math.Max(_inBusCount, 1)];
        ulong* outArr = stackalloc ulong[Math.Max(_outBusCount, 1)];
        for (int i = 0; i < _inBusCount; i++) if (GetArrangement(0, i, &inArr[i]) != 0) inArr[i] = Stereo;
        for (int i = 0; i < _outBusCount; i++) if (GetArrangement(1, i, &outArr[i]) != 0) outArr[i] = Stereo;
        if (_inBusCount > 0) inArr[0] = Stereo;
        if (_outBusCount > 0) outArr[0] = Stereo;

        // 拒否された場合はプラグインの既定構成 (モノラル等) を使う
        ((delegate* unmanaged[Stdcall]<IntPtr, ulong*, int, ulong*, int, int>)Fn(_processor, 3))(_processor, inArr, _inBusCount, outArr, _outBusCount);

        ulong arr = 0;
        _inCh = _inBusCount > 0 && GetArrangement(0, 0, &arr) == 0 ? BitOperations.PopCount(arr) : 0;
        arr = 0;
        _outCh = _outBusCount > 0 && GetArrangement(1, 0, &arr) == 0 ? BitOperations.PopCount(arr) : 0;

        // 先頭(メイン)バスのみ有効化し、サイドチェイン等は無効のままにする
        for (int i = 0; i < _inBusCount; i++)
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, byte, int>)Fn(_component, 10))(_component, 0, 0, i, (byte)(i == 0 ? 1 : 0));
        for (int i = 0; i < _outBusCount; i++)
            ((delegate* unmanaged[Stdcall]<IntPtr, int, int, int, byte, int>)Fn(_component, 10))(_component, 0, 1, i, (byte)(i == 0 ? 1 : 0));

        int sample32 = ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Fn(_processor, 5))(_processor, 0 /* kSample32 */);
        if (sample32 != 0) throw new InvalidOperationException(CoreLoc.T("Core.Vst3.No32bit"));
    }

    void AllocateBuffers()
    {
        _inAlloc = Math.Max(_inCh, 2);
        _outAlloc = Math.Max(_outCh, 2);
        _inBufs = (float**)NativeMemory.Alloc((nuint)_inAlloc, (nuint)sizeof(float*));
        _outBufs = (float**)NativeMemory.Alloc((nuint)_outAlloc, (nuint)sizeof(float*));
        for (int i = 0; i < _inAlloc; i++) _inBufs[i] = (float*)NativeMemory.AllocZeroed((nuint)MaxBlock, sizeof(float));
        for (int i = 0; i < _outAlloc; i++) _outBufs[i] = (float*)NativeMemory.AllocZeroed((nuint)MaxBlock, sizeof(float));

        _inBus = (AudioBusBuffers*)NativeMemory.AllocZeroed((nuint)Math.Max(_inBusCount, 1), (nuint)sizeof(AudioBusBuffers));
        _outBus = (AudioBusBuffers*)NativeMemory.AllocZeroed((nuint)Math.Max(_outBusCount, 1), (nuint)sizeof(AudioBusBuffers));
        if (_inBusCount > 0) { _inBus[0].numChannels = _inCh; _inBus[0].channelBuffers32 = _inBufs; }
        if (_outBusCount > 0) { _outBus[0].numChannels = _outCh; _outBus[0].channelBuffers32 = _outBufs; }

        // ProcessContext (サイズ 112 バイト): 再生中・120 BPM・4/4 として通知する
        _context = (byte*)NativeMemory.AllocZeroed(128);
    }

    void Activate()
    {
        var setup = new ProcessSetup { processMode = 0, symbolicSampleSize = 0, maxSamplesPerBlock = MaxBlock, sampleRate = _sampleRate };
        ((delegate* unmanaged[Stdcall]<IntPtr, ProcessSetup*, int>)Fn(_processor, 7))(_processor, &setup);
        ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(_component, 11))(_component, 1);
        ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(_processor, 8))(_processor, 1);
        _processing = true;
    }

    void Deactivate()
    {
        _processing = false;
        if (_processor != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(_processor, 8))(_processor, 0);
        if (_component != IntPtr.Zero)
            ((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Fn(_component, 11))(_component, 0);
    }

    public void Prepare(int sampleRate, int maxBlock)
    {
        if (_disposed) return;
        _thread.Invoke(() =>
        {
            Deactivate();
            _sampleRate = sampleRate;
            _samplePos = 0;
            Activate();
        });
    }

    // ================= オーディオ処理 =================

    public void Process(float[] left, float[] right, int frames)
    {
        fixed (float* l = left, r = right)
            ProcessCore(l, r, l, r, frames);
    }

    public void ProcessCore(float* inL, float* inR, float* outL, float* outR, int frames)
    {
        if (_disposed || !_processing) return;
        if (frames > MaxBlock) frames = MaxBlock;
        if (frames <= 0) return;
        int bytes = frames * sizeof(float);

        if (_inCh == 1)
        {
            float* d = _inBufs[0];
            for (int i = 0; i < frames; i++) d[i] = (inL[i] + inR[i]) * 0.5f;
        }
        else if (_inCh >= 2)
        {
            Buffer.MemoryCopy(inL, _inBufs[0], bytes, bytes);
            Buffer.MemoryCopy(inR, _inBufs[1], bytes, bytes);
        }

        // エディタで操作されたパラメータを、次の process でプロセッサ側へ伝える
        _inChanges.Reset();
        _outChanges.Reset();
        for (int n = 0; n < 64 && _pendingEdits.TryDequeue(out var edit); n++)
            _inChanges.SetValue(edit.Id, edit.Value);

        UpdateContext(frames);

        for (int i = 0; i < _inBusCount; i++) _inBus[i].silenceFlags = 0;
        for (int i = 0; i < _outBusCount; i++) _outBus[i].silenceFlags = 0;

        var data = new ProcessData
        {
            processMode = 0,
            symbolicSampleSize = 0,
            numSamples = frames,
            numInputs = _inBusCount,
            numOutputs = _outBusCount,
            inputs = _inBus,
            outputs = _outBus,
            inputParameterChanges = _inChanges.Primary,
            outputParameterChanges = _outChanges.Primary,
            inputEvents = _events.Primary,
            outputEvents = IntPtr.Zero,
            processContext = (IntPtr)_context,
        };

        int rc = ((delegate* unmanaged[Stdcall]<IntPtr, ProcessData*, int>)Fn(_processor, 9))(_processor, &data);

        if (rc != 0 || _outCh == 0)
        {
            // 処理失敗 / 出力バスなし: 入力をそのまま通す
            if (outL != inL) Buffer.MemoryCopy(inL, outL, bytes, bytes);
            if (outR != inR) Buffer.MemoryCopy(inR, outR, bytes, bytes);
            return;
        }

        // サイレントフラグが立ったチャンネルは出力バッファが未書き込みの場合があるので無音扱いにする
        ulong silence = _outBus[0].silenceFlags;
        for (int c = 0; c < Math.Min(_outCh, 2); c++)
            if ((silence & (1UL << c)) != 0) new Span<float>(_outBufs[c], frames).Clear();

        Buffer.MemoryCopy(_outBufs[0], outL, bytes, bytes);
        Buffer.MemoryCopy(_outCh >= 2 ? _outBufs[1] : _outBufs[0], outR, bytes, bytes);
    }

    void UpdateContext(int frames)
    {
        const double tempo = 120.0;
        byte* p = _context;
        *(uint*)p = (1u << 1) | (1u << 9) | (1u << 10) | (1u << 13);   // kPlaying | kProjectTimeMusicValid | kTempoValid | kTimeSigValid
        *(double*)(p + 8) = _sampleRate;                                // sampleRate
        *(long*)(p + 16) = _samplePos;                                  // projectTimeSamples
        *(long*)(p + 32) = _samplePos;                                  // continousTimeSamples
        *(double*)(p + 40) = _samplePos / (double)_sampleRate * (tempo / 60.0); // projectTimeMusic
        *(double*)(p + 72) = tempo;                                     // tempo
        *(int*)(p + 80) = 4;                                            // timeSigNumerator
        *(int*)(p + 84) = 4;                                            // timeSigDenominator
        _samplePos += frames;
    }

    // ================= 破棄 =================

    void TeardownOnThread()
    {
        CloseEditorOnThread();
        Deactivate();

        if (_componentConn != IntPtr.Zero && _controllerConn != IntPtr.Zero)
        {
            try
            {
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_componentConn, 4))(_componentConn, _controllerConn);
                ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controllerConn, 4))(_controllerConn, _componentConn);
            }
            catch
            {
            }
        }
        Release(ref _componentConn);
        Release(ref _controllerConn);

        if (_controller != IntPtr.Zero)
        {
            try { ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Fn(_controller, 16))(_controller, IntPtr.Zero); } catch { }
            if (!_controllerIsComponent) { try { Terminate(_controller); } catch { } }
        }
        Release(ref _controller);
        Release(ref _processor);

        if (_component != IntPtr.Zero && _initialized)
        {
            try { Terminate(_component); } catch { }
        }
        Release(ref _component);
        Release(ref _factory);

        if (_module != IntPtr.Zero)
        {
            IntPtr exitDll = Win32.GetProcAddress(_module, "ExitDll");
            if (exitDll != IntPtr.Zero)
            {
                try { ((delegate* unmanaged[Cdecl]<byte>)exitDll)(); } catch { }
            }
            Win32.FreeLibrary(_module);
            _module = IntPtr.Zero;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _processing = false;

        try { _thread.Invoke(TeardownOnThread, 5000); } catch { }
        _thread.Stop();

        if (_inBufs != null)
        {
            for (int i = 0; i < _inAlloc; i++) NativeMemory.Free(_inBufs[i]);
            NativeMemory.Free(_inBufs);
            _inBufs = null;
        }
        if (_outBufs != null)
        {
            for (int i = 0; i < _outAlloc; i++) NativeMemory.Free(_outBufs[i]);
            NativeMemory.Free(_outBufs);
            _outBufs = null;
        }
        if (_inBus != null) { NativeMemory.Free(_inBus); _inBus = null; }
        if (_outBus != null) { NativeMemory.Free(_outBus); _outBus = null; }
        if (_context != null) { NativeMemory.Free(_context); _context = null; }

        _handler?.Dispose();
        _frame?.Dispose();
        _hostApp.Dispose();
        _inChanges.Dispose();
        _outChanges.Dispose();
        _events.Dispose();
    }
}
