using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Curfew.Core.Security;

/// <summary>
/// Verifies that a downloaded update installer was signed by Curfew's own
/// code-signing key before it is ever executed (as SYSTEM by the service, or
/// elevated by the app). This is the last line of defence against a malicious
/// update: even if URL pinning and HTTPS were somehow defeated, an installer not
/// signed by our key is refused.
/// </summary>
/// <remarks>
/// <para>
/// Two independent checks must both pass:
/// </para>
/// <list type="number">
/// <item><b>Integrity</b> — <c>WinVerifyTrust</c> confirms the file carries a
/// well-formed Authenticode signature whose hash matches the file contents, so a
/// single tampered byte invalidates it. Our certificate is self-signed (not
/// chained to a trusted CA), so the only non-fatal trust error we accept is
/// <c>CERT_E_UNTRUSTEDROOT</c>; every other status (no signature, bad hash,
/// expired, explicitly distrusted, …) is fatal.</item>
/// <item><b>Authenticity</b> — the signer certificate's public key
/// (SubjectPublicKeyInfo) must hash to <see cref="PinnedPublicKeySha256"/>. The
/// public key is pinned rather than the whole certificate so a renewed
/// certificate carrying the same key still validates.</item>
/// </list>
/// <para>
/// Fail closed: any error, any non-Windows host, or any mismatch returns
/// <see langword="false"/>. Rotating the signing key requires updating
/// <see cref="PinnedPublicKeySha256"/> and shipping a new app build.
/// </para>
/// </remarks>
public static class InstallerSignature
{
    /// <summary>
    /// SHA-256 of the signing certificate's SubjectPublicKeyInfo (uppercase hex).
    /// Pin for the Curfew self-signed code-signing key. Must match the public key
    /// of the certificate the release workflow signs installers with.
    /// </summary>
    public const string PinnedPublicKeySha256 =
        "BFE95CE974EB1059325D1504310CA554565DFD4D7393B9CAFB5B74D8FE90909B";

    // WinVerifyTrust status values.
    private const uint TrustSuccess = 0;                 // ERROR_SUCCESS
    private const uint CertUntrustedRoot = 0x800B0109;   // CERT_E_UNTRUSTEDROOT (self-signed: expected)

    // WINTRUST_DATA option values.
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>
    /// Returns whether <paramref name="filePath"/> is an Authenticode-signed
    /// executable whose signature is valid over its contents and whose signer's
    /// public key matches <see cref="PinnedPublicKeySha256"/>. Never throws.
    /// </summary>
    public static bool Verify(string filePath)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return false;

        try
        {
            return VerifyAuthenticodeIntegrity(filePath) && SignerKeyMatchesPin(filePath);
        }
        catch
        {
            // Any failure to evaluate the signature is treated as untrusted.
            return false;
        }
    }

    /// <summary>
    /// Confirms the file has a valid Authenticode signature over its own bytes.
    /// Accepts only success or the self-signed "untrusted root" status; every
    /// other WinVerifyTrust result is rejected.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool VerifyAuthenticodeIntegrity(string filePath)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var pFileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFileInfo, false);

            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WtdUiNone,
                fdwRevocationChecks = WtdRevokeNone,
                dwUnionChoice = WtdChoiceFile,
                pFile = pFileInfo,
                dwStateAction = WtdStateActionVerify,
            };

            var pData = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_DATA>());
            try
            {
                Marshal.StructureToPtr(data, pData, false);
                var action = WinTrustActionGenericVerifyV2;
                var result = WinVerifyTrust(IntPtr.Zero, action, pData);

                // Release the state handle regardless of the verdict.
                var closeData = (WINTRUST_DATA)Marshal.PtrToStructure(pData, typeof(WINTRUST_DATA))!;
                closeData.dwStateAction = WtdStateActionClose;
                Marshal.StructureToPtr(closeData, pData, true);
                WinVerifyTrust(IntPtr.Zero, action, pData);

                return result == TrustSuccess || result == CertUntrustedRoot;
            }
            finally
            {
                Marshal.FreeHGlobal(pData);
            }
        }
        finally
        {
            // DestroyStructure releases the native copy of the LPWStr path that
            // StructureToPtr allocated; FreeHGlobal alone would leak it every check.
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFileInfo);
            Marshal.FreeHGlobal(pFileInfo);
        }
    }

    /// <summary>
    /// Extracts the Authenticode signer certificate and compares the SHA-256 of
    /// its SubjectPublicKeyInfo against the pin.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SignerKeyMatchesPin(string filePath)
    {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the supported way to read the Authenticode signer.
        using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
#pragma warning restore SYSLIB0057
        var spki = signer.PublicKey.ExportSubjectPublicKeyInfo();
        var hash = Convert.ToHexString(SHA256.HashData(spki));
        return CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(hash),
            System.Text.Encoding.ASCII.GetBytes(PinnedPublicKeySha256));
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint WinVerifyTrust(
        IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, IntPtr pWVTData);

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
