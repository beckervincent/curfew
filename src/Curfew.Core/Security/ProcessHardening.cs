using System.Runtime.InteropServices;

namespace Curfew.Core.Security;

/// <summary>
/// Applies process-level exploit mitigations that harden a Curfew executable
/// against DLL injection / search-order hijacking and code injection. Best-effort:
/// every call is guarded so an older OS that lacks a policy never blocks start-up.
/// </summary>
/// <remarks>
/// <para>
/// The primary DLL-planting defence is the install-directory ACL (read-only for
/// limited users, set by the installer): a child cannot drop a DLL next to our
/// binaries. These runtime mitigations close the remaining gaps — the current
/// working directory, remote/UNC and low-integrity DLL sources, and legacy
/// AppInit_DLLs / <c>SetWindowsHookEx</c> injection into our own process.
/// </para>
/// <para>
/// Deliberately NOT enabled, because they would break a self-contained .NET app:
/// <c>PreferSystem32Images</c> (our runtime and app DLLs live beside the exe) and
/// the <c>MicrosoftSignedOnly</c> signature policy (our assemblies are not
/// MS-signed). The dynamic-code policy is also left off because the CLR JIT needs
/// to generate executable code.
/// </para>
/// </remarks>
public static class ProcessHardening
{
    // PROCESS_MITIGATION_POLICY enum values.
    private const int ProcessExtensionPointDisablePolicy = 6;
    private const int ProcessImageLoadPolicy = 10;

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY bit flags.
    private const uint NoRemoteImages = 0x1;
    private const uint NoLowMandatoryLabelImages = 0x2;

    // PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY bit flags.
    private const uint DisableExtensionPoints = 0x1;

    /// <summary>
    /// Hardens the current process. <paramref name="disableExtensionPoints"/>
    /// blocks AppInit_DLLs / window-hook injection into this process; leave it off
    /// for the WinUI app, which may rely on input extension points (IMEs) for text
    /// entry. (Our own low-level keyboard hook is unaffected — it injects nothing.)
    /// </summary>
    public static void Apply(bool disableExtensionPoints = true)
    {
        if (!OperatingSystem.IsWindows()) return;

        // Drop the current working directory from the DLL search order so a DLL
        // planted in the launch directory cannot be side-loaded. The application
        // directory and System32 remain searchable.
        TryRun(static () => SetDllDirectoryW(string.Empty));

        // Refuse DLLs loaded from a remote (UNC) share or a low-integrity path.
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
        catch { /* policy unavailable on this OS, or already locked — ignore. */ }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, nuint length);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetDllDirectoryW(string path);
}
