using System.Security.Principal;
using System.Text.Json;
using Curfew.Core;
using Curfew.Core.Cli;

namespace Curfew.Cli;

/// <summary>
/// Headless command-line tool for Curfew. Lets a parent read and set all settings non-interactively
/// (scriptable / remote over SSH), authorised by running elevated rather than by a passcode.
/// </summary>
/// <remarks>
/// A console-subsystem executable, deliberately separate from the WinUI <c>Curfew.App.exe</c>: the Windows
/// App SDK bootstrap initializer runs before <c>Main</c> and requires an interactive desktop, so it hangs
/// when launched headless (over SSH / in session 0). A plain console app has none of that.
/// <para>Parsing/validation live in <see cref="CliCommandParser"/> (Curfew.Core, unit-tested); this is the
/// Windows glue — SID resolution, elevation check, config-pipe writes, output, exit codes.</para>
/// <para>The elevation check here is a courtesy that lets scripts fail fast with a readable message. It is
/// NOT the security boundary: the service independently impersonates the pipe client and confirms an
/// elevated administrator before applying anything, so patching this binary gains nothing.</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] args)
    {
        var parsed = CliCommandParser.Parse(args);
        if (!parsed.Ok)
        {
            Console.Error.WriteLine($"curfew: {parsed.Error}");
            return (int)parsed.ErrorCode;
        }

        var command = parsed.Command!;

        if (CliCommandParser.RequiresElevation(command.Verb) && !IsElevatedAdmin())
        {
            Console.Error.WriteLine(
                "curfew: this command changes settings and must be run as an elevated administrator.");
            Console.Error.WriteLine(
                "curfew: open an admin terminal (or use 'runas') and try again. No PIN is required.");
            return (int)CliExit.NotElevated;
        }

        return command.Verb switch
        {
            CliVerb.Help => PrintHelp(),
            CliVerb.ListUsers => ListUsers(command),
            CliVerb.Status => Status(command),
            CliVerb.Get => Get(command),
            CliVerb.Set => Set(command),
            CliVerb.Provision => Provision(command),
            CliVerb.ResetLockout => ResetLockout(),
            _ => (int)CliExit.Invalid,
        };
    }

    /// <summary>whether this process holds an elevated administrator token. A filtered (non-elevated) admin
    /// token reports false, which is what we want — membership alone is not authority here.</summary>
    private static bool IsElevatedAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static int Get(CliCommand command)
    {
        if (!TryResolveScopeSid(command, out var sid, out var code)) return code;
        using var settings = OpenSettings();
        if (sid is not null) settings.UserSid = sid;
        var value = settings.Get(command.GetKey!) ?? string.Empty;

        if (command.Json)
            Console.WriteLine(JsonSerializer.Serialize(new { key = command.GetKey, value }));
        else
            Console.WriteLine(value);
        return (int)CliExit.Ok;
    }

    private static int Set(CliCommand command)
    {
        if (!TryResolveScopeSid(command, out var sid, out var code)) return code;

        // writes are applied one key at a time over the config pipe (same as the GUI's per-key writes); there
        // is no batch/transaction op. on failure we stop and report which keys were already set, so a partial
        // `set-limit all` is visible rather than silent. re-running the command is idempotent.
        foreach (var write in command.Writes)
        {
            // scope per-user base keys to the resolved SID; device-wide + state keys pass through unchanged.
            var key = command.PerUser ? SettingsPartition.Scope(write.BaseKey, sid) : write.BaseKey;
            var response = ConfigClient.Send(new ConfigRequest(ConfigPipe.OpSet, Key: key, Value: write.Value));
            if (!response.Ok)
            {
                Console.Error.WriteLine($"curfew: failed to set '{write.BaseKey}': {response.Error}");
                return (int)MapError(response.Error);
            }
            Console.WriteLine($"set {key}");
        }
        return (int)CliExit.Ok;
    }

    /// <summary>set a Windows user up without going through the lock screen: writes their daily limit for
    /// every weekday and adds them to the provisioned list, in one verified service call.</summary>
    private static int Provision(CliCommand command)
    {
        if (!TryResolveScopeSid(command, out var sid, out var code)) return code;

        var response = ConfigClient.Provision(sid!, null, command.Minutes);
        if (!response.Ok)
        {
            Console.Error.WriteLine($"curfew: could not provision '{command.UserArg}': {response.Error}");
            return (int)MapError(response.Error);
        }

        Console.WriteLine($"provisioned {sid} ({command.Minutes} min/day)");
        return (int)CliExit.Ok;
    }

    /// <summary>clear the failed-attempt lockout. The recovery path when a parent has locked themselves out
    /// of the lock screen; the service allows it for an elevated administrator without the passcode.</summary>
    private static int ResetLockout()
    {
        var response = ConfigClient.ResetFailures(null);
        if (!response)
        {
            Console.Error.WriteLine("curfew: could not clear the lockout (is the service running?)");
            return (int)CliExit.ServiceUnavailable;
        }
        Console.WriteLine("lockout cleared");
        return (int)CliExit.Ok;
    }

    /// <summary>read-only snapshot of what is currently enforced, for humans or for scripts via --json.</summary>
    private static int Status(CliCommand command)
    {
        if (!TryResolveScopeSid(command, out var sid, out var code)) return code;

        using var settings = OpenSettings();
        if (sid is not null) settings.UserSid = sid;

        var today = TimeMath.MondayBasedWeekday(DateOnly.FromDateTime(DateTime.Now));
        var snapshot = new
        {
            serviceReachable = ConfigClient.Ping(),
            elevated = IsElevatedAdmin(),
            passcodeSet = !string.IsNullOrEmpty(settings.Get("passcode")),
            user = sid ?? "(current device defaults)",
            dailyLimitMinutes = settings.GetDailyLimit(today),
            limitEnabled = settings.Get("limit_enabled") == "1",
            scheduleEnabled = settings.Get("schedule_enabled") == "1",
            lockTimeoutSeconds = settings.GetInt("lock_screen_timeout", 600),
            contentFilter = settings.Get("dns_filter_mode") ?? "off",
            locked = settings.Get("lock_active") == "1",
            lockReason = settings.Get("lock_reason") ?? string.Empty,
            failedAttempts = settings.GetInt("failed_attempts", 0),
        };

        if (command.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
            return (int)CliExit.Ok;
        }

        Console.WriteLine($"service reachable : {YesNo(snapshot.serviceReachable)}");
        Console.WriteLine($"elevated          : {YesNo(snapshot.elevated)}");
        Console.WriteLine($"passcode set      : {YesNo(snapshot.passcodeSet)}");
        Console.WriteLine($"scope             : {snapshot.user}");
        Console.WriteLine($"daily limit       : {snapshot.dailyLimitMinutes} min ({(snapshot.limitEnabled ? "enforced" : "disabled")})");
        Console.WriteLine($"weekly schedule   : {(snapshot.scheduleEnabled ? "enabled" : "disabled")}");
        Console.WriteLine($"lock timeout      : {snapshot.lockTimeoutSeconds / 60} min");
        Console.WriteLine($"content filter    : {snapshot.contentFilter}");
        Console.WriteLine($"currently locked  : {YesNo(snapshot.locked)}{(snapshot.locked ? $" ({snapshot.lockReason})" : string.Empty)}");
        Console.WriteLine($"failed attempts   : {snapshot.failedAttempts}");
        return (int)CliExit.Ok;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static int ListUsers(CliCommand command)
    {
        using var settings = OpenSettings();
        var sids = UserProvisioning.Parse(settings.Get("provisioned_users"))
            .Concat(settings.UsersWithHistory())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (command.Json)
        {
            Console.WriteLine(JsonSerializer.Serialize(
                sids.Select(s => new { sid = s, name = ResolveUserName(s) })));
            return (int)CliExit.Ok;
        }

        if (sids.Count == 0)
        {
            Console.WriteLine("(no users)");
            return (int)CliExit.Ok;
        }

        foreach (var sid in sids)
            Console.WriteLine($"{sid}\t{ResolveUserName(sid)}");
        return (int)CliExit.Ok;
    }

    /// <summary>resolve the SID a write/read scopes to. Returns true with <paramref name="sid"/> null when not
    /// per-user; false with an exit <paramref name="code"/> when a <c>--user</c> value can't be resolved.</summary>
    private static bool TryResolveScopeSid(CliCommand command, out string? sid, out int code)
    {
        sid = null;
        code = (int)CliExit.Ok;
        if (!command.PerUser) return true;

        sid = ResolveSid(command.UserArg!);
        if (sid is null)
        {
            Console.Error.WriteLine($"curfew: could not resolve user '{command.UserArg}'");
            code = (int)CliExit.Invalid;
            return false;
        }
        return true;
    }

    /// <summary>accept a raw SID (<c>S-1-...</c>) as-is, else translate a Windows account name to its SID. null on failure.</summary>
    private static string? ResolveSid(string userArg)
    {
        if (userArg.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase))
        {
            try { return new SecurityIdentifier(userArg).Value; }
            catch { return null; }
        }
        try
        {
            return ((SecurityIdentifier)new NTAccount(userArg).Translate(typeof(SecurityIdentifier))).Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>resolve a SID back to a bare account name for display; falls back to the SID string.</summary>
    private static string ResolveUserName(string sid)
    {
        try
        {
            var name = new SecurityIdentifier(sid).Translate(typeof(NTAccount)).Value;
            var slash = name.LastIndexOf('\\');
            return slash >= 0 ? name[(slash + 1)..] : name;
        }
        catch
        {
            return sid;
        }
    }

    /// <summary>map a service error string to an exit code: refusals vs an unreachable/refusing service.</summary>
    private static CliExit MapError(string? error)
    {
        if (error is null) return CliExit.ServiceUnavailable;
        if (error.Contains("passcode", StringComparison.OrdinalIgnoreCase)
            || error.Contains("locked out", StringComparison.OrdinalIgnoreCase)
            || error.Contains("wrong code", StringComparison.OrdinalIgnoreCase))
            return CliExit.AuthFailed;
        return CliExit.ServiceUnavailable;
    }

    private static SettingsStore OpenSettings() =>
        CurfewPaths.OpenSettings(DateOnly.FromDateTime(DateTime.Now));

    private static int PrintHelp()
    {
        Console.WriteLine("curfew-cli - set Curfew parental controls non-interactively.");
        Console.WriteLine();
        Console.WriteLine("Usage: curfew-cli <command> [--user <name|sid>] [--json]");
        Console.WriteLine();
        Console.WriteLine("Read-only commands (any user):");
        Console.WriteLine("  status                            Summarise what is currently enforced.");
        Console.WriteLine("  get <key>                         Print a config value.");
        Console.WriteLine("  list-users                        List known users (SID + name).");
        Console.WriteLine();
        Console.WriteLine("Write commands (require an elevated administrator):");
        Console.WriteLine("  set <key> <value>                 Write any config key.");
        Console.WriteLine("  set-limit <day|all> <minutes>     Daily time limit (0-1440). day = monday..sunday.");
        Console.WriteLine("  set-schedule <enabled|disabled> [grid]");
        Console.WriteLine("                                    Toggle weekly schedule; optional 7x96 '0'/'1' grid.");
        Console.WriteLine("  set-timeout <minutes>             Lock-screen timeout before logoff (1-720).");
        Console.WriteLine("  set-passcode <new-pin>            Set the parent PIN used by the lock screen (min 8 chars).");
        Console.WriteLine("  provision --user <u> <minutes>    Set a user up without the lock screen.");
        Console.WriteLine("  reset-lockout                     Clear the failed-attempt lockout.");
        Console.WriteLine();
        Console.WriteLine("Authorisation: run elevated. There is no --pin; an administrator can already rewrite");
        Console.WriteLine("the config directly, so the service accepts an elevated caller without one. The lock");
        Console.WriteLine("screen still requires the PIN, which is what a non-administrator (a child) sees.");
        Console.WriteLine();
        Console.WriteLine("--user scopes per-user keys (limits, schedule, timeout); rejected for device-wide keys.");
        Console.WriteLine("--json emits machine-readable output for status, get and list-users.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok, 1 refused, 2 invalid args, 3 service unavailable, 4 not elevated.");
        return (int)CliExit.Ok;
    }
}
