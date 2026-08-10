using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Curfew.Core;
using Curfew.Core.Security;

namespace Curfew.Service;

/// <summary>Host config-write named pipe as SYSTEM. Connecting buys nothing: a write needs either the parent
/// passcode, or a caller the service has confirmed is an elevated administrator (see
/// <c>CallerIsElevatedAdmin</c>), or the first-run bootstrap window before a passcode exists.</summary>
internal sealed class ConfigPipeServer
{
    private readonly SettingsStore _config;

    /// <summary>Invoked (with the key) after a config key is successfully written, so the worker can apply
    /// the change immediately (e.g. re-run the content filter) instead of waiting for the next reboot or
    /// network change. Null = no listener.</summary>
    private readonly Action<string>? _onConfigChanged;
    private static readonly JsonSerializerOptions Json = new() { IncludeFields = false };

    /// <summary>Max time one connection may take to deliver request line; drop stallers else accept loop never returns to <see cref="NamedPipeServerStream.WaitForConnectionAsync"/> and parent writes starve.</summary>
    private static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Cap request line in bytes; without cap child streams gigabytes no newline and OOMs service, since <see cref="StreamReader.ReadLineAsync()"/> buffers till newline or EOF.</summary>
    private const int MaxRequestBytes = 8 * 1024;

    /// <param name="config">A config-writable settings store owned by the service.</param>
    public ConfigPipeServer(SettingsStore config, Action<string>? onConfigChanged = null)
    {
        _config = config;
        _onConfigChanged = onConfigChanged;
    }

    /// <summary>Accept connections till cancelled. Never throw out of loop.</summary>
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
                // pause so persistent failure no spin CPU
                try { await Task.Delay(500, ct).ConfigureAwait(false); } catch { return; }
            }
        }
    }

    private static NamedPipeServerStream CreateServer()
    {
        var security = new PipeSecurity();
        // ReadWrite only — CreateNewInstance would let local user host own instance, harvest cleartext passcodes. Only SYSTEM hosts.
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

        // bound connection: stalled child no hold accept loop; cancel read after ConnectionTimeout (or ct shutdown, first wins)
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ConnectionTimeout);

        string? line;
        try
        {
            line = await ReadRequestAsync(reader, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // client stalled past ConnectionTimeout. drop; loop re-accepts
            return;
        }
        if (line is null) return; // over MaxRequestBytes — refuse, no more buffering

        // Determine the caller's authority from the CONNECTION, not from anything in the request: an
        // elevated administrator is trusted without a passcode, everyone else keeps the full passcode +
        // lockout treatment. Nothing about this is forgeable by the request body.
        var callerIsAdmin = CallerIsElevatedAdmin(server);

        var response = Handle(line, callerIsAdmin);
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct).ConfigureAwait(false);
    }

    /// <summary>Whether the connected client holds an elevated administrator token.
    /// <para>Impersonating the pipe client is the only trustworthy way to ask this. A flag in the request, or
    /// a check performed by the CLI before connecting, would prove nothing — a child can write their own pipe
    /// client and claim anything. Here the answer comes from the token the OS attached to the connection.</para>
    /// <para>A filtered (non-elevated) administrator token carries Administrators as deny-only, so
    /// <see cref="WindowsPrincipal.IsInRole(WindowsBuiltInRole)"/> returns false for it. That is deliberate:
    /// "administrator" here means actually elevated, not merely a member of the group.</para>
    /// <para>Fails closed. Any error — impersonation refused, token unavailable — is treated as "not an
    /// administrator", which falls back to the passcode gate rather than opening one.</para></summary>
    private static bool CallerIsElevatedAdmin(NamedPipeServerStream server)
    {
        try
        {
            var isAdmin = false;
            server.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent();
                isAdmin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            });
            return isAdmin;
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config pipe: could not identify caller ({ex.Message}); treating as unprivileged");
            return false;
        }
    }

    /// <summary>Read one newline-terminated request; return <c>null</c> once bytes exceed <see cref="MaxRequestBytes"/>. Char-at-a-time, not <see cref="StreamReader.ReadLineAsync()"/>, which buffers whole line before cap applies.</summary>
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
                if (sb.Length > MaxRequestBytes) return null; // over cap — refuse
            }
        }
        return null;
    }

    private ConfigResponse Handle(string? line, bool callerIsAdmin)
    {
        if (string.IsNullOrWhiteSpace(line)) return new ConfigResponse(false, "empty request");

        ConfigRequest? request;
        try { request = JsonSerializer.Deserialize<ConfigRequest>(line, Json); }
        catch (JsonException) { return new ConfigResponse(false, "bad request"); }
        if (request is null) return new ConfigResponse(false, "bad request");

        return request.Op switch
        {
            ConfigPipe.OpSet => HandleSet(request, callerIsAdmin),
            ConfigPipe.OpProvision => HandleProvision(request, callerIsAdmin),
            // liveness only: no auth, no state touched, so it cannot be used to probe or grind anything
            ConfigPipe.OpPing => new ConfigResponse(true),
            ConfigPipe.OpRecordFailure => HandleRecordFailure(),
            ConfigPipe.OpResetFailures => HandleResetFailures(request, callerIsAdmin),
            // deliberately NOT admin-bypassed: redeeming is a one-time-code operation, and an
            // administrator has no need of it (they can write the underlying keys directly).
            ConfigPipe.OpRedeem => HandleRedeem(request),
            _ => new ConfigResponse(false, $"unknown op '{request.Op}'"),
        };
    }

    /// <summary>Verify an offline unlock (TOTP) code against the device secret and, on success, advance the
    /// replay counter — both in write-protected config.db, so a child (who in the offline-grant case knows the
    /// code) cannot reset the counter to replay it. The code arrives in <see cref="ConfigRequest.Passcode"/>.
    /// Brute-force lockout is enforced here too, since a direct pipe client bypasses the lock UI.</summary>
    private ConfigResponse HandleRedeem(ConfigRequest request)
    {
        var secret = _config.Get("unlock_secret");
        if (string.IsNullOrEmpty(secret)) return new ConfigResponse(false, "no unlock secret");

        if (IsLockedOut(out var locked)) return locked;

        var minCounter = long.TryParse(_config.Get("unlock_last_counter"), out var last) ? last : long.MinValue;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // window=10 (+/-5 min) matches the lock UI; replay blocked by minCounter from config
        if (!UnlockCode.Verify(secret, request.Passcode, now, 10, minCounter, out var matched))
            return RecordFailureAndReject();

        // advance the counter (SYSTEM-only write) so this and earlier steps can't be redeemed again
        _config.Set("unlock_last_counter", matched.ToString());
        return new ConfigResponse(true);
    }

    /// <summary>Set up new Windows user: write per-user daily limit (all weekdays) and add SID to set-up list.
    /// Authorised by the parent passcode, or by an elevated administrator (the CLI's <c>provision</c> path).
    /// For the passcode route the brute-force lockout is enforced here, not just in the lock UI.</summary>
    private ConfigResponse HandleProvision(ConfigRequest request, bool callerIsAdmin)
    {
        if (string.IsNullOrEmpty(request.Sid))
            return new ConfigResponse(false, "sid required");

        // an elevated administrator provisions without the passcode (and without being subject to, or
        // contributing to, the brute-force counter — there is nothing to brute-force on this path).
        if (!callerIsAdmin)
        {
            if (IsLockedOut(out var locked)) return locked;

            var passcode = _config.Get("passcode");
            if (string.IsNullOrEmpty(passcode) || !PasscodeHash.Verify(request.Passcode, passcode))
                return RecordFailureAndReject();
        }

        // persist per-user daily limit (every weekday) so budget seeds from it not device default. clamp sane range; skip if absent
        if (int.TryParse(request.Value, out var limitMinutes))
        {
            limitMinutes = Math.Clamp(limitMinutes, 0, 24 * 60);
            var value = limitMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var dayKey in SettingsStore.WeekdayKeys)
                _config.Set(SettingsPartition.Scope(dayKey, request.Sid), value);
        }

        _config.Set("provisioned_users", UserProvisioning.Add(_config.Get("provisioned_users"), request.Sid));
        return new ConfigResponse(true);
    }

    /// <summary>Bump failed-attempt lockout counter (no auth — only rate-limits).</summary>
    private ConfigResponse HandleRecordFailure()
    {
        RecordFailure();
        return new ConfigResponse(true);
    }

    /// <summary>Increment failed-attempt counter and stamp time (UTC seconds).</summary>
    private void RecordFailure()
    {
        var count = int.TryParse(_config.Get("failed_attempts"), out var n) ? n : 0;
        _config.Set("failed_attempts", (count + 1).ToString());
        _config.Set("failed_attempt_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString());
    }

    /// <summary>True if brute-force backoff blocks attempt; enforced server-side since child drives pipe direct, skip UI check. <paramref name="response"/> carries retry-after seconds when locked.</summary>
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

    /// <summary>Record wrong guess in handler (so direct pipe client no skip <see cref="ConfigClient.RecordFailure"/>) and return rejection.</summary>
    private ConfigResponse RecordFailureAndReject()
    {
        RecordFailure();
        return new ConfigResponse(false, "wrong code");
    }

    /// <summary>Clear failed-attempt counter after success. Gated on parent passcode: unauthenticated reset would let child zero counter between guesses and defeat lockout.</summary>
    private ConfigResponse HandleResetFailures(ConfigRequest request, bool callerIsAdmin)
    {
        var passcode = _config.Get("passcode");

        // bootstrap: no passcode yet, reset trivially allowed, no touch counter (nothing to brute-force)
        // an elevated administrator may also clear it: this is the recovery path for a parent who has
        // locked themselves out, and it grants nothing they could not do by editing config.db directly.
        if (string.IsNullOrEmpty(passcode) || callerIsAdmin)
        {
            _config.Set("failed_attempts", "0");
            return new ConfigResponse(true);
        }

        // refuse while locked out, no eval code; else child grinds reset guesses (each free PBKDF2)
        if (IsLockedOut(out var locked)) return locked;

        if (!PasscodeHash.Verify(request.Passcode, passcode)) return RecordFailureAndReject();

        _config.Set("failed_attempts", "0");
        return new ConfigResponse(true);
    }

    private ConfigResponse HandleSet(ConfigRequest request, bool callerIsAdmin)
    {
        if (string.IsNullOrEmpty(request.Key) || request.Value is null)
            return new ConfigResponse(false, "key/value required");

        // only config keys writable via pipe; state is child-writable. this bound applies to
        // administrators too — the partition is about which store owns a key, not about authority.
        if (SettingsPartition.StoreFor(request.Key) != SettingsStoreKind.Config)
            return new ConfigResponse(false, "not a config key");

        // gate on parent passcode, except first-run bootstrap (no passcode yet)
        var stored = _config.Get("passcode");
        if (!string.IsNullOrEmpty(stored) && !callerIsAdmin)
        {
            // enforce lockout and count wrong guesses here so direct pipe client no grind passcode by skipping client-side check
            if (IsLockedOut(out var locked)) return locked;
            if (!PasscodeHash.Verify(request.Passcode, stored))
            {
                RecordFailure();
                return new ConfigResponse(false, "passcode required");
            }
        }

        _config.Set(request.Key, request.Value);
        _onConfigChanged?.Invoke(request.Key);
        return new ConfigResponse(true);
    }
}
