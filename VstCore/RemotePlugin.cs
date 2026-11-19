using System.IO.MemoryMappedFiles;
using System.IO.Pipes;
using Microsoft.Win32.SafeHandles;

namespace VstCore;

/// <summary>
/// 別プロセス(プラグインホスト)にロードされたプラグインを <see cref="IVstEffect"/> として扱うクライアント。
/// プラグインは 1 つにつき 1 プロセスで動くので、プラグインがクラッシュしても、このアプリや他のプラグインには影響しない。
/// </summary>
public sealed unsafe class RemotePlugin : IVstEffect
{
    readonly System.Diagnostics.Process _process;
    readonly MemoryMappedFile _mmf;
    readonly MemoryMappedViewAccessor _view;
    readonly EventWaitHandle _req;
    readonly EventWaitHandle _ack;
    readonly NamedPipeClientStream _pipe;
    readonly BinaryReader _reader;
    readonly BinaryWriter _writer;
    readonly object _controlLock = new();
    readonly byte* _base;
    readonly float* _inL, _inR, _outL, _outR;
    readonly ProcessExitHandle _exitHandle;
    readonly WaitHandle[] _waitHandles;   // { ack, プロセス終了 }。音声スレッドで毎回割り当てないよう使い回す
    volatile bool _faulted;
    volatile bool _timedOut;
    bool _disposed;

    /// <summary>
    /// 制御用の 1 回の呼び出し (パイプの応答待ち) の期限。超えたらホストを強制終了して <see cref="IsFaulted"/> にする
    /// (プラグインがモーダルダイアログなどで止まっても、UI が固まったままにならないように)。ロードは長いライブラリを読むことがあるので別。
    /// </summary>
    public const int DefaultCallTimeoutMs = 30_000;
    public const int LoadTimeoutMs = 180_000;

    /// <summary>1 回の呼び出しの期限 (ミリ秒)。テストで短くするために変えられる。</summary>
    internal int CallTimeoutMs { get; set; } = DefaultCallTimeoutMs;

    /// <summary><see cref="Dispose"/> が、実行中の呼び出しの終了を待つ最大 (ミリ秒)。超えたら、待たずにホストを強制終了する。</summary>
    const int DisposeLockWaitMs = 1000;
    bool _processedOnce;

    public string Name { get; private set; } = "";
    public string Path { get; }
    public PluginBitness Bitness { get; }
    public bool HasEditor { get; private set; }

    /// <summary>プラグインを動かしているホストプロセスの ID (診断・テスト用)。</summary>
    public int ProcessId { get; }

    /// <summary>プラグインが申告する遅延 (サンプル数)。ロード時と <see cref="Prepare"/> 時に更新するキャッシュ。</summary>
    public int LatencySamples { get; private set; }

    /// <summary>
    /// 通信の失敗・不正な応答・ホストプロセスの終了 (プラグインのクラッシュ) のいずれかで true。
    /// 以後このプラグインは処理されず、音声はそのまま通る。
    /// </summary>
    public bool IsFaulted => _faulted || (!_disposed && _exitHandle.WaitOne(0));

    RemotePlugin(string hostExe, string pluginPath, PluginBitness bitness, int sampleRate)
    {
        Path = pluginPath;
        Bitness = bitness;

        string id = Guid.NewGuid().ToString("N");
        _mmf = MemoryMappedFile.CreateNew(HostProtocol.MemoryName(id), HostProtocol.MemorySize);
        _view = _mmf.CreateViewAccessor(0, HostProtocol.MemorySize);
        byte* ptr = null;
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref ptr);
        _base = ptr + _view.PointerOffset;
        _inL = (float*)(_base + HostProtocol.InLOffset);
        _inR = (float*)(_base + HostProtocol.InROffset);
        _outL = (float*)(_base + HostProtocol.OutLOffset);
        _outR = (float*)(_base + HostProtocol.OutROffset);
        _req = new EventWaitHandle(false, EventResetMode.AutoReset, HostProtocol.RequestName(id));
        _ack = new EventWaitHandle(false, EventResetMode.AutoReset, HostProtocol.AckName(id));

        _pipe = new NamedPipeClientStream(".", HostProtocol.PipeName(id), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
        _reader = new BinaryReader(_pipe);
        _writer = new BinaryWriter(_pipe);

        var startInfo = new System.Diagnostics.ProcessStartInfo(hostExe, id)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = System.IO.Path.GetDirectoryName(hostExe)!,
        };
        // 32bit ホストは framework-dependent で、x86 版の .NET ランタイムを探す。`dotnet run` / `dotnet test` などが子プロセスへ渡す
        // x64 用の DOTNET_ROOT を引き継ぐと、x64 の hostfxr.dll を読もうとして起動できないので、外して登録済みの場所 (レジストリ) から探させる
        if (bitness == PluginBitness.X86)
        {
            startInfo.Environment.Remove("DOTNET_ROOT");
            startInfo.Environment.Remove("DOTNET_ROOT_X64");
        }
        _process = System.Diagnostics.Process.Start(startInfo) ?? throw new InvalidOperationException(CoreLoc.T("Core.HostStartFailed"));
        ProcessId = _process.Id;
        _exitHandle = new ProcessExitHandle(_process);
        _waitHandles = new WaitHandle[] { _ack, _exitHandle };

        try
        {
            ConnectToHost();
            lock (_controlLock)
            {
                using var deadline = StartDeadline(LoadTimeoutMs);
                _writer.Write(HostProtocol.CmdLoad);
                _writer.Write(pluginPath);
                _writer.Write(sampleRate);
                _writer.Flush();

                if (!_reader.ReadBoolean())
                    throw new InvalidOperationException(HostProtocol.ReadString(_reader, "エラーメッセージ"));
                Name = HostProtocol.ReadString(_reader, "プラグイン名");
                HasEditor = _reader.ReadBoolean();
            }
            RefreshLatency();
        }
        catch (Exception) when (_timedOut)
        {
            // 期限で強制終了したあとの、パイプの切断などの例外を、原因が分かる文面にする
            Cleanup(kill: true);
            throw new InvalidOperationException(CoreLoc.F("Core.HostLoadTimeout", LoadTimeoutMs / 1000));
        }
        catch
        {
            Cleanup(kill: true);
            throw;
        }
    }

    /// <summary>
    /// ホストのパイプへ接続する。ホストが起動直後に終了した、または 10 秒つながらなかったときは、原因の見当を付けやすい文面にする
    /// (32bit ホストは framework-dependent なので、x86 版の .NET ランタイムが無いと起動できない)。
    /// </summary>
    void ConnectToHost()
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            try
            {
                _pipe.Connect(200);
                return;
            }
            catch (TimeoutException)
            {
                bool exited = _process.HasExited;
                if (!exited && timer.ElapsedMilliseconds < 10000) continue;

                string hint = Bitness == PluginBitness.X86
                    ? CoreLoc.T("Core.X86Hint")
                    : "";
                throw new InvalidOperationException(exited
                    ? CoreLoc.F("Core.HostExited", _process.ExitCode, hint)
                    : CoreLoc.F("Core.HostTimeout", hint));
            }
        }
    }

    /// <summary>ホストプロセス (<paramref name="hostExe"/>) を起動して、その中に <paramref name="pluginPath"/> をロードする。</summary>
    public static RemotePlugin Start(string hostExe, string pluginPath, PluginBitness bitness, int sampleRate)
    {
        if (!File.Exists(hostExe))
            throw new FileNotFoundException(CoreLoc.F("Core.HostNotFound", hostExe));
        return new RemotePlugin(hostExe, pluginPath, bitness, sampleRate);
    }

    public void Prepare(int sampleRate, int maxBlock)
    {
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdPrepare);
            _writer.Write(sampleRate);
            _writer.Write(maxBlock);
            _writer.Flush();
            _reader.ReadBoolean();
        });
        RefreshLatency(); // サンプルレートで遅延が変わるプラグインがあるため、Prepare のたびに取り直す
    }

    void RefreshLatency()
    {
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdGetLatency);
            _writer.Flush();
            LatencySamples = HostProtocol.Check(_reader.ReadInt32(), 0, HostProtocol.MaxLatencySamples, "プラグインの遅延");
        });
    }

    public void Process(float[] left, float[] right, int frames)
    {
        if (_faulted || _disposed) return;
        if (frames > PluginLimits.MaxBlock) frames = PluginLimits.MaxBlock;
        if (frames <= 0) return;

        left.AsSpan(0, frames).CopyTo(new Span<float>(_inL, frames));
        right.AsSpan(0, frames).CopyTo(new Span<float>(_inR, frames));
        *(int*)_base = frames;
        _req.Set();

        // 初回はプラグイン側の遅延初期化があり得るため長めに待つ。ホストプロセスが終了した (プラグインがクラッシュした)
        // 場合は、タイムアウトを待たずにすぐ検出する
        if (WaitHandle.WaitAny(_waitHandles, _processedOnce ? 250 : 2000) != 0)
        {
            _faulted = true;
            return;
        }
        _processedOnce = true;

        new Span<float>(_outL, frames).CopyTo(left);
        new Span<float>(_outR, frames).CopyTo(right);
    }

    public void OpenEditor()
    {
        // エディタのウィンドウはホストのプロセスが作る。ユーザーが操作した直後の本体が持つ前面の権利を、ホストへ渡す
        // (渡さないと、ホストの SetForegroundWindow が前面ロックで拒否され、エディタが本体の後ろに開く)。失敗しても Z オーダーは前に出る。
        try { Win32.AllowSetForegroundWindow((uint)ProcessId); } catch { }

        Call(() =>
        {
            _writer.Write(HostProtocol.CmdOpenEditor);
            _writer.Flush();
            _reader.ReadBoolean();
        });
    }

    public void CloseEditor()
    {
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdCloseEditor);
            _writer.Flush();
            _reader.ReadBoolean();
        });
    }

    public byte[]? GetState()
    {
        byte[]? result = null;
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdGetState);
            _writer.Flush();
            int len = _reader.ReadInt32();
            // -1 は「状態なし」。それ以外は上限内でなければ不正な応答として扱う
            result = len == -1 ? null : HostProtocol.ReadBlob(_reader, HostProtocol.Check(len, 0, HostProtocol.MaxStateBytes, "状態データ長"));
        });
        return result;
    }

    public void SetState(byte[] data)
    {
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdSetState);
            _writer.Write(data.Length);
            _writer.Write(data);
            _writer.Flush();
            _reader.ReadBoolean();
        });
    }

    public ParameterInfo[] GetParameters()
    {
        var result = Array.Empty<ParameterInfo>();
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdGetParameters);
            _writer.Flush();
            int count = HostProtocol.ReadBounded(_reader, HostProtocol.MaxItemCount, "パラメータ数");
            var budget = new ResponseBudget();    // 1 回の応答で読む文字列の合計 (個数 × 文字列の上限だけでは、理論上は十数 GiB になる)
            var list = new ParameterInfo[count];
            for (int i = 0; i < count; i++)
            {
                string name = HostProtocol.ReadString(_reader, "パラメータ名", budget);
                string unit = HostProtocol.ReadString(_reader, "単位", budget);
                int steps = HostProtocol.Check(_reader.ReadInt32(), 0, HostProtocol.MaxStepCount, "パラメータのステップ数");
                double defaultValue = HostProtocol.CheckUnit(_reader.ReadDouble(), "パラメータの既定値");
                list[i] = new ParameterInfo(i, name, unit, steps, defaultValue);
            }
            result = list;
        });
        return result;
    }

    public ParameterValue GetParameterValue(int index)
    {
        var result = default(ParameterValue);
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdGetParameterValue);
            _writer.Write(index);
            _writer.Flush();
            double value = HostProtocol.CheckUnit(_reader.ReadDouble(), "パラメータの値");
            result = new ParameterValue(value, HostProtocol.ReadString(_reader, "表示文字列"));
        });
        return result;
    }

    public void SetParameter(int index, double normalized)
    {
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdSetParameter);
            _writer.Write(index);
            _writer.Write(normalized);
            _writer.Flush();
            _reader.ReadBoolean();
        });
    }
    public ProgramList GetPrograms()
    {
        var result = new ProgramList(Array.Empty<string>(), 0);
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdGetPrograms);
            _writer.Flush();
            int current = _reader.ReadInt32();
            int count = HostProtocol.ReadBounded(_reader, HostProtocol.MaxItemCount, "プログラム数");
            if (count > 0) HostProtocol.Check(current, 0, count - 1, "現在のプログラム番号");
            var budget = new ResponseBudget();
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = HostProtocol.ReadString(_reader, "プログラム名", budget);
            result = new ProgramList(names, current);
        });
        return result;
    }

    public void SetProgram(int index)
    {
        Call(() =>
        {
            _writer.Write(HostProtocol.CmdSetProgram);
            _writer.Write(index);
            _writer.Flush();
            _reader.ReadBoolean();
        });
    }
    /// <summary>以後このプラグインを使わない。プラグインホストが生きていれば終了させる (不正な応答で壊れたストリームは再利用できない)。</summary>
    void Fault()
    {
        _faulted = true;
        try { _process.Kill(true); }
        catch { }
    }

    /// <summary>
    /// 呼び出しの期限を計る。期限までに <see cref="IDisposable.Dispose"/> されなければ、ホストを強制終了する
    /// (待っているパイプの読み取りが、切断の例外で戻る)。終了と期限切れが同時でも、終わったあとには何もしない。
    /// </summary>
    Deadline StartDeadline(int milliseconds) => new(this, milliseconds);

    sealed class Deadline : IDisposable
    {
        readonly Timer _timer;
        int _done;

        public Deadline(RemotePlugin owner, int milliseconds)
        {
            _timer = new Timer(_ =>
            {
                if (Interlocked.Exchange(ref _done, 1) != 0) return;     // すでに終わっている
                owner._timedOut = true;
                HostLog.Warn($"プラグインホストが {milliseconds} ms 応答しないため、強制終了します: {owner.Name} ({owner.Path})");
                owner.Fault();
            }, null, milliseconds, Timeout.Infinite);
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _done, 1);
            _timer.Dispose();
        }
    }

    void Call(Action action)
    {
        if (_faulted || _disposed) return;
        lock (_controlLock)
        {
            using var deadline = StartDeadline(CallTimeoutMs);
            try { action(); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                Fault(); // 切断・プラグインホストの終了
            }
            catch (Exception ex) when (ex is HostProtocolException or FormatException or OutOfMemoryException or OverflowException or ArgumentException)
            {
                // 範囲外の値・割り当て失敗など、プラグインホストの応答が信用できない。ストリームの同期も取れないので切り離す
                Fault();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Cleanup(kill: false);
    }

    void Cleanup(bool kill)
    {
        try
        {
            if (!kill && !_faulted && _pipe.IsConnected)
            {
                // 実行中の呼び出し (応答待ち) がロックを持ったままのときは、待ち続けない。
                // 待つと、このあとの強制終了に進めず、Dispose (終了時の処理) ごと固まるため
                if (Monitor.TryEnter(_controlLock, DisposeLockWaitMs))
                {
                    try
                    {
                        _writer.Write(HostProtocol.CmdExit);
                        _writer.Flush();
                    }
                    finally
                    {
                        Monitor.Exit(_controlLock);
                    }
                }
                else
                {
                    kill = true;
                }
            }
        }
        catch
        {
        }

        try
        {
            if (kill || !_process.WaitForExit(3000))
            {
                _process.Kill(true);
                _process.WaitForExit(1000);
            }
        }
        catch
        {
        }

        _exitHandle?.Dispose(); // プロセスのハンドルは所有していないので、閉じるのは待機用のラッパーだけ
        _pipe.Dispose();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _mmf.Dispose();
        _req.Dispose();
        _ack.Dispose();
        _process.Dispose();
    }
}

/// <summary>
/// 子プロセスの終了で signaled になる待機ハンドル。<see cref="System.Diagnostics.Process"/> のハンドルを借りるだけで、所有しない。
/// 音声スレッドの待機に ack と並べて渡し、プラグインのクラッシュを即座に検出するために使う。
/// </summary>
sealed class ProcessExitHandle : WaitHandle
{
    public ProcessExitHandle(System.Diagnostics.Process process)
    {
        SafeWaitHandle = new SafeWaitHandle(process.SafeHandle.DangerousGetHandle(), ownsHandle: false);
    }
}
