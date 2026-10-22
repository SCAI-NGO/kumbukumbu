// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kumbu.Core.Sh;

/// <summary>A word of the command line, or the pipe operator. A quoted "|" is a word.</summary>
public readonly record struct Token(string Text, bool IsPipe)
{
    public static Token Pipe { get; } = new("|", true);

    public static Token Word(string text) => new(text, false);
}

/// <summary>Splits a command line into tokens using a small subset of POSIX quoting rules.</summary>
public static class Tokenizer
{
    private enum State { Normal, Single, Double }

    public static bool TryTokenize(
        string input, out IReadOnlyList<Token> tokens, [NotNullWhen(false)] out string? error)
    {
        // Trailing newlines are what a model's "ls /Work\n" usually carries; strip them before tokenizing.
        input = input.Trim();
        var result = new List<Token>();
        var current = new StringBuilder();
        var inToken = false;
        var state = State.Normal;

        void EndWord()
        {
            if (!inToken) return;
            result.Add(Token.Word(current.ToString()));
            current.Clear();
            inToken = false;
        }

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

                    if (UnsupportedOperator(input, i) is { } op)
                    {
                        tokens = [];
                        error = $"sh: '{op}' is not supported; the only operator is '|' (quote it to use it literally)";
                        return false;
                    }

                    if (c is ' ' or '\t') EndWord();
                    else if (c == '|') { EndWord(); result.Add(Token.Pipe); }
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

        EndWord();
        tokens = result;
        error = null;
        return true;
    }

    /// <summary>Returns the shell syntax starting at <paramref name="i"/> that a real shell would act on
    /// and we do not support, or null. Only called outside quotes.</summary>
    private static string? UnsupportedOperator(string input, int i)
    {
        var c = input[i];
        var next = i + 1 < input.Length ? input[i + 1] : '\0';
        return c switch
        {
            '|' when next == '|' => "||",
            '&' when next == '&' => "&&",
            '>' or '<' when next == c => new string(c, 2),
            '&' or ';' or '>' or '<' or '`' or '(' or ')' => c.ToString(),
            '$' when next is '(' or '{' => "$" + next,
            _ => null,
        };
    }
}
