// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Links;

/// <summary>
/// A wikilink as written: <c>[[target#Heading|alias]]</c>, or an embed <c>![[target]]</c>. Keeps the exact
/// source text of each part, so a rewrite can change the target and leave everything else byte for byte.
/// </summary>
/// <param name="Line">0-based line of the note.</param>
/// <param name="Start">Character index of the link (its '!' for an embed) in that line.</param>
/// <param name="Target">The link path, e.g. "kumbu" or "projects/kumbu"; empty for [[#Heading]].</param>
/// <param name="Subpath">"#Heading", "#^block" or "".</param>
/// <param name="Alias">"|alias" or "\|alias" (in tables), separator included, or "".</param>
public sealed partial record WikiLink(int Line, int Start, string Raw, bool Embed, string Target, string Subpath, string Alias)
    : NoteLink(Line, Start, Raw, Target, Subpath)
{
    /// <summary>The same link pointing to <paramref name="target"/>, everything else unchanged.</summary>
    public string WithTarget(string target) => $"{(Embed ? "!" : "")}[[{target}{Subpath}{Alias}]]";

    /// <summary>All wikilinks of a note, in order. Fenced code blocks and inline code spans don't count;
    /// frontmatter does, as Obsidian treats links in properties as links.</summary>
    public static new IReadOnlyList<WikiLink> Parse(string content)
    {
        var lines = Lines.Split(content);
        var code = Outline.CodeLines(lines);
        var result = new List<WikiLink>();
        for (var i = 0; i < lines.Count; i++)
        {
            if (code[i]) continue;
            var line = lines[i];
            var spans = CodeSpans(line);
            foreach (Match m in LinkRegex().Matches(line))
            {
                if (spans.Any(s => m.Index >= s.Start && m.Index < s.End)) continue;
                var link = FromMatch(i, m);
                // [[ ]] and [[|x]] name nothing; Obsidian doesn't treat them as links either.
                if (link.Target.Trim().Length == 0 && link.Subpath.Length == 0) continue;
                result.Add(link);
            }
        }

        return result;
    }

    private static WikiLink FromMatch(int line, Match m)
    {
        var inner = m.Groups[2].Value;
        var alias = "";
        var bar = inner.IndexOf('|');
        if (bar >= 0)
        {
            var start = bar > 0 && inner[bar - 1] == '\\' ? bar - 1 : bar;
            alias = inner[start..];
            inner = inner[..start];
        }

        var subpath = "";
        var hash = inner.IndexOf('#');
        if (hash >= 0)
        {
            subpath = inner[hash..];
            inner = inner[..hash];
        }

        return new WikiLink(line, m.Index, m.Value, m.Groups[1].Length > 0, inner, subpath, alias);
    }

    [GeneratedRegex(@"(!?)\[\[([^\[\]\n]+?)\]\]")]
    internal static partial Regex LinkRegex();
}
