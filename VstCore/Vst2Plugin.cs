using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VstCore;

/// <summary>VST 2.x プラグインを同一プロセス内にロードして駆動するホスト実装。</summary>
public sealed unsafe class Vst2Plugin : ILocalPlugin, IEditorOwner
{
    const int MaxBlock = PluginLimits.MaxBlock;

    const int kEffectMagic = 0x56737450; // 'VstP'

    // AEffectOpcodes / AEffectXOpcodes
    const int effOpen = 0, effClose = 1, effSetProgram = 2, effGetProgram = 3, effGetProgramName = 5, effGetProgramNameIndexed = 29, effGetParamLabel = 6, effGetParamDisplay = 7, effGetParamName = 8, effSetSampleRate = 10, effSetBlockSize = 11,
        effMainsChanged = 12, effEditGetRect = 13, effEditOpen = 14, effEditClose = 15, effEditIdle = 19,
        effGetChunk = 23, effSetChunk = 24, effGetEffectName = 45, effStartProcess = 71, effStopProcess = 72;

    const int effFlagsHasEditor = 1 << 0, effFlagsCanReplacing = 1 << 4, effFlagsProgramChunks = 1 << 5;

    [StructLayout(LayoutKind.Sequential)]
    struct AEffect
    {
        public int magic;
        public IntPtr dispatcher, process, setParameter, getParameter;
        public int numPrograms, numParams, numInputs, numOutputs, flags;
        public IntPtr resvd1, resvd2;
        public int initialDelay, realQualities, offQualities;
        public float ioRatio;
        public IntPtr obj, user;
        public int uniqueID, version;
        public IntPtr processReplacing, processDoubleReplacing;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ERect
    {
        public short top, left, bottom, right;
    }

    // ---- ホストコールバック共有状態 ----
    static readonly Dictionary<IntPtr, Vst2Plugin> Instances = new();
    static readonly object StaticLock = new();
    static IntPtr _timeInfo;
    static volatile int _hostSampleRate = 48000;
    static volatile int _hostBlockSize = MaxBlock;

    readonly IntPtr _module;
    readonly IntPtr _effect;
    readonly AEffect _fx;
    readonly float** _inBufs;
    readonly float** _outBufs;
    readonly int _inCount, _outCount;
    readonly string _path;
    readonly MessageThread _thread;
    volatile EditorWindow? _editor;
    volatile bool _disposed;

    public string Name { get; }
    public string Path => _path;
    public PluginBitness Bitness => PeInfo.Current;
    public bool HasEditor => (_fx.flags & effFlagsHasEditor) != 0;
    public bool IsFaulted => false;

    /// <summary>AEffect.initialDelay (サンプル数)。</summary>
    public int LatencySamples => _disposed ? 0 : Math.Max(0, ((AEffect*)_effect)->initialDelay);
    public int NumInputs => _fx.numInputs;
    public int NumOutputs => _fx.numOutputs;
    public int ParameterCount => _fx.numParams;

    Vst2Plugin(string path, IntPtr module, IntPtr effect, MessageThread thread)
    {
        _thread = thread;
        _path = path;
        _module = module;
        _effect = effect;
        _fx = *(AEffect*)effect;

        _inCount = Math.Max(_fx.numInputs, 2);
        _outCount = Math.Max(_fx.numOutputs, 2);
        _inBufs = (float**)NativeMemory.Alloc((nuint)_inCount, (nuint)sizeof(float*));
        _outBufs = (float**)NativeMemory.Alloc((nuint)_outCount, (nuint)sizeof(float*));
        for (int i = 0; i < _inCount; i++)
            _inBufs[i] = (float*)NativeMemory.AllocZeroed((nuint)MaxBlock, sizeof(float));
        for (int i = 0; i < _outCount; i++)
            _outBufs[i] = (float*)NativeMemory.AllocZeroed((nuint)MaxBlock, sizeof(float));

        Name = ReadString(effGetEffectName, 256);
        if (string.IsNullOrWhiteSpace(Name))
            Name = System.IO.Path.GetFileNameWithoutExtension(path);
    }

    public static Vst2Plugin Load(string path, int sampleRate)
    {
        var bitness = PeInfo.GetBitness(path);
        if (bitness != PeInfo.Current)
            throw new BadImageFormatException(CoreLoc.F("Core.Vst.BitnessMismatch", PeInfo.Current, bitness));

        // JUCE 製などは、ロードしたスレッドを「メッセージスレッド」とみなし、エディタの操作をそこへ回す。
        // そのスレッドがメッセージを処理しないとエディタが開けないので、ロード・エディタ・状態・破棄は専用のメッセージループのスレッドで行う。
        var thread = new MessageThread("VST2: " + System.IO.Path.GetFileName(path));
        try
        {
            return thread.Invoke(() => LoadOnThread(path, sampleRate, thread));
        }
        catch
        {
            thread.Stop();
            throw;
        }
    }

    static Vst2Plugin LoadOnThread(string path, int sampleRate, MessageThread thread)
    {
        _hostSampleRate = sampleRate;
        IntPtr module = Win32.LoadLibraryExW(path, IntPtr.Zero, 0x00000008 /* LOAD_WITH_ALTERED_SEARCH_PATH */);
        if (module == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), CoreLoc.F("Core.Vst.DllLoadFailed", path));

        IntPtr entry = Win32.GetProcAddress(module, "VSTPluginMain");
        if (entry == IntPtr.Zero) entry = Win32.GetProcAddress(module, "main");
        if (entry == IntPtr.Zero)
        {
            Win32.FreeLibrary(module);
            throw new InvalidOperationException(CoreLoc.T("Core.Vst2.NoEntry"));
        }

        IntPtr effect;
        try
        {
            var callback = (IntPtr)(delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, IntPtr, float, IntPtr>)&HostCallback;
            effect = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr>)entry)(callback);
        }
        catch
        {
            Win32.FreeLibrary(module);
            throw;
        }

        if (effect == IntPtr.Zero || ((AEffect*)effect)->magic != kEffectMagic)
        {
            Win32.FreeLibrary(module);
            throw new InvalidOperationException(CoreLoc.T("Core.Vst2.Invalid"));
        }

        // 入出力数などは effOpen 後に確定するプラグインがあるため、先に open してから構造体を読む
        ((delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, IntPtr, float, IntPtr>)((AEffect*)effect)->dispatcher)(
            effect, effOpen, 0, IntPtr.Zero, IntPtr.Zero, 0f);

        var plugin = new Vst2Plugin(path, module, effect, thread);
        lock (StaticLock) Instances[effect] = plugin;
        plugin.PrepareCore(sampleRate, MaxBlock);
        return plugin;
    }

    /// <summary>メッセージスレッドで実行して結果を待つ (すでにそのスレッドなら直接実行)。</summary>
    T OnThread<T>(Func<T> func) => _disposed ? default! : _thread.Invoke(func);

    void OnThread(Action action)
    {
        if (!_disposed) _thread.Invoke(action);
    }

    IntPtr Dispatch(int opcode, int index, IntPtr value, IntPtr ptr, float opt = 0f)
        => ((delegate* unmanaged[Cdecl]<IntPtr, int, int, IntPtr, IntPtr, float, IntPtr>)_fx.dispatcher)(_effect, opcode, index, value, ptr, opt);

    string ReadString(int opcode, int size, int index = 0)
    {
        byte* buf = stackalloc byte[size];
        new Span<byte>(buf, size).Clear();
        try { Dispatch(opcode, index, IntPtr.Zero, (IntPtr)buf); }
        catch { return ""; }
        return Marshal.PtrToStringAnsi((IntPtr)buf) ?? "";
    }

    public void Prepare(int sampleRate, int maxBlock) => OnThread(() => PrepareCore(sampleRate, maxBlock));

    void PrepareCore(int sampleRate, int maxBlock)
    {
        _hostSampleRate = sampleRate;
        _hostBlockSize = Math.Min(maxBlock, MaxBlock);
        Dispatch(effStopProcess, 0, IntPtr.Zero, IntPtr.Zero);
        Dispatch(effMainsChanged, 0, IntPtr.Zero, IntPtr.Zero);
        Dispatch(effSetSampleRate, 0, IntPtr.Zero, IntPtr.Zero, sampleRate);
        Dispatch(effSetBlockSize, 0, (IntPtr)_hostBlockSize, IntPtr.Zero);
        Dispatch(effMainsChanged, 0, (IntPtr)1, IntPtr.Zero);
        Dispatch(effStartProcess, 0, IntPtr.Zero, IntPtr.Zero);
    }

    public void Process(float[] left, float[] right, int frames)
    {
        fixed (float* l = left, r = right)
            ProcessCore(l, r, l, r, frames);
    }

    /// <summary>入力と出力が同一ポインタでも安全 (入力を内部バッファへコピーしてから処理する)。</summary>
    public void ProcessCore(float* inL, float* inR, float* outL, float* outR, int frames)
    {
        if (_disposed) return;
        if (frames > MaxBlock) frames = MaxBlock;
        if (frames <= 0) return;
        int bytes = frames * sizeof(float);

        if (_fx.numInputs == 1)
        {
            float* d = _inBufs[0];
            for (int i = 0; i < frames; i++) d[i] = (inL[i] + inR[i]) * 0.5f;
        }
        else
        {
            Buffer.MemoryCopy(inL, _inBufs[0], bytes, bytes);
            Buffer.MemoryCopy(inR, _inBufs[1], bytes, bytes);
        }

        if (_fx.numOutputs == 0)
        {
            if (outL != inL) Buffer.MemoryCopy(inL, outL, bytes, bytes);
            if (outR != inR) Buffer.MemoryCopy(inR, outR, bytes, bytes);
            return;
        }

        if ((_fx.flags & effFlagsCanReplacing) != 0 && _fx.processReplacing != IntPtr.Zero)
        {
            ((delegate* unmanaged[Cdecl]<IntPtr, float**, float**, int, void>)_fx.processReplacing)(_effect, _inBufs, _outBufs, frames);
        }
        else
        {
            // 旧式の accumulating process: 出力を 0 クリアしてから加算させる
            for (int c = 0; c < _outCount; c++) new Span<float>(_outBufs[c], frames).Clear();
            ((delegate* unmanaged[Cdecl]<IntPtr, float**, float**, int, void>)_fx.process)(_effect, _inBufs, _outBufs, frames);
        }

        Buffer.MemoryCopy(_outBufs[0], outL, bytes, bytes);
        Buffer.MemoryCopy(_fx.numOutputs >= 2 ? _outBufs[1] : _outBufs[0], outR, bytes, bytes);
    }

    // ---- 状態 (プリセット) ----

    public byte[]? GetState() => OnThread(GetStateCore);

    byte[]? GetStateCore()
    {
        if ((_fx.flags & effFlagsProgramChunks) != 0)
        {
            IntPtr data = IntPtr.Zero;
            int size = (int)Dispatch(effGetChunk, 0, IntPtr.Zero, (IntPtr)(&data));
            if (size > 0 && data != IntPtr.Zero)
            {
                var result = new byte[size + 1];
                result[0] = 1;
                Marshal.Copy(data, result, 1, size);
                return result;
            }
        }

        int count = _fx.numParams;
        if (count <= 0) return null;
        var bytes = new byte[1 + count * sizeof(float)];
        bytes[0] = 2;
        var getParameter = (delegate* unmanaged[Cdecl]<IntPtr, int, float>)_fx.getParameter;
        for (int i = 0; i < count; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(1 + i * sizeof(float)), getParameter(_effect, i));
        return bytes;
    }

    public void SetState(byte[] data) => OnThread(() => SetStateCore(data));

    void SetStateCore(byte[] data)
    {
        if (data.Length < 2) return;
        if (data[0] == 1)
        {
            fixed (byte* p = &data[1])
                Dispatch(effSetChunk, 0, (IntPtr)(data.Length - 1), (IntPtr)p);
        }
        else if (data[0] == 2)
        {
            var setParameter = (delegate* unmanaged[Cdecl]<IntPtr, int, float, void>)_fx.setParameter;
            int count = Math.Min(_fx.numParams, (data.Length - 1) / sizeof(float));
            for (int i = 0; i < count; i++)
                setParameter(_effect, i, BitConverter.ToSingle(data, 1 + i * sizeof(float)));
        }
    }

    // ---- プログラム (プリセット) ----

    public ProgramList GetPrograms() => OnThread(GetProgramsCore) ?? new ProgramList(Array.Empty<string>(), 0);

    ProgramList GetProgramsCore()
    {
        int count = Math.Min(_fx.numPrograms, HostProtocol.MaxItemCount);
        if (_disposed || count <= 0) return new ProgramList(Array.Empty<string>(), 0);

        var names = new string[count];
        int current = (int)Dispatch(effGetProgram, 0, IntPtr.Zero, IntPtr.Zero);

        // effGetProgramNameIndexed 非対応の古いプラグインは、切り替えて名前を読み、元に戻す
        bool indexed = !string.IsNullOrEmpty(ReadString(effGetProgramNameIndexed, 256, 0));
        for (int i = 0; i < count; i++)
        {
            if (indexed)
                names[i] = ReadString(effGetProgramNameIndexed, 256, i);
            else
            {
                Dispatch(effSetProgram, 0, (IntPtr)i, IntPtr.Zero);
                names[i] = ReadString(effGetProgramName, 256);
            }
            if (string.IsNullOrWhiteSpace(names[i])) names[i] = $"Program {i + 1}";
        }
        if (!indexed) Dispatch(effSetProgram, 0, (IntPtr)current, IntPtr.Zero);

        return new ProgramList(names, Math.Clamp(current, 0, count - 1));
    }

    public void SetProgram(int index) => OnThread(() => SetProgramCore(index));

    void SetProgramCore(int index)
    {
        if (_disposed || index < 0 || index >= _fx.numPrograms) return;
        Dispatch(effSetProgram, 0, (IntPtr)index, IntPtr.Zero);
    }
    // ---- パラメータ (汎用 UI 用) ----

    public ParameterInfo[] GetParameters() => OnThread(GetParametersCore) ?? Array.Empty<ParameterInfo>();

    ParameterInfo[] GetParametersCore()
    {
        var getParameter = (delegate* unmanaged[Cdecl]<IntPtr, int, float>)_fx.getParameter;
        var result = new ParameterInfo[Math.Clamp(_fx.numParams, 0, HostProtocol.MaxItemCount)];
        for (int i = 0; i < result.Length; i++)
        {
            string name = ReadString(effGetParamName, 256, i);
            if (string.IsNullOrWhiteSpace(name)) name = $"Param {i + 1}";
            result[i] = new ParameterInfo(i, name, ReadString(effGetParamLabel, 256, i), 0, getParameter(_effect, i));
        }
        return result;
    }

    public ParameterValue GetParameterValue(int index) => OnThread(() => GetParameterValueCore(index));

    ParameterValue GetParameterValueCore(int index)
    {
        if (_disposed || index < 0 || index >= _fx.numParams) return default;
        double value = ((delegate* unmanaged[Cdecl]<IntPtr, int, float>)_fx.getParameter)(_effect, index);
        string display = ReadString(effGetParamDisplay, 256, index);
        string label = ReadString(effGetParamLabel, 256, index);
        return new ParameterValue(value, string.IsNullOrEmpty(label) ? display : $"{display} {label}");
    }

    public void SetParameter(int index, double normalized) => OnThread(() => SetParameterCore(index, normalized));

    void SetParameterCore(int index, double normalized)
    {
        if (_disposed || index < 0 || index >= _fx.numParams) return;
        ((delegate* unmanaged[Cdecl]<IntPtr, int, float, void>)_fx.setParameter)(_effect, index, (float)Math.Clamp(normalized, 0, 1));
    }

    // ---- エディタ (メッセージスレッド上の Win32 ウィンドウ。ウィンドウの部分は EditorWindow) ----

    /// <summary>エディタを開く。ウィンドウもプラグインのエディタも、メッセージスレッドで作る (待たずに戻る)。</summary>
    public void OpenEditor()
    {
        if (!HasEditor || _disposed) return;
        _thread.Post(() =>
        {
            if (!_disposed) OpenEditorOnThread();
        });
    }

    /// <summary>エディタを閉じる。ウィンドウもプラグインのエディタも、メッセージスレッドで片付ける (待たずに戻る)。</summary>
    public void CloseEditor()
    {
        if (_disposed) return;
        _thread.Post(() =>
        {
            if (!_disposed) _editor?.Close();
        });
    }

    void OpenEditorOnThread()
    {
        EditorWindow? window = null;
        try
        {
            if (_editor != null)
            {
                _editor.Activate();
                return;
            }

            GetEditorSize(out int width, out int height);
            window = EditorWindow.Create(this, Name, width, height, GetProgramsCore());
            _editor = window;

            Dispatch(effEditOpen, 0, IntPtr.Zero, window.Handle);

            // 多くのプラグインは effEditOpen 後に正しいサイズを返す
            GetEditorSize(out width, out height);
            window.Resize(width, height);

            window.Show();
            window.StartIdleTimer(30);
        }
        catch (Exception ex)
        {
            // エディタの失敗でホスト全体を落とさない。原因はログに残し、作りかけのウィンドウは片付ける
            HostLog.Error($"VST2 エディタを開けません: {Name} ({_path})", ex);
            window?.Close();
        }
    }

    void IEditorOwner.SetProgram(int index) => SetProgram(index);

    void IEditorOwner.Idle() => Dispatch(effEditIdle, 0, IntPtr.Zero, IntPtr.Zero);

    void IEditorOwner.CloseContent() => Dispatch(effEditClose, 0, IntPtr.Zero, IntPtr.Zero);

    void IEditorOwner.Destroyed() => _editor = null;

    void GetEditorSize(out int width, out int height)
    {
        ERect* rect = null;
        Dispatch(effEditGetRect, 0, IntPtr.Zero, (IntPtr)(&rect));
        width = 0;
        height = 0;
        if (rect != null)
        {
            width = rect->right - rect->left;
            height = rect->bottom - rect->top;
        }
        if (width <= 0) width = 400;
        if (height <= 0) height = 300;
    }

    // ---- ホストコールバック ----

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    static IntPtr HostCallback(IntPtr effect, int opcode, int index, IntPtr value, IntPtr ptr, float opt)
    {
        try
        {
            switch (opcode)
            {
                case 1: return (IntPtr)2400;                      // audioMasterVersion
                case 7: return GetTimeInfo();                     // audioMasterGetTime
                case 15:                                          // audioMasterSizeWindow
                    {
                        Vst2Plugin? p;
                        lock (StaticLock) Instances.TryGetValue(effect, out p);
                        var editor = p?._editor;
                        if (editor == null) return IntPtr.Zero;
                        editor.Resize(index, (int)value);
                        return (IntPtr)1;
                    }
                case 16: return (IntPtr)_hostSampleRate;          // audioMasterGetSampleRate
                case 17: return (IntPtr)_hostBlockSize;           // audioMasterGetBlockSize
                case 32: return WriteString(ptr, "Koromogae");   // audioMasterGetVendorString
                case 33: return WriteString(ptr, "Koromogae");   // audioMasterGetProductString
                case 34: return (IntPtr)1000;                     // audioMasterGetVendorVersion
                case 37:                                          // audioMasterCanDo
                    {
                        string? what = Marshal.PtrToStringAnsi(ptr);
                        return what is "sizeWindow" or "supplyIdle" or "receiveVstTimeInfo" ? (IntPtr)1 : IntPtr.Zero;
                    }
                case 38: return (IntPtr)1;                        // audioMasterGetLanguage (English)
                case 43:                                          // audioMasterBeginEdit
                case 44: return (IntPtr)1;                        // audioMasterEndEdit
                default: return IntPtr.Zero;
            }
        }
        catch
        {
            return IntPtr.Zero;
        }
    }

    static IntPtr WriteString(IntPtr dest, string text)
    {
        if (dest == IntPtr.Zero) return IntPtr.Zero;
        var bytes = Encoding.ASCII.GetBytes(text);
        Marshal.Copy(bytes, 0, dest, bytes.Length);
        Marshal.WriteByte(dest, bytes.Length, 0);
        return (IntPtr)1;
    }

    static IntPtr GetTimeInfo()
    {
        if (_timeInfo == IntPtr.Zero)
        {
            // VstTimeInfo: 8 個の double + 6 個の int = 88 バイト
            byte* mem = (byte*)NativeMemory.AllocZeroed(96);
            _timeInfo = (IntPtr)mem;
        }
        byte* p = (byte*)_timeInfo;
        *(double*)(p + 8) = _hostSampleRate;   // sampleRate
        *(double*)(p + 32) = 120.0;            // tempo
        *(int*)(p + 64) = 4;                   // timeSigNumerator
        *(int*)(p + 68) = 4;                   // timeSigDenominator
        // kVstTransportPlaying | kVstTempoValid | kVstTimeSigValid
        *(int*)(p + 84) = (1 << 1) | (1 << 10) | (1 << 13);
        return _timeInfo;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // エディタを閉じ、プラグインを閉じるのは、ロードしたスレッドで行う。
        // そのスレッドが固まっている (タイムアウトした) なら、足元を壊さないよう、解放もアンロードもせずに戻る (プロセスごと終了する前提)。
        try
        {
            _thread.Invoke(() =>
            {
                _editor?.Close();
                try
                {
                    Dispatch(effStopProcess, 0, IntPtr.Zero, IntPtr.Zero);
                    Dispatch(effMainsChanged, 0, IntPtr.Zero, IntPtr.Zero);
                    Dispatch(effClose, 0, IntPtr.Zero, IntPtr.Zero);
                }
                catch
                {
                }
            }, 5000);
        }
        catch (TimeoutException)
        {
            return;
        }
        catch
        {
        }

        lock (StaticLock) Instances.Remove(_effect);
        _thread.Stop();
        Win32.FreeLibrary(_module);

        for (int i = 0; i < _inCount; i++) NativeMemory.Free(_inBufs[i]);
        for (int i = 0; i < _outCount; i++) NativeMemory.Free(_outBufs[i]);
        NativeMemory.Free(_inBufs);
        NativeMemory.Free(_outBufs);
    }
}

