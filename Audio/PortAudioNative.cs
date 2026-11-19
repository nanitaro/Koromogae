using System.Runtime.InteropServices;

namespace Koromogae.Audio;

/// <summary>portaudio.dll (WASAPI / WDM-KS / DirectSound / MME を内包) への最小限の P/Invoke。</summary>
internal static unsafe class Pa
{
    const string Lib = "portaudio";

    public const int HostDirectSound = 1;
    public const int HostMme = 2;
    public const int HostWdmKs = 11;
    public const int HostWasapi = 13;

    public const uint Float32 = 0x1;
    public const uint Int32 = 0x2;
    public const uint Int24 = 0x4;
    public const uint Int16 = 0x8;

    public const uint WasapiExclusive = 1 << 0;
    public const uint WasapiAutoConvert = 1 << 6;

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamParameters
    {
        public int device;
        public int channelCount;
        public uint sampleFormat;
        public double suggestedLatency;
        public IntPtr hostApiSpecificStreamInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int structVersion;
        public IntPtr name;
        public int hostApi;
        public int maxInputChannels;
        public int maxOutputChannels;
        public double defaultLowInputLatency;
        public double defaultLowOutputLatency;
        public double defaultHighInputLatency;
        public double defaultHighOutputLatency;
        public double defaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct HostApiInfo
    {
        public int structVersion;
        public int type;
        public IntPtr name;
        public int deviceCount;
        public int defaultInputDevice;
        public int defaultOutputDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamInfo
    {
        public int structVersion;
        public double inputLatency;
        public double outputLatency;
        public double sampleRate;
    }

    // PaStreamCallbackFlags
    public const uint InputUnderflow = 0x1, InputOverflow = 0x2, OutputUnderflow = 0x4, OutputOverflow = 0x8, PrimingOutput = 0x10;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int StreamCallback(IntPtr input, IntPtr output, uint frameCount, IntPtr timeInfo, uint statusFlags, IntPtr userData);

    [DllImport(Lib)] public static extern int Pa_Initialize();
    [DllImport(Lib)] public static extern int Pa_Terminate();
    [DllImport(Lib)] public static extern IntPtr Pa_GetVersionText();
    [DllImport(Lib)] public static extern int Pa_GetHostApiCount();
    [DllImport(Lib)] public static extern IntPtr Pa_GetHostApiInfo(int hostApi);
    [DllImport(Lib)] public static extern int Pa_HostApiDeviceIndexToDeviceIndex(int hostApi, int hostApiDeviceIndex);
    [DllImport(Lib)] public static extern IntPtr Pa_GetDeviceInfo(int device);
    [DllImport(Lib)] public static extern IntPtr Pa_GetErrorText(int errorCode);
    [DllImport(Lib)] public static extern int Pa_OpenStream(out IntPtr stream, StreamParameters* input, StreamParameters* output,
        double sampleRate, uint framesPerBuffer, uint streamFlags, StreamCallback callback, IntPtr userData);
    [DllImport(Lib)] public static extern int Pa_StartStream(IntPtr stream);
    [DllImport(Lib)] public static extern int Pa_StopStream(IntPtr stream);
    [DllImport(Lib)] public static extern int Pa_CloseStream(IntPtr stream);
    [DllImport(Lib)] public static extern IntPtr Pa_GetStreamInfo(IntPtr stream);

    public static string ErrorText(int code) => Marshal.PtrToStringUTF8(Pa_GetErrorText(code)) ?? code.ToString();
}
