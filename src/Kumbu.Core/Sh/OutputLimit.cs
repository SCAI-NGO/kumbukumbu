// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>Caps command output so a single call cannot flood the model's context.</summary>
public static class OutputLimit
{
    public const int MaxChars = 24_000;
    public const int CharsPerToken = 3;

    public static ShellResult Apply(ShellResult result) => Apply(result, MaxChars);

    /// <summary>Cuts to <paramref name="maxChars"/> (plus the footer), for output that gets more text appended.</summary>
    public static ShellResult Apply(ShellResult result, int maxChars)
    {
        var output = result.Output;
        if (output.Length <= maxChars) return result;

        // Cut after the last whole line: a cut line could pass for a complete one. Only a first line
        // longer than the limit is cut inside, and the footer says so.
        var lastBreak = output.LastIndexOf('\n', maxChars);
        var keep = lastBreak > 0 ? lastBreak : maxChars;
        // Never cut between the halves of a surrogate pair.
        if (lastBreak <= 0 && char.IsHighSurrogate(output[keep - 1])) keep--;

        var lines = output.AsSpan(0, keep).Count('\n') + 1;
        var where = lastBreak > 0 ? $"after line {lines}" : "inside line 1, which is longer than the limit";
        var tokens = (output.Length + CharsPerToken - 1) / CharsPerToken;
        var footer = $"\n[truncated {where} — showing {keep} of {output.Length} chars (~{tokens} tokens); narrow it with outline, cat 'note.md#Section', rg, head or tail]";
        return result with { Output = output[..keep] + footer };
    }
}
