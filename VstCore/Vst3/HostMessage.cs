using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace VstCore.Vst3;

/// <summary>
/// IAttributeList (ivstattributes.h): キー (ASCII の const char*) と値 (整数・浮動小数・UTF-16 の文字列・バイナリ) の表。
/// プラグインが IHostApplication::createInstance で作り、IMessage の中身としても使う。複数のスレッドから触られうるので、lock で守る。
/// 値の上限 (<see cref="MaxKeys"/> / <see cref="MaxKeyLength"/> / <see cref="MaxValueBytes"/>) を超える set は、kResultFalse 相当の失敗にする (パイプ越しの値と同じく、相手を信用しない)。
/// 無いキー・型違いの get は kResultFalse。
/// </summary>
internal sealed unsafe class HostAttributeList : HostObject
{
    public const int MaxKeys = 1024;
    public const int MaxKeyLength = 256;
    public const int MaxValueBytes = 64 * 1024 * 1024;

    // 公式ヘッダの順: setInt, getInt, setFloat, getFloat, setString, getString, setBinary, getBinary (FUnknown の 3 つに続く)
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, long, int>)&SetInt,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, long*, int>)&GetInt,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, double, int>)&SetFloat,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, double*, int>)&GetFloat,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, char*, int>)&SetString,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, char*, uint, int>)&GetString,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, uint, int>)&SetBinary,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, byte**, uint*, int>)&GetBinary);

    enum Kind { Int, Float, String, Binary }

    sealed class Entry
    {
        public Kind Kind;
        public long Int;
        public double Float;
        public string? Text;
        public IntPtr Binary;       // ネイティブにコピーした値 (getBinary が、そのまま返す)
        public uint BinarySize;
    }

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _items = new(StringComparer.Ordinal);
    readonly List<IntPtr> _retired = new();     // 上書きされたバイナリ (プラグインが前のポインタを持っていても壊れないよう、解放まで残す)
    readonly bool _ownedByParent;

    /// <param name="ownedByParent">IMessage の中身なら true (メッセージが解放する。getAttributes は参照を増やさずに返すので、プラグインの Release の数え間違いで消えないようにする)。</param>
    public HostAttributeList(bool ownedByParent = false)
    {
        _ownedByParent = ownedByParent;
        Register(Tuids.IAttributeList, Vt);
    }

    protected override bool FreeOnLastRelease => !_ownedByParent;

    /// <summary>上書きされて、解放まで残してあるバイナリのバッファの数 (「前のポインタを壊さない」ことを、テストで決定的に確かめるため)。</summary>
    internal int RetiredBufferCount { get { lock (_gate) return _retired.Count; } }

    protected override void ReleaseResources()
    {
        lock (_gate)
        {
            foreach (var entry in _items.Values)
                if (entry.Binary != IntPtr.Zero) NativeMemory.Free((void*)entry.Binary);
            foreach (var ptr in _retired) NativeMemory.Free((void*)ptr);
            _items.Clear();
            _retired.Clear();
        }
    }

    static string? Key(byte* id)
    {
        if (id == null) return null;
        string key = Marshal.PtrToStringUTF8((IntPtr)id) ?? "";
        return key.Length is > 0 and <= MaxKeyLength ? key : null;
    }

    bool Put(string key, Entry entry)
    {
        lock (_gate)
        {
            if (_items.TryGetValue(key, out var old))
            {
                if (old.Binary != IntPtr.Zero) _retired.Add(old.Binary);
            }
            else if (_items.Count >= MaxKeys)
            {
                if (entry.Binary != IntPtr.Zero) NativeMemory.Free((void*)entry.Binary);
                return false;
            }
            _items[key] = entry;
            return true;
        }
    }

    Entry? Find(string key, Kind kind)
    {
        lock (_gate)
            return _items.TryGetValue(key, out var e) && e.Kind == kind ? e : null;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int SetInt(IntPtr self, byte* id, long value)
    {
        try
        {
            if (Key(id) is not { } key) return Tresult.InvalidArgument;
            return From<HostAttributeList>(self).Put(key, new Entry { Kind = Kind.Int, Int = value }) ? Tresult.Ok : Tresult.Fail;
        }
        catch { return Tresult.Fail; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetInt(IntPtr self, byte* id, long* value)
    {
        try
        {
            if (Key(id) is not { } key || value == null) return Tresult.InvalidArgument;
            if (From<HostAttributeList>(self).Find(key, Kind.Int) is not { } e) return Tresult.False;
            *value = e.Int;
            return Tresult.Ok;
        }
        catch { return Tresult.Fail; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int SetFloat(IntPtr self, byte* id, double value)
    {
        try
        {
            if (Key(id) is not { } key) return Tresult.InvalidArgument;
            return From<HostAttributeList>(self).Put(key, new Entry { Kind = Kind.Float, Float = value }) ? Tresult.Ok : Tresult.Fail;
        }
        catch { return Tresult.Fail; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetFloat(IntPtr self, byte* id, double* value)
    {
        try
        {
            if (Key(id) is not { } key || value == null) return Tresult.InvalidArgument;
            if (From<HostAttributeList>(self).Find(key, Kind.Float) is not { } e) return Tresult.False;
            *value = e.Float;
            return Tresult.Ok;
        }
        catch { return Tresult.Fail; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int SetString(IntPtr self, byte* id, char* text)
    {
        try
        {
            if (Key(id) is not { } key || text == null) return Tresult.InvalidArgument;
            string value = new(text);       // ヌル終端の UTF-16 (ヘッダ: must be null-terminated)
            if ((long)value.Length * 2 > MaxValueBytes) return Tresult.Fail;
            return From<HostAttributeList>(self).Put(key, new Entry { Kind = Kind.String, Text = value }) ? Tresult.Ok : Tresult.Fail;
        }
        catch { return Tresult.Fail; }
    }

    /// <summary>サイズはバイト数 (ヘッダ: 文字数ではない)。収まらないときは、ヌル終端を含めて収まる長さで切り詰める。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetString(IntPtr self, byte* id, char* buffer, uint sizeInBytes)
    {
        try
        {
            if (Key(id) is not { } key || buffer == null || sizeInBytes < sizeof(char)) return Tresult.InvalidArgument;
            if (From<HostAttributeList>(self).Find(key, Kind.String) is not { } e) return Tresult.False;
            string text = e.Text ?? "";
            int capacity = (int)(sizeInBytes / sizeof(char)) - 1;     // ヌル終端の分を引く
            int n = Math.Min(text.Length, capacity);
            text.AsSpan(0, n).CopyTo(new Span<char>(buffer, n));
            buffer[n] = '\0';
            return Tresult.Ok;
        }
        catch { return Tresult.Fail; }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int SetBinary(IntPtr self, byte* id, byte* data, uint sizeInBytes)
    {
        byte* copy = null;
        try
        {
            if (Key(id) is not { } key || (data == null && sizeInBytes > 0)) return Tresult.InvalidArgument;
            if (sizeInBytes > MaxValueBytes) return Tresult.Fail;
            copy = (byte*)NativeMemory.Alloc(Math.Max(sizeInBytes, 1u));        // 大きさ 0 でも、null でないポインタを返せるように
            if (sizeInBytes > 0) Buffer.MemoryCopy(data, copy, sizeInBytes, sizeInBytes);
            var entry = new Entry { Kind = Kind.Binary, Binary = (IntPtr)copy, BinarySize = sizeInBytes };
            copy = null;                                                        // 所有は表に移った (Put が失敗しても、Put の中で解放する)
            return From<HostAttributeList>(self).Put(key, entry) ? Tresult.Ok : Tresult.Fail;
        }
        catch { return Tresult.Fail; }
        finally
        {
            if (copy != null) NativeMemory.Free(copy);
        }
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static int GetBinary(IntPtr self, byte* id, byte** data, uint* sizeInBytes)
    {
        try
        {
            if (Key(id) is not { } key || data == null || sizeInBytes == null) return Tresult.InvalidArgument;
            if (From<HostAttributeList>(self).Find(key, Kind.Binary) is not { } e) return Tresult.False;
            *data = (byte*)e.Binary;
            *sizeInBytes = e.BinarySize;
            return Tresult.Ok;
        }
        catch { return Tresult.Fail; }
    }
}

/// <summary>
/// IMessage (ivstmessage.h): メッセージ ID と、属性の表 (<see cref="HostAttributeList"/>)。
/// コンポーネントとコントローラの間で、プラグインが IConnectionPoint::notify に渡す。ホストは作って渡すだけで、配送には関わらない
/// (コンポーネントとコントローラは直接つないである)。
/// </summary>
internal sealed unsafe class HostMessage : HostObject
{
    public const int MaxIdLength = 256;

    // 公式ヘッダの順: getMessageID, setMessageID, getAttributes (FUnknown の 3 つに続く)
    static readonly IntPtr* Vt = Vtbl.Create(
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*>)&GetMessageId,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)&SetMessageId,
        (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)&GetAttributes);

    readonly object _gate = new();
    readonly HostAttributeList _attributes = new(ownedByParent: true);
    readonly List<IntPtr> _ids = new();         // これまでの ID の文字列 (前のポインタを持つプラグインが壊れないよう、解放まで残す)
    byte* _id;

    public HostMessage()
    {
        _id = NewId("");                        // 未設定のとき "" を返す (strcmp に null を渡して落ちるプラグインを避ける)
        Register(Tuids.IMessage, Vt);
    }

    protected override bool FreeOnLastRelease => true;

    byte* NewId(string id)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(id);
        var mem = (byte*)NativeMemory.Alloc((nuint)bytes.Length + 1);
        bytes.AsSpan().CopyTo(new Span<byte>(mem, bytes.Length));
        mem[bytes.Length] = 0;
        _ids.Add((IntPtr)mem);
        return mem;
    }

    protected override void ReleaseResources()
    {
        lock (_gate)
        {
            foreach (var ptr in _ids) NativeMemory.Free((void*)ptr);
            _ids.Clear();
            _id = null;
        }
        _attributes.Dispose();
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static byte* GetMessageId(IntPtr self)
    {
        var m = From<HostMessage>(self);
        lock (m._gate) return m._id;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static void SetMessageId(IntPtr self, byte* id)
    {
        try
        {
            string text = id == null ? "" : Marshal.PtrToStringUTF8((IntPtr)id) ?? "";
            if (text.Length > MaxIdLength) text = text[..MaxIdLength];
            var m = From<HostMessage>(self);
            lock (m._gate) m._id = m.NewId(text);
        }
        catch { /* 戻り値が無いので、失敗は無視する (ID は前のまま) */ }
    }

    /// <summary>この属性を、参照を増やさずに返す (ヘッダの規則。寿命はメッセージと同じ)。</summary>
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvStdcall) })]
    static IntPtr GetAttributes(IntPtr self) => From<HostMessage>(self)._attributes.Primary;
}