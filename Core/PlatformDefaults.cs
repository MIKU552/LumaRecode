using System.Runtime.InteropServices;

namespace VideoConverter.Core;

/// <summary>Platform defaults for GUI/CLI choices and architecture-specific decoder workarounds.</summary>
public static class PlatformDefaults
{
    // Intel QSV is the x64 quick-share path. ARM64 starts with software HEVC.
    public static string Purpose => PurposeFor(RuntimeInformation.ProcessArchitecture);
    internal static string PurposeFor(Architecture architecture) => architecture == Architecture.Arm64 ? "archive" : "share";

    // The ProRes RAW coefficient-permutation workaround targets the x86 AVX2
    // implementation. ARM FFmpeg builds do not accept x86 CPU feature names.
    internal static string[] RawCpuArguments => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? [] : ["-cpuflags", "-avx2"];
}
