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

    /// <summary>record failed unlock attempt (advances lockout counter)</summary>
    public const string OpRecordFailure = "fail";

    /// <summary>clear failed-attempt lockout counter after a success</summary>
    public const string OpResetFailures = "reset";
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
            using var pipe = new NamedPipeClientStream(".", ConfigPipe.PipeName, PipeDirection.InOut);
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

    /// <summary>record failed unlock attempt (advances lockout counter)</summary>
    public static bool RecordFailure() =>
        Send(new ConfigRequest(ConfigPipe.OpRecordFailure)).Ok;

    /// <summary>clear lockout counter after success. the code that just unlocked (passcode or device code) authenticates the reset — without it any local user could zero the counter between guesses and brute-force</summary>
    public static bool ResetFailures(string? code) =>
        Send(new ConfigRequest(ConfigPipe.OpResetFailures, Passcode: code)).Ok;
}
