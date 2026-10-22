// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>How text flows through a pipe: as lines separated by '\n'.</summary>
public static class Lines
{
    /// <summary>Splits on '\n'. One trailing newline ends the last line rather than adding an empty one.</summary>
    public static IReadOnlyList<string> Split(string text)
    {
        if (text.Length == 0) return [];
        if (text[^1] == '\n') text = text[..^1];
        return text.Split('\n');
    }

    public static string Join(IEnumerable<string> lines) => string.Join('\n', lines);
}
