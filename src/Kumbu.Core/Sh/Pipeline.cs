// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;

namespace Kumbu.Core.Sh;

/// <summary>Splits a command line into the stages of a pipeline; each stage is a command and its arguments.</summary>
public static class Pipeline
{
    public static bool TryParse(
        string input, out IReadOnlyList<IReadOnlyList<string>> stages, [NotNullWhen(false)] out string? error)
    {
        stages = [];
        if (!Tokenizer.TryTokenize(input, out var tokens, out error)) return false;
        if (tokens.Count == 0) return true;

        var result = new List<IReadOnlyList<string>>();
        var current = new List<string>();
        foreach (var token in tokens)
        {
            if (!token.IsPipe)
            {
                current.Add(token.Text);
                continue;
            }

            if (current.Count == 0)
            {
                error = "sh: syntax error near '|'";
                return false;
            }

            result.Add(current);
            current = [];
        }

        if (current.Count == 0)
        {
            error = "sh: syntax error: '|' needs a command after it";
            return false;
        }

        result.Add(current);
        stages = result;
        return true;
    }
}
