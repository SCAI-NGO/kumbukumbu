// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Sh;

/// <summary>Caps command output so a single call cannot flood the model's context.</summary>
public static class OutputLimit
{
    public const int MaxChars = 24_000;
    public const int CharsPerToken = 3;

    public static ShellResult Apply(ShellResult result)
    {
        var output = result.Output;
        if (output.Length <= MaxChars) return result;

        var keep = MaxChars;
        // Never cut between the halves of a surrogate pair.
        if (char.IsHighSurrogate(output[keep - 1])) keep--;

        var tokens = (output.Length + CharsPerToken - 1) / CharsPerToken;
        var footer = $"\n[truncated — showing {keep} of {output.Length} chars (~{tokens} tokens)]";
        return result with { Output = output[..keep] + footer };
    }
}
