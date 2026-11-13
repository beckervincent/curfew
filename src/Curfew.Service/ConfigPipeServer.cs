using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Curfew.Core;
using Curfew.Core.Security;

namespace Curfew.Service;

/// <summary>Host config-write named pipe as SYSTEM. Every write passcode-gated (except first-run bootstrap before <c>config.db</c> passcode exists); connecting buys nothing.</summary>
internal sealed class ConfigPipeServer
{
    private readonly SettingsStore _config;

    /// <summary>Service-private credentials (passcode hash, unlock seed, lockout counters). Never readable by users.</summary>
    private readonly SecretStore _secrets;

    /// <summary>Grants older than this are refused on consume (overlay polls every second).</summary>
    private const long GrantLifetimeSeconds = 120;

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
    public ConfigPipeServer(SettingsStore config, SecretStore secrets, Action<string>? onConfigChanged = null)
    {
        _config = config;
        _secrets = secrets;
        _onConfigChanged = onConfigChanged;
        try { _secrets.MigrateFrom(_config); }
        catch (Exception ex) { ServiceLog.Write($"config pipe: secret migration failed: {ex.Message}"); }
    }

    /// <summary>Authenticated identity of a pipe client: Windows SID (from its token) and terminal-services session.</summary>
    private readonly record struct Caller(string? Sid, int Session);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientSessionId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint sessionId);

    /// <summary>Identify the client from its token (Identification-level impersonation) + pipe session. Never trust request fields for identity.</summary>
    private static Caller Identify(NamedPipeServerStream server)
    {
        string? sid = null;
        try
        {
            server.RunAsClient(() =>
            {
                using var identity = WindowsIdentity.GetCurrent(ifImpersonating: true);
                sid = identity?.User?.Value;
            });
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config pipe: client identity unavailable: {ex.Message}");
        }

        var session = GetNamedPipeClientSessionId(server.SafePipeHandle, out var s) ? (int)s : -1;
        return new Caller(sid, session);
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

        var response = Handle(line, Identify(server));
        await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json).AsMemory(), ct).ConfigureAwait(false);
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

    private ConfigResponse Handle(string? line, Caller caller)
    {
        if (string.IsNullOrWhiteSpace(line)) return new ConfigResponse(false, "empty request");

        ConfigRequest? request;
        try { request = JsonSerializer.Deserialize<ConfigRequest>(line, Json); }
        catch (JsonException) { return new ConfigResponse(false, "bad request"); }
        if (request is null) return new ConfigResponse(false, "bad request");

        try
        {
            return request.Op switch
            {
                ConfigPipe.OpSet => HandleSet(request),
                ConfigPipe.OpProvision => HandleProvision(request),
                ConfigPipe.OpResetFailures => HandleResetFailures(request),
                ConfigPipe.OpRedeem => HandleRedeem(request),
                ConfigPipe.OpVerify => CheckPasscode(request.Passcode, out var r) ? new ConfigResponse(true) : r,
                ConfigPipe.OpAuthorize => HandleAuthorize(request, caller),
                ConfigPipe.OpConsume => HandleConsume(request, caller),
                ConfigPipe.OpLedger => HandleLedger(request, caller),
                ConfigPipe.OpLockState => HandleLockState(request, caller),
                ConfigPipe.OpSecret => HandleSecret(request),
                ConfigPipe.OpUpdate => HandleUpdate(request),
                _ => new ConfigResponse(false, $"unknown op '{request.Op}'"),
            };
        }
        catch (Exception ex)
        {
            ServiceLog.Write($"config pipe: {request.Op} failed: {ex.Message}");
            return new ConfigResponse(false, "internal error");
        }
    }

    // ---- credentials -------------------------------------------------------

    /// <summary>Verify parent passcode with server-side lockout. Records the failure / clears the counter itself,
    /// so no client ever reports failures (an unauthenticated "fail" op let a child keep the parent locked out).</summary>
    private bool CheckPasscode(string? passcode, out ConfigResponse response)
    {
        if (IsLockedOut(out response)) return false;

        var stored = _secrets.Get("passcode");
        if (string.IsNullOrEmpty(stored) || !PasscodeHash.Verify(passcode, stored))
        {
            response = RecordFailureAndReject();
            return false;
        }

        ResetFailures();
        response = new ConfigResponse(true);
        return true;
    }

    /// <summary>Verify an offline unlock (TOTP) code and advance the replay counter (no lockout check - caller does it).</summary>
    private bool TryRedeemCode(string? code)
    {
        var secret = _secrets.Get("unlock_secret");
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(code)) return false;

        var minCounter = long.TryParse(_secrets.Get("unlock_last_counter"), out var last) ? last : long.MinValue;
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // window=10 (+/-5 min); replay blocked by minCounter
        if (!UnlockCode.Verify(secret, code, now, 10, minCounter, out var matched)) return false;
        _secrets.Set("unlock_last_counter", matched.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Verify an offline unlock code without granting anything (legacy op; lock screen now uses authorize).</summary>
    private ConfigResponse HandleRedeem(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(_secrets.Get("unlock_secret"))) return new ConfigResponse(false, "no unlock secret");
        if (IsLockedOut(out var locked)) return locked;
        return TryRedeemCode(request.Passcode) ? new ConfigResponse(true) : RecordFailureAndReject();
    }

    /// <summary>Set up new Windows user after parent passcode verified. Brute-force lockout enforced here.</summary>
    private ConfigResponse HandleProvision(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(request.Sid)) return new ConfigResponse(false, "sid required");
        if (!CheckPasscode(request.Passcode, out var rejected)) return rejected;
        Provision(request.Sid, request.Value);
        return new ConfigResponse(true);
    }

    /// <summary>write per-user daily limit (all weekdays, clamped) + add SID to set-up list</summary>
    private void Provision(string sid, string? limit)
    {
        if (int.TryParse(limit, out var limitMinutes))
        {
            var value = Math.Clamp(limitMinutes, 0, 24 * 60).ToString(CultureInfo.InvariantCulture);
            foreach (var dayKey in SettingsStore.WeekdayKeys)
                _config.Set(SettingsPartition.Scope(dayKey, sid), value);
        }
        _config.Set("provisioned_users", UserProvisioning.Add(_config.Get("provisioned_users"), sid));
    }

    private void RecordFailure()
    {
        var count = int.TryParse(_secrets.Get("failed_attempts"), out var n) ? n : 0;
        _secrets.Set("failed_attempts", (count + 1).ToString(CultureInfo.InvariantCulture));
        _secrets.Set("failed_attempt_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
    }

    private void ResetFailures() => _secrets.Set("failed_attempts", "0");

    /// <summary>True if brute-force backoff blocks attempt. <paramref name="response"/> carries retry-after seconds when locked.</summary>
    private bool IsLockedOut(out ConfigResponse response)
    {
        var state = new LockoutState(
            int.TryParse(_secrets.Get("failed_attempts"), out var n) ? n : 0,
            long.TryParse(_secrets.Get("failed_attempt_at"), out var at) ? at : 0);

        if (LockoutPolicy.IsLockedOut(state, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), out var retryAfter))
        {
            response = new ConfigResponse(false, $"locked out, retry in {retryAfter}s");
            return true;
        }
        response = new ConfigResponse(true);
        return false;
    }

    private ConfigResponse RecordFailureAndReject()
    {
        RecordFailure();
        return new ConfigResponse(false, "wrong code");
    }

    /// <summary>Clear failed-attempt counter. Passcode-gated (a free reset would defeat lockout).</summary>
    private ConfigResponse HandleResetFailures(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(_secrets.Get("passcode")))
        {
            ResetFailures();
            return new ConfigResponse(true);
        }
        return CheckPasscode(request.Passcode, out var r) ? new ConfigResponse(true) : r;
    }

    /// <summary>Return (optionally rotating first) the offline unlock seed for enrolment display. Passcode-gated.</summary>
    private ConfigResponse HandleSecret(ConfigRequest request)
    {
        if (!CheckPasscode(request.Passcode, out var rejected)) return rejected;

        var secret = _secrets.Get("unlock_secret");
        if (string.IsNullOrEmpty(secret) || request.Value == "rotate")
        {
            secret = UnlockCode.GenerateSecret();
            _secrets.Set("unlock_secret", secret);
            _secrets.Set("unlock_last_counter", long.MinValue.ToString(CultureInfo.InvariantCulture));
        }
        return new ConfigResponse(true, Value: secret);
    }

    /// <summary>Parent-requested update: download, verify and launch from service-protected staging (never a user-writable path). Runs in background; reply means "started".</summary>
    private ConfigResponse HandleUpdate(ConfigRequest request)
    {
        if (!CheckPasscode(request.Passcode, out var rejected)) return rejected;

        var version = typeof(ConfigPipeServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        _ = Task.Run(async () =>
        {
            try
            {
                using var settings = CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now), configWritable: true);
                await UpdateService.RunAsync(settings, version, CancellationToken.None, force: true).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ServiceLog.Write($"manual update failed: {ex.Message}");
            }
        });
        return new ConfigResponse(true);
    }

    // ---- config writes -----------------------------------------------------

    /// <summary>Keys only the service itself writes (never via set).</summary>
    private static bool IsServiceOwned(string key) =>
        key is "passcode_set" or "grandfathered_users" or "ledger_migrated"
        || key.StartsWith(SettingsPartition.GrantPrefix, StringComparison.Ordinal);

    private ConfigResponse HandleSet(ConfigRequest request)
    {
        if (string.IsNullOrEmpty(request.Key) || request.Value is null)
            return new ConfigResponse(false, "key/value required");

        var isSecret = SettingsPartition.IsSecret(request.Key);

        // only policy keys + the passcode writable via set; state is child-writable, ledger/grants service-owned
        if (!isSecret && (SettingsPartition.StoreFor(request.Key) != SettingsStoreKind.Config || IsServiceOwned(request.Key)))
            return new ConfigResponse(false, "not a config key");
        if (isSecret && request.Key != "passcode")
            return new ConfigResponse(false, "not a config key");

        // gate on parent passcode, except first-run bootstrap (no passcode yet)
        if (!string.IsNullOrEmpty(_secrets.Get("passcode"))
            && !CheckPasscode(request.Passcode, out var rejected))
            return rejected;

        if (request.Key == "passcode")
        {
            if (request.Value.Length == 0) return new ConfigResponse(false, "passcode required");
            // never store plaintext, whatever the client sent
            var hash = PasscodeHash.IsHashed(request.Value) ? request.Value : PasscodeHash.Hash(request.Value);
            _secrets.Set("passcode", hash);
            _config.Set("passcode_set", "1");
            // seed the offline unlock seed service-side (clients can no longer read or write it)
            if (string.IsNullOrEmpty(_secrets.Get("unlock_secret")))
                _secrets.Set("unlock_secret", UnlockCode.GenerateSecret());
            return new ConfigResponse(true);
        }

        _config.Set(request.Key, request.Value);
        _onConfigChanged?.Invoke(request.Key);
        return new ConfigResponse(true);
    }

    // ---- session-bound grants ---------------------------------------------

    /// <summary>Authorize a lock/tray action for the caller's own SID + session and publish a one-shot grant.
    /// Gated actions need the parent passcode; lock-screen time actions alternatively accept an offline unlock
    /// code (granted as "redeem"). The grant is written to write-protected config.db, so a child can't forge one,
    /// and it names the caller's session, so it can't be aimed at another user's overlay.</summary>
    private ConfigResponse HandleAuthorize(ConfigRequest request, Caller caller)
    {
        if (string.IsNullOrEmpty(caller.Sid) || caller.Session <= 0) return new ConfigResponse(false, "unidentified caller");
        var action = request.Key ?? string.Empty;

        if (ConfigPipe.UngatedActions.Contains(action)) return IssueGrant(caller, action);
        if (!ConfigPipe.GatedActions.Contains(action)) return new ConfigResponse(false, "unknown action");

        if (IsLockedOut(out var locked)) return locked;

        var stored = _secrets.Get("passcode");
        if (!string.IsNullOrEmpty(stored) && PasscodeHash.Verify(request.Passcode, stored))
        {
            ResetFailures();
            if (action == "provision") Provision(caller.Sid, request.Value);
            return IssueGrant(caller, action);
        }

        // offline unlock code: bonus time on an ordinary lock only (never new-user setup / tray control)
        if (action is "extend15" or "extend30" or "extend60" or "unlock" or "ignore_schedule"
            && TryRedeemCode(request.Passcode))
            return IssueGrant(caller, "redeem");

        return RecordFailureAndReject();
    }

    private ConfigResponse IssueGrant(Caller caller, string action)
    {
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        _config.Set(ConfigPipe.GrantKey(caller.Sid!, caller.Session), $"{id}|{action}|{now}");
        return new ConfigResponse(true, Value: action);
    }

    /// <summary>Consume the caller session's grant: delete it, apply any time credit to the ledger atomically
    /// (service is the only ledger writer, so no lost-update race with the overlay), return "action|balance".</summary>
    private ConfigResponse HandleConsume(ConfigRequest request, Caller caller)
    {
        if (string.IsNullOrEmpty(caller.Sid) || caller.Session <= 0) return new ConfigResponse(false, "unidentified caller");

        var key = ConfigPipe.GrantKey(caller.Sid, caller.Session);
        if (!ConfigPipe.TryParseGrant(_config.Get(key), out var id, out var action, out var issued)
            || !string.Equals(id, request.Value, StringComparison.Ordinal))
            return new ConfigResponse(false, "no grant");

        _config.Delete(key);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (now - issued > GrantLifetimeSeconds || issued - now > 5) return new ConfigResponse(false, "stale grant");

        var today = DateOnly.FromDateTime(DateTime.Now);
        int creditSeconds;
        switch (action)
        {
            case "extend15": creditSeconds = 15 * 60; break;
            case "extend30": creditSeconds = 30 * 60; break;
            case "extend45": creditSeconds = 45 * 60; break;
            case "extend60": creditSeconds = 60 * 60; break;
            case "redeem":
                creditSeconds = Math.Max(0, IntFor("unlock_bonus_minutes", caller.Sid, 30)) * 60;
                break;
            case "break":
                creditSeconds = RedeemBreak(caller.Sid, today, now);
                if (creditSeconds <= 0) return new ConfigResponse(false, "no break available");
                break;
            default:
                return new ConfigResponse(true, Value: $"{action}|");
        }

        var balance = Credit(caller.Sid, today, creditSeconds);
        return new ConfigResponse(true, Value: $"{action}|{balance.ToString(CultureInfo.InvariantCulture)}");
    }

    /// <summary>Spend the available break (parent pause policy) as bonus time; returns granted seconds, 0 if none.</summary>
    private int RedeemBreak(string sid, DateOnly today, long now)
    {
        if (!BoolFor("pause_enabled", sid, true)) return 0;

        var date = Date(today);
        var usedKey = $"pause_used_{sid}_{date}";
        var lastEndKey = $"pause_last_end_{sid}";
        var used = int.TryParse(_config.Get(usedKey), out var u) ? u : 0;
        var dailyBudget = IntFor("pause_daily_budget", sid, 45) * 60;
        if (PauseRules.RemainingBudget(dailyBudget, used) <= 0) return 0;

        if (long.TryParse(_config.Get(lastEndKey), out var lastEnd) && lastEnd > 0
            && now - lastEnd < IntFor("pause_cooldown", sid, 15) * 60L)
            return 0;

        var granted = PauseRules.MaxPauseDuration(IntFor("pause_max_duration", sid, 20) * 60, dailyBudget, used);
        if (granted <= 0) return 0;

        _config.Set(usedKey, (used + granted).ToString(CultureInfo.InvariantCulture));
        _config.Set(lastEndKey, now.ToString(CultureInfo.InvariantCulture));
        return granted;
    }

    /// <summary>Add <paramref name="seconds"/> to today's remaining balance (seeding from the daily limit when the overlay hasn't persisted yet); returns the new balance.</summary>
    private int Credit(string sid, DateOnly today, int seconds)
    {
        var key = $"remaining_time_{sid}_{Date(today)}";
        var limit = DailyLimit(sid, today);
        var current = TimeKeeper.InitialRemaining(int.TryParse(_config.Get(key), out var r) ? r : null, limit);
        var updated = TimeKeeper.Extend(Math.Max(0, current), seconds / 60);
        _config.Set(key, updated.ToString(CultureInfo.InvariantCulture));
        if (_config.Get($"remaining_limit_{sid}_{Date(today)}") is null)
            _config.Set($"remaining_limit_{sid}_{Date(today)}", limit.ToString(CultureInfo.InvariantCulture));
        return updated;
    }

    // ---- ledger --------------------------------------------------------------

    /// <summary>Overlay accounting write. Accepted only for the caller's own SID, a date within a day of today, and
    /// only in the restrictive direction: remaining time may go down (or up exactly by a parent limit increase),
    /// usage/pause counters only up. Every way to GAIN time goes through an authorized grant instead.</summary>
    private ConfigResponse HandleLedger(ConfigRequest request, Caller caller)
    {
        var key = request.Key;
        var sid = caller.Sid;
        if (string.IsNullOrEmpty(sid) || string.IsNullOrEmpty(key) || request.Value is null)
            return new ConfigResponse(false, "bad ledger write");
        if (SettingsPartition.StoreFor(key) != SettingsStoreKind.Ledger) return new ConfigResponse(false, "not a ledger key");

        var prefix = SettingsPartition.LedgerPrefixes.First(p => key.StartsWith(p, StringComparison.Ordinal));
        var rest = key[prefix.Length..];
        if (!rest.StartsWith(sid, StringComparison.Ordinal)) return new ConfigResponse(false, "foreign sid");
        rest = rest[sid.Length..];

        var today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly date = today;
        if (prefix == "pause_last_end_")
        {
            if (rest.Length != 0) return new ConfigResponse(false, "bad key");
        }
        else if (rest.Length != 11 || rest[0] != '_'
                 || !DateOnly.TryParseExact(rest[1..], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
                 || Math.Abs(date.DayNumber - today.DayNumber) > 1)
        {
            return new ConfigResponse(false, "bad date");
        }

        var stored = _config.Get(key);
        switch (prefix)
        {
            case "remaining_time_":
            {
                if (!int.TryParse(request.Value, out var value) || value < 0) return new ConfigResponse(false, "bad value");
                var limitNow = DailyLimit(sid, date);
                var limitKey = $"remaining_limit_{sid}{rest}";
                int allowed;
                if (!int.TryParse(stored, out var current))
                    allowed = limitNow * 60;
                else
                {
                    var limitAt = int.TryParse(_config.Get(limitKey), out var l) ? l : limitNow;
                    allowed = current + Math.Max(0, limitNow - limitAt) * 60;
                }
                if (value > allowed) return new ConfigResponse(false, "remaining may only decrease");
                _config.Set(key, value.ToString(CultureInfo.InvariantCulture));
                _config.Set(limitKey, limitNow.ToString(CultureInfo.InvariantCulture));
                return new ConfigResponse(true);
            }
            case "used_time_":
            case "pause_used_":
            {
                if (!int.TryParse(request.Value, out var value) || value < 0 || value > 2 * 86_400) return new ConfigResponse(false, "bad value");
                // a used_time row would grandfather an unapproved user out of the setup gate
                if (prefix == "used_time_" && !IsKnownUser(sid)) return new ConfigResponse(false, "user not set up");
                if (int.TryParse(stored, out var current) && value < current) return new ConfigResponse(false, "usage may only increase");
                _config.Set(key, value.ToString(CultureInfo.InvariantCulture));
                return new ConfigResponse(true);
            }
            case "pause_last_end_":
            {
                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                if (!long.TryParse(request.Value, out var value) || value > now + 120) return new ConfigResponse(false, "bad value");
                if (long.TryParse(stored, out var current) && value < current) return new ConfigResponse(false, "may only increase");
                _config.Set(key, value.ToString(CultureInfo.InvariantCulture));
                return new ConfigResponse(true);
            }
            case "app_usage_":
            {
                // per-app counters merge by max: a write can add usage, never erase it
                var merged = AppUsageMap.Parse(stored);
                foreach (var (app, used) in AppUsageMap.Parse(request.Value))
                    if (used >= 0 && (!merged.TryGetValue(app, out var old) || used > old))
                        merged[app] = Math.Min(used, 2 * 86_400);
                _config.Set(key, AppUsageMap.Serialize(merged));
                return new ConfigResponse(true);
            }
            default:
                return new ConfigResponse(false, "not writable");
        }
    }

    private bool IsKnownUser(string sid) =>
        UserProvisioning.IsProvisioned(_config.Get("provisioned_users"), sid)
        || UserProvisioning.IsProvisioned(_config.Get("grandfathered_users"), sid);

    // ---- lock state (Task Manager lockdown) -----------------------------------

    /// <summary>Overlay reports lock up/down; bound to the caller's token SID (never a SID named in shared state).</summary>
    private static ConfigResponse HandleLockState(ConfigRequest request, Caller caller)
    {
        if (string.IsNullOrEmpty(caller.Sid)) return new ConfigResponse(false, "unidentified caller");
        LockStateRegistry.Set(caller.Sid, request.Value == "1");
        return new ConfigResponse(true);
    }

    // ---- helpers ---------------------------------------------------------------

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>per-user config value with global fallback (same resolution the overlay's scoped store uses)</summary>
    private string? GetFor(string key, string sid) =>
        _config.Get(SettingsPartition.Scope(key, sid)) ?? _config.Get(key);

    private int IntFor(string key, string sid, int fallback) =>
        int.TryParse(GetFor(key, sid), out var v) ? v : fallback;

    private bool BoolFor(string key, string sid, bool fallback) =>
        GetFor(key, sid) is { } raw ? raw == "1" : fallback;

    private int DailyLimit(string sid, DateOnly date) =>
        IntFor(SettingsStore.WeekdayKeys[TimeMath.MondayBasedWeekday(date)], sid, 120);
}

/// <summary>Lock state per account as reported over the authenticated pipe. Read by the worker's Task Manager reconcile.</summary>
internal static class LockStateRegistry
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> States = new(StringComparer.Ordinal);

    public static void Set(string sid, bool active) => States[sid] = active;

    /// <summary>A currently locked account SID, or null.</summary>
    public static string? ActiveSid() => States.FirstOrDefault(kv => kv.Value).Key;
}
