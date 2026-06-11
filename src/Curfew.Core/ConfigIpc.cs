using System.IO.Pipes;
using System.Text.Json;

namespace Curfew.Core;

/// <summary>Shared constants + message shapes for the config-write IPC.</summary>
public static class ConfigPipe
{
    /// <summary>Local named pipe the SYSTEM service hosts for config writes.</summary>
    public const string PipeName = "Curfew.Config";

    /// <summary>Set a config key (passcode-gated unless no passcode exists yet).</summary>
    public const string OpSet = "set";

    /// <summary>Provision a Windows user (device code or passcode-gated).</summary>
    public const string OpProvision = "provision";

    /// <summary>Record a failed unlock attempt (advances the lockout counter).</summary>
    public const string OpRecordFailure = "fail";

    /// <summary>Clear the failed-attempt lockout counter after a success.</summary>
    public const string OpResetFailures = "reset";
}

/// <summary>A config-IPC request. Unused fields are null for a given op.</summary>
public sealed record ConfigRequest(
    string Op,
    string? Key = null,
    string? Value = null,
    string? Passcode = null,
    string? Sid = null);

/// <summary>A config-IPC response.</summary>
public sealed record ConfigResponse(bool Ok, string? Error = null, string? Value = null);

/// <summary>
/// Client for the config-write IPC. The app uses this to write config keys through
/// the SYSTEM service once config.db is write-protected. Every call is best-effort
/// and never throws — a failure (service down, pipe busy) returns
/// <c>Ok = false</c> so the caller can fall back or surface an error.
/// </summary>
public static class ConfigClient
{
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>Sends one request and returns the response (or a failure response).</summary>
    public static ConfigResponse Send(ConfigRequest request, int timeoutMs = 3000)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", ConfigPipe.PipeName, PipeDirection.InOut);
            pipe.Connect(timeoutMs);

            using var reader = new StreamReader(pipe);
            var writer = new StreamWriter(pipe) { AutoFlush = true };

            writer.WriteLine(JsonSerializer.Serialize(request, Json));
            var line = reader.ReadLine();
            if (line is null) return new ConfigResponse(false, "no response");

            return JsonSerializer.Deserialize<ConfigResponse>(line, Json)
                   ?? new ConfigResponse(false, "unparseable response");
        }
        catch (Exception ex)
        {
            return new ConfigResponse(false, ex.Message);
        }
    }

    /// <summary>Writes a config key via the service. Returns whether it was accepted.</summary>
    public static bool SetConfig(string key, string value, string? passcode) =>
        Send(new ConfigRequest(ConfigPipe.OpSet, Key: key, Value: value, Passcode: passcode)).Ok;

    /// <summary>Activates a Windows user given the device code (or parent passcode).</summary>
    public static bool Provision(string sid, string? code) =>
        Send(new ConfigRequest(ConfigPipe.OpProvision, Sid: sid, Passcode: code)).Ok;

    /// <summary>Records a failed unlock attempt (advances the lockout counter).</summary>
    public static bool RecordFailure() =>
        Send(new ConfigRequest(ConfigPipe.OpRecordFailure)).Ok;

    /// <summary>Clears the lockout counter after a success.</summary>
    public static bool ResetFailures() =>
        Send(new ConfigRequest(ConfigPipe.OpResetFailures)).Ok;
}
