using System.Runtime.InteropServices;
using System.Security.Principal;
using Curfew.Core;
using Curfew.Core.Cli;

namespace Curfew.App;

/// <summary>
/// Headless <c>--config</c> CLI: applies the parent's settings non-interactively, gated by the same PIN and
/// lockout as the GUI. Parsing/validation live in <see cref="CliCommandParser"/> (Core, unit-tested); this
/// class is the Windows-only glue — resolving <c>--user</c> to a SID, resolving the PIN source, sending
/// writes through the SYSTEM config pipe, reading values back, and printing to the parent console.
/// </summary>
/// <remarks>never shows a window; always ends the process via <see cref="Environment.Exit(int)"/> with a
/// <see cref="CliExit"/> code so scripts/SSH callers can branch on the result.</remarks>
internal static class CliConfig
{
    /// <summary>marker that selects this mode; stripped before parsing.</summary>
    public const string Argument = "--config";

    /// <summary>env var carrying the parent PIN when stdin isn't used (least visible scriptable option).</summary>
    private const string PinEnvVar = "CURFEW_PIN";

    /// <summary>run the CLI for the args following <c>--config</c>, then hard-exit with a <see cref="CliExit"/> code.</summary>
    public static void Run(IReadOnlyList<string> configArgs)
    {
        AttachParentConsole();

        var parsed = CliCommandParser.Parse(configArgs);
        if (!parsed.Ok)
        {
            Console.Error.WriteLine($"curfew: {parsed.Error}");
            Exit(parsed.ErrorCode);
        }

        var command = parsed.Command!;
        switch (command.Verb)
        {
            case CliVerb.Help:
                PrintHelp();
                Exit(CliExit.Ok);
                break;
            case CliVerb.ListUsers:
                ListUsers();
                break;
            case CliVerb.Get:
                Get(command);
                break;
            case CliVerb.Set:
                Set(command);
                break;
        }

        Exit(CliExit.Ok);
    }

    private static void Get(CliCommand command)
    {
        var sid = ResolveScopeSid(command);
        using var settings = OpenSettings();
        if (sid is not null) settings.UserSid = sid;
        var value = settings.Get(command.GetKey!);
        Console.WriteLine(value ?? string.Empty);
        Exit(CliExit.Ok);
    }

    private static void Set(CliCommand command)
    {
        var sid = ResolveScopeSid(command);
        var pin = ResolvePin(command);

        foreach (var write in command.Writes)
        {
            // scope per-user base keys to the resolved SID; device-wide + state keys pass through unchanged.
            var key = command.PerUser ? SettingsPartition.Scope(write.BaseKey, sid) : write.BaseKey;
            var response = ConfigClient.Send(new ConfigRequest(ConfigPipe.OpSet, Key: key, Value: write.Value, Passcode: pin));
            if (!response.Ok)
            {
                Console.Error.WriteLine($"curfew: failed to set '{write.BaseKey}': {response.Error}");
                Exit(MapError(response.Error));
            }
            Console.WriteLine($"set {key}");
        }
        Exit(CliExit.Ok);
    }

    private static void ListUsers()
    {
        using var settings = OpenSettings();
        var provisioned = UserProvisioning.Parse(settings.Get("provisioned_users"));
        var withHistory = settings.UsersWithHistory();
        var sids = provisioned.Concat(withHistory).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        if (sids.Count == 0)
        {
            Console.WriteLine("(no users)");
            Exit(CliExit.Ok);
        }

        foreach (var sid in sids)
            Console.WriteLine($"{sid}\t{ResolveUserName(sid)}");
        Exit(CliExit.Ok);
    }

    /// <summary>resolve the SID a write/read should be scoped to, or null when not per-user. exits Invalid if a
    /// <c>--user</c> name/SID can't be resolved.</summary>
    private static string? ResolveScopeSid(CliCommand command)
    {
        if (!command.PerUser) return null;
        var sid = ResolveSid(command.UserArg!);
        if (sid is null)
        {
            Console.Error.WriteLine($"curfew: could not resolve user '{command.UserArg}'");
            Exit(CliExit.Invalid);
        }
        return sid;
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

    /// <summary>resolve the PIN from stdin (if redirected), then <c>CURFEW_PIN</c>, then <c>--pin</c>.</summary>
    private static string? ResolvePin(CliCommand command)
    {
        string? stdin = null;
        try
        {
            if (Console.IsInputRedirected) stdin = Console.In.ReadToEnd();
        }
        catch
        {
            // no usable stdin; fall back to env/arg
        }
        return CliCommandParser.ResolvePin(stdin, Environment.GetEnvironmentVariable(PinEnvVar), command.PinArg);
    }

    /// <summary>map a service error string to an exit code: auth failures vs an unreachable/refusing service.</summary>
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

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Curfew config CLI - set parental controls non-interactively (PIN required).");
        Console.WriteLine();
        Console.WriteLine("Usage: Curfew.App.exe --config <command> [--user <name|sid>] [--pin <pin>]");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  get <key>                         Print a config value.");
        Console.WriteLine("  set <key> <value>                 Write any config key.");
        Console.WriteLine("  set-limit <day|all> <minutes>     Daily time limit (0-1440). day = monday..sunday.");
        Console.WriteLine("  set-schedule <enabled|disabled> [grid]");
        Console.WriteLine("                                    Toggle weekly schedule; optional 7x96 '0'/'1' grid.");
        Console.WriteLine("  set-timeout <minutes>             Lock-screen timeout before logoff (1-720).");
        Console.WriteLine("  set-passcode <new-pin>            Set parent PIN (min 8 chars).");
        Console.WriteLine("  list-users                        List provisioned users (SID + name).");
        Console.WriteLine();
        Console.WriteLine("PIN source (first wins): stdin, CURFEW_PIN env var, --pin arg.");
        Console.WriteLine("--user scopes per-user keys (limits, schedule, timeout); rejected for device-wide keys.");
        Console.WriteLine();
        Console.WriteLine("Exit codes: 0 ok, 1 auth failed/locked out, 2 invalid args, 3 service unavailable.");
        Console.Out.Flush();
    }

    private static void AttachParentConsole()
    {
        try { AttachConsole(AttachParentProcess); } catch { /* no parent console; writes are harmless no-ops */ }
    }

    private static void Exit(CliExit code)
    {
        try { Console.Out.Flush(); Console.Error.Flush(); } catch { /* best effort */ }
        Environment.Exit((int)code);
    }

    private const uint AttachParentProcess = unchecked((uint)-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}
