using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Curfew.Core;
using Curfew.Core.Security;

namespace Curfew.Service;

/// <summary>
/// Hosts the config-write named pipe as SYSTEM. The app sends config writes here
/// once <c>config.db</c> is read-only for ordinary users; the service verifies the
/// parent passcode and performs the write itself. The pipe ACL lets any
/// authenticated user connect, but every write is passcode-gated (except the
/// first-run bootstrap before a passcode exists), so connecting buys nothing.
/// </summary>
internal sealed class ConfigPipeServer
{
    private readonly SettingsStore _config;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>
    /// How long a single connection may take to deliver its request line. The child
    /// is the adversary and any AuthenticatedUser may connect, so a client that
    /// stalls (never sends a newline) must be dropped: without this the accept loop
    /// never returns to <see cref="NamedPipeServerStream.WaitForConnectionAsync"/>
    /// and every legitimate parent config write is denied for as long as the child
    /// holds the pipe.
    /// </summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Cap on the request line, in bytes. A request is a small JSON envelope; without
    /// a cap a child could stream gigabytes with no newline and OOM the SYSTEM
    /// service, since <see cref="StreamReader.ReadLineAsync()"/> buffers until a
    /// newline or EOF.
    /// </summary>
    private const int MaxRequestBytes = 8 * 1024;

    /// <param name="config">A config-writable settings store owned by the service.</param>
    public ConfigPipeServer(SettingsStore config) => _config = config;

    /// <summary>Accepts connections until cancelled. Never throws out of the loop.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var server = CreateServer();
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                await HandleConnectionAsync(server, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                ServiceLog.Write($"config pipe: {ex.Message}");
                // Brief pause so a persistent failure cannot spin the CPU.
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        // ReadWrite only — granting CreateNewInstance would let any local user add
        // their own server instance under this name and harvest the passcodes that
        // clients send in the clear. Only SYSTEM (below) may host instances.
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(
            ConfigPipe.PipeName, PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        using var reader = new StreamReader(server);
        var writer = new StreamWriter(server) { AutoFlush = true };

        // Bound the connection: a stalled or slow client (the child) must never hold
        // the accept loop open, so the request read is cancelled after
        // ConnectionTimeout (or on service shutdown via ct, whichever is first).
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectionTimeout);

        string? line;
        try
        {
            line = await ReadRequestAsync(reader, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // The client stalled past ConnectionTimeout. Drop it; the loop re-accepts.
            return;
        }
        if (line is null) return; // request exceeded MaxRequestBytes — refuse without buffering more

        var response = Handle(line);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads one newline-terminated request, refusing (returns <c>null</c>) once the
    /// accumulated bytes exceed <see cref="MaxRequestBytes"/> so an unbounded body
    /// cannot exhaust memory. Reads a character at a time rather than
    /// <see cref="StreamReader.ReadLineAsync()"/> precisely because the latter would
    /// buffer the whole line before the cap could apply.
    /// </summary>
    private static async Task<string?> ReadRequestAsync(StreamReader reader, CancellationToken ct)
    {
        var sb = new System.Text.StringBuilder();
        var buffer = new char[256];
        while (sb.Length <= MaxRequestBytes)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (read == 0) return sb.Length == 0 ? null : sb.ToString(); // EOF before newline

            for (var i = 0; i < read; i++)
            {
                if (buffer[i] == '\n') return sb.ToString();
                if (buffer[i] != '\r') sb.Append(buffer[i]);
                if (sb.Length > MaxRequestBytes) return null; // over the cap — refuse
            }
        }
        return null;
    }

    private ConfigResponse Handle(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return new ConfigResponse(false, "empty request");

        ConfigRequest? request;
        try { request = JsonSerializer.Deserialize<ConfigRequest>(line, Json); }
        catch (JsonException) { return new ConfigResponse(false, "bad request"); }
        if (request is null) return new ConfigResponse(false, "bad request");

        return request.Op switch
        {
            ConfigPipe.OpSet => HandleSet(request),
            ConfigPipe.OpProvision => HandleProvision(request),
            ConfigPipe.OpRecordFailure => HandleRecordFailure(),
            ConfigPipe.OpResetFailures => HandleResetFailures(request),
            _ => new ConfigResponse(false, $"unknown op '{request.Op}'"),
        };
    }

    /// <summary>Activates a Windows user after the device code (or parent passcode) is verified.</summary>
    private ConfigResponse HandleProvision(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(request.Sid))
            return new ConfigResponse(false, "sid required");

        // The pipe is the authoritative verification path and any AuthenticatedUser
        // can connect, so the brute-force lockout must be enforced here — not just in
        // the client UIs. Refuse while locked out without evaluating the code (a
        // short device_code would otherwise be ground directly against the service).
        if (IsLockedOut(out var locked)) return locked;

        var deviceCode = _config.Get("device_code");
        var passcode = _config.Get("passcode");
        var ok = (!string.IsNullOrEmpty(deviceCode) && PasscodeHash.Verify(request.Passcode, deviceCode))
                 || (!string.IsNullOrEmpty(passcode) && PasscodeHash.Verify(request.Passcode, passcode));
        if (!ok) return RecordFailureAndReject();

        _config.Set("provisioned_users", UserProvisioning.Add(_config.Get("provisioned_users"), request.Sid));
        return new ConfigResponse(true);
    }

    /// <summary>Advances the failed-attempt lockout counter (no auth — it only rate-limits).</summary>
    private ConfigResponse HandleRecordFailure()
    {
        RecordFailure();
        return new ConfigResponse(true);
    }

    /// <summary>Increments the failed-attempt counter and stamps the time (UTC seconds).</summary>
    private void RecordFailure()
    {
        var count = int.TryParse(_config.Get("failed_attempts"), out var n) ? n : 0;
        _config.Set("failed_attempts", (count + 1).ToString());
        _config.Set("failed_attempt_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    /// <summary>
    /// Whether the brute-force backoff currently blocks a verification attempt, using
    /// the same counter the client UIs read. Enforced server-side because the pipe is
    /// the authoritative path and a child can drive it directly, skipping any UI
    /// check; <paramref name="response"/> carries the retry-after seconds when locked.
    /// </summary>
    private bool IsLockedOut(out ConfigResponse response)
    {
        var state = new LockoutState(
            int.TryParse(_config.Get("failed_attempts"), out var n) ? n : 0,
            long.TryParse(_config.Get("failed_attempt_at"), out var at) ? at : 0);

        if (LockoutPolicy.IsLockedOut(state, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var retryAfter))
        {
            response = new ConfigResponse(false, $"locked out, retry in {retryAfter}s");
            return true;
        }
        response = new ConfigResponse(true);
        return false;
    }

    /// <summary>
    /// Records a wrong guess inside the handler (so a direct pipe client cannot skip
    /// <see cref="ConfigClient.RecordFailure"/>) and returns the rejection response.
    /// </summary>
    private ConfigResponse RecordFailureAndReject()
    {
        RecordFailure();
        return new ConfigResponse(false, "wrong code");
    }

    /// <summary>
    /// Clears the failed-attempt counter after a success. Gated on the code that
    /// just succeeded (parent passcode or device code): an unauthenticated reset
    /// would let the child zero the counter between guesses and defeat the
    /// brute-force lockout entirely.
    /// </summary>
    private ConfigResponse HandleResetFailures(ConfigRequest request)
    {
        var passcode = _config.Get("passcode");
        var deviceCode = _config.Get("device_code");

        // Bootstrap: no code is set yet, so the reset is trivially allowed and must
        // not touch the counter (there is nothing to brute-force against).
        if (string.IsNullOrEmpty(passcode) && string.IsNullOrEmpty(deviceCode))
        {
            _config.Set("failed_attempts", "0");
            return new ConfigResponse(true);
        }

        // Refuse while locked out without evaluating the code; otherwise a child could
        // grind reset guesses (each a free PBKDF2) against the service unhindered.
        if (IsLockedOut(out var locked)) return locked;

        var authenticated =
            (!string.IsNullOrEmpty(passcode) && PasscodeHash.Verify(request.Passcode, passcode))
            || (!string.IsNullOrEmpty(deviceCode) && PasscodeHash.Verify(request.Passcode, deviceCode));
        if (!authenticated) return RecordFailureAndReject();

        _config.Set("failed_attempts", "0");
        return new ConfigResponse(true);
    }

    private ConfigResponse HandleSet(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(request.Key) || request.Value is null)
            return new ConfigResponse(false, "key/value required");

        // Only config keys may be written through the pipe; state is child-writable.
        if (SettingsPartition.StoreFor(request.Key) != SettingsStoreKind.Config)
            return new ConfigResponse(false, "not a config key");

        // Gate on the parent passcode, except the first-run bootstrap (no passcode yet).
        var stored = _config.Get("passcode");
        if (!string.IsNullOrEmpty(stored))
        {
            // The pipe is the authoritative verification path; enforce the brute-force
            // lockout and count wrong guesses here so a direct pipe client cannot grind
            // the passcode by skipping the client-side check.
            if (IsLockedOut(out var locked)) return locked;
            if (!PasscodeHash.Verify(request.Passcode, stored))
            {
                RecordFailure();
                return new ConfigResponse(false, "passcode required");
            }
        }

        _config.Set(request.Key, request.Value);
        return new ConfigResponse(true);
    }
}
