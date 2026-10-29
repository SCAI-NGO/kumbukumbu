// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

public class ShellPartsTests
{
    private sealed class ImageCommand : ICommand
    {
        public int Runs { get; private set; }
        public string Name => "fake";
        public string Usage => "fake";
        public string Summary => "shows images";
        public string Help => "fake";
        public bool ReadsInput => true;
        public bool ProducesImages => true;

        public Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
        {
            Runs++;
            ShellPart[] parts = [new TextPart(new string('t', 30_000)), new ImagePart([1, 2, 3], "image/png")];
            return Task.FromResult(ShellResult.Ok(new string('t', 30_000)) with { Parts = parts });
        }
    }

    private sealed class Recorder : IShellObserver
    {
        public List<IReadOnlyList<string>> Lines { get; } = [];

        public void Ran(IReadOnlyList<string> commands) => Lines.Add(commands);

        public void Timed(string command, TimeSpan elapsed)
        {
        }
    }

    private static (Shell Shell, ImageCommand Fake, Recorder Seen) Make()
    {
        var store = new InMemoryNoteStore(("/V/a.md", "one\ntwo\n"));
        var time = new FakeTimeProvider(InMemoryNoteStore.Seeded.AddDays(1));
        var seen = new Recorder();
        var shell = new Shell(store, new NoteWriter(store, time, new FixedCaller(Writers.Dev)), new NoteMover(store, time, new FixedCaller(Writers.Dev)), observer: seen);
        var fake = new ImageCommand();
        shell.Register(fake);
        return (shell, fake, seen);
    }

    [Theory]
    [InlineData("fake | head")]
    [InlineData("cat /V/a.md | fake")]
    [InlineData("cat /V/a.md | fake | head")]
    public async Task A_command_that_shows_images_cannot_be_piped(string line)
    {
        var (shell, fake, _) = Make();

        var result = await shell.RunAsync(line, CancellationToken.None);

        Assert.True(result.IsError);
        Assert.Equal("sh: fake shows images and can't be part of a pipeline; run it on its own", result.Output);
        Assert.Equal(0, fake.Runs);
    }

    [Fact]
    public async Task A_refused_pipeline_is_reported_like_an_unknown_command()
    {
        var (shell, _, seen) = Make();

        await shell.RunAsync("fake | head", CancellationToken.None);

        Assert.Equal(["fake", "head"], Assert.Single(seen.Lines));
    }

    [Fact]
    public async Task Alone_it_returns_its_parts_and_the_output_limit_leaves_them()
    {
        var (shell, fake, _) = Make();

        var result = await shell.RunAsync("fake", CancellationToken.None);

        Assert.False(result.IsError);
        Assert.Equal(1, fake.Runs);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Equal(30_000, Assert.IsType<TextPart>(result.Parts[0]).Text.Length);
        Assert.Equal([1, 2, 3], Assert.IsType<ImagePart>(result.Parts[1]).Data);
        Assert.Equal(30_000, result.Output.Length);
    }
}
