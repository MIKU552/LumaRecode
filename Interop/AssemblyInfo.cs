using System.Runtime.InteropServices;

// Resolve packaged native components from trusted application/system locations,
// rather than the current working directory or an arbitrary PATH directory.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
