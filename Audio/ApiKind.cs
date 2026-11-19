namespace Koromogae.Audio;

public enum ApiKind
{
    WasapiExclusive,
    WasapiShared,
    KernelStreaming,
    DirectSound,
    /// <summary>MME (winmm) API。</summary>
    Mme,
}
