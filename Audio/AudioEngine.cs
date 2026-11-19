using System.Diagnostics;
using VstCore;

namespace Koromogae.Audio;

/// <summary>入力デバイス → VST チェーン → 出力デバイスのリアルタイム処理。</summary>
public sealed unsafe class AudioEngine : IDisposable
{
    const int MaxBlock = PluginLimits.MaxBlock;

    readonly float[] _blockL = new float[MaxBlock];
    readonly float[] _blockR = new float[MaxBlock];
    readonly float[] _capL = new float[MaxBlock];
    readonly float[] _capR = new float[MaxBlock];

    PaStream? _input;
    PaStream? _output;
    StereoRing? _ring;   // 開始のたびに作り直す。ストリームを開く前に代入するので、コールバックは必ず新しいものを見る
    int _sampleRate;
    Pcm.DitherState _ditherState = Pcm.DitherState.Create(0x2545F491);   // 出力コールバック (1 スレッド) だけが使う。乱数の種は 0 にしてはいけない
    DitherMode _ditherMode;
    int _inPeakBits;
    int _outPeakBits;

    // ---- 診断 (出力コールバックのスレッドだけが書く。リングの分は StereoRing が持つ) ----
    double _maxChainMs;
    CallbackWatch? _inputWatch, _outputWatch;   // コールバックが止まっていないかの監視 (UI スレッドだけが使う)
    volatile Recorder? _recorder;               // 録音 (動作中だけ。止めたあとも、次の開始までは状態を読めるよう残す)

    public PluginChain Chain { get; } = new();
    public bool IsRunning { get; private set; }
    /// <summary>動作中の説明 (API・サンプルレート・形式・ディザ)。画面の言語。停止中は空。</summary>
    public string Description => Describe(null);

    /// <summary>ログに残す説明 (ログは日本語のまま)。</summary>
    public string LogDescription => Describe(Loc.Japanese);

    ApiKind _describedApi;

    string Describe(System.Globalization.CultureInfo? culture)
        => !IsRunning || _input == null || _output == null
            ? ""
            : Loc.F(culture, "Status.Running", AudioDevices.Label(_describedApi, culture), _sampleRate,
                _input.Channels, _input.Format, _output.Channels, _output.Format, Pcm.DitherLabel(_output.Format, _ditherMode, culture));

    /// <summary>前回取得以降のピーク値 (0..1) を取り出してリセットする。</summary>
    public float TakeInputPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _inPeakBits, 0));
    public float TakeOutputPeak() => BitConverter.Int32BitsToSingle(Interlocked.Exchange(ref _outPeakBits, 0));

    static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>診断を集計する。コールバックの停止の監視 (<see cref="CallbackWatch"/>) があるので、UI スレッドだけから呼ぶこと。</summary>
    public AudioStats? GetStats()
    {
        var input = _input;
        var output = _output;
        var ring = _ring;
        if (input == null || output == null || ring == null || _sampleRate <= 0) return null;

        var inputStats = input.GetStats();
        var outputStats = output.GetStats();
        double now = NowMs();
        return new AudioStats(
            inputStats, outputStats, ring.Underruns, ring.SilentChunks, ring.Trims, ring.TrimmedFrames,
            ring.MinSlackFrames, _maxChainMs,
            ring.WrittenFrames, ring.InputBlock * 1000.0 / _sampleRate,
            _inputWatch?.Update(inputStats.Callbacks, now) ?? 0,
            _outputWatch?.Update(outputStats.Callbacks, now) ?? 0,
            _recorder?.Status.DroppedMs ?? 0);
    }

    /// <summary>
    /// いまの実遅延の内訳。動作中でなければ null。UI スレッドから呼ぶ (プラグインの遅延の合計が UI スレッド専用のため)。
    /// 内部バッファは出力コールバックでの平均なので、始めの数百 ms は値が安定しない。
    /// </summary>
    public LatencyInfo? GetLatency()
    {
        var input = _input;
        var output = _output;
        var ring = _ring;
        if (!IsRunning || input == null || output == null || ring == null || _sampleRate <= 0) return null;
        return new LatencyInfo(
            input.ReportedLatencyMs,
            ring.AverageFrames * 1000.0 / _sampleRate,
            Chain.LatencySamples * 1000.0 / _sampleRate,
            output.ReportedLatencyMs);
    }

    public void Start(AudioDevice input, AudioDevice output, EngineOptions options)
    {
        if (IsRunning) Stop();
        var (api, sampleRate, latencyMs) = (options.Api, options.SampleRate, options.LatencyMs);
        _ditherMode = options.DitherMode;
        _ditherState = Pcm.DitherState.Create(0x2545F491, sampleRate);
        _maxChainMs = 0;
        _recorder = null;

        _sampleRate = sampleRate;
        _ring = new StereoRing(sampleRate, (int)(sampleRate * (long)Math.Max(0, options.CushionExtraMs) / 1000));

        Chain.Prepare(sampleRate, MaxBlock);

        try
        {
            _output = PaStream.Open(output, isInput: false, api, sampleRate, latencyMs, OnRender, options.OutputFormat);
            _input = PaStream.Open(input, isInput: true, api, sampleRate, latencyMs, OnCapture, options.InputFormat);
            _input.Start();
            _output.Start();
            _inputWatch = new CallbackWatch(NowMs());
            _outputWatch = new CallbackWatch(NowMs());
        }
        catch
        {
            Stop();
            throw;
        }

        IsRunning = true;
        _describedApi = api;
    }

    // ---- 録音 ----

    /// <summary>実際に開いた再生の形式 (動作中だけ)。ディザが働くかの表示に使う。</summary>
    public FormatChoice? OutputFormatInUse => _output is { } output ? SampleFormats.ToChoice(output.Format) : null;

    /// <summary>録音が動いているか。</summary>
    public bool IsRecording => _recorder?.IsActive == true;

    /// <summary>録音の状態 (録音したことがなければ null)。録音を止めたあとも、次の開始までは最後の状態が読める。</summary>
    public RecorderStatus? RecordingStatus => _recorder?.Status;

    /// <summary>
    /// 録音を始める (動作中だけ)。保存先のフォルダが作れない・ファイルが開けない・ディスクの空きが足りないときは例外 (録音は始まらない)。
    /// ディザの方式は、再生と同じ (開始時に選んでいたもの)。
    /// </summary>
    public void StartRecording(string folder, RecordDepth depth, bool includeDry,
        RecordFormat fileFormat = RecordFormat.Wav, int flacLevel = FlacWriter.DefaultLevel)
    {
        if (!IsRunning) throw new InvalidOperationException("録音は動作中だけ始められます");   // i18n:internal
        if (IsRecording) return;

        var format = SampleFormats.ToSampleFormat(depth);
        var recorder = new Recorder(folder, DateTime.Now.ToString("yyyyMMdd-HHmmss"), _sampleRate, format, _ditherMode, includeDry,
            fileFormat: fileFormat, flacLevel: flacLevel);
        recorder.Start();
        _recorder = recorder;
    }

    /// <summary>録音を終える (溜まっている音を書き切って、ファイルを閉じる)。録音していなければ null。</summary>
    public RecorderStatus? StopRecording()
    {
        var recorder = _recorder;
        if (recorder == null) return null;
        recorder.Stop();
        return recorder.Status;
    }

    public void Stop()
    {
        StopRecording();
        LogDiagnosticsIfNeeded();
        _input?.Dispose();
        _output?.Dispose();
        _input = null;
        _output = null;
        _inputWatch = _outputWatch = null;
        _recorder = null;
        IsRunning = false;
    }

    /// <summary>音飛び・コールバックの例外・コールバックの停止・録音の欠落があった場合だけ、停止時に診断をログへ残す (利用者が後から原因を調べられるように)。判断と文面は <see cref="DiagnosticsLog"/>。</summary>
    void LogDiagnosticsIfNeeded()
    {
        var stats = IsRunning ? GetStats() : null;
        if (stats == null || !DiagnosticsLog.ShouldLog(stats)) return;
        AppLog.Warn(DiagnosticsLog.Format(LogDescription, stats));
    }
    // ---- 入力コールバック: デバイス形式 → ステレオ float → リングバッファ ----
    void OnCapture(byte* buffer, int frames)
    {
        var stream = _input;
        var ring = _ring;
        if (stream == null || ring == null) return;
        ring.InputBlock = frames;

        fixed (float* l = _capL, r = _capR)
        {
            int done = 0;
            while (done < frames)
            {
                int n = Math.Min(frames - done, MaxBlock);
                byte* src = buffer + done * Pcm.BytesPerSample(stream.Format) * stream.Channels;
                Pcm.ReadStereo(src, stream.Format, stream.Channels, n, l, r);
                UpdatePeak(ref _inPeakBits, Pcm.Peak(l, r, n));
                ring.Write(new ReadOnlySpan<float>(l, n), new ReadOnlySpan<float>(r, n));
                done += n;
            }
        }
    }

    // ---- 出力コールバック: リングバッファ → VST チェーン → デバイス形式 ----
    void OnRender(byte* buffer, int frames)
    {
        var stream = _output;
        var ring = _ring;
        if (stream == null || ring == null) return;
        var recorder = _recorder;
        bool recording = recorder != null && recorder.IsActive;
        bool recordDry = recording && recorder!.IncludesDry;

        int frameBytes = Pcm.BytesPerSample(stream.Format) * stream.Channels;
        fixed (float* l = _blockL, r = _blockR)
        {
            int done = 0;
            while (done < frames)
            {
                int n = Math.Min(frames - done, MaxBlock);
                if (ring.Read(new Span<float>(l, n), new Span<float>(r, n)))
                {
                    // 加工前の音は、チェーンに通す前にコピーする (チェーンは同じ配列を書き換える)
                    if (recordDry) recorder!.PushDry(new ReadOnlySpan<float>(l, n), new ReadOnlySpan<float>(r, n));
                    long t0 = Stopwatch.GetTimestamp();
                    Chain.Process(_blockL, _blockR, n);
                    double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
                    if (ms > _maxChainMs) _maxChainMs = ms;
                }
                else
                {
                    new Span<float>(l, n).Clear();
                    new Span<float>(r, n).Clear();
                    // 音飛びの無音も録音する (ファイルの長さを、聞こえた時間と一致させる)
                    if (recordDry) recorder!.PushDry(new ReadOnlySpan<float>(l, n), new ReadOnlySpan<float>(r, n));
                }

                if (recording) recorder!.PushWet(new ReadOnlySpan<float>(l, n), new ReadOnlySpan<float>(r, n));

                UpdatePeak(ref _outPeakBits, Pcm.Peak(l, r, n));
                Pcm.WriteStereo(l, r, stream.Format, stream.Channels, buffer + done * frameBytes, n, ref _ditherState, _ditherMode);
                done += n;
            }
        }
    }

    static void UpdatePeak(ref int bits, float value)
    {
        int current;
        do
        {
            current = Volatile.Read(ref bits);
            if (BitConverter.Int32BitsToSingle(current) >= value) return;
        }
        while (Interlocked.CompareExchange(ref bits, BitConverter.SingleToInt32Bits(value), current) != current);
    }

    public void Dispose()
    {
        Stop();
        Chain.Dispose();
    }
}
