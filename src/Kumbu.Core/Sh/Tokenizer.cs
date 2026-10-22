// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kumbu.Core.Sh;

/// <summary>Splits a command line into tokens using a small subset of POSIX quoting rules.</summary>
public static class Tokenizer
{
    private enum State { Normal, Single, Double }

    public static bool TryTokenize(
        string input, out IReadOnlyList<string> tokens, [NotNullWhen(false)] out string? error)
    {
        // Trailing newlines are what a model's "ls /Work\n" usually carries; strip them before tokenizing.
        input = input.Trim();
        var result = new List<string>();
        var current = new StringBuilder();
        var inToken = false;
        var state = State.Normal;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            switch (state)
            {
                case State.Normal:
                    // An unquoted line break would start a second command; we run exactly one per call.
                    if (c is '\n' or '\r' || (c == '\\' && i + 1 < input.Length && input[i + 1] is '\n' or '\r'))
                    {
                        tokens = [];
                        error = "sh: one command per call";
                        return false;
                    }

                    if (c is ' ' or '\t')
                    {
                        if (inToken)
                        {
                            result.Add(current.ToString());
                            current.Clear();
                            inToken = false;
                        }
                    }
                    else if (c == '\'') { state = State.Single; inToken = true; }
                    else if (c == '"') { state = State.Double; inToken = true; }
                    else if (c == '\\' && i + 1 < input.Length)
                    {
                        current.Append(input[++i]);
                        inToken = true;
                    }
                    else { current.Append(c); inToken = true; }
                    break;
                case State.Single:
                    if (c == '\'') state = State.Normal;
                    else current.Append(c);
                    break;
                case State.Double:
                    if (c == '"') state = State.Normal;
                    else if (c == '\\' && i + 1 < input.Length && input[i + 1] is '"' or '\\')
                        current.Append(input[++i]);
                    else current.Append(c);
                    break;
            }
        }

        if (state != State.Normal)
        {
            tokens = [];
            error = "sh: unterminated quote";
            return false;
        }

        if (inToken) result.Add(current.ToString());
        tokens = result;
        error = null;
        return true;
    }
}
