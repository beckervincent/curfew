using Curfew.Core;
using Xunit;

namespace Curfew.Core.Tests;

public class VersionCompareTests
{
    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v2.0.0", 2, 0, 0)]
    public void Parse_accepts_valid(string text, int major, int minor, int patch)
    {
        var v = SemVer.Parse(text);
        Assert.Equal(new SemVer(major, minor, patch), v);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("x.y.z")]
    public void Parse_rejects_invalid(string text)
    {
        Assert.Null(SemVer.Parse(text));
    }

    [Fact]
    public void Ordering_is_semantic()
    {
        Assert.True(SemVer.Parse("2.1.0") > SemVer.Parse("2.0.9"));
        Assert.True(SemVer.Parse("1.0.0") < SemVer.Parse("1.0.1"));
        Assert.True(SemVer.Parse("2.2.0") >= SemVer.Parse("2.2.0"));
    }
}
