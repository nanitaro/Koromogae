using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace VstCore.Vst3;

internal static class Tuids
{
    /// <summary>Steinberg の INLINE_UID (COM 互換レイアウト) と同じバイト列になる Guid を作る。</summary>
    static Guid Make(uint l1, uint l2, uint l3, uint l4) => new(
        (int)l1, (short)(l2 >> 16), (short)(l2 & 0xFFFF),
        (byte)(l3 >> 24), (byte)(l3 >> 16), (byte)(l3 >> 8), (byte)l3,
        (byte)(l4 >> 24), (byte)(l4 >> 16), (byte)(l4 >> 8), (byte)l4);

    public static readonly Guid FUnknown = Make(0x00000000, 0x00000000, 0xC0000000, 0x00000046);
    public static readonly Guid IComponent = Make(0xE831FF31, 0xF2D54301, 0x928EBBEE, 0x25697802);
    public static readonly Guid IAudioProcessor = Make(0x42043F99, 0xB7DA453C, 0xA569E79D, 0x9AAEC33D);
    public static readonly Guid IEditController = Make(0xDCD7BBE3, 0x7742448D, 0xA874AACC, 0x979C759E);
    public static readonly Guid IConnectionPoint = Make(0x70A4156F, 0x6E6E4026, 0x989148BF, 0xAA60D8D1);
    /// <summary>ivstmessage.h の IMessage (公式ヘッダで確認した値)。</summary>
    public static readonly Guid IMessage = Make(0x936F033B, 0xC6C047DB, 0xBB0882F8, 0x13C1E613);
    /// <summary>ivstattributes.h の IAttributeList (公式ヘッダで確認した値。記憶していた末尾 ...5B4D は誤りで、正しくは ...5EE4)。</summary>
    public static readonly Guid IAttributeList = Make(0x1E5F0AEB, 0xCC7F4533, 0xA2544011, 0x38AD5EE4);
    public static readonly Guid IHostApplication = Make(0x58E595CC, 0xDB2D4969, 0x8B6AAF8C, 0x36A664E5);
    public static readonly Guid IComponentHandler = Make(0x93A0BEA3, 0x0BD045DB, 0x8E890B0C, 0xC1E46AC6);
    public static readonly Guid IPlugFrame = Make(0x367FAF01, 0xAFA94693, 0x8D4DA2A0, 0xED0882A3);
    public static readonly Guid IBStream = Make(0xC3BF6EA2, 0x30994752, 0x9B6BF990, 0x1EE33E9B);
    public static readonly Guid IParamValueQueue = Make(0x01263A18, 0xED074F6F, 0x98C9D356, 0x4686F9BA);
    public static readonly Guid IParameterChanges = Make(0xA4779663, 0x0BB64A56, 0xB44384A8, 0x466FEB9D);
    public static readonly Guid IUnitInfo = Make(0x3D4BD6B5, 0x913A4FD2, 0xA886E768, 0xA5EB92C1);
    public static readonly Guid IEventList = Make(0x3A2C4214, 0x346349FE, 0xB2C4F397, 0xB9695A44);
}

internal static class Tresult
{
    public const int Ok = 0;
    public const int False = 1;
    public const int NoInterface = unchecked((int)0x80004002);
    public const int NotImplemented = unchecked((int)0x80004001);
    public const int InvalidArgument = unchecked((int)0x80070057);
    public const int Fail = unchecked((int)0x80004005);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ViewRect
{
    public int left, top, right, bottom;
}

internal static unsafe class Vtbl
{
    /// <summary>FUnknown の 3 メソッドに続けて、指定のメソッド群を並べた vtable を作る (解放しない静的データ)。</summary>
    public static IntPtr* Create(params IntPtr[] methods)
    {
        var table = (IntPtr*)NativeMemory.Alloc((nuint)((3 + methods.Length) * sizeof(IntPtr)));
        table[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&HostObject.QueryInterfaceThunk;
        table[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HostObject.AddRefThunk;
        table[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&HostObject.ReleaseThunk;
        for (int i = 0; i < methods.Length; i++) table[3 + i] = methods[i];
        return table;
    }
}

/// <summary>
/// プラグインへ渡すホスト側 COM オブジェクトの基底。ネイティブ側は [vtbl*][GCHandle] の 2 ワードで、
/// インターフェースごとに 1 つ持つ。寿命は、既定では <see cref="Vst3Plugin"/> が管理するので、参照カウントでは解放しない。
/// 例外: <see cref="FreeOnLastRelease"/> が true のオブジェクト (プラグインが作って捨てる IMessage / IAttributeList) は、
/// 最後の Release で解放する (大量に作られるので、解放しないとメモリが増え続ける)。
/// </summary>
internal abstract unsafe class HostObject : IDisposable
{
    readonly List<(Guid Iid, IntPtr Ptr)> _interfaces = new();
    GCHandle _handle;
    int _refCount = 1;
    int _disposed;
    static int _live;

    /// <summary>まだ解放されていないホストオブジェクトの数 (解放の漏れを、テストで見つけるため)。</summary>
    internal static int LiveObjects => Volatile.Read(ref _live);

    /// <summary>true なら、参照カウントが 0 になったときに自分で解放する (プラグインが作って捨てるオブジェクト用)。</summary>
    protected virtual bool FreeOnLastRelease => false;

    protected HostObject()
    {
        _handle = GCHandle.Alloc(this);
        Interlocked.Increment(ref _live);
    }

    protected IntPtr Register(Guid iid, IntPtr* vtbl)
    {
        var mem = (IntPtr*)NativeMemory.Alloc((nuint)(2 * sizeof(IntPtr)));
        mem[0] = (IntPtr)vtbl;
        mem[1] = GCHandle.ToIntPtr(_handle);
        _interfaces.Add((iid, (IntPtr)mem));
        return (IntPtr)mem;
    }

    /// <summary>最初に登録したインターフェース (FUnknown* として渡せる)。</summary>
    public IntPtr Primary => _interfaces[0].Ptr;

    /// <summary>指定のインターフェースのポインタ (参照カウントは増やさない)。FUnknown は Primary。持っていなければ null。</summary>
    internal IntPtr? PointerFor(Guid iid)
    {
        if (iid == Tuids.FUnknown) return Primary;
        foreach (var (id, ptr) in _interfaces)
            if (id == iid) return ptr;
        return null;
    }

    public static T From<T>(IntPtr self) where T : HostObject
        => (T)GCHandle.FromIntPtr(((IntPtr*)self)[1]).Target!;

    int QueryInterface(Guid iid, IntPtr* obj)
    {
        if (iid == Tuids.FUnknown)
        {
            *obj = Primary;
            Interlocked.Increment(ref _refCount);
            return Tresult.Ok;
        }
        foreach (var (id, ptr) in _interfaces)
        {
            if (id != iid) continue;
            *obj = ptr;
            Interlocked.Increment(ref _refCount);
            return Tresult.Ok;
        }
        *obj = IntPtr.Zero;
        return Tresult.NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static int QueryInterfaceThunk(IntPtr self, Guid* iid, IntPtr* obj)
        => From<HostObject>(self).QueryInterface(*iid, obj);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static uint AddRefThunk(IntPtr self)
        => (uint)Interlocked.Increment(ref From<HostObject>(self)._refCount);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    internal static uint ReleaseThunk(IntPtr self)
    {
        var obj = From<HostObject>(self);
        int count = Interlocked.Decrement(ref obj._refCount);
        if (count <= 0 && obj.FreeOnLastRelease)
        {
            obj.Dispose();      // プラグインが作って捨てるオブジェクト: ここで解放する
            return 0;
        }
        return (uint)Math.Max(count, 1); // それ以外の実体は Vst3Plugin.Dispose まで残す
    }

    /// <summary>派生クラスが持つ資源 (ネイティブのメモリなど) を解放する。<see cref="Dispose"/> が、1 回だけ呼ぶ。</summary>
    protected virtual void ReleaseResources() { }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;   // 二重の解放を防ぐ (Release と Vst3Plugin.Dispose が重なっても 1 回)
        ReleaseResources();
        Interlocked.Decrement(ref _live);
        foreach (var (_, ptr) in _interfaces) NativeMemory.Free((void*)ptr);
        _interfaces.Clear();
        if (_handle.IsAllocated) _handle.Free();
    }
}

/// <summary>IHostApplication: ホスト名を返し、IMessage / IAttributeList を作る (<see cref="HostMessage"/>)。それ以外の cid は未対応。</summary>
internal sealed unsafe class HostApplication : HostObject
{
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, char*, int>)&GetName,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)&CreateInstance);

    public HostApplication() => Register(Tuids.IHostApplication, Vt);

    /// <summary>ログに出す、このホストを使うプラグインの識別 (パス)。</summary>
    public string Owner { get; set; } = "";

    /// <summary>createInstance で要求された (cid, iid) のうち、ログに残したもの。1 つの組につき 1 回だけ残す (音声スレッドや大量の要求でログを溢れさせない)。</summary>
    readonly HashSet<(Guid, Guid)> _loggedRequests = new();

    void LogRequest(Guid cid, Guid iid, string result)
    {
        lock (_loggedRequests)
        {
            if (!_loggedRequests.Add((cid, iid))) return;
        }
        string what = cid == Tuids.IMessage ? "IMessage" : cid == Tuids.IAttributeList ? "IAttributeList" : "(未知)";   // i18n:log (app.log に書く診断。日本語のまま)
        HostLog.Info($"VST3: プラグインが createInstance を要求しました: {what} cid={cid} iid={iid} → {result} ({Owner})");
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetName(IntPtr self, char* name)
    {
        const string text = "Koromogae";
        for (int i = 0; i < text.Length; i++) name[i] = text[i];
        name[text.Length] = '\0';
        return Tresult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int CreateInstance(IntPtr self, Guid* cid, Guid* iid, IntPtr* obj)
    {
        if (obj == null) return Tresult.InvalidArgument;
        *obj = IntPtr.Zero;
        if (cid == null || iid == null) return Tresult.InvalidArgument;

        HostObject? created = null;
        try
        {
            var app = From<HostApplication>(self);
            if (*cid == Tuids.IMessage) created = new HostMessage();
            else if (*cid == Tuids.IAttributeList) created = new HostAttributeList();

            if (created == null)
            {
                app.LogRequest(*cid, *iid, "未対応");   // i18n:log (app.log に書く診断。日本語のまま)
                return Tresult.NotImplemented;
            }
            if (created.PointerFor(*iid) is not { } pointer)
            {
                app.LogRequest(*cid, *iid, "iid が合いません");   // i18n:log (app.log に書く診断。日本語のまま)
                return Tresult.NoInterface;       // created は finally で解放する
            }

            *obj = pointer;                       // 作った側の参照 (カウント 1) を、そのままプラグインへ渡す (プラグインが Release する)
            created = null;
            app.LogRequest(*cid, *iid, "作成");   // i18n:log (app.log に書く診断。日本語のまま)
            return Tresult.Ok;
        }
        catch
        {
            return Tresult.Fail;                  // 例外を、ネイティブの呼び出し元へ出さない
        }
        finally
        {
            created?.Dispose();
        }
    }
}

/// <summary>IComponentHandler: エディタ操作によるパラメータ変更を受け取る。</summary>
internal sealed unsafe class ComponentHandler : HostObject
{
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&BeginEdit,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, double, int>)&PerformEdit,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, int>)&EndEdit,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&RestartComponent);

    readonly Action<uint, double> _onEdit;

    public ComponentHandler(Action<uint, double> onEdit)
    {
        _onEdit = onEdit;
        Register(Tuids.IComponentHandler, Vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int BeginEdit(IntPtr self, uint id) => Tresult.Ok;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int PerformEdit(IntPtr self, uint id, double value)
    {
        try { From<ComponentHandler>(self)._onEdit(id, value); }
        catch { }
        return Tresult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int EndEdit(IntPtr self, uint id) => Tresult.Ok;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int RestartComponent(IntPtr self, int flags) => Tresult.Ok;
}

/// <summary>IPlugFrame: エディタ側のリサイズ要求を受け取る。</summary>
internal sealed unsafe class PlugFrame : HostObject
{
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ViewRect*, int>)&ResizeView);

    readonly Action<IntPtr, ViewRect> _onResize;

    public PlugFrame(Action<IntPtr, ViewRect> onResize)
    {
        _onResize = onResize;
        Register(Tuids.IPlugFrame, Vt);
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int ResizeView(IntPtr self, IntPtr view, ViewRect* rect)
    {
        try { From<PlugFrame>(self)._onResize(view, *rect); }
        catch { return Tresult.Fail; }
        return Tresult.Ok;
    }
}

/// <summary>IBStream: メモリ上のストリーム (状態の保存/復元用)。</summary>
internal sealed unsafe class MemStream : HostObject
{
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int*, int>)&Read,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, int, int*, int>)&Write,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long, int, long*, int>)&Seek,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, long*, int>)&Tell);

    byte[] _data;
    int _length;
    int _position;

    public MemStream(byte[]? initial = null)
    {
        _data = initial != null ? (byte[])initial.Clone() : new byte[256];
        _length = initial?.Length ?? 0;
        Register(Tuids.IBStream, Vt);
    }

    public byte[] ToArray() => _data.AsSpan(0, _length).ToArray();

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int Read(IntPtr self, byte* buffer, int numBytes, int* read)
    {
        var s = From<MemStream>(self);
        int n = Math.Max(0, Math.Min(numBytes, s._length - s._position));
        if (n > 0) new ReadOnlySpan<byte>(s._data, s._position, n).CopyTo(new Span<byte>(buffer, n));
        s._position += n;
        if (read != null) *read = n;
        return Tresult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int Write(IntPtr self, byte* buffer, int numBytes, int* written)
    {
        var s = From<MemStream>(self);
        if (numBytes < 0) return Tresult.InvalidArgument;
        int end = s._position + numBytes;
        if (end > s._data.Length) Array.Resize(ref s._data, Math.Max(end, s._data.Length * 2));
        new ReadOnlySpan<byte>(buffer, numBytes).CopyTo(s._data.AsSpan(s._position));
        s._position = end;
        if (end > s._length) s._length = end;
        if (written != null) *written = numBytes;
        return Tresult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int Seek(IntPtr self, long pos, int mode, long* result)
    {
        var s = From<MemStream>(self);
        long target = mode switch
        {
            0 => pos,                  // kIBSeekSet
            1 => s._position + pos,    // kIBSeekCur
            2 => s._length + pos,      // kIBSeekEnd
            _ => -1,
        };
        if (target < 0 || target > s._length) return Tresult.InvalidArgument;
        s._position = (int)target;
        if (result != null) *result = target;
        return Tresult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int Tell(IntPtr self, long* pos)
    {
        *pos = From<MemStream>(self)._position;
        return Tresult.Ok;
    }
}

/// <summary>IParamValueQueue: 1 パラメータ分の変化点列。</summary>
internal sealed unsafe class ParamQueue : HostObject
{
    const int MaxPoints = 16;

    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&GetParameterId,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&GetPointCount,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int*, double*, int>)&GetPoint,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, double, int*, int>)&AddPoint);

    readonly int[] _offsets = new int[MaxPoints];
    readonly double[] _values = new double[MaxPoints];

    public uint Id;
    public int Count;

    public ParamQueue() => Register(Tuids.IParamValueQueue, Vt);

    /// <summary>ブロック先頭の 1 点だけに置き換える (最新値のみ有効)。</summary>
    public void SetSingle(double value)
    {
        _offsets[0] = 0;
        _values[0] = value;
        Count = 1;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static uint GetParameterId(IntPtr self) => From<ParamQueue>(self).Id;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetPointCount(IntPtr self) => From<ParamQueue>(self).Count;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetPoint(IntPtr self, int index, int* sampleOffset, double* value)
    {
        var q = From<ParamQueue>(self);
        if (index < 0 || index >= q.Count) return Tresult.Fail;
        *sampleOffset = q._offsets[index];
        *value = q._values[index];
        return Tresult.Ok;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int AddPoint(IntPtr self, int sampleOffset, double value, int* index)
    {
        var q = From<ParamQueue>(self);
        if (q.Count >= MaxPoints) return Tresult.Fail;
        q._offsets[q.Count] = sampleOffset;
        q._values[q.Count] = value;
        if (index != null) *index = q.Count;
        q.Count++;
        return Tresult.Ok;
    }
}

/// <summary>IParameterChanges: 1 ブロック分のパラメータ変更。入力用/出力用の両方に使う。</summary>
internal sealed unsafe class ParamChanges : HostObject
{
    const int PoolSize = 64;

    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&GetParameterCount,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr>)&GetParameterData,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int*, IntPtr>)&AddParameterData);

    readonly ParamQueue[] _pool = new ParamQueue[PoolSize];
    int _count;

    public ParamChanges()
    {
        for (int i = 0; i < PoolSize; i++) _pool[i] = new ParamQueue();
        Register(Tuids.IParameterChanges, Vt);
    }

    public void Reset()
    {
        for (int i = 0; i < _count; i++) _pool[i].Count = 0;
        _count = 0;
    }

    ParamQueue? FindOrAdd(uint id, out int index)
    {
        for (index = 0; index < _count; index++)
            if (_pool[index].Id == id) return _pool[index];
        if (_count >= PoolSize) return null;
        index = _count++;
        _pool[index].Id = id;
        _pool[index].Count = 0;
        return _pool[index];
    }

    public void SetValue(uint id, double value) => FindOrAdd(id, out _)?.SetSingle(value);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetParameterCount(IntPtr self) => From<ParamChanges>(self)._count;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static IntPtr GetParameterData(IntPtr self, int index)
    {
        var c = From<ParamChanges>(self);
        return index >= 0 && index < c._count ? c._pool[index].Primary : IntPtr.Zero;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static IntPtr AddParameterData(IntPtr self, uint* id, int* index)
    {
        var queue = From<ParamChanges>(self).FindOrAdd(*id, out int i);
        if (queue == null) return IntPtr.Zero;
        if (index != null) *index = i;
        return queue.Primary;
    }
}

/// <summary>IEventList: 常に空 (MIDI 等のイベントは扱わない)。</summary>
internal sealed unsafe class EventList : HostObject
{
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int>)&GetEventCount,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int>)&GetEvent,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)&AddEvent);

    public EventList() => Register(Tuids.IEventList, Vt);

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetEventCount(IntPtr self) => 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetEvent(IntPtr self, int index, IntPtr e) => Tresult.False;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int AddEvent(IntPtr self, IntPtr e) => Tresult.False;
}
