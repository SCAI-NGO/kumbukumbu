// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;

namespace Kumbu.Core.Tests.Sh;

public class OptionsTests
{
    private static readonly Option[] Spec =
    [
        new('i', "ignore-case"), new('v', "invert"), new('n', "lines", TakesValue: true), new(null, "files"),
    ];

    private static Options Parse(params string[] args)
    {
        Assert.True(Options.TryParse("t", args, Spec, out var options, out var error, digitsMean: "lines"), error?.Output);
        return options;
    }

    private static string Fail(params string[] args)
    {
        Assert.False(Options.TryParse("t", args, Spec, out _, out var error, digitsMean: "lines"));
        Assert.True(error.IsError);
        return error.Output;
    }

    [Fact]
    public void CombinedFlags()
    {
        var o = Parse("-iv", "x");
        Assert.True(o.Has("ignore-case"));
        Assert.True(o.Has("invert"));
        Assert.Equal(["x"], o.Operands);
    }

    [Theory]
    [InlineData("-n", "5")]
    [InlineData("-n5")]
    [InlineData("-5")]
    [InlineData("--lines=5")]
    [InlineData("--lines", "5")]
    [InlineData("-in5")]
    public void ValueForms(params string[] args) => Assert.Equal("5", Parse(args).Value("lines"));

    [Fact]
    public void LastValueWinsAndAllAreKept()
    {
        var o = Parse("-n", "1", "-n", "2");
        Assert.Equal("2", o.Value("lines"));
        Assert.Equal(["1", "2"], o.Values("lines"));
    }

    [Fact]
    public void OptionsMayFollowOperands()
    {
        var o = Parse("x", "-i", "/W");
        Assert.True(o.Has("ignore-case"));
        Assert.Equal(["x", "/W"], o.Operands);
    }

    [Fact]
    public void DoubleDashEndsOptions() => Assert.Equal(["-i", "--files"], Parse("--", "-i", "--files").Operands);

    [Fact]
    public void LoneDashIsAnOperand() => Assert.Equal(["-"], Parse("-").Operands);

    [Fact]
    public void LongOnlyOption() => Assert.True(Parse("--files").Has("files"));

    [Fact]
    public void UnknownShort() => Assert.Equal("t: invalid option -- 'x'", Fail("-ix"));

    [Fact]
    public void UnknownLong() => Assert.Equal("t: unrecognized option '--nope'", Fail("--nope"));

    [Fact]
    public void MissingShortValue() => Assert.Equal("t: option requires an argument -- 'n'", Fail("-n"));

    [Fact]
    public void MissingLongValue() => Assert.Equal("t: option '--lines' requires an argument", Fail("--lines"));

    [Fact]
    public void ValueForAFlag() => Assert.Equal("t: option '--files' doesn't allow an argument", Fail("--files=x"));
}
