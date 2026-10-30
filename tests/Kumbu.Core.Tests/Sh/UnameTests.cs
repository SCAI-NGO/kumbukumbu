// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

public class UnameTests
{
    private static Task<ShellResult> Run(string line)
    {
        var store = new InMemoryNoteStore();
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded);
        var caller = new FixedCaller(Writers.Dev);
        var shell = new Shell(store, new NoteWriter(store, time, caller), new NoteMover(store, time, caller), version: "0.3.0+a6362ec");
        return shell.RunAsync(line, CancellationToken.None);
    }

    [Theory]
    [InlineData("uname", "Kumbukumbu")]
    [InlineData("uname -s", "Kumbukumbu")]
    [InlineData("uname -r", "0.3.0")]
    [InlineData("uname -v", "0.3.0+a6362ec")]
    [InlineData("uname -o", "Kumbu²")]
    [InlineData("uname -sr", "Kumbukumbu 0.3.0")]
    [InlineData("uname -r -s", "Kumbukumbu 0.3.0")]
    public async Task PrintsWhatTheOptionsAskFor(string line, string expected)
    {
        Assert.Equal(ShellResult.Ok(expected), await Run(line));
    }

    [Theory]
    [InlineData("uname -a")]
    [InlineData("uname --all")]
    public async Task AllInUnameOrder(string line)
    {
        var result = await Run(line);
        Assert.False(result.IsError);
        Assert.Matches(@"^Kumbukumbu 0\.3\.0 0\.3\.0\+a6362ec \S+ Kumbu²$", result.Output);
    }

    [Theory]
    [InlineData("uname -x", "uname: invalid option -- 'x'")]
    [InlineData("uname --kernel", "uname: unrecognized option '--kernel'")]
    [InlineData("uname /Work", "uname: extra operand '/Work'")]
    public async Task RefusesWhatItDoesNotKnow(string line, string error)
    {
        var result = await Run(line);
        Assert.True(result.IsError);
        Assert.Equal(error, result.Output);
    }

    [Fact]
    public async Task ReportsALocalBuildAsDev()
    {
        Assert.Equal(ShellResult.Ok("0.0.0-dev"), await TestShell.Of().RunAsync("uname -r", CancellationToken.None));
    }
}
