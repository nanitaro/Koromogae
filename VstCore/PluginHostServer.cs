using System.IO.MemoryMappedFiles;
using System.IO.Pipes;

namespace VstCore;

/// <summary>プラグインホストプロセス側 (32bit / 64bit)。1 プロセスにつき 1 プラグインをホストする。</summary>
public static unsafe class PluginHostServer
{
    public static int Run(string id)
    {
        using var pipe = new NamedPipeServerStream(HostProtocol.PipeName(id), PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.CurrentUserOnly);
        using var mmf = MemoryMappedFile.OpenExisting(HostProtocol.MemoryName(id));
        using var view = mmf.CreateViewAccessor(0, HostProtocol.MemorySize);
        using var req = new EventWaitHandle(false, EventResetMode.AutoReset, HostProtocol.RequestName(id));
        using var ack = new EventWaitHandle(false, EventResetMode.AutoReset, HostProtocol.AckName(id));

        byte* basePtr = null;
        view.SafeMemoryMappedViewHandle.AcquirePointer(ref basePtr);
        basePtr += view.PointerOffset;

        pipe.WaitForConnection();
        using var reader = new BinaryReader(pipe);
        using var writer = new BinaryWriter(pipe);

        ILocalPlugin? plugin = null;
        Thread? audioThread = null;
        int stop = 0;
        int exitCode = 0;

        try
        {
            while (true)
            {
                int cmd;
                try { cmd = reader.ReadInt32(); }
                catch (EndOfStreamException) { break; }
                catch (IOException) { break; }

                if (cmd == HostProtocol.CmdExit) break;

                // 各コマンドの引数は検証してから使う。相手の切断 (IOException) は正常終了、不正な値は接続ごと打ち切る
                try
                {
                switch (cmd)
                {
                    case HostProtocol.CmdLoad:
                        {
                            if (plugin != null) throw new HostProtocolException("Load が 2 回送られました");
                            string path = HostProtocol.ReadString(reader, "プラグインのパス");
                            int sampleRate = HostProtocol.Check(reader.ReadInt32(), HostProtocol.MinSampleRate, HostProtocol.MaxSampleRate, "サンプルレート");
                            try
                            {
                                plugin = PluginFactory.LoadLocal(path, sampleRate);
                                var p = plugin;
                                IntPtr b = (IntPtr)basePtr;
                                audioThread = new Thread(() => AudioLoop(p, (byte*)b, req, ack, () => Volatile.Read(ref stop) != 0))
                                {
                                    IsBackground = true,
                                    Name = "Plugin Audio",
                                    Priority = ThreadPriority.Highest,
                                };
                                audioThread.Start();

                                writer.Write(true);
                                writer.Write(plugin.Name);
                                writer.Write(plugin.HasEditor);
                            }
                            catch (Exception ex)
                            {
                                writer.Write(false);
                                writer.Write(ex.Message);
                            }
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdPrepare:
                        {
                            int sampleRate = HostProtocol.Check(reader.ReadInt32(), HostProtocol.MinSampleRate, HostProtocol.MaxSampleRate, "サンプルレート");
                            int block = HostProtocol.Check(reader.ReadInt32(), 1, PluginLimits.MaxBlock, "ブロックサイズ");
                            plugin?.Prepare(sampleRate, block);
                            writer.Write(true);
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdGetParameters:
                        {
                            // 受け取る側が拒否しない形に整えて送る (個数・文字列の長さ・文字列の合計・値の範囲。超える分は送らない)
                            var infos = plugin?.GetParameters() ?? Array.Empty<ParameterInfo>();
                            var budget = new ResponseBudget();
                            var send = new List<(string Name, string Unit, int Steps, double Default)>();
                            foreach (var p in infos.Take(HostProtocol.MaxItemCount))
                            {
                                string name = HostProtocol.Clip(p.Name), unit = HostProtocol.Clip(p.Unit);
                                if (!budget.TryTake(HostProtocol.Cost(name) + HostProtocol.Cost(unit))) break;
                                send.Add((name, unit, HostProtocol.SanitizeStepCount(p.StepCount), HostProtocol.SanitizeUnit(p.DefaultValue)));
                            }
                            writer.Write(send.Count);
                            foreach (var p in send)
                            {
                                writer.Write(p.Name);
                                writer.Write(p.Unit);
                                writer.Write(p.Steps);
                                writer.Write(p.Default);
                            }
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdGetParameterValue:
                        {
                            // 引数は必ず先に読む (plugin?.X(読み取り) だと、未ロード時に読み取りが実行されず、双方が書き込み待ちで固まる)
                            int index = HostProtocol.ReadBounded(reader, HostProtocol.MaxItemCount - 1, "パラメータ番号");
                            var value = plugin?.GetParameterValue(index) ?? default;
                            writer.Write(HostProtocol.SanitizeUnit(value.Normalized));
                            writer.Write(HostProtocol.Clip(value.Display));
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdSetParameter:
                        {
                            int index = HostProtocol.ReadBounded(reader, HostProtocol.MaxItemCount - 1, "パラメータ番号");
                            double value = reader.ReadDouble();
                            if (!double.IsFinite(value)) throw new HostProtocolException("パラメータ値が有限の数ではありません");
                            plugin?.SetParameter(index, value);
                            writer.Write(true);
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdGetPrograms:
                        {
                            var list = plugin?.GetPrograms() ?? new ProgramList(Array.Empty<string>(), 0);
                            var budget = new ResponseBudget();
                            var names = new List<string>();
                            foreach (string raw in list.Names.Take(HostProtocol.MaxItemCount))
                            {
                                string name = HostProtocol.Clip(raw);
                                if (!budget.TryTake(HostProtocol.Cost(name))) break;     // 合計の上限を超える分は送らない
                                names.Add(name);
                            }
                            writer.Write(names.Count == 0 ? 0 : Math.Clamp(list.Current, 0, names.Count - 1));
                            writer.Write(names.Count);
                            foreach (string name in names) writer.Write(name);
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdSetProgram:
                    {
                        int program = HostProtocol.ReadBounded(reader, HostProtocol.MaxItemCount - 1, "プログラム番号");
                        plugin?.SetProgram(program);
                        writer.Write(true);
                        writer.Flush();
                        break;
                    }
                    case HostProtocol.CmdGetLatency:
                        writer.Write(plugin?.LatencySamples ?? 0);
                        writer.Flush();
                        break;
                    case HostProtocol.CmdOpenEditor:
                        plugin?.OpenEditor();
                        writer.Write(true);
                        writer.Flush();
                        break;
                    case HostProtocol.CmdCloseEditor:
                        plugin?.CloseEditor();
                        writer.Write(true);
                        writer.Flush();
                        break;
                    case HostProtocol.CmdGetState:
                        {
                            byte[]? state = plugin?.GetState();
                            // 上限を超える状態は、相手に拒否されて接続が切れるより、状態なし(-1)として返す
                            if (state != null && state.Length > HostProtocol.MaxStateBytes) state = null;
                            writer.Write(state?.Length ?? -1);
                            if (state != null) writer.Write(state);
                            writer.Flush();
                            break;
                        }
                    case HostProtocol.CmdSetState:
                        {
                            int len = HostProtocol.ReadBounded(reader, HostProtocol.MaxStateBytes, "状態データ長");
                            byte[] data = HostProtocol.ReadBlob(reader, len);
                            plugin?.SetState(data);
                            writer.Write(true);
                            writer.Flush();
                            break;
                        }
                    default:
                        throw new HostProtocolException($"未知のコマンド番号です: {cmd}");
                }
                }
                catch (IOException)
                {
                    break; // 相手が切断した
                }
            }
        }
        catch (Exception ex) when (ex is HostProtocolException or FormatException)
        {
            exitCode = 3; // 不正な値を受け取った。ストリームの同期が取れないのでプロセスごと終了する
        }
        finally
        {
            Volatile.Write(ref stop, 1);
            req.Set();
            bool audioStopped = HostShutdown.WaitForAudioThread(audioThread);
            if (audioStopped)
            {
                plugin?.Dispose();
                view.SafeMemoryMappedViewHandle.ReleasePointer();
            }
            else
            {
                // 音声スレッドがプラグイン内で処理中のまま。ここで破棄/アンマップすると、そのスレッドが
                // 解放済みのメモリやアンロード済みのコードに触れてクラッシュする。何も解放せず、プロセス終了
                // (OS が全て回収する) に任せる。using による解放を避けるため、スコープを抜けずにここで終了する。
                Environment.Exit(HostShutdown.ResolveExitCode(exitCode, audioThreadStopped: false));
            }
        }
        return exitCode;
    }

    static void AudioLoop(ILocalPlugin plugin, byte* basePtr, EventWaitHandle req, EventWaitHandle ack, Func<bool> shouldStop)
    {
        float* inL = (float*)(basePtr + HostProtocol.InLOffset);
        float* inR = (float*)(basePtr + HostProtocol.InROffset);
        float* outL = (float*)(basePtr + HostProtocol.OutLOffset);
        float* outR = (float*)(basePtr + HostProtocol.OutROffset);

        while (true)
        {
            req.WaitOne();
            if (shouldStop()) break;
            int frames = *(int*)basePtr;
            if (frames < 0) break;
            plugin.ProcessCore(inL, inR, outL, outR, frames);
            ack.Set();
        }
    }
}
