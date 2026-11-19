using System.IO.Pipes;

// 偽プラグインホスト: 本物のプラグインホストとして起動され (引数 = セッション id)、名前付きパイプでホストと話す。
// 動作モードは Load コマンドで渡されるプラグインパス文字列で指定する (環境変数を使わないので、テストを並列実行しても干渉しない)。
//
// ここでは HostProtocol の定数を参照せず、ワイヤ上のコマンド番号を直接書いている。
// 本体側の番号が意図せず変わったときに、このテストで検出できるようにするため。
const int CmdLoad = 1, CmdPrepare = 2, CmdGetState = 4, CmdExit = 6, CmdGetParameters = 7, CmdGetParameterValue = 8, CmdGetPrograms = 10, CmdGetLatency = 12, CmdCloseEditor = 13;

if (args.Length != 1) return 1;
string id = args[0];

using var pipe = new NamedPipeServerStream($"VstHost_{id}_pipe", PipeDirection.InOut, 1, PipeTransmissionMode.Byte);
pipe.WaitForConnection();
var reader = new BinaryReader(pipe);
var writer = new BinaryWriter(pipe);
string mode = "ok";

while (true)
{
    int cmd;
    try { cmd = reader.ReadInt32(); }
    catch (IOException) { return 0; }

    switch (cmd)
    {
        case CmdLoad:
            mode = reader.ReadString();
            reader.ReadInt32(); // sampleRate
            writer.Write(true);
            writer.Write("Fake");
            writer.Write(true);
            break;

        case CmdPrepare:
            reader.ReadInt32(); // sampleRate
            reader.ReadInt32(); // maxBlock (引数は応答の前に必ず読む。読まないと、同期型パイプで双方が書き込み待ちになる)
            writer.Write(true);
            break;

        case CmdGetParameters:
            switch (mode)
            {
                case "params_huge": writer.Write(2_000_000_000); break;
                case "params_over_limit": writer.Write(65537); break;
                case "params_negative": writer.Write(-5); break;
                case "param_default_nan": WriteOneParameter(0, double.NaN); break;
                case "param_default_infinity": WriteOneParameter(0, double.PositiveInfinity); break;
                case "param_default_out_of_range": WriteOneParameter(0, 1.5); break;
                case "param_default_negative": WriteOneParameter(0, -0.25); break;
                case "param_steps_negative": WriteOneParameter(-1, 0.5); break;
                case "param_steps_over_limit": WriteOneParameter(1_000_001, 0.5); break;
                case "params_total_over_budget":
                    // 個数も、文字列 1 つの長さも、上限内。合計だけが上限 (16 MiB) を超える (65,535 バイト × 1,000 個 = 約 64 MB)
                    writer.Write(1000);
                    for (int i = 0; i < 1000; i++) { writer.Write(new string('a', 65535)); writer.Write(""); writer.Write(0); writer.Write(0.5); }
                    break;
                case "params_many_ok":
                    // 多くて長めでも、合計が上限に収まる応答は受け入れられる (約 0.7 MB)
                    writer.Write(5000);
                    for (int i = 0; i < 5000; i++) { writer.Write(new string('p', 100)); writer.Write("dB"); writer.Write(i % 8); writer.Write(i % 2 == 0 ? 0.0 : 1.0); }
                    break;
                case "string_huge": writer.Write(1); writer.Write7BitEncodedInt(int.MaxValue); break;
                default: // "ok"
                    writer.Write(2);
                    writer.Write("Gain"); writer.Write("dB"); writer.Write(0); writer.Write(0.5);
                    writer.Write("Mode"); writer.Write(""); writer.Write(3); writer.Write(0.0);
                    break;
            }
            break;

        case CmdGetParameterValue:
            reader.ReadInt32(); // index (応答前に必ず読む。読まないと、同期型パイプで双方が書き込み待ちになる)
            writer.Write(mode switch
            {
                "value_nan" => double.NaN,
                "value_infinity" => double.PositiveInfinity,
                "value_out_of_range" => 1.5,
                "value_negative" => -0.1,
                _ => 0.25,
            });
            if (mode == "string_huge") writer.Write7BitEncodedInt(int.MaxValue);
            else writer.Write("-6.0 dB");
            break;

        case CmdGetState:
            switch (mode)
            {
                case "state_huge": writer.Write(2_000_000_000); break;
                case "state_over_limit": writer.Write(64 * 1024 * 1024 + 1); break;
                case "state_negative": writer.Write(-7); break;  // -1 だけが「状態なし」
                case "state_none": writer.Write(-1); break;
                case "state_hang": Thread.Sleep(Timeout.Infinite); break;   // 応答しないホスト (モーダルダイアログなどでメッセージスレッドが止まったプラグイン)
                default: writer.Write(3); writer.Write(new byte[] { 1, 2, 3 }); break;
            }
            break;

        case CmdGetPrograms:
            switch (mode)
            {
                case "programs_huge": writer.Write(0); writer.Write(1_000_000_000); break;
                case "programs_total_over_budget":
                    writer.Write(0); writer.Write(1000);
                    for (int i = 0; i < 1000; i++) writer.Write(new string('p', 65535));
                    break;
                case "programs_current_out_of_range":
                    writer.Write(99); writer.Write(3); writer.Write("a"); writer.Write("b"); writer.Write("c");
                    break;
                default: writer.Write(1); writer.Write(2); writer.Write("First"); writer.Write("Second"); break;
            }
            break;

        case CmdGetLatency:
            switch (mode)
            {
                case "latency_negative": writer.Write(-5); break;
                case "latency_huge": writer.Write(int.MaxValue); break;
                case "latency_over_limit": writer.Write(4_000_001); break;
                default: writer.Write(128); break;   // 128 サンプル
            }
            break;

        case CmdCloseEditor:
            if (mode == "close_hang") Thread.Sleep(Timeout.Infinite);   // 応答しないホスト (メッセージスレッドが止まって、パイプのコマンドも処理できない)
            writer.Write(true);
            break;

        case CmdExit:
            return 0;
    }
    writer.Flush();
}

void WriteOneParameter(int stepCount, double defaultValue)
{
    writer.Write(1);
    writer.Write("Gain"); writer.Write("dB"); writer.Write(stepCount); writer.Write(defaultValue);
}
