// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Links;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Links;

public class LinkResolverTests
{
    private static VaultPath P(string s) => VaultPath.TryParse(s, out var p) ? p : throw new ArgumentException(s);

    private static readonly LinkResolver Resolver = new("W",
    [
        P("/W/projects/kumbu.md"), P("/W/inbox.md"), P("/W/a/plan.md"), P("/W/b/plan.md"),
        P("/W/Überlegungen.md"), P("/Other/inbox.md"), P("/W/v1.2 notes.md"),
    ]);

    private static string Resolve(string source, string target)
    {
        var r = Resolver.Resolve(P(source), target);
        return r.Kind switch
        {
            LinkKind.Note => r.Target!.Value,
            LinkKind.Ambiguous => "ambiguous: " + string.Join(", ", r.Candidates.Select(c => c.Value).Order()),
            _ => r.Kind.ToString(),
        };
    }

    [Theory]
    [InlineData("/W/inbox.md", "kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "projects/kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "projects/kumbu.md", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "/projects/kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "KUMBU", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "überlegungen", "/W/Überlegungen.md")]
    [InlineData("/W/inbox.md", "Überlegungen", "/W/Überlegungen.md")]
    [InlineData("/W/inbox.md", "v1.2 notes", "/W/v1.2 notes.md")]
    [InlineData("/W/a/plan.md", "plan", "/W/a/plan.md")]
    [InlineData("/W/b/x.md", "plan", "/W/b/plan.md")]
    [InlineData("/W/inbox.md", "a/plan", "/W/a/plan.md")]
    [InlineData("/W/a/plan.md", "../b/plan", "/W/b/plan.md")]
    [InlineData("/W/projects/kumbu.md", "./kumbu", "/W/projects/kumbu.md")]
    [InlineData("/W/inbox.md", "plan", "ambiguous: /W/a/plan.md, /W/b/plan.md")]
    [InlineData("/W/inbox.md", "nope", "NotFound")]
    [InlineData("/W/inbox.md", "./kumbu", "NotFound")]
    [InlineData("/W/inbox.md", "/kumbu", "NotFound")]
    [InlineData("/W/inbox.md", "../../../etc/passwd", "NotFound")]
    [InlineData("/W/inbox.md", "jects/kumbu", "NotFound")]
    [InlineData("/W/inbox.md", "diagram.png", "Attachment")]
    [InlineData("/W/inbox.md", "", "Self")]
    public void Resolves(string source, string target, string expected) => Assert.Equal(expected, Resolve(source, target));

    [Fact]
    public void StaysInsideTheVault() => Assert.Equal("/W/inbox.md", Resolve("/W/projects/kumbu.md", "inbox"));

    [Fact]
    public void LinkTextIsTheVaultPathWithoutMd() =>
        Assert.Equal("projects/kumbu", LinkResolver.LinkText(P("/W/projects/kumbu.md")));
}
