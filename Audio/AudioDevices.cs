using System.Runtime.InteropServices;

namespace Koromogae.Audio;

public sealed record AudioDevice(int Index, string Name, int MaxInput, int MaxOutput, double DefaultRate,
    bool IsDefaultInput, bool IsDefaultOutput)
{
    public override string ToString() => Name;
}

public static unsafe class AudioDevices
{
    public static string Label(ApiKind api, System.Globalization.CultureInfo? culture = null) => api switch
    {
        ApiKind.WasapiExclusive => Loc.T("Api.WasapiExclusive", culture),
        ApiKind.WasapiShared => Loc.T("Api.WasapiShared", culture),
        ApiKind.KernelStreaming => Loc.T("Api.KernelStreaming", culture),
        ApiKind.DirectSound => "DirectSound",
        ApiKind.Mme => "MME",
        _ => api.ToString(),
    };

    internal static int HostType(ApiKind api) => api switch
    {
        ApiKind.WasapiExclusive or ApiKind.WasapiShared => Pa.HostWasapi,
        ApiKind.KernelStreaming => Pa.HostWdmKs,
        ApiKind.DirectSound => Pa.HostDirectSound,
        _ => Pa.HostMme,
    };

    // ---- PortAudio の初期化と終了 (画面や App は、PortAudio の関数を直接呼ばずに、ここを使う) ----

    /// <summary>PortAudio を初期化する。成功したら null、失敗したら理由の文字列。</summary>
    public static string? Initialize()
    {
        int rc = Pa.Pa_Initialize();
        return rc == 0 ? null : Pa.ErrorText(rc);
    }

    public static void Shutdown() => Pa.Pa_Terminate();

    /// <summary>同梱している PortAudio のバージョンの文字列 (例: <c>PortAudio V19.7.0-devel, revision ...</c>)。</summary>
    public static string VersionText() => Marshal.PtrToStringUTF8(Pa.Pa_GetVersionText()) ?? Loc.T("Ver.Unknown");

    /// <summary>デバイス一覧を最新にする。PortAudio は再初期化しないと一覧を更新しない。動作中に呼んではいけない。</summary>
    public static void Refresh()
    {
        Pa.Pa_Terminate();
        Pa.Pa_Initialize();
    }

    public static (List<AudioDevice> Inputs, List<AudioDevice> Outputs) Enumerate(ApiKind api)
    {
        var inputs = new List<AudioDevice>();
        var outputs = new List<AudioDevice>();
        int type = HostType(api);

        int hostCount = Pa.Pa_GetHostApiCount();
        for (int h = 0; h < hostCount; h++)
        {
            var host = (Pa.HostApiInfo*)Pa.Pa_GetHostApiInfo(h);
            if (host == null || host->type != type) continue;

            for (int i = 0; i < host->deviceCount; i++)
            {
                int index = Pa.Pa_HostApiDeviceIndexToDeviceIndex(h, i);
                var info = (Pa.DeviceInfo*)Pa.Pa_GetDeviceInfo(index);
                if (info == null) continue;

                var device = new AudioDevice(index, Marshal.PtrToStringUTF8(info->name) ?? $"Device {index}",
                    info->maxInputChannels, info->maxOutputChannels, info->defaultSampleRate,
                    index == host->defaultInputDevice, index == host->defaultOutputDevice);

                if (device.MaxInput > 0) inputs.Add(device);
                if (device.MaxOutput > 0) outputs.Add(device);
            }
        }
        return (inputs, outputs);
    }
}
