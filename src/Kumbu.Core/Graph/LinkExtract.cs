// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Calendar;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Tags;

namespace Kumbu.Core.Graph;

/// <summary>What one note links to, read in one pass: the input the graph resolves into edges (spec §4).</summary>
/// <param name="Tags">Distinct, in first-use order, spelled as <c>sh tags</c> names them (no '#').</param>
/// <param name="When">None for a day or week note without its own <c>when</c>: the node kind <c>day</c> carries that.</param>
public sealed record LinkExtract(IReadOnlyList<NoteLink> Links, IReadOnlyList<string> Aliases, IReadOnlyList<string> Tags,
    CalendarTime? When, CalendarTime? Until)
{
    /// <summary>Raised together with LinkExtractTests.Scheme_is_pinned whenever a parser change alters what a note extracts.</summary>
    public const int SchemeVersion = 1;

    public static LinkExtract Of(VaultPath path, string content)
    {
        var tags = TagReader.Of(content).Select(u => u.Tag).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var entry = EntryReader.Read(new Note(path, content), board: null).Entry;
        var dated = entry is { ByName: false };
        return new LinkExtract(NoteLink.Parse(content), NoteAliases.Of(content), tags, dated ? entry!.When : null, dated ? entry!.Until : null);
    }
}
