using Curfew.Core.Security;

namespace Curfew.Core.Cli;

/// <summary>process exit codes for the <c>--config</c> CLI. matches the values documented in help.</summary>
public enum CliExit
{
    /// <summary>command succeeded.</summary>
    Ok = 0,

    /// <summary>the service refused the request (e.g. an unknown user, or a passcode-gated op the caller is not authorised for).</summary>
    AuthFailed = 1,

    /// <summary>bad arguments, value out of range, unknown/disallowed key.</summary>
    Invalid = 2,

    /// <summary>SYSTEM config service unreachable (pipe down / not session 0).</summary>
    ServiceUnavailable = 3,

    /// <summary>the process is not running elevated. Every write verb requires an elevated administrator;
    /// see <see cref="CliCommandParser"/> remarks for why that replaced the parent PIN.</summary>
    NotElevated = 4,
}

/// <summary>which CLI action the parsed command performs.</summary>
public enum CliVerb
{
    /// <summary>print usage.</summary>
    Help,

    /// <summary>read one key.</summary>
    Get,

    /// <summary>write one or more keys.</summary>
    Set,

    /// <summary>list provisioned users.</summary>
    ListUsers,

    /// <summary>print a read-only summary of the current enforcement state.</summary>
    Status,

    /// <summary>set up a Windows user (write their daily limit, add them to the provisioned list).</summary>
    Provision,

    /// <summary>clear the failed-attempt lockout counter.</summary>
    ResetLockout,
}

/// <summary>a single base-key/value write the command wants applied (scoping added later from a resolved SID).</summary>
public sealed record CliKeyValue(string BaseKey, string Value);

/// <summary>fully parsed CLI command, ready for the App layer to resolve the SID and apply.</summary>
public sealed record CliCommand(
    CliVerb Verb,
    IReadOnlyList<CliKeyValue> Writes,
    string? GetKey,
    bool PerUser,
    string? UserArg,
    bool Json = false,
    int Minutes = 0);

/// <summary>parse result: either a <see cref="CliCommand"/> or an error with an exit code + message.</summary>
public sealed record CliParse(bool Ok, CliCommand? Command, CliExit ErrorCode, string? Error)
{
    internal static CliParse Success(CliCommand command) => new(true, command, CliExit.Ok, null);
    internal static CliParse Fail(CliExit code, string error) => new(false, null, code, error);
}

/// <summary>
/// Pure, cross-platform parser + validator for the CLI surface. Knows nothing about IPC, Windows SIDs, or the
/// console — it turns an argument vector into a validated <see cref="CliCommand"/> (or a typed error). The App
/// layer resolves <c>--user</c> to a SID and applies writes through the config pipe. Kept here in Core so it is
/// unit-testable without WinUI.
/// </summary>
/// <remarks>
/// <para>Authorisation is by <b>elevation</b>, not by the parent PIN. The CLI takes no <c>--pin</c>: an
/// administrator already has FullControl on the Curfew data directory (the installer's ACL grants
/// S-1-5-32-544), so anyone who can run elevated can already rewrite config.db by hand. Demanding a PIN on top
/// of that protected nothing while pushing the secret through argv, environment variables and shell history,
/// where it is far easier to capture than to guess.</para>
/// <para>The check that matters is server-side: the service impersonates the pipe client and tests for an
/// elevated administrator token. The CLI's own check is a courtesy so scripts fail fast with a clear message.
/// A non-elevated caller — which is what a child is — still gets the full PIN-and-lockout treatment, so the
/// lock screen's guarantees are unchanged.</para>
/// </remarks>
public static class CliCommandParser
{
    private const int MinLimitMinutes = 0;
    private const int MaxLimitMinutes = 24 * 60;       // 1440
    private const int MinTimeoutMinutes = 1;
    private const int MaxTimeoutMinutes = 720;

    /// <summary>verbs that change state and therefore require an elevated administrator.</summary>
    public static bool RequiresElevation(CliVerb verb) =>
        verb is CliVerb.Set or CliVerb.Provision or CliVerb.ResetLockout;

    /// <summary>parse the CLI arguments.</summary>
    public static CliParse Parse(IReadOnlyList<string> args)
    {
        // pull recognised flags (--user, --json) out first; whatever remains is the verb + positionals.
        if (!TryExtractFlags(args, out var positional, out var userArg, out var json, out var flagError))
            return CliParse.Fail(CliExit.Invalid, flagError!);

        var perUser = userArg is not null;

        if (positional.Count == 0)
            return CliParse.Success(new CliCommand(CliVerb.Help, Array.Empty<CliKeyValue>(), null, perUser, userArg, json));

        var verb = positional[0].ToLowerInvariant();
        var rest = positional.Skip(1).ToList();

        return verb switch
        {
            "help" or "--help" or "-h" or "/?" => Ok(CliVerb.Help, Array.Empty<CliKeyValue>(), null, perUser, userArg, json),
            "list-users" => Ok(CliVerb.ListUsers, Array.Empty<CliKeyValue>(), null, perUser, userArg, json),
            "status" => Ok(CliVerb.Status, Array.Empty<CliKeyValue>(), null, perUser, userArg, json),
            "reset-lockout" => ParseResetLockout(rest, perUser, userArg, json),
            "provision" => ParseProvision(rest, perUser, userArg, json),
            "get" => ParseGet(rest, perUser, userArg, json),
            "set" => ParseSet(rest, perUser, userArg, json),
            "set-limit" => ParseSetLimit(rest, perUser, userArg, json),
            "set-schedule" => ParseSetSchedule(rest, perUser, userArg, json),
            "set-timeout" => ParseSetTimeout(rest, perUser, userArg, json),
            "set-passcode" => ParseSetPasscode(rest, perUser, userArg, json),
            _ => CliParse.Fail(CliExit.Invalid, $"unknown command '{positional[0]}'"),
        };
    }

    private static CliParse Ok(CliVerb verb, IReadOnlyList<CliKeyValue> writes, string? getKey, bool perUser, string? userArg, bool json, int minutes = 0) =>
        CliParse.Success(new CliCommand(verb, writes, getKey, perUser, userArg, json, minutes));

    private static CliParse ParseResetLockout(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count != 0) return CliParse.Fail(CliExit.Invalid, "usage: reset-lockout");
        // the lockout counter is device-wide config, so scoping it to a user is meaningless
        if (perUser) return CliParse.Fail(CliExit.Invalid, "--user not allowed for reset-lockout");
        return Ok(CliVerb.ResetLockout, Array.Empty<CliKeyValue>(), null, false, null, json);
    }

    /// <summary>set up a Windows user without the lock screen: <c>provision --user &lt;name|sid&gt; &lt;minutes&gt;</c>.</summary>
    private static CliParse ParseProvision(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (!perUser) return CliParse.Fail(CliExit.Invalid, "provision requires --user <name|sid>");
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: provision --user <name|sid> <minutes>");
        if (!TryParseInt(rest[0], out var minutes) || minutes < MinLimitMinutes || minutes > MaxLimitMinutes)
            return CliParse.Fail(CliExit.Invalid, $"minutes must be {MinLimitMinutes}-{MaxLimitMinutes}");
        return Ok(CliVerb.Provision, Array.Empty<CliKeyValue>(), null, true, userArg, json, minutes);
    }

    private static CliParse ParseGet(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: get <key>");
        var key = rest[0];
        if (SettingsPartition.StoreFor(key) != SettingsStoreKind.Config)
            return CliParse.Fail(CliExit.Invalid, $"'{key}' is not a config key");
        if (perUser && !SettingsPartition.IsPerUser(key))
            return CliParse.Fail(CliExit.Invalid, $"--user not allowed for device-wide key '{key}'");
        return Ok(CliVerb.Get, Array.Empty<CliKeyValue>(), key, perUser, userArg, json);
    }

    private static CliParse ParseSet(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count != 2) return CliParse.Fail(CliExit.Invalid, "usage: set <key> <value>");
        var key = rest[0];
        // the passcode must go through set-passcode (min-length check + PBKDF2 hashing); a raw generic write
        // would store an unhashed/short value and silently weaken or corrupt the gate.
        if (key == "passcode")
            return CliParse.Fail(CliExit.Invalid, "use 'set-passcode' to change the passcode");
        if (SettingsPartition.StoreFor(key) != SettingsStoreKind.Config)
            return CliParse.Fail(CliExit.Invalid, $"'{key}' is not a writable config key");
        if (perUser && !SettingsPartition.IsPerUser(key))
            return CliParse.Fail(CliExit.Invalid, $"--user not allowed for device-wide key '{key}'");
        return Writes(CliVerb.Set, new[] { new CliKeyValue(key, rest[1]) }, perUser, userArg, json);
    }

    private static CliParse ParseSetLimit(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count != 2) return CliParse.Fail(CliExit.Invalid, "usage: set-limit <monday..sunday|all> <minutes>");
        if (!TryParseInt(rest[1], out var minutes) || minutes < MinLimitMinutes || minutes > MaxLimitMinutes)
            return CliParse.Fail(CliExit.Invalid, $"minutes must be {MinLimitMinutes}-{MaxLimitMinutes}");

        var value = minutes.ToString();
        var day = rest[0].ToLowerInvariant();
        if (day == "all")
        {
            var writes = SettingsStore.WeekdayKeys.Select(k => new CliKeyValue(k, value)).ToArray();
            return Writes(CliVerb.Set, writes, perUser, userArg, json);
        }

        var index = Array.IndexOf(SettingsStore.WeekdayNames.Select(n => n.ToLowerInvariant()).ToArray(), day);
        if (index < 0) return CliParse.Fail(CliExit.Invalid, "day must be monday..sunday or all");
        return Writes(CliVerb.Set, new[] { new CliKeyValue(SettingsStore.WeekdayKeys[index], value) }, perUser, userArg, json);
    }

    private static CliParse ParseSetSchedule(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count is < 1 or > 2)
            return CliParse.Fail(CliExit.Invalid, "usage: set-schedule <enabled|disabled> [grid]");

        var state = rest[0].ToLowerInvariant();
        var enabled = state switch { "enabled" => "1", "disabled" => "0", _ => null };
        if (enabled is null) return CliParse.Fail(CliExit.Invalid, "state must be enabled or disabled");

        var writes = new List<CliKeyValue> { new("schedule_enabled", enabled) };
        if (rest.Count == 2)
        {
            if (!TryNormalizeGrid(rest[1], out var normalized, out var gridError))
                return CliParse.Fail(CliExit.Invalid, gridError!);
            writes.Add(new CliKeyValue("schedule", normalized!));
        }
        return Writes(CliVerb.Set, writes, perUser, userArg, json);
    }

    private static CliParse ParseSetTimeout(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: set-timeout <minutes>");
        if (!TryParseInt(rest[0], out var minutes) || minutes < MinTimeoutMinutes || minutes > MaxTimeoutMinutes)
            return CliParse.Fail(CliExit.Invalid, $"minutes must be {MinTimeoutMinutes}-{MaxTimeoutMinutes}");
        var seconds = (minutes * 60).ToString();
        return Writes(CliVerb.Set, new[] { new CliKeyValue("lock_screen_timeout", seconds) }, perUser, userArg, json);
    }

    private static CliParse ParseSetPasscode(List<string> rest, bool perUser, string? userArg, bool json)
    {
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: set-passcode <new-pin>");
        if (perUser) return CliParse.Fail(CliExit.Invalid, "--user not allowed for device-wide key 'passcode'");
        var newPin = rest[0];
        if (newPin.Length < PasscodeHash.MinLength)
            return CliParse.Fail(CliExit.Invalid, $"passcode must be at least {PasscodeHash.MinLength} characters");
        return Writes(CliVerb.Set, new[] { new CliKeyValue("passcode", PasscodeHash.Hash(newPin)) }, perUser, userArg, json);
    }

    private static CliParse Writes(CliVerb verb, IReadOnlyList<CliKeyValue> writes, bool perUser, string? userArg, bool json) =>
        Ok(verb, writes, null, perUser, userArg, json);

    /// <summary>strip <c>--user &lt;v&gt;</c> (at most once) and <c>--json</c> from <paramref name="args"/>; rest is positional.</summary>
    private static bool TryExtractFlags(
        IReadOnlyList<string> args, out List<string> positional,
        out string? userArg, out bool json, out string? error)
    {
        positional = new List<string>();
        userArg = null;
        json = false;
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a == "--json")
            {
                json = true;
            }
            else if (a == "--pin")
            {
                // explicit rejection rather than "unknown command": scripts written against the old
                // PIN-gated CLI must fail loudly, not silently run unauthenticated-looking commands.
                error = "--pin is no longer accepted; run elevated instead (see 'curfew-cli help')";
                return false;
            }
            else if (a == "--user")
            {
                if (i + 1 >= args.Count) { error = $"{a} requires a value"; return false; }
                var value = args[++i];
                if (userArg is not null) { error = "--user specified more than once"; return false; }
                if (string.IsNullOrWhiteSpace(value)) { error = "--user value is empty"; return false; }
                userArg = value;
            }
            else
            {
                positional.Add(a);
            }
        }
        return true;
    }

    /// <summary>validate a schedule grid: exactly 7 ';'-rows, each exactly 96 chars of '0'/'1'. returns normalized round-tripped form.</summary>
    private static bool TryNormalizeGrid(string grid, out string? normalized, out string? error)
    {
        normalized = null;
        error = null;
        var rows = grid.Split(';');
        if (rows.Length != Schedule.Days)
        {
            error = $"schedule must have {Schedule.Days} ';'-separated rows";
            return false;
        }
        foreach (var row in rows)
        {
            if (row.Length != Schedule.SlotsPerDay)
            {
                error = $"each schedule row must be {Schedule.SlotsPerDay} characters";
                return false;
            }
            if (row.Any(c => c is not ('0' or '1')))
            {
                error = "schedule rows may contain only '0' (blocked) and '1' (allowed)";
                return false;
            }
        }
        normalized = Schedule.Parse(grid).Serialize();
        return true;
    }

    private static bool TryParseInt(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
}
