// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sections;

/// <summary>
/// A heading and the section it opens. Line numbers are 0-based indexes into the note's lines.
/// </summary>
/// <param name="Index">1-based position among siblings with the same text, or 0 if the text is unique there.</param>
/// <param name="End">Exclusive: the next heading of the same or a higher level, or the end of the note.</param>
/// <param name="ContentEnd">Exclusive: <paramref name="End"/> without the trailing blank lines.</param>
public sealed record Heading(int Level, string Text, int Index, int Line, int End, int ContentEnd, Heading? Parent)
{
    /// <summary>The full address from the top, e.g. <c>#Architecture#Notes[2]</c>.</summary>
    public string Address => (Parent?.Address ?? "") + "#" + SectionAddress.Escape(Text) + (Index > 0 ? $"[{Index}]" : "");

    /// <summary>This heading and its ancestors, innermost first.</summary>
    public IEnumerable<Heading> Chain()
    {
        for (var h = this; h is not null; h = h.Parent) yield return h;
    }
}
