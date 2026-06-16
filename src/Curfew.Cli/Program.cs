using System.Security.Principal;
using Curfew.Core;
using Curfew.Core.Cli;

namespace Curfew.Cli;

/// <summary>
/// Headless, PIN-gated command-line tool for Curfew. Lets a parent set all settings non-interactively
/// (scriptable / remote over SSH), gated by the same passcode and lockout as the GUI.
/// </summary>
/// <remarks>
/// A console-subsystem executable, deliberately separate from the WinUI <c>Curfew.App.exe</c>: the Windows
/// App SDK bootstrap initializer runs before <c>Main</c> and requires an interactive desktop, so it hangs
/// when launched headless (over SSH / in session 0). A plain console app has none of that.
/// <para>Parsing/validation live in <see cref="CliCommandParser"/> (Curfew.Core, unit-tested); this is the
/// Windows glue — SID resolution, PIN source, config-pipe writes, output, exit codes.</para>
/// </remarks>
internal static class Program
{
    /// <summary>env var carrying the parent PIN when stdin isn't used (least visible scriptable option).</summary>
    private const string PinEnvVar = "CURFEW_PIN";

    private static int Main(string[] args)
    {
        var parsed = CliCommandParser.Parse(args);
        if (!parsed.Ok)
        {
            Console.Error.WriteLine($"curfew: {parsed.Error}");
            return (int)parsed.ErrorCode;
        }

        var command = parsed.Command!;
        return command.Verb switch
        {
            CliVerb.Help => PrintHelp(),
            CliVerb.ListUsers => ListUsers(),
            CliVerb.Get => Get(command),
            CliVerb.Set => Set(command),
            _ => (int)CliExit.Invalid,
        };
    }

    private static int Get(CliCommand command)
    {
        if (!TryResolveScopeSid(command, out var sid, out var code)) return code;
        using var settings = OpenSettings();
        if (sid is not null) settings.UserSid = sid;
        Console.WriteLine(settings.Get(command.GetKey!) ?? string.Empty);
        return (int)CliExit.Ok;
    }

    private static int Set(CliCommand command)
    {
        if (!TryResolveScopeSid(command, out var sid, out var code)) return code;
        var pin = ResolvePin(command);

        foreach (var write in command.Writes)
        {
            // scope per-user base keys to the resolved SID; device-wide + state keys pass through unchanged.
            var key = command.PerUser ? SettingsPartition.Scope(write.BaseKey, sid) : write.BaseKey;
            var response = ConfigClient.Send(new ConfigRequest(ConfigPipe.OpSet, Key: key, Value: write.Value, Passcode: pin));
            if (!response.Ok)
            {
                Console.Error.WriteLine($"curfew: failed to set '{write.BaseKey}': {response.Error}");
                return (int)MapError(response.Error);
            }
            Console.WriteLine($"set {key}");
        }
        return (int)CliExit.Ok;
    }

    private static int ListUsers()
    {
        using var settings = OpenSettings();
        var sids = UserProvisioning.Parse(settings.Get("provisioned_users"))
            .Concat(settings.UsersWithHistory())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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

    /// <summary>resolve the PIN from stdin (if piped), then <c>CURFEW_PIN</c>, then <c>--pin</c>.</summary>
    /// <remarks>stdin is only consulted when neither env nor <c>--pin</c> supplied a value. otherwise we would
    /// call <see cref="TextReader.ReadToEnd"/> on a still-open stdin (e.g. an SSH channel) and block forever,
    /// even though a PIN was already provided. when stdin IS the source, the caller is expected to pipe it
    /// (<c>echo pin | curfew-cli ...</c>), which closes the stream and yields EOF.</remarks>
    private static string? ResolvePin(CliCommand command)
    {
        var arg = command.PinArg;
        var env = Environment.GetEnvironmentVariable(PinEnvVar);
        if (!string.IsNullOrEmpty(arg) || !string.IsNullOrEmpty(env))
            return CliCommandParser.ResolvePin(null, env, arg);

        string? stdin = null;
        try
        {
            // bounded read: a piped PIN (`echo pin | curfew-cli ...`) closes the stream and returns at once;
            // an open-but-idle stdin (e.g. an inherited SSH channel) would otherwise block ReadToEnd forever.
            // on timeout, treat as no PIN — the IPC call then fails with a clear auth error instead of hanging.
            if (Console.IsInputRedirected)
            {
                var read = System.Threading.Tasks.Task.Run(() => Console.In.ReadToEnd());
                stdin = read.Wait(TimeSpan.FromSeconds(2)) ? read.Result : null;
            }
        }
        catch
        {
            // no usable stdin
        }
        return CliCommandParser.ResolvePin(stdin, env, arg);
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

    private static int PrintHelp()
    {
        Console.WriteLine("curfew-cli - set Curfew parental controls non-interactively (PIN required).");
        Console.WriteLine();
        Console.WriteLine("Usage: curfew-cli <command> [--user <name|sid>] [--pin <pin>]");
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
        return (int)CliExit.Ok;
    }
}
