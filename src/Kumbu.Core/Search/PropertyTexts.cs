// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Paths;
using Kumbu.Core.Queries;

namespace Kumbu.Core.Search;

/// <summary>
/// The properties embedded beside a note's sections (embeddable properties design, decisions 1–2): a text value of
/// <see cref="Names"/>, not blank, as the note's title, a line break and the value, the shape of a chunk's embedding
/// text, so its vector lives in the sections' space. A value too long for the bound is cut as a section's core is.
/// </summary>
public static class PropertyTexts
{
    public static readonly IReadOnlyList<string> Names = ["summary", "description"];

    /// <summary>The texts to embed, in <see cref="Names"/> order.</summary>
    public static IReadOnlyList<(string Name, string Text)> Of(VaultPath path, string content, ChunkBound bound)
    {
        var values = Properties.Read(content).Values;
        var texts = new List<(string Name, string Text)>();
        (string Text, int Cost)? context = null;
        foreach (var name in Names)
        {
            if (!values.TryGetValue(name, out var value) || value is not TextValue { Text: var raw } || string.IsNullOrWhiteSpace(raw)) continue;
            context ??= ChunkSplitter.Shortened(Chunker.Title(path), bound);
            var text = raw.Trim();
            var whole = context.Value.Text + "\n" + text;
            texts.Add((name, bound.Fits(whole) && Chunker.Words(text) <= Chunker.MaxWords
                ? whole
                : context.Value.Text + "\n" + ChunkSplitter.Pieces(text, context.Value, bound)[0]));
        }

        return texts;
    }
}
