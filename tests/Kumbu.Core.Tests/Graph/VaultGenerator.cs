// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Tests.Graph;

/// <summary>One state of a generated vault; <paramref name="Trash"/> holds removed notes a restore can bring back.</summary>
public sealed record GeneratedVault(List<Note> Notes, List<VaultPath> Attachments, List<Note> Trash)
{
    public override string ToString() =>
        string.Join("\n", Notes.Select(n => $"== {n.Path}\n{n.Content}").Concat(Attachments.Select(a => $"== {a} (attachment)")));
}

/// <summary>
/// Seeded vaults that are hard on link resolution (spec §10): folders, the same name in several folders and in the
/// archive, case variants of names and of link text, aliases shared by several notes, wikilinks and Markdown links in
/// every form the parsers read, embeds, headings and blocks, links in code and front matter, attachments and folder
/// markers, missing targets and relative links that leave the vault. No two paths differ only by case: which of them a
/// link leads to would depend on the order the resolver meets them in.
/// </summary>
public static class VaultGenerator
{
    public const string VaultName = "W";

    private static readonly string[] Folders =
        ["", "", "Work", "work/Old", "Work/Projects", "Projects", "Projects/Work", "Diary", "a b", "Ünï", "_archive", "_archive/Work",
         "_archive/Projects/Work"];

    private static readonly string[] Names =
        ["Plan", "plan", "PLAN", "Café", "Café au lait", "Meeting Notes", "Plan (v2)", "Plan.v2", "Index", "Anna", "a.b", "x",
         "2026-11-16", "2026-W47", "Kumbu", "Report 2026", "shot.png", "Ünïcode", "Pumpe", "Work"];

    private static readonly string[] AttachmentNames =
        ["shot.png", "Shot.PNG", "doc.pdf", "Plan.png", "a b.jpg", "x.gif", "Café.webp", "report.pdf", ".vaultkeep"];

    private static readonly string[] Aliases = ["Pumpe", "Boss", "Anna", "Plan", "Kumbu", "Big Plan", "x"];

    private static readonly string[] Missing =
        ["Nowhere", "Lost/Note", "report.docx", "missing.png", "gone.pdf", "../../../../out", "/Nope/Plan", "./nope", "Notes.txt"];

    public static GeneratedVault Vault(int seed, int notes)
    {
        var random = new Random(seed);
        var vault = new GeneratedVault([], [], []);
        for (var tries = 0; vault.Attachments.Count < notes / 5 && tries < notes; tries++)
        {
            if (FreePath(random, vault, AttachmentNames, attachment: true) is { } path) vault.Attachments.Add(path);
        }

        // Paths first, then content, so links can name notes made after their source.
        for (var tries = 0; vault.Notes.Count < notes && tries < notes * 20; tries++)
        {
            if (FreePath(random, vault, Names, attachment: false) is { } path) vault.Notes.Add(new Note(path, ""));
        }

        for (var i = 0; i < vault.Notes.Count; i++) vault.Notes[i] = vault.Notes[i] with { Content = Content(random, vault, vault.Notes[i].Path) };
        return vault;
    }

    /// <summary>One random operation on a copy of <paramref name="vault"/>, and what it did.</summary>
    public static (GeneratedVault Vault, string Operation) Step(int seed, GeneratedVault vault)
    {
        var random = new Random(seed);
        var next = new GeneratedVault([.. vault.Notes], [.. vault.Attachments], [.. vault.Trash]);
        for (var tries = 0; tries < 20; tries++)
        {
            if (Apply(random, next) is { } operation) return (next, operation);
        }

        return (next, "nothing");
    }

    private static string? Apply(Random random, GeneratedVault v)
    {
        var any = v.Notes.Count > 0;
        switch (random.Next(10))
        {
            case 0:
            {
                if (FreePath(random, v, Names, attachment: false) is not { } path) return null;
                v.Notes.Add(new Note(path, Content(random, v, path)));
                return $"create {path}";
            }
            case 1 when any:
            {
                var i = random.Next(v.Notes.Count);
                v.Notes[i] = v.Notes[i] with { Content = Content(random, v, v.Notes[i].Path) };
                return $"rewrite {v.Notes[i].Path}";
            }
            case 2 when any:
            {
                var i = random.Next(v.Notes.Count);
                var note = v.Notes[i];
                var lines = note.Content.Split('\n').ToList();
                if (random.Next(2) == 0 || lines.Count < 2) lines.Insert(random.Next(lines.Count + 1), Line(random, v, note.Path));
                else lines.RemoveAt(random.Next(lines.Count));
                v.Notes[i] = note with { Content = string.Join('\n', lines) };
                return $"edit {note.Path}";
            }
            case 3 when any:
            {
                var i = random.Next(v.Notes.Count);
                var from = v.Notes[i].Path;
                var others = v with { Notes = [.. v.Notes.Where((_, j) => j != i)] };
                if (FreePath(random, others, Names, attachment: false) is not { } to) return null;
                v.Notes[i] = v.Notes[i] with { Path = to };
                return $"mv {from} {to}";
            }
            case 4:
            {
                // A folder moved, into the archive or out of it among others.
                var folders = v.Notes.Select(n => n.Path).Concat(v.Attachments).Where(p => p.Segments.Count > 2)
                    .Select(p => string.Join('/', p.Segments.Skip(1).SkipLast(1))).Distinct().ToList();
                if (folders.Count == 0) return null;
                var from = folders[random.Next(folders.Count)];
                var to = Pick(random, Folders);
                if (to.Length == 0 || to == from || to.StartsWith(from + "/", StringComparison.Ordinal)) return null;
                VaultPath Moved(VaultPath p)
                {
                    var inside = string.Join('/', p.Segments.Skip(1));
                    return inside.StartsWith(from + "/", StringComparison.Ordinal)
                        ? P($"/{VaultName}/{to}/{inside[(from.Length + 1)..]}")
                        : p;
                }

                var notes = v.Notes.Select(n => n with { Path = Moved(n.Path) }).ToList();
                var attachments = v.Attachments.Select(Moved).ToList();
                if (!Unique(notes.Select(n => n.Path).Concat(attachments))) return null;
                v.Notes.Clear();
                v.Notes.AddRange(notes);
                v.Attachments.Clear();
                v.Attachments.AddRange(attachments);
                return $"mv {from}/ {to}/";
            }
            case 5 when any:
            {
                var i = random.Next(v.Notes.Count);
                v.Trash.Add(v.Notes[i]);
                v.Notes.RemoveAt(i);
                return $"rm {v.Trash[^1].Path}";
            }
            case 6 when v.Trash.Count > 0:
            {
                var i = random.Next(v.Trash.Count);
                var note = v.Trash[i];
                if (!Unique(v.Notes.Select(n => n.Path).Concat(v.Attachments).Append(note.Path))) return null;
                v.Trash.RemoveAt(i);
                v.Notes.Add(note);
                return $"restore {note.Path}";
            }
            case 7 when any:
            {
                var i = random.Next(v.Notes.Count);
                var note = v.Notes[i];
                v.Notes[i] = note with { Content = WithAliases(random, note.Content) };
                return $"aliases {note.Path}";
            }
            case 8:
            {
                if (FreePath(random, v, AttachmentNames, attachment: true) is not { } path) return null;
                v.Attachments.Add(path);
                return $"upload {path}";
            }
            case 9 when v.Attachments.Count > 0:
            {
                var i = random.Next(v.Attachments.Count);
                var path = v.Attachments[i];
                v.Attachments.RemoveAt(i);
                return $"rm {path}";
            }
        }

        return null;
    }

    private static VaultPath? FreePath(Random random, GeneratedVault v, string[] names, bool attachment)
    {
        for (var tries = 0; tries < 10; tries++)
        {
            var folder = Pick(random, Folders);
            var name = Pick(random, names);
            if (attachment && name == ".vaultkeep" && folder.Length == 0) continue;
            var path = P($"/{VaultName}/{(folder.Length > 0 ? folder + "/" : "")}{name}{(attachment ? "" : ".md")}");
            if (Unique(v.Notes.Select(n => n.Path).Concat(v.Attachments).Append(path))) return path;
        }

        return null;
    }

    private static bool Unique(IEnumerable<VaultPath> paths)
    {
        var all = paths.ToList();
        return all.Select(p => p.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() == all.Count;
    }

    private static string Content(Random random, GeneratedVault v, VaultPath self)
    {
        var lines = new List<string>();
        if (random.Next(3) == 0) lines.AddRange(Frontmatter(random, v, self));
        var count = random.Next(0, 12);
        for (var i = 0; i < count; i++) lines.Add(Line(random, v, self));
        var newline = random.Next(8) == 0 ? "\r\n" : "\n";
        return string.Join(newline, lines) + (random.Next(2) == 0 ? newline : "");
    }

    private static IEnumerable<string> Frontmatter(Random random, GeneratedVault v, VaultPath self)
    {
        yield return "---";
        switch (random.Next(6))
        {
            case 0: yield return $"aliases: {Pick(random, Aliases)}"; break;
            case 1: yield return $"aliases: [{Pick(random, Aliases)}, {Flip(random, Pick(random, Aliases))}]"; break;
            case 2:
                yield return "aliases:";
                yield return $"  - {Pick(random, Aliases)}";
                yield return $"  - \"{Pick(random, Aliases)}\"";
                yield return "  - a/b";
                break;
            case 3: yield return "aliases: [unclosed"; break;
            case 4: yield return $"related: \"{Wiki(random, v, self)}\""; break;
            default: yield return "tags: [x]"; break;
        }

        if (random.Next(3) == 0) yield return "when: 2026-11-16";
        yield return "---";
    }

    private static string WithAliases(Random random, string content)
    {
        var lines = content.Split('\n').ToList();
        var body = lines.Count > 0 && lines[0].TrimEnd('\r') == "---" && lines.Skip(1).ToList().FindIndex(l => l.TrimEnd('\r') == "---") is var end and >= 0
            ? lines.Skip(end + 2)
            : lines;
        if (random.Next(4) == 0) return string.Join('\n', body);
        var aliases = Enumerable.Range(0, random.Next(1, 4)).Select(_ => Flip(random, Pick(random, Aliases)));
        return string.Join('\n', ["---", $"aliases: [{string.Join(", ", aliases)}]", "---", .. body]);
    }

    private static string Line(Random random, GeneratedVault v, VaultPath self) => random.Next(14) switch
    {
        0 => "",
        1 => "Some prose without links.",
        2 => $"# {Pick(random, Names)}",
        3 => $"A paragraph with a block ^b{random.Next(10)}",
        4 => random.Next(2) == 0 ? "```" : "~~~",
        5 => $"Code `{Link(random, v, self)}` and ``{Link(random, v, self)}`` stay code",
        6 => $"| {Wiki(random, v, self).Replace("|", "\\|")} | cell |",
        7 => $"    {Link(random, v, self)}",
        8 => $"\\[[{Target(random, v, self)}]] and \\[x]({Target(random, v, self)}.md)",
        9 => $"Twice: {Link(random, v, self)} {Link(random, v, self)} {Link(random, v, self)}",
        _ => $"See {Link(random, v, self)}{(random.Next(3) == 0 ? $", then {Link(random, v, self)}" : "")}.",
    };

    private static string Link(Random random, GeneratedVault v, VaultPath self) =>
        random.Next(5) < 3 ? Wiki(random, v, self) : Markdown(random, v, self);

    private static string Wiki(Random random, GeneratedVault v, VaultPath self)
    {
        var target = Target(random, v, self);
        var alias = random.Next(4) == 0 ? "|shown" : "";
        return $"{(random.Next(5) == 0 ? "!" : "")}[[{target}{Subpath(random)}{alias}]]";
    }

    private static string Markdown(Random random, GeneratedVault v, VaultPath self)
    {
        var target = Target(random, v, self);
        if (target.Trim().Length > 0 && !target.Contains('.') && random.Next(4) > 0) target += ".md";
        var destination = random.Next(3) switch
        {
            0 => $"<{target}{Subpath(random)}>",
            1 => string.Join('/', target.Split('/').Select(Uri.EscapeDataString)) + Subpath(random),
            _ => target.Replace(" ", "%20") + Subpath(random),
        };
        if (destination.Length == 0) destination = "#Heading";
        var title = random.Next(5) == 0 ? " \"a title\"" : "";
        return $"{(random.Next(5) == 0 ? "!" : "")}[text]({destination}{title})";
    }

    private static string Subpath(Random random) => random.Next(5) switch
    {
        0 => "#Plan",
        1 => "#^b1",
        _ => "",
    };

    /// <summary>What a link names: an existing note, attachment or alias in one of the forms that find it, or nothing.</summary>
    private static string Target(Random random, GeneratedVault v, VaultPath self)
    {
        var roll = random.Next(20);
        if (roll == 0) return "";
        if (roll < 3) return Pick(random, Missing);
        if (roll < 5) return Flip(random, Pick(random, Aliases));
        var paths = roll < 8 && v.Attachments.Count > 0 ? v.Attachments : v.Notes.Select(n => n.Path).ToList();
        if (paths.Count == 0) return Pick(random, Missing);
        var target = paths[random.Next(paths.Count)];
        var inside = target.Segments.Skip(1).ToList();
        if (target.IsNote && random.Next(3) > 0) inside[^1] = inside[^1][..^".md".Length];
        var text = random.Next(6) switch
        {
            // The name, or a tail of the path.
            0 or 1 => inside[^1],
            2 => string.Join('/', inside.Skip(random.Next(inside.Count))),
            3 => "/" + string.Join('/', inside),
            // Relative to the source's folder, sometimes past the vault's root.
            4 => (random.Next(self.Segments.Count) is var up && up == 0 ? "./" : string.Concat(Enumerable.Repeat("../", up)))
                + string.Join('/', inside.Skip(random.Next(inside.Count))),
            _ => " " + inside[^1] + " ",
        };
        if (random.Next(4) == 0) text = Flip(random, text);
        // Link text is read NFC, as paths are.
        return random.Next(3) == 0 ? text.Replace("é", "e\u0301") : text;
    }

    private static string Flip(Random random, string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (var c in text) result.Append(random.Next(3) == 0 ? (char.IsUpper(c) ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
        return result.ToString();
    }

    private static string Pick(Random random, string[] items) => items[random.Next(items.Length)];

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);
}
