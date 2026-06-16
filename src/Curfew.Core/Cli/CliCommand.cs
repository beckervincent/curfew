using Curfew.Core.Security;

namespace Curfew.Core.Cli;

/// <summary>process exit codes for the <c>--config</c> CLI. matches the values documented in help.</summary>
public enum CliExit
{
    /// <summary>command succeeded.</summary>
    Ok = 0,

    /// <summary>parent passcode wrong or locked out.</summary>
    AuthFailed = 1,

    /// <summary>bad arguments, value out of range, unknown/disallowed key.</summary>
    Invalid = 2,

    /// <summary>SYSTEM config service unreachable (pipe down / not session 0).</summary>
    ServiceUnavailable = 3,
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
}

/// <summary>a single base-key/value write the command wants applied (scoping added later from a resolved SID).</summary>
public sealed record CliKeyValue(string BaseKey, string Value);

/// <summary>fully parsed <c>--config</c> command, ready for the App layer to resolve the SID/PIN and apply.</summary>
public sealed record CliCommand(
    CliVerb Verb,
    IReadOnlyList<CliKeyValue> Writes,
    string? GetKey,
    bool PerUser,
    string? UserArg,
    string? PinArg);

/// <summary>parse result: either a <see cref="CliCommand"/> or an error with an exit code + message.</summary>
public sealed record CliParse(bool Ok, CliCommand? Command, CliExit ErrorCode, string? Error)
{
    internal static CliParse Success(CliCommand command) => new(true, command, CliExit.Ok, null);
    internal static CliParse Fail(CliExit code, string error) => new(false, null, code, error);
}

/// <summary>
/// Pure, cross-platform parser + validator for the <c>--config</c> CLI surface. Knows nothing about IPC,
/// Windows SIDs, or the console — it turns an argument vector into a validated <see cref="CliCommand"/> (or a
/// typed error). The App layer resolves <c>--user</c> to a SID, resolves the PIN source, and applies writes
/// through the existing PIN-gated config pipe. Kept here in Core so it is unit-testable without WinUI.
/// </summary>
public static class CliCommandParser
{
    private const int MinLimitMinutes = 0;
    private const int MaxLimitMinutes = 24 * 60;       // 1440
    private const int MinTimeoutMinutes = 1;
    private const int MaxTimeoutMinutes = 720;

    /// <summary>parse the arguments that follow <c>--config</c> (the leading <c>--config</c> already stripped).</summary>
    public static CliParse Parse(IReadOnlyList<string> args)
    {
        // pull recognised flags (--user, --pin) out first; whatever remains is the verb + positionals.
        if (!TryExtractFlags(args, out var positional, out var userArg, out var pinArg, out var flagError))
            return CliParse.Fail(CliExit.Invalid, flagError!);

        var perUser = userArg is not null;

        if (positional.Count == 0)
            return CliParse.Success(new CliCommand(CliVerb.Help, Array.Empty<CliKeyValue>(), null, perUser, userArg, pinArg));

        var verb = positional[0].ToLowerInvariant();
        var rest = positional.Skip(1).ToList();

        return verb switch
        {
            "help" or "--help" or "-h" or "/?" => Ok(CliVerb.Help, Array.Empty<CliKeyValue>(), null, perUser, userArg, pinArg),
            "list-users" => Ok(CliVerb.ListUsers, Array.Empty<CliKeyValue>(), null, perUser, userArg, pinArg),
            "get" => ParseGet(rest, perUser, userArg, pinArg),
            "set" => ParseSet(rest, perUser, userArg, pinArg),
            "set-limit" => ParseSetLimit(rest, perUser, userArg, pinArg),
            "set-schedule" => ParseSetSchedule(rest, perUser, userArg, pinArg),
            "set-timeout" => ParseSetTimeout(rest, perUser, userArg, pinArg),
            "set-passcode" => ParseSetPasscode(rest, perUser, userArg, pinArg),
            _ => CliParse.Fail(CliExit.Invalid, $"unknown command '{positional[0]}'"),
        };
    }

    /// <summary>first non-empty of stdin, env (<c>CURFEW_PIN</c>), then <c>--pin</c> arg. stdin trimmed of surrounding whitespace/newline (pipes/heredocs append one).</summary>
    public static string? ResolvePin(string? stdin, string? env, string? arg)
    {
        var trimmedStdin = stdin?.Trim();
        if (!string.IsNullOrEmpty(trimmedStdin)) return trimmedStdin;
        if (!string.IsNullOrEmpty(env)) return env;
        if (!string.IsNullOrEmpty(arg)) return arg;
        return null;
    }

    private static CliParse Ok(CliVerb verb, IReadOnlyList<CliKeyValue> writes, string? getKey, bool perUser, string? userArg, string? pinArg) =>
        CliParse.Success(new CliCommand(verb, writes, getKey, perUser, userArg, pinArg));

    private static CliParse ParseGet(List<string> rest, bool perUser, string? userArg, string? pinArg)
    {
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: get <key>");
        var key = rest[0];
        if (SettingsPartition.StoreFor(key) != SettingsStoreKind.Config)
            return CliParse.Fail(CliExit.Invalid, $"'{key}' is not a config key");
        if (perUser && !SettingsPartition.IsPerUser(key))
            return CliParse.Fail(CliExit.Invalid, $"--user not allowed for device-wide key '{key}'");
        return Ok(CliVerb.Get, Array.Empty<CliKeyValue>(), key, perUser, userArg, pinArg);
    }

    private static CliParse ParseSet(List<string> rest, bool perUser, string? userArg, string? pinArg)
    {
        if (rest.Count != 2) return CliParse.Fail(CliExit.Invalid, "usage: set <key> <value>");
        var key = rest[0];
        if (SettingsPartition.StoreFor(key) != SettingsStoreKind.Config)
            return CliParse.Fail(CliExit.Invalid, $"'{key}' is not a writable config key");
        if (perUser && !SettingsPartition.IsPerUser(key))
            return CliParse.Fail(CliExit.Invalid, $"--user not allowed for device-wide key '{key}'");
        return Writes(CliVerb.Set, new[] { new CliKeyValue(key, rest[1]) }, perUser, userArg, pinArg);
    }

    private static CliParse ParseSetLimit(List<string> rest, bool perUser, string? userArg, string? pinArg)
    {
        if (rest.Count != 2) return CliParse.Fail(CliExit.Invalid, "usage: set-limit <monday..sunday|all> <minutes>");
        if (!TryParseInt(rest[1], out var minutes) || minutes < MinLimitMinutes || minutes > MaxLimitMinutes)
            return CliParse.Fail(CliExit.Invalid, $"minutes must be {MinLimitMinutes}-{MaxLimitMinutes}");

        var value = minutes.ToString();
        var day = rest[0].ToLowerInvariant();
        if (day == "all")
        {
            var writes = SettingsStore.WeekdayKeys.Select(k => new CliKeyValue(k, value)).ToArray();
            return Writes(CliVerb.Set, writes, perUser, userArg, pinArg);
        }

        var index = Array.IndexOf(SettingsStore.WeekdayNames.Select(n => n.ToLowerInvariant()).ToArray(), day);
        if (index < 0) return CliParse.Fail(CliExit.Invalid, "day must be monday..sunday or all");
        return Writes(CliVerb.Set, new[] { new CliKeyValue(SettingsStore.WeekdayKeys[index], value) }, perUser, userArg, pinArg);
    }

    private static CliParse ParseSetSchedule(List<string> rest, bool perUser, string? userArg, string? pinArg)
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
        return Writes(CliVerb.Set, writes, perUser, userArg, pinArg);
    }

    private static CliParse ParseSetTimeout(List<string> rest, bool perUser, string? userArg, string? pinArg)
    {
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: set-timeout <minutes>");
        if (!TryParseInt(rest[0], out var minutes) || minutes < MinTimeoutMinutes || minutes > MaxTimeoutMinutes)
            return CliParse.Fail(CliExit.Invalid, $"minutes must be {MinTimeoutMinutes}-{MaxTimeoutMinutes}");
        var seconds = (minutes * 60).ToString();
        return Writes(CliVerb.Set, new[] { new CliKeyValue("lock_screen_timeout", seconds) }, perUser, userArg, pinArg);
    }

    private static CliParse ParseSetPasscode(List<string> rest, bool perUser, string? userArg, string? pinArg)
    {
        if (rest.Count != 1) return CliParse.Fail(CliExit.Invalid, "usage: set-passcode <new-pin>");
        if (perUser) return CliParse.Fail(CliExit.Invalid, "--user not allowed for device-wide key 'passcode'");
        var newPin = rest[0];
        if (newPin.Length < PasscodeHash.MinLength)
            return CliParse.Fail(CliExit.Invalid, $"passcode must be at least {PasscodeHash.MinLength} characters");
        return Writes(CliVerb.Set, new[] { new CliKeyValue("passcode", PasscodeHash.Hash(newPin)) }, perUser, userArg, pinArg);
    }

    private static CliParse Writes(CliVerb verb, IReadOnlyList<CliKeyValue> writes, bool perUser, string? userArg, string? pinArg) =>
        Ok(verb, writes, null, perUser, userArg, pinArg);

    /// <summary>strip <c>--user &lt;v&gt;</c> and <c>--pin &lt;v&gt;</c> (each at most once) from <paramref name="args"/>; rest is positional.</summary>
    private static bool TryExtractFlags(
        IReadOnlyList<string> args, out List<string> positional,
        out string? userArg, out string? pinArg, out string? error)
    {
        positional = new List<string>();
        userArg = null;
        pinArg = null;
        error = null;

        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "--user" or "--pin")
            {
                if (i + 1 >= args.Count) { error = $"{a} requires a value"; return false; }
                var value = args[++i];
                if (a == "--user")
                {
                    if (userArg is not null) { error = "--user specified more than once"; return false; }
                    if (string.IsNullOrWhiteSpace(value)) { error = "--user value is empty"; return false; }
                    userArg = value;
                }
                else
                {
                    if (pinArg is not null) { error = "--pin specified more than once"; return false; }
                    pinArg = value;
                }
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
