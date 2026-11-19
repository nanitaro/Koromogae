using System.Text;
using VstCore.Vst3;
using Xunit;

namespace PluginHost.Tests;

/// <summary>
/// ホスト側の IMessage / IAttributeList (<see cref="HostMessage"/> / <see cref="HostAttributeList"/>) と、IHostApplication::createInstance。
/// プラグインが呼ぶのと同じに、vtable のスロットを関数ポインタで直接呼んで確かめる (実プラグインは使わない。手元の VST3 は、どれもメッセージを要求しなかった)。
/// スロット番号と IID は、Steinberg の公式ヘッダ (ivstmessage.h / ivstattributes.h / ivsthostapplication.h) どおり。
/// 解放の確認が <see cref="HostObject.LiveObjects"/> (静的な数) に頼るので、このクラスの中は直列に実行される。
/// </summary>
public unsafe class HostMessageTests
{
    const int Ok = 0, False = 1;
    static readonly int NoInterface = unchecked((int)0x80004002);
    static readonly int NotImplemented = unchecked((int)0x80004001);
    static readonly int InvalidArgument = unchecked((int)0x80070057);
    static readonly int Fail = unchecked((int)0x80004005);

    // ---- vtable の呼び出し (プラグインと同じ) ----

    static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];
    static uint AddRef(IntPtr o) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(o, 1))(o);
    static uint Release(IntPtr o) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(o, 2))(o);

    static int Query(IntPtr o, Guid iid, out IntPtr result)
    {
        IntPtr r;
        int rc = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(o, 0))(o, &iid, &r);
        result = r;
        return rc;
    }

    /// <summary>IHostApplication の createInstance (スロット 4: FUnknown の 3 つ + getName)。</summary>
    static int CreateInstance(HostApplication app, Guid cid, Guid iid, out IntPtr obj)
    {
        IntPtr p = app.Primary, r = (IntPtr)0x1234;       // 失敗時に null へ直されることも確かめる
        int rc = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(p, 4))(p, &cid, &iid, &r);
        obj = r;
        return rc;
    }

    static byte[] Z(string text) => Encoding.UTF8.GetBytes(text + "\0");

    static string AsciiAt(byte* p) => System.Runtime.InteropServices.Marshal.PtrToStringUTF8((IntPtr)p) ?? "<null>";

    /// <summary>IAttributeList (スロット 3〜10)。</summary>
    sealed class Attrs
    {
        public readonly IntPtr P;
        public Attrs(IntPtr p) => P = p;

        public int SetInt(string key, long v) { fixed (byte* k = Z(key)) return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, long, int>)Slot(P, 3))(P, k, v); }
        public int GetInt(string key, out long v) { long r = -1; int rc; fixed (byte* k = Z(key)) rc = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, long*, int>)Slot(P, 4))(P, k, &r); v = r; return rc; }
        public int SetFloat(string key, double v) { fixed (byte* k = Z(key)) return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, double, int>)Slot(P, 5))(P, k, v); }
        public int GetFloat(string key, out double v) { double r = double.NaN; int rc; fixed (byte* k = Z(key)) rc = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, double*, int>)Slot(P, 6))(P, k, &r); v = r; return rc; }
        public int SetString(string key, string v) { fixed (byte* k = Z(key)) fixed (char* s = v + "\0") return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, char*, int>)Slot(P, 7))(P, k, s); }

        /// <summary>バッファの大きさは「バイト数」で渡す (ヘッダ: 文字数ではない)。</summary>
        public int GetString(string key, int bufferChars, out string v, out char[] raw)
        {
            raw = Enumerable.Repeat('#', bufferChars).ToArray();
            int rc;
            fixed (byte* k = Z(key)) fixed (char* b = raw) rc = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, char*, uint, int>)Slot(P, 8))(P, k, b, (uint)(bufferChars * sizeof(char)));
            int end = Array.IndexOf(raw, '\0');
            v = end < 0 ? new string(raw) : new string(raw, 0, end);
            return rc;
        }

        public int SetBinary(string key, byte[] data) { fixed (byte* k = Z(key)) fixed (byte* d = data.Length == 0 ? new byte[1] : data) return ((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, uint, int>)Slot(P, 9))(P, k, d, (uint)data.Length); }
        public int GetBinary(string key, out IntPtr ptr, out uint size) { byte* d = null; uint s = 0; int rc; fixed (byte* k = Z(key)) rc = ((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte**, uint*, int>)Slot(P, 10))(P, k, &d, &s); ptr = (IntPtr)d; size = s; return rc; }
    }

    /// <summary>IMessage (スロット 3〜5)。</summary>
    sealed class Message
    {
        public readonly IntPtr P;
        public Message(IntPtr p) => P = p;
        public byte* GetId() => ((delegate* unmanaged[Stdcall]<IntPtr, byte*>)Slot(P, 3))(P);
        public void SetId(string id) { fixed (byte* b = Z(id)) ((delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)Slot(P, 4))(P, b); }
        public Attrs Attributes() => new(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr>)Slot(P, 5))(P));
    }

    static (HostApplication App, Message Msg) NewMessage()
    {
        var app = new HostApplication();
        Assert.Equal(Ok, CreateInstance(app, Tuids.IMessage, Tuids.IMessage, out var obj));
        return (app, new Message(obj));
    }

    // ---- IID ----

    [Fact]
    public void The_interface_ids_match_the_official_headers()
    {
        // ivstmessage.h:    DECLARE_CLASS_IID (IMessage,       0x936F033B, 0xC6C047DB, 0xBB0882F8, 0x13C1E613)
        // ivstattributes.h: DECLARE_CLASS_IID (IAttributeList, 0x1E5F0AEB, 0xCC7F4533, 0xA2544011, 0x38AD5EE4)
        Assert.Equal(new Guid("936F033B-C6C0-47DB-BB08-82F813C1E613"), Tuids.IMessage);
        Assert.Equal(new Guid("1E5F0AEB-CC7F-4533-A254-401138AD5EE4"), Tuids.IAttributeList);
    }

    // ---- createInstance ----

    [Fact]
    public void CreateInstance_makes_a_message_and_an_attribute_list_and_hands_over_the_reference()
    {
        int before = HostObject.LiveObjects;
        var app = new HostApplication();

        Assert.Equal(Ok, CreateInstance(app, Tuids.IMessage, Tuids.IMessage, out var m));
        Assert.Equal(Ok, CreateInstance(app, Tuids.IAttributeList, Tuids.IAttributeList, out var a));
        Assert.NotEqual(IntPtr.Zero, m);
        Assert.NotEqual(IntPtr.Zero, a);

        Assert.Equal(1u, AddRef(m) - 1);      // 作った側の参照 (1) を、そのままプラグインへ渡す (AddRef の結果は 2)
        Release(m);
        Release(m);                           // プラグインの Release で、解放される
        Release(a);
        app.Dispose();
        Assert.Equal(before, HostObject.LiveObjects);
    }

    [Fact]
    public void CreateInstance_accepts_FUnknown_as_the_requested_interface_too()
    {
        var app = new HostApplication();
        Assert.Equal(Ok, CreateInstance(app, Tuids.IMessage, Tuids.FUnknown, out var m));
        Assert.NotEqual(IntPtr.Zero, m);
        Release(m);
        app.Dispose();
    }

    [Fact]
    public void CreateInstance_refuses_an_unknown_class_and_a_mismatched_interface_without_leaking()
    {
        int before = HostObject.LiveObjects;
        var app = new HostApplication();

        Assert.Equal(NotImplemented, CreateInstance(app, Guid.NewGuid(), Tuids.IMessage, out var unknown));
        Assert.Equal(IntPtr.Zero, unknown);                                                                // 失敗のときは、出力を null にする
        Assert.Equal(NoInterface, CreateInstance(app, Tuids.IMessage, Tuids.IAttributeList, out var mismatch));
        Assert.Equal(IntPtr.Zero, mismatch);                                                               // メッセージは、IAttributeList としては渡さない

        app.Dispose();
        Assert.Equal(before, HostObject.LiveObjects);                                                      // 作りかけを残さない
    }

    [Fact]
    public void CreateInstance_rejects_null_arguments()
    {
        var app = new HostApplication();
        IntPtr p = app.Primary;
        Guid g = Tuids.IMessage;
        IntPtr r = 0x1;

        Assert.Equal(InvalidArgument, ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(p, 4))(p, null, &g, &r));
        Assert.Equal(InvalidArgument, ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(p, 4))(p, &g, null, &r));
        Assert.Equal(InvalidArgument, ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, IntPtr*, int>)Slot(p, 4))(p, &g, &g, null));
        app.Dispose();
    }

    // ---- IMessage ----

    [Fact]
    public void A_new_message_has_an_empty_id_not_null_and_the_id_round_trips()
    {
        var (app, m) = NewMessage();

        Assert.Equal("", AsciiAt(m.GetId()));          // null だと strcmp で落ちるプラグインがある
        m.SetId("TextMessage");
        Assert.Equal("TextMessage", AsciiAt(m.GetId()));

        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void Changing_the_id_keeps_the_previous_pointer_readable()
    {
        var (app, m) = NewMessage();
        m.SetId("First");
        byte* first = m.GetId();

        m.SetId("Second");

        Assert.Equal("First", AsciiAt(first));         // 前のポインタを持つプラグインが、壊れない
        Assert.Equal("Second", AsciiAt(m.GetId()));
        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void A_very_long_id_is_cut_and_a_null_id_means_empty()
    {
        var (app, m) = NewMessage();

        m.SetId(new string('x', 5000));
        Assert.Equal(HostMessage.MaxIdLength, AsciiAt(m.GetId()).Length);

        ((delegate* unmanaged[Stdcall]<IntPtr, byte*, void>)Slot(m.P, 4))(m.P, null);
        Assert.Equal("", AsciiAt(m.GetId()));
        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void GetAttributes_returns_the_same_list_each_time_without_adding_a_reference()
    {
        var (app, m) = NewMessage();

        var first = m.Attributes();
        var second = m.Attributes();

        Assert.Equal(first.P, second.P);
        Assert.Equal(2u, AddRef(first.P));              // getAttributes は参照を増やさない (増やしていれば 3 以上になる)
        Assert.Equal(1u, Release(first.P));
        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void The_message_answers_IMessage_and_FUnknown_but_not_IAttributeList()
    {
        var (app, m) = NewMessage();

        Assert.Equal(Ok, Query(m.P, Tuids.IMessage, out var viaMessage));
        Assert.Equal(Ok, Query(m.P, Tuids.FUnknown, out var viaUnknown));
        Assert.Equal(NoInterface, Query(m.P, Tuids.IAttributeList, out var viaAttributes));
        Assert.Equal(IntPtr.Zero, viaAttributes);
        Assert.NotEqual(IntPtr.Zero, viaMessage);
        Assert.NotEqual(IntPtr.Zero, viaUnknown);

        Release(viaMessage);
        Release(viaUnknown);
        Release(m.P);
        app.Dispose();
    }

    // ---- IAttributeList ----

    [Fact]
    public void Every_kind_of_value_round_trips()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();
        byte[] blob = { 0, 1, 2, 255, 128, 7 };

        Assert.Equal(Ok, a.SetInt("count", long.MinValue + 5));
        Assert.Equal(Ok, a.SetFloat("level", -0.123456789012345));
        Assert.Equal(Ok, a.SetString("title", "メーター: 音量 ♪ 123"));
        Assert.Equal(Ok, a.SetBinary("data", blob));

        Assert.Equal(Ok, a.GetInt("count", out long i));
        Assert.Equal(long.MinValue + 5, i);
        Assert.Equal(Ok, a.GetFloat("level", out double f));
        Assert.Equal(-0.123456789012345, f);
        Assert.Equal(Ok, a.GetString("title", 64, out string s, out _));
        Assert.Equal("メーター: 音量 ♪ 123", s);
        Assert.Equal(Ok, a.GetBinary("data", out IntPtr p, out uint size));
        Assert.Equal(blob.Length, (int)size);
        Assert.Equal(blob, new ReadOnlySpan<byte>((void*)p, (int)size).ToArray());

        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void A_missing_key_or_a_value_of_another_type_gives_false_and_leaves_the_output_alone()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();
        a.SetInt("n", 5);
        a.SetString("s", "text");

        Assert.Equal(False, a.GetInt("nothing", out long missing));
        Assert.Equal(-1, missing);
        Assert.Equal(False, a.GetFloat("n", out double wrong));          // 整数として入れた値を、浮動小数としては読めない
        Assert.True(double.IsNaN(wrong));
        Assert.Equal(False, a.GetInt("s", out _));
        Assert.Equal(False, a.GetBinary("s", out IntPtr noPtr, out _));
        Assert.Equal(IntPtr.Zero, noPtr);
        Assert.Equal(False, a.GetString("n", 8, out _, out _));

        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void Setting_a_key_again_replaces_it_even_with_another_type()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();
        a.SetInt("k", 1);

        a.SetString("k", "now text");

        Assert.Equal(False, a.GetInt("k", out _));
        Assert.Equal(Ok, a.GetString("k", 32, out string s, out _));
        Assert.Equal("now text", s);
        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void A_string_is_cut_to_the_buffer_given_in_bytes_and_always_null_terminated()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();
        a.SetString("t", "ABCDEFGH");

        Assert.Equal(Ok, a.GetString("t", 4, out string cut, out char[] raw));           // 4 文字ぶん (8 バイト) のバッファ = 3 文字 + ヌル
        Assert.Equal("ABC", cut);
        Assert.Equal('\0', raw[3]);

        Assert.Equal(Ok, a.GetString("t", 9, out string exact, out _));                  // ちょうど収まる (8 文字 + ヌル)
        Assert.Equal("ABCDEFGH", exact);

        Assert.Equal(Ok, a.GetString("t", 1, out string none, out char[] raw1));         // ヌルだけ
        Assert.Equal("", none);
        Assert.Equal('\0', raw1[0]);
        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void An_empty_string_and_an_empty_binary_are_valid_values()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();

        Assert.Equal(Ok, a.SetString("e", ""));
        Assert.Equal(Ok, a.GetString("e", 4, out string s, out _));
        Assert.Equal("", s);

        Assert.Equal(Ok, a.SetBinary("b", Array.Empty<byte>()));
        Assert.Equal(Ok, a.GetBinary("b", out IntPtr p, out uint size));
        Assert.Equal(0u, size);
        Assert.NotEqual(IntPtr.Zero, p);                  // 大きさ 0 でも、null を返さない

        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void Replacing_a_binary_keeps_the_previous_pointer_readable_until_the_message_is_released()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();
        byte[] first = Enumerable.Range(0, 64).Select(i => (byte)(i + 1)).ToArray();
        a.SetBinary("blob", first);
        a.GetBinary("blob", out IntPtr oldPtr, out _);

        // 同じ大きさで何度も上書きする。前のバッファを先に解放していると、そのメモリが次の値に再利用されて、前のポインタの中身が変わる
        for (int round = 0; round < 5; round++)
            a.SetBinary("blob", Enumerable.Repeat((byte)(0xA0 + round), 64).ToArray());

        Assert.Equal(first, new ReadOnlySpan<byte>((void*)oldPtr, 64).ToArray());                 // 前のポインタは壊れない
        Assert.Equal(5, HostObject.From<HostAttributeList>(a.P).RetiredBufferCount);              // 上書きした 5 つぶんの前のバッファを、メッセージが解放されるまで残している (即解放していれば 0)
        a.GetBinary("blob", out IntPtr newPtr, out uint size);
        Assert.Equal(64u, size);
        Assert.NotEqual(oldPtr, newPtr);
        Assert.Equal(Enumerable.Repeat((byte)0xA4, 64).ToArray(), new ReadOnlySpan<byte>((void*)newPtr, 64).ToArray());
        Release(m.P);
        app.Dispose();
    }
    [Fact]
    public void Bad_arguments_and_the_limits_are_refused_without_harm()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();

        Assert.Equal(InvalidArgument, a.SetInt("", 1));                                              // 空のキー
        Assert.Equal(InvalidArgument, a.SetInt(new string('k', HostAttributeList.MaxKeyLength + 1), 1));   // 長すぎるキー
        Assert.Equal(InvalidArgument, ((delegate* unmanaged[Stdcall]<IntPtr, byte*, long, int>)Slot(a.P, 3))(a.P, null, 1));   // null のキー
        Assert.Equal(InvalidArgument, ((delegate* unmanaged[Stdcall]<IntPtr, byte*, long*, int>)Slot(a.P, 4))(a.P, null, null));        // null のキーと null の出力

        for (int i = 0; i < HostAttributeList.MaxKeys; i++) Assert.Equal(Ok, a.SetInt("key" + i, i));
        Assert.Equal(Fail, a.SetInt("one-too-many", 1));                                              // キーの数の上限
        Assert.Equal(Fail, a.SetBinary("also-too-many", new byte[] { 1 }));                           // 上限のときの binary も、漏らさない
        Assert.Equal(Ok, a.SetInt("key0", 99));                                                       // 既存のキーの上書きは、上限でもできる
        Assert.Equal(Ok, a.GetInt("key0", out long v));
        Assert.Equal(99, v);

        byte one = 1;
        byte* k = null;
        fixed (byte* key = Z("huge"))
        {
            k = key;
            Assert.Equal(Fail, ((delegate* unmanaged[Stdcall]<IntPtr, byte*, byte*, uint, int>)Slot(a.P, 9))(a.P, k, &one, (uint)HostAttributeList.MaxValueBytes + 1));   // 大きすぎる値
        }
        Release(m.P);
        app.Dispose();
    }

    // ---- 解放 ----

    [Fact]
    public void The_last_release_frees_the_message_and_its_attributes_together()
    {
        int before = HostObject.LiveObjects;
        var (app, m) = NewMessage();
        m.Attributes().SetBinary("b", new byte[100]);
        Assert.Equal(before + 1 /*app*/ + 2 /*メッセージ + 属性*/, HostObject.LiveObjects);

        Assert.Equal(1u, AddRef(m.P) - 1);
        Assert.Equal(1u, Release(m.P));                              // まだ 1 つ持っている人がいる
        Assert.Equal(before + 3, HostObject.LiveObjects);
        Assert.Equal(0u, Release(m.P));                              // 最後の Release で解放
        Assert.Equal(before + 1, HostObject.LiveObjects);            // app だけが残る
        app.Dispose();
        Assert.Equal(before, HostObject.LiveObjects);
    }

    [Fact]
    public void Releasing_the_attribute_list_of_a_message_one_time_too_many_does_not_free_it_early()
    {
        // getAttributes は参照を増やさないので、プラグインが数え間違えて Release しても、メッセージの寿命の間は残す
        var (app, m) = NewMessage();
        var a = m.Attributes();
        a.SetInt("n", 7);

        Release(a.P);
        Release(a.P);

        Assert.Equal(Ok, a.GetInt("n", out long v));
        Assert.Equal(7, v);
        Release(m.P);
        app.Dispose();
    }

    [Fact]
    public void A_standalone_attribute_list_is_freed_by_its_own_last_release()
    {
        int before = HostObject.LiveObjects;
        var app = new HostApplication();
        CreateInstance(app, Tuids.IAttributeList, Tuids.IAttributeList, out var p);
        new Attrs(p).SetBinary("b", new byte[10]);

        Assert.Equal(before + 2, HostObject.LiveObjects);
        Assert.Equal(0u, Release(p));
        Assert.Equal(before + 1, HostObject.LiveObjects);
        app.Dispose();
    }

    [Fact]
    public void Creating_and_releasing_many_messages_does_not_grow_the_live_object_count()
    {
        int before = HostObject.LiveObjects;
        var app = new HostApplication();

        for (int i = 0; i < 10_000; i++)
        {
            Assert.Equal(Ok, CreateInstance(app, Tuids.IMessage, Tuids.IMessage, out var obj));
            var m = new Message(obj);
            m.SetId("Meter");
            var a = m.Attributes();
            a.SetInt("i", i);
            a.SetString("s", "x");
            a.SetBinary("b", new byte[64]);
            Release(obj);
        }

        Assert.Equal(before + 1, HostObject.LiveObjects);            // app だけ
        app.Dispose();
        Assert.Equal(before, HostObject.LiveObjects);
    }

    [Fact]
    public void Objects_the_plugin_does_not_own_are_not_freed_by_release_and_dispose_is_safe_to_repeat()
    {
        // 既存の方針: IHostApplication などは、Vst3Plugin の寿命と同じ。余分な Release で壊れない
        int before = HostObject.LiveObjects;
        var app = new HostApplication();

        for (int i = 0; i < 5; i++) Release(app.Primary);
        Assert.Equal(Ok, Query(app.Primary, Tuids.IHostApplication, out var again));      // まだ使える
        Release(again);
        Assert.Equal(before + 1, HostObject.LiveObjects);

        app.Dispose();
        app.Dispose();                                                                    // 2 回呼んでも、数が狂わない
        Assert.Equal(before, HostObject.LiveObjects);
    }

    // ---- 並行 ----

    [Fact]
    public void Several_threads_can_set_and_get_the_same_attribute_list()
    {
        var (app, m) = NewMessage();
        var a = m.Attributes();
        var errors = new System.Collections.Concurrent.ConcurrentBag<string>();

        Parallel.For(0, 8, t =>
        {
            for (int i = 0; i < 2_000; i++)
            {
                if (i < 100 && a.SetInt($"u{t}_{i}", i) != Ok) errors.Add("set unique");     // 新しいキーの追加どうしを競合させる (表の拡張)
                string key = "k" + (i % 50);
                if (a.SetInt(key, t * 10_000 + i) != Ok) errors.Add("set");
                int rc = a.GetInt(key, out long v);
                if (rc != Ok && rc != False) errors.Add("get rc " + rc);
                if (rc == Ok && (v < 0 || v >= 80_000)) errors.Add("value " + v);
                if (a.SetString("s" + (i % 20), "テキスト" + i) != Ok) errors.Add("set string");
                a.GetString("s" + (i % 20), 32, out _, out _);
                if (a.SetBinary("b" + (i % 10), new byte[(i % 7) + 1]) != Ok) errors.Add("set binary");
                a.GetBinary("b" + (i % 10), out _, out _);
            }
        });

        Assert.Empty(errors);
        Release(m.P);
        app.Dispose();
    }

    // ---- ログ ----

    [Fact]
    public void Each_request_is_logged_once_per_class_and_interface_with_the_owner()
    {
        // 診断用: プラグインが何を要求したか (特に未対応のもの) を、app.log から分かるようにする。連発するプラグインでログを溢れさせない
        string dir = Path.Combine(Path.GetTempPath(), "KoromogaeHostLog_" + Guid.NewGuid().ToString("N")[..8]);
        string? previous = Environment.GetEnvironmentVariable("KOROMOGAE_DATA_DIR");
        Environment.SetEnvironmentVariable("KOROMOGAE_DATA_DIR", dir);
        try
        {
            var app = new HostApplication { Owner = @"C:\vst\Some Plugin.vst3" };
            Guid unknown = Guid.Parse("11111111-2222-3333-4444-555555555555");

            for (int i = 0; i < 3; i++)
            {
                CreateInstance(app, Tuids.IMessage, Tuids.IMessage, out var m);
                Release(m);
                CreateInstance(app, unknown, Tuids.IMessage, out _);
            }
            app.Dispose();

            string log = File.ReadAllText(Path.Combine(dir, "app.log"));
            var lines = log.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            Assert.Equal(2, lines.Length);                                                          // 3 回ずつ要求しても、組ごとに 1 行
            Assert.Single(lines, l => l.Contains("IMessage") && l.Contains("作成"));
            Assert.Single(lines, l => l.Contains("(未知)") && l.Contains("未対応") && l.Contains(unknown.ToString()));
            Assert.All(lines, l => Assert.Contains(@"C:\vst\Some Plugin.vst3", l));                 // どのプラグインか分かる
            Assert.All(lines, l => Assert.Contains("[INFO]", l));
        }
        finally
        {
            Environment.SetEnvironmentVariable("KOROMOGAE_DATA_DIR", previous);
            try { Directory.Delete(dir, recursive: true); } catch { /* ログが残っても構わない */ }
        }
    }
}