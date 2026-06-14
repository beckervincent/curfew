using System.Runtime.InteropServices;

namespace Curfew.Core.Security;

/// <summary>apply process-level exploit mitigations hardening Curfew exe vs DLL injection / search-order hijacking + code injection. best-effort: every call guarded so older OS lacking a policy never blocks start-up</summary>
/// <remarks>
/// <para>primary DLL-planting defence is install-dir ACL (read-only for limited users, set by installer): child can't drop DLL next to our binaries. these runtime mitigations close remaining gaps — current working dir, remote/UNC + low-integrity DLL sources, legacy AppInit_DLLs / <c>SetWindowsHookEx</c> injection into our process</para>
/// <para>NOT enabled, would break self-contained .NET app: <c>PreferSystem32Images</c> (runtime + app DLLs live beside exe) and <c>MicrosoftSignedOnly</c> (our assemblies not MS-signed). dynamic-code policy off too because CLR JIT generates executable code</para>
/// </remarks>
public static class ProcessHardening
{
    // PROCESS_MITIGATION_POLICY enum values
    private const int ProcessExtensionPointDisablePolicy = 6;
    private const int ProcessImageLoadPolicy = 10;

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY bit flags
    private const uint NoRemoteImages = 0x1;
    private const uint NoLowMandatoryLabelImages = 0x2;

    // PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY bit flags
    private const uint DisableExtensionPoints = 0x1;

    /// <summary>harden current process. <paramref name="disableExtensionPoints"/> blocks AppInit_DLLs / window-hook injection into this process; leave off for WinUI app which may need input extension points (IMEs) for text entry. (our low-level keyboard hook unaffected — injects nothing)</summary>
    public static void Apply(bool disableExtensionPoints = true)
    {
        if (!OperatingSystem.IsWindows()) return;

        // drop current working dir from DLL search order so DLL planted in launch
        // dir can't be side-loaded. app dir + System32 stay searchable
        TryRun(static () => SetDllDirectoryW(string.Empty));

        // refuse DLLs from remote (UNC) share or low-integrity path
        TryRun(static () =>
        {
            var image = NoRemoteImages | NoLowMandatoryLabelImages;
            SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref image, (nuint)sizeof(uint));
        });

        if (disableExtensionPoints)
            TryRun(static () =>
            {
                var ext = DisableExtensionPoints;
                SetProcessMitigationPolicy(ProcessExtensionPointDisablePolicy, ref ext, (nuint)sizeof(uint));
            });
    }

    private static void TryRun(Action action)
    {
        try { action(); }
        catch { /* policy unavailable on this OS, or already locked — ignore */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string path);
}
