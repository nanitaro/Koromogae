using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Koromogae.Audio;

/// <summary>PortAudio のコールバックストリーム (入力専用 or 出力専用)。</summary>
internal sealed unsafe class PaStream : IDisposable
{
    public delegate void Handler(byte* buffer, int frames);

    readonly Pa.StreamCallback _callback;   // GC されないよう保持する
    readonly Handler _handler;
    IntPtr _stream;

    // ---- 診断 (コールバックのスレッドだけが書き、別スレッドが読む。厳密な同期は不要) ----
    long _callbacks, _xruns, _gapCount, _lastStart;
    uint _statusOr;
    long _handlerErrors;
    string? _firstError;   // 最初の例外だけ文字列にする (例外が続いても、音声スレッドで割り当て続けない)
    int _minFrames = int.MaxValue, _maxFrames;
    double _maxHandlerMs, _minGapMs = double.MaxValue, _maxGapMs, _sumGapMs;
    static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;

    public int Channels { get; private set; }
    public SampleFormat Format { get; private set; }

    PaStream(Handler handler)
    {
        _handler = handler;
        _callback = OnCallback;
    }

    int OnCallback(IntPtr input, IntPtr output, uint frameCount, IntPtr timeInfo, uint statusFlags, IntPtr userData)
    {
        long start = Stopwatch.GetTimestamp();
        if (_lastStart != 0)
        {
            double gap = (start - _lastStart) * TickToMs;
            if (gap < _minGapMs) _minGapMs = gap;
            if (gap > _maxGapMs) _maxGapMs = gap;
            _sumGapMs += gap;
            _gapCount++;
        }
        _lastStart = start;
        _callbacks++;
        if ((int)frameCount < _minFrames) _minFrames = (int)frameCount;
        if ((int)frameCount > _maxFrames) _maxFrames = (int)frameCount;
        if (statusFlags != 0) { _xruns++; _statusOr |= statusFlags; }

        try
        {
            _handler((byte*)(input != IntPtr.Zero ? input : output), (int)frameCount);
        }
        catch (Exception ex)
        {
            // ネイティブコールバックへ例外を漏らさない。回数と最初の内容は診断に出す (続くと無音や音割れになるため)
            _handlerErrors++;
            _firstError ??= $"{ex.GetType().Name}: {ex.Message}";
        }

        double handlerMs = (Stopwatch.GetTimestamp() - start) * TickToMs;
        if (handlerMs > _maxHandlerMs) _maxHandlerMs = handlerMs;
        return 0; // paContinue
    }

    /// <summary>PortAudio が報告するこのストリームの遅延 (ms)。入力ストリームでは入力側、出力ストリームでは出力側。</summary>
    public double ReportedLatencyMs
    {
        get
        {
            if (_stream == IntPtr.Zero) return 0;
            var info = (Pa.StreamInfo*)Pa.Pa_GetStreamInfo(_stream);
            return info == null ? 0 : Math.Max(info->inputLatency, info->outputLatency) * 1000.0; // 使わない側は 0
        }
    }

    public StreamStats GetStats()
    {
        double latencyMs = ReportedLatencyMs, rate = 0;
        if (_stream != IntPtr.Zero)
        {
            var info = (Pa.StreamInfo*)Pa.Pa_GetStreamInfo(_stream);
            if (info != null) rate = info->sampleRate;
        }
        return new StreamStats(_callbacks, _xruns, _statusOr, _minFrames == int.MaxValue ? 0 : _minFrames, _maxFrames,
            _maxHandlerMs, _gapCount == 0 ? 0 : _minGapMs, _maxGapMs, _gapCount == 0 ? 0 : _sumGapMs / _gapCount, latencyMs, rate,
            _handlerErrors, _firstError);
    }

    /// <summary>チャンネル数・サンプル形式・WASAPI フラグを順に試して最初に開けた構成を採用する。</summary>
    public static PaStream Open(AudioDevice device, bool isInput, ApiKind api, int sampleRate, int latencyMs, Handler handler,
        FormatChoice formatChoice = FormatChoice.Auto)
    {
        var stream = new PaStream(handler);
        int maxChannels = isInput ? device.MaxInput : device.MaxOutput;
        int[] channelCandidates = maxChannels >= 2 ? new[] { 2, 1 } : new[] { 1 };
        var formats = SampleFormats.Candidates(formatChoice);
        var infoCandidates = HostInfoCandidates(api);

        int lastError = 0;
        foreach (int channels in channelCandidates)
        {
            foreach (var format in formats)
            {
                foreach (var (size, flags) in infoCandidates)
                {
                    IntPtr info = size == 0 ? IntPtr.Zero : CreateWasapiInfo(size, flags);
                    try
                    {
                        var parameters = new Pa.StreamParameters
                        {
                            device = device.Index,
                            channelCount = channels,
                            sampleFormat = ToPaFormat(format),
                            suggestedLatency = latencyMs / 1000.0,
                            hostApiSpecificStreamInfo = info,
                        };

                        int rc = Pa.Pa_OpenStream(out IntPtr handle,
                            isInput ? &parameters : null, isInput ? null : &parameters,
                            sampleRate, 0, 0, stream._callback, IntPtr.Zero);
                        if (rc == 0)
                        {
                            stream._stream = handle;
                            stream.Channels = channels;
                            stream.Format = format;
                            return stream;
                        }
                        lastError = rc;
                    }
                    finally
                    {
                        if (info != IntPtr.Zero) Marshal.FreeHGlobal(info);
                    }
                }
            }
        }

        string formatText = formatChoice == FormatChoice.Auto ? "" : Loc.F("Err.FormatSpecified", formatChoice);
        throw new InvalidOperationException(
            Loc.F("Err.OpenStream", Loc.T(isInput ? "Err.Input" : "Err.Output"), device.Name, sampleRate, formatText, Pa.ErrorText(lastError)));
    }

    static uint ToPaFormat(SampleFormat f) => f switch
    {
        SampleFormat.Float32 => Pa.Float32,
        SampleFormat.Int32 => Pa.Int32,
        SampleFormat.Int24 => Pa.Int24,
        _ => Pa.Int16,
    };

    // PaWasapiStreamInfo はバージョンにより末尾フィールドが異なり size が一致しないと拒否されるため、複数サイズを試す
    static (int Size, uint Flags)[] HostInfoCandidates(ApiKind api) => api switch
    {
        ApiKind.WasapiExclusive => new[] { (56, Pa.WasapiExclusive), (48, Pa.WasapiExclusive) },
        ApiKind.WasapiShared => new[] { (56, Pa.WasapiAutoConvert), (48, Pa.WasapiAutoConvert), (0, 0u) },
        _ => new[] { (0, 0u) },
    };

    static IntPtr CreateWasapiInfo(int size, uint flags)
    {
        IntPtr mem = Marshal.AllocHGlobal(64);
        new Span<byte>((void*)mem, 64).Clear();
        Marshal.WriteInt32(mem, 0, size);          // size
        Marshal.WriteInt32(mem, 4, Pa.HostWasapi); // hostApiType
        Marshal.WriteInt32(mem, 8, 1);             // version
        Marshal.WriteInt32(mem, 12, (int)flags);   // flags
        return mem;
    }

    public void Start()
    {
        int rc = Pa.Pa_StartStream(_stream);
        if (rc != 0) throw new InvalidOperationException(Loc.F("Err.StartStream", Pa.ErrorText(rc)));
    }

    public void Dispose()
    {
        if (_stream == IntPtr.Zero) return;
        Pa.Pa_StopStream(_stream);
        Pa.Pa_CloseStream(_stream);
        _stream = IntPtr.Zero;
    }
}
