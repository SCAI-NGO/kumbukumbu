// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.Json;
using Kumbu.Core.Sh;
using Kumbu.Server.Api;

namespace Kumbu.Server.Tests.Api;

/// <summary>The web console's tokeniser and command lists are checked against the same JSON file
/// (<c>web/src/console/console-fixture.json</c>) that its own tests read.</summary>
public class ConsoleFixtureTests
{
    private static JsonElement Fixture()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (!File.Exists(Path.Combine(dir.FullName, "Kumbu.slnx"))) continue;
            var path = Path.Combine(dir.FullName, "web", "src", "console", "console-fixture.json");
            return JsonDocument.Parse(File.ReadAllText(path)).RootElement.Clone();
        }

        throw new InvalidOperationException("Could not find Kumbu.slnx above " + AppContext.BaseDirectory);
    }

    private static string[] Names(string property) =>
        [.. Fixture().GetProperty(property).EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public void Lines_parse_as_the_fixture_says()
    {
        var lines = Fixture().GetProperty("lines").EnumerateArray().ToList();
        Assert.NotEmpty(lines);
        foreach (var entry in lines)
        {
            var line = entry.GetProperty("line").GetString()!;
            var parsed = Pipeline.TryParse(line, out var stages, out _);
            if (entry.TryGetProperty("refused", out _))
            {
                Assert.False(parsed, $"{line} should be refused");
                continue;
            }

            Assert.True(parsed, $"{line} should parse");
            var expected = entry.GetProperty("stages").EnumerateArray()
                .Select(s => s.EnumerateArray().Select(w => w.GetString()!).ToArray()).ToArray();
            Assert.True(
                expected.Length == stages.Count && expected.Zip(stages).All(p => p.First.SequenceEqual(p.Second)),
                $"{line} gave {JsonSerializer.Serialize(stages)}, the fixture says {JsonSerializer.Serialize(expected)}");
        }
    }

    [Fact]
    public void Allowed_is_the_servers() =>
        Assert.True(ConsoleApi.Allowed.SetEquals(Names("allowed")));

    [Fact]
    public void Refused_is_the_servers() =>
        Assert.True(ConsoleApi.Refused.SetEquals(Names("refused")));
}
