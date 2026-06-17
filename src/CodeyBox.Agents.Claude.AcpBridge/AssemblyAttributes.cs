using System.Runtime.Versioning;

// The bridge only ever runs inside the in-sandbox Linux VM where claude --ide
// lives. Marking the whole assembly Linux-only silences the cross-platform
// availability analyzer (CA1416) for File.SetUnixFileMode and friends without
// reaching for one-off platform-guard wrappers.
[assembly: SupportedOSPlatform("linux")]
