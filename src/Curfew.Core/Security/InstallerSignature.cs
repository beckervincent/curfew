using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Curfew.Core.Security;

/// <summary>verify downloaded update installer signed by Curfew's own code-signing key before it runs (SYSTEM via service, or elevated by app). last line of defence vs malicious update: even if URL pinning + HTTPS defeated, installer not signed by our key refused</summary>
/// <remarks>
/// <para>two checks must both pass:</para>
/// <list type="number">
/// <item><b>Integrity</b> — <c>WinVerifyTrust</c> confirms well-formed Authenticode signature whose hash matches file, one tampered byte invalidates. cert self-signed (no trusted CA chain), so only non-fatal trust error we accept is <c>CERT_E_UNTRUSTEDROOT</c>; every other status (no sig, bad hash, expired, distrusted, …) fatal</item>
/// <item><b>Authenticity</b> — signer cert public key (SubjectPublicKeyInfo) must hash to <see cref="PinnedPublicKeySha256"/>. pin public key not whole cert so a renewed cert with same key still validates</item>
/// </list>
/// <para>fail closed: any error, non-Windows host, or mismatch returns <see langword="false"/>. rotating signing key needs updating <see cref="PinnedPublicKeySha256"/> + new app build</para>
/// </remarks>
public static class InstallerSignature
{
    /// <summary>SHA-256 of signing cert SubjectPublicKeyInfo (uppercase hex). pin for Curfew self-signed code-signing key. must match public key of cert release workflow signs installers with</summary>
    public const string PinnedPublicKeySha256 =
        "BFE95CE974EB1059325D1504310CA554565DFD4D7393B9CAFB5B74D8FE90909B";

    // WinVerifyTrust status values
    private const uint TrustSuccess = 0;                 // ERROR_SUCCESS
    private const uint CertUntrustedRoot = 0x800B0109;   // CERT_E_UNTRUSTEDROOT (self-signed: expected)

    // WINTRUST_DATA option values
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;

    private static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    /// <summary>is <paramref name="filePath"/> an Authenticode-signed exe with valid sig over its contents and signer public key matching <see cref="PinnedPublicKeySha256"/>. never throws</summary>
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
            // any failure to evaluate signature = untrusted
            return false;
        }
    }

    /// <summary>confirm file has valid Authenticode signature over its own bytes. accept only success or self-signed "untrusted root"; every other WinVerifyTrust result rejected</summary>
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

                // release state handle regardless of verdict
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
            // DestroyStructure frees native LPWStr path copy StructureToPtr made;
            // FreeHGlobal alone leaks it every check
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(pFileInfo);
            Marshal.FreeHGlobal(pFileInfo);
        }
    }

    /// <summary>extract Authenticode signer cert, compare SHA-256 of its SubjectPublicKeyInfo against pin</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool SignerKeyMatchesPin(string filePath)
    {
#pragma warning disable SYSLIB0057 // CreateFromSignedFile is the supported way to read Authenticode signer
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
