using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Curfew.Core;

/// <summary>shared constants + message shapes for config-write IPC</summary>
public static class ConfigPipe
{
    /// <summary>local named pipe SYSTEM service hosts for config writes</summary>
    public const string PipeName = "Curfew.Config";

    /// <summary>set a config key (passcode-gated unless no passcode exists yet)</summary>
    public const string OpSet = "set";

    /// <summary>set up new Windows user (passcode-gated): write per-user daily limit + add SID to set-up list, one verified call</summary>
    public const string OpProvision = "provision";

    /// <summary>clear failed-attempt lockout counter after a success</summary>
    public const string OpResetFailures = "reset";

    /// <summary>verify an offline unlock (TOTP) code and advance the replay counter, all SYSTEM-side. The
    /// counter lives in write-protected config.db so a child can't reset it to replay a known code.</summary>
    public const string OpRedeem = "redeem";

    /// <summary>verify parent passcode SYSTEM-side (secret never leaves the service). lockout enforced + failures recorded there</summary>
    public const string OpVerify = "verify";

    /// <summary>authorize a lock/tray action (Key) for the CALLER's SID + session: passcode, unlock code or
    /// ungated per action. On success the service publishes a one-shot grant <c>grant_&lt;sid&gt;_&lt;session&gt;</c>
    /// in write-protected config.db that only that session's overlay consumes.</summary>
    public const string OpAuthorize = "authorize";

    /// <summary>overlay consumes its pending grant (Value = grant id); service applies any time credit to the ledger and returns the new balance</summary>
    public const string OpConsume = "consume";

    /// <summary>overlay accounting write (Key/Value). service accepts only keys for the caller's SID and only monotonic changes (remaining down, usage up)</summary>
    public const string OpLedger = "ledger";

    /// <summary>overlay reports lock up/down (Value "1"/"0"); service binds it to the caller's SID for the Task Manager lockdown</summary>
    public const string OpLockState = "lockstate";

    /// <summary>return (Value "rotate" = regenerate first) the offline unlock seed for enrolment display; passcode-gated</summary>
    public const string OpSecret = "secret";

    /// <summary>check for and install an update now from service-protected staging; passcode-gated</summary>
    public const string OpUpdate = "update";

    /// <summary>lock/tray actions any session user may trigger for THEMSELVES without a passcode</summary>
    public static readonly IReadOnlySet<string> UngatedActions = new HashSet<string> { "break", "logoff" };

    /// <summary>actions that require the parent passcode (or, on the lock screen, an offline unlock code)</summary>
    public static readonly IReadOnlySet<string> GatedActions = new HashSet<string>
    {
        "extend15", "extend30", "extend45", "extend60", "unlock", "ignore_schedule", "pause", "resume", "quit", "provision",
    };

    /// <summary>Format of a published grant value: <c>id|action|issuedUnix</c>.</summary>
    public static bool TryParseGrant(string? raw, out string id, out string action, out long issuedUnix)
    {
        id = action = string.Empty;
        issuedUnix = 0;
        var parts = raw?.Split('|');
        if (parts is not { Length: 3 } || !long.TryParse(parts[2], out issuedUnix)) return false;
        id = parts[0];
        action = parts[1];
        return id.Length > 0 && action.Length > 0;
    }

    /// <summary>config.db key holding the grant for <paramref name="sid"/> in <paramref name="session"/>.</summary>
    public static string GrantKey(string sid, int session) => $"{SettingsPartition.GrantPrefix}{sid}_{session}";
}

/// <summary>config-IPC request. unused fields null for a given op</summary>
public sealed record ConfigRequest(
    string Op,
    string? Key = null,
    string? Value = null,
    string? Passcode = null,
    string? Sid = null);

/// <summary>config-IPC response</summary>
public sealed record ConfigResponse(bool Ok, string? Error = null, string? Value = null);

/// <summary>config-write IPC client. write config keys through SYSTEM service once config.db is write-protected. best-effort, never throws — failure (service down, pipe busy) returns <c>Ok = false</c> so caller can fall back or surface error</summary>
public static class ConfigClient
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>send one request, return response (or failure response)</summary>
    public static ConfigResponse Send(ConfigRequest request, int timeoutMs = 3000)
    {
        try
        {
            // Identification: service may learn our SID (it binds grants/ledger writes to it) but not act as us
            using var pipe = new NamedPipeClientStream(".", ConfigPipe.PipeName, PipeDirection.InOut,
                PipeOptions.None, System.Security.Principal.TokenImpersonationLevel.Identification);
            pipe.Connect(timeoutMs);

            // requests carry parent passcode in the clear — confirm other end is the SYSTEM service (session 0), not a user-session process squatting the pipe name to harvest it
            if (OperatingSystem.IsWindows() && !ServerIsSessionZero(pipe.SafePipeHandle))
                return new ConfigResponse(false, "untrusted pipe server");

            using var reader = new StreamReader(pipe);
            var writer = new StreamWriter(pipe) { AutoFlush = true };

            writer.WriteLine(JsonSerializer.Serialize(request, Json));

            // sync pipe reads have no timeout; a server that accepts but never answers would freeze caller (overlay calls from UI thread). disposing pipe on timeout unblocks the abandoned read
            var readTask = reader.ReadLineAsync();
            if (!readTask.Wait(timeoutMs)) return new ConfigResponse(false, "response timeout");

            var line = readTask.Result;
            if (line is null) return new ConfigResponse(false, "no response");

            return JsonSerializer.Deserialize<ConfigResponse>(line, Json)
                   ?? new ConfigResponse(false, "unparseable response");
        }
        catch (Exception ex)
        {
            return new ConfigResponse(false, ex.Message);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeServerSessionId(SafePipeHandle hPipe, out uint sessionId);

    /// <summary>whether connected pipe server runs in session 0 — where Windows services live and interactive processes cannot enter</summary>
    private static bool ServerIsSessionZero(SafePipeHandle pipe) =>
        GetNamedPipeServerSessionId(pipe, out var session) && session == 0;

    /// <summary>write config key via service. returns whether accepted</summary>
    public static bool SetConfig(string key, string value, string? passcode) =>
        Send(new ConfigRequest(ConfigPipe.OpSet, Key: key, Value: value, Passcode: passcode)).Ok;

    /// <summary>set up new Windows user given parent passcode: write per-user daily limit (<paramref name="limitMinutes"/>) + add SID to set-up list. one verified service call</summary>
    public static bool Provision(string sid, string? passcode, int limitMinutes) =>
        Send(new ConfigRequest(ConfigPipe.OpProvision, Sid: sid, Passcode: passcode,
            Value: limitMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture))).Ok;

    /// <summary>clear lockout counter after success. the code that just unlocked (passcode or device code) authenticates the reset — without it any local user could zero the counter between guesses and brute-force</summary>
    public static bool ResetFailures(string? code) =>
        Send(new ConfigRequest(ConfigPipe.OpResetFailures, Passcode: code)).Ok;

    /// <summary>Verify an offline unlock code and advance the replay counter SYSTEM-side. Returns
    /// <c>Ok = true</c> when the code is valid and freshly redeemed (counter advanced in config.db); the
    /// overlay then applies the bonus. The code travels in the passcode field (it is the authenticator).</summary>
    public static ConfigResponse Redeem(string? code) =>
        Send(new ConfigRequest(ConfigPipe.OpRedeem, Passcode: code));

    /// <summary>verify parent passcode in the service. Error "locked out, retry in Ns" while throttled</summary>
    public static ConfigResponse Verify(string? passcode) =>
        Send(new ConfigRequest(ConfigPipe.OpVerify, Passcode: passcode));

    /// <summary>authorize <paramref name="action"/> for the calling user's session (see <see cref="ConfigPipe.OpAuthorize"/>). Value of an ok response = granted action (e.g. "redeem" when an unlock code was used)</summary>
    public static ConfigResponse Authorize(string action, string? code = null, string? value = null) =>
        Send(new ConfigRequest(ConfigPipe.OpAuthorize, Key: action, Passcode: code, Value: value));

    /// <summary>consume the caller session's grant <paramref name="id"/>; Value = "action|remainingSeconds" (balance empty when unchanged)</summary>
    public static ConfigResponse Consume(string id) =>
        Send(new ConfigRequest(ConfigPipe.OpConsume, Value: id), timeoutMs: 1500);

    /// <summary>forward an accounting write to the service (short timeout: called from overlay pump thread)</summary>
    public static bool Ledger(string key, string value) =>
        Send(new ConfigRequest(ConfigPipe.OpLedger, Key: key, Value: value), timeoutMs: 500).Ok;

    /// <summary>report lock up/down for the caller's account</summary>
    public static bool LockState(bool active) =>
        Send(new ConfigRequest(ConfigPipe.OpLockState, Value: active ? "1" : "0"), timeoutMs: 1000).Ok;

    /// <summary>fetch (or rotate then fetch) the offline unlock seed for display; passcode-gated</summary>
    public static ConfigResponse UnlockSecret(string? passcode, bool rotate) =>
        Send(new ConfigRequest(ConfigPipe.OpSecret, Passcode: passcode, Value: rotate ? "rotate" : string.Empty));

    /// <summary>ask the service to update now; passcode-gated. long timeout: download + verify happen before the reply</summary>
    public static ConfigResponse UpdateNow(string? passcode) =>
        Send(new ConfigRequest(ConfigPipe.OpUpdate, Passcode: passcode), timeoutMs: 120_000);
}
