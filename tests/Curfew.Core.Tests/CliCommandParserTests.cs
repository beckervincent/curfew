using System.Linq;
using Curfew.Core;
using Curfew.Core.Cli;
using Curfew.Core.Security;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>Tests for <see cref="CliCommandParser"/> (pure parsing/validation of the --config CLI).</summary>
public class CliCommandParserTests
{
    private static CliParse Parse(params string[] args) => CliCommandParser.Parse(args);

    [Fact]
    public void No_args_is_help()
    {
        var r = Parse();
        Assert.True(r.Ok);
        Assert.Equal(CliVerb.Help, r.Command!.Verb);
    }

    [Theory]
    [InlineData("help")]
    [InlineData("--help")]
    [InlineData("-h")]
    public void Help_verbs(string arg)
    {
        Assert.Equal(CliVerb.Help, Parse(arg).Command!.Verb);
    }

    [Fact]
    public void Unknown_command_is_invalid()
    {
        var r = Parse("frobnicate");
        Assert.False(r.Ok);
        Assert.Equal(CliExit.Invalid, r.ErrorCode);
    }

    [Fact]
    public void Get_requires_a_config_key()
    {
        var ok = Parse("get", "limit_monday");
        Assert.True(ok.Ok);
        Assert.Equal(CliVerb.Get, ok.Command!.Verb);
        Assert.Equal("limit_monday", ok.Command.GetKey);

        // state key rejected
        Assert.False(Parse("get", "lock_active").Ok);
        // arity
        Assert.False(Parse("get").Ok);
    }

    [Fact]
    public void Set_rejects_state_keys()
    {
        Assert.False(Parse("set", "lock_active", "1").Ok);
    }

    [Fact]
    public void Set_rejects_passcode_key_directing_to_set_passcode()
    {
        // generic set must not write a raw/unhashed passcode; set-passcode is the only path.
        var r = Parse("set", "passcode", "weak");
        Assert.False(r.Ok);
        Assert.Equal(CliExit.Invalid, r.ErrorCode);
    }

    [Fact]
    public void Set_writes_generic_config_key()
    {
        var r = Parse("set", "blocking_message", "stop");
        Assert.True(r.Ok);
        var w = Assert.Single(r.Command!.Writes);
        Assert.Equal("blocking_message", w.BaseKey);
        Assert.Equal("stop", w.Value);
    }

    [Fact]
    public void Set_limit_single_day()
    {
        var r = Parse("set-limit", "monday", "90");
        Assert.True(r.Ok);
        var w = Assert.Single(r.Command!.Writes);
        Assert.Equal("limit_monday", w.BaseKey);
        Assert.Equal("90", w.Value);
    }

    [Fact]
    public void Set_limit_all_writes_seven_weekdays()
    {
        var r = Parse("set-limit", "all", "60");
        Assert.True(r.Ok);
        Assert.Equal(7, r.Command!.Writes.Count);
        Assert.All(r.Command.Writes, w => Assert.Equal("60", w.Value));
        Assert.Equal(SettingsStore.WeekdayKeys.OrderBy(k => k), r.Command.Writes.Select(w => w.BaseKey).OrderBy(k => k));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1441")]
    [InlineData("abc")]
    public void Set_limit_rejects_out_of_range(string minutes)
    {
        var r = Parse("set-limit", "monday", minutes);
        Assert.False(r.Ok);
        Assert.Equal(CliExit.Invalid, r.ErrorCode);
    }

    [Fact]
    public void Set_limit_rejects_bad_day()
    {
        Assert.False(Parse("set-limit", "funday", "60").Ok);
    }

    [Fact]
    public void Set_timeout_converts_minutes_to_seconds()
    {
        var r = Parse("set-timeout", "10");
        Assert.True(r.Ok);
        var w = Assert.Single(r.Command!.Writes);
        Assert.Equal("lock_screen_timeout", w.BaseKey);
        Assert.Equal("600", w.Value);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("721")]
    public void Set_timeout_rejects_out_of_range(string minutes)
    {
        Assert.False(Parse("set-timeout", minutes).Ok);
    }

    [Fact]
    public void Set_schedule_toggle_only()
    {
        var r = Parse("set-schedule", "enabled");
        Assert.True(r.Ok);
        var w = Assert.Single(r.Command!.Writes);
        Assert.Equal("schedule_enabled", w.BaseKey);
        Assert.Equal("1", w.Value);

        Assert.Equal("0", Parse("set-schedule", "disabled").Command!.Writes.Single().Value);
    }

    [Fact]
    public void Set_schedule_with_grid_normalizes_and_round_trips()
    {
        var blocked0to6 = new string('0', 24) + new string('1', 72); // 96 chars
        var grid = string.Join(';', Enumerable.Repeat(blocked0to6, 7));
        var r = Parse("set-schedule", "enabled", grid);
        Assert.True(r.Ok);
        Assert.Equal(2, r.Command!.Writes.Count);
        var scheduleWrite = r.Command.Writes.Single(w => w.BaseKey == "schedule");
        Assert.Equal(Schedule.Parse(grid).Serialize(), scheduleWrite.Value);
    }

    [Theory]
    [InlineData("tooShort")]                       // not 7 rows
    [InlineData("0;0;0;0;0;0;0")]                  // rows not 96 chars
    public void Set_schedule_rejects_bad_grid(string grid)
    {
        Assert.False(Parse("set-schedule", "enabled", grid).Ok);
    }

    [Fact]
    public void Set_schedule_rejects_non_binary_chars()
    {
        var badRow = new string('2', 96);
        var grid = string.Join(';', Enumerable.Repeat(badRow, 7));
        Assert.False(Parse("set-schedule", "enabled", grid).Ok);
    }

    [Fact]
    public void Set_passcode_hashes_and_meets_min_length()
    {
        var r = Parse("set-passcode", "69696969");
        Assert.True(r.Ok);
        var w = Assert.Single(r.Command!.Writes);
        Assert.Equal("passcode", w.BaseKey);
        Assert.True(PasscodeHash.IsHashed(w.Value));
        Assert.True(PasscodeHash.Verify("69696969", w.Value));
    }

    [Fact]
    public void Set_passcode_rejects_short()
    {
        Assert.False(Parse("set-passcode", "1234").Ok);
    }

    [Fact]
    public void Set_passcode_rejects_user_scope()
    {
        Assert.False(Parse("--user", "alice", "set-passcode", "69696969").Ok);
    }

    [Fact]
    public void User_flag_extracted_and_recorded()
    {
        var r = Parse("--user", "alice", "set-limit", "monday", "60");
        Assert.True(r.Ok);
        Assert.True(r.Command!.PerUser);
        Assert.Equal("alice", r.Command.UserArg);
    }

    [Fact]
    public void User_flag_rejected_on_global_key()
    {
        Assert.False(Parse("--user", "alice", "set", "dns_filter_mode", "family").Ok);
        Assert.False(Parse("--user", "alice", "get", "passcode").Ok);
    }

    [Fact]
    public void Pin_flag_extracted()
    {
        var r = Parse("set-limit", "monday", "60", "--pin", "69696969");
        Assert.True(r.Ok);
        Assert.Equal("69696969", r.Command!.PinArg);
    }

    [Fact]
    public void Flag_without_value_is_invalid()
    {
        Assert.False(Parse("set-limit", "monday", "60", "--pin").Ok);
        Assert.False(Parse("--user").Ok);
    }

    [Fact]
    public void Duplicate_flag_is_invalid()
    {
        Assert.False(Parse("--user", "a", "--user", "b", "get", "limit_monday").Ok);
    }

    [Theory]
    [InlineData("  5\n", null, null, "5")]
    [InlineData(null, "env-pin", null, "env-pin")]
    [InlineData(null, null, "arg-pin", "arg-pin")]
    [InlineData("stdin", "env", "arg", "arg")]   // explicit --pin wins over env + stdin
    [InlineData("stdin", "env", null, "env")]    // env wins over stdin
    [InlineData("", "", "arg", "arg")]
    public void Resolve_pin_precedence(string? stdin, string? env, string? arg, string expected)
    {
        Assert.Equal(expected, CliCommandParser.ResolvePin(stdin, env, arg));
    }

    [Fact]
    public void Resolve_pin_none()
    {
        Assert.Null(CliCommandParser.ResolvePin(null, null, null));
    }
}
