using System.Runtime.InteropServices;

namespace VideoConverter.Core;

/// <summary>Architecture-specific decoder workarounds.</summary>
public static class PlatformDefaults
{
    // The ProRes RAW coefficient-permutation workaround targets the x86 AVX2
    // implementation. ARM FFmpeg builds do not accept x86 CPU feature names.
    internal static string[] RawCpuArguments => RuntimeInformation.ProcessArchitecture == Architecture.Arm64
        ? [] : ["-cpuflags", "-avx2"];
}
