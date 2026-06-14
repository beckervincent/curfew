using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

/// <summary>tests for <see cref="SemVer"/> three-part version type the updater uses to tell if a GitHub release is newer; pins parsing rules and most-significant-first ordering</summary>
public class VersionCompareTests
{
    // ---- Parsing: accepted forms -------------------------------------------------

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v2.0.0", 2, 0, 0)]
    [InlineData("V2.0.0", 2, 0, 0)]              // uppercase 'V' prefix also stripped
    [InlineData("0.0.0", 0, 0, 0)]              // all-zero is valid
    [InlineData("10.20.30", 10, 20, 30)]        // multi-digit components
    [InlineData("  1.2.3  ", 1, 2, 3)]          // surrounding whitespace trimmed
    [InlineData("  v1.2.3  ", 1, 2, 3)]         // trim before prefix removal
    [InlineData("1.2.3.4", 1, 2, 3)]            // components beyond patch ignored
    [InlineData("1.2.3.4.5", 1, 2, 3)]
    public void Parse_accepts_valid(string text, int major, int minor, int patch)
    {
        var v = SemVer.Parse(text);
        Assert.Equal(new SemVer(major, minor, patch), v);
    }

    [Fact]
    public void Parse_accepts_large_components()
    {
        // components parsed as Int32; max round-trips, no overflow
        var v = SemVer.Parse($"{int.MaxValue}.{int.MaxValue}.{int.MaxValue}");
        Assert.Equal(new SemVer(int.MaxValue, int.MaxValue, int.MaxValue), v);
    }

    // ---- Parsing: rejected forms -------------------------------------------------

    [Theory]
    [InlineData(null)]                  // null
    [InlineData("")]                    // empty
    [InlineData("   ")]                 // whitespace only
    [InlineData("1.2")]                 // too few components
    [InlineData("1")]
    [InlineData("v1.2")]
    [InlineData("x.y.z")]               // non-numeric components
    [InlineData("1.2.x")]              // only patch non-numeric
    [InlineData("1.x.3")]
    [InlineData("a.2.3")]
    [InlineData("-1.2.3")]              // leading sign rejected (NumberStyles.None)
    [InlineData("1.-2.3")]
    [InlineData("+1.2.3")]
    [InlineData("1.2.3-rc.1")]          // pre-release suffix not interpreted, patch fails
    [InlineData("1.2.3+build.5")]      // build-metadata suffix likewise
    [InlineData("1 .2.3")]             // whitespace inside component rejected
    [InlineData("1.2. 3")]
    [InlineData("1.2.3 4")]
    [InlineData("1,2,3")]              // comma not a separator
    [InlineData("1.2.3,4")]
    [InlineData("0x10.2.3")]           // hex rejected
    [InlineData("1..3")]               // empty middle component
    [InlineData(".2.3")]               // empty leading component
    [InlineData("vv1.2.3")]            // only single 'v'/'V' prefix stripped
    [InlineData("ver1.2.3")]
    public void Parse_rejects_invalid(string? text)
    {
        Assert.Null(SemVer.Parse(text));
    }

    [Fact]
    public void Parse_rejects_thousands_separator()
    {
        // NumberStyles.None: grouping separators never sneak a big number through, any culture
        Assert.Null(SemVer.Parse("1,000.2.3"));
    }

    [Fact]
    public void Parse_rejects_overflowing_component()
    {
        // one past Int32.MaxValue overflows; reject, not wrap
        Assert.Null(SemVer.Parse("2147483648.0.0"));
    }

    // ---- Equality and record semantics ------------------------------------------

    [Fact]
    public void Equal_versions_compare_equal()
    {
        var a = new SemVer(1, 2, 3);
        var b = SemVer.Parse("v1.2.3");

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.Equal(a.GetHashCode(), b!.Value.GetHashCode());
    }

    [Theory]
    [InlineData(1, 2, 3, 1, 2, 4)]
    [InlineData(1, 2, 3, 1, 3, 3)]
    [InlineData(1, 2, 3, 2, 2, 3)]
    public void Different_versions_are_not_equal(int aMaj, int aMin, int aPat, int bMaj, int bMin, int bPat)
    {
        var a = new SemVer(aMaj, aMin, aPat);
        var b = new SemVer(bMaj, bMin, bPat);

        Assert.NotEqual(a, b);
        Assert.False(a == b);
        Assert.True(a != b);
    }

    // ---- Ordering: operators -----------------------------------------------------

    [Fact]
    public void Ordering_is_semantic()
    {
        Assert.True(SemVer.Parse("2.1.0") > SemVer.Parse("2.0.9"));
        Assert.True(SemVer.Parse("1.0.0") < SemVer.Parse("1.0.1"));
        Assert.True(SemVer.Parse("2.2.0") >= SemVer.Parse("2.2.0"));
    }

    [Fact]
    public void Major_dominates_minor_and_patch()
    {
        // bigger major wins even with smaller minor/patch; keeps "2.0.0" newer than "1.9.9"
        Assert.True(new SemVer(2, 0, 0) > new SemVer(1, 9, 9));
        Assert.True(new SemVer(1, 9, 9) < new SemVer(2, 0, 0));
    }

    [Fact]
    public void Minor_dominates_patch()
    {
        Assert.True(new SemVer(1, 2, 0) > new SemVer(1, 1, 9));
        Assert.True(new SemVer(1, 1, 9) < new SemVer(1, 2, 0));
    }

    [Fact]
    public void Comparison_operators_are_consistent_for_equal_versions()
    {
        var a = new SemVer(1, 2, 3);
        var b = new SemVer(1, 2, 3);

        Assert.False(a < b);
        Assert.False(a > b);
        Assert.True(a <= b);
        Assert.True(a >= b);
    }

    [Theory]
    [InlineData(1, 0, 0, 2, 0, 0)]      // strictly increasing per component
    [InlineData(1, 0, 0, 1, 1, 0)]
    [InlineData(1, 1, 0, 1, 1, 1)]
    public void Strictly_ordered_pairs_satisfy_all_operators(
        int aMaj, int aMin, int aPat, int bMaj, int bMin, int bPat)
    {
        var lower = new SemVer(aMaj, aMin, aPat);
        var higher = new SemVer(bMaj, bMin, bPat);

        Assert.True(lower < higher);
        Assert.True(lower <= higher);
        Assert.True(higher > lower);
        Assert.True(higher >= lower);
        Assert.False(lower > higher);
        Assert.False(lower >= higher);
    }

    // ---- Ordering: CompareTo (the operators delegate to it) ----------------------

    [Fact]
    public void CompareTo_returns_sign_only()
    {
        // callers and operators rely on sign, not magnitude
        Assert.True(new SemVer(2, 0, 0).CompareTo(new SemVer(1, 0, 0)) > 0);
        Assert.True(new SemVer(1, 0, 0).CompareTo(new SemVer(2, 0, 0)) < 0);
        Assert.Equal(0, new SemVer(1, 2, 3).CompareTo(new SemVer(1, 2, 3)));
    }

    [Fact]
    public void CompareTo_is_antisymmetric()
    {
        var lower = new SemVer(1, 2, 3);
        var higher = new SemVer(1, 2, 4);

        Assert.True(lower.CompareTo(higher) < 0);
        Assert.True(higher.CompareTo(lower) > 0);
    }

    [Fact]
    public void CompareTo_is_transitive()
    {
        var a = new SemVer(1, 0, 0);
        var b = new SemVer(1, 1, 0);
        var c = new SemVer(2, 0, 0);

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(c) < 0);
        Assert.True(a.CompareTo(c) < 0);
    }

    // ---- ToString and round-tripping ---------------------------------------------

    [Theory]
    [InlineData(1, 2, 3, "1.2.3")]
    [InlineData(0, 0, 0, "0.0.0")]
    [InlineData(10, 20, 30, "10.20.30")]
    public void ToString_produces_canonical_form(int major, int minor, int patch, string expected)
    {
        Assert.Equal(expected, new SemVer(major, minor, patch).ToString());
    }

    [Fact]
    public void ToString_drops_v_prefix_and_extra_components()
    {
        // canonical string never carries 'v' prefix or ignored components
        Assert.Equal("1.2.3", SemVer.Parse("v1.2.3.4")!.Value.ToString());
    }

    [Theory]
    [InlineData("1.2.3")]
    [InlineData("0.0.0")]
    [InlineData("10.20.30")]
    public void Parse_and_ToString_round_trip(string canonical)
    {
        var parsed = SemVer.Parse(canonical);
        Assert.NotNull(parsed);
        Assert.Equal(canonical, parsed!.Value.ToString());
    }
}
