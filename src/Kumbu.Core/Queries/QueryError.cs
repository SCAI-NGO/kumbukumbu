// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Queries;

/// <summary>A query that no note can make right: a function, method or formula that doesn't exist, formulas that use
/// each other in a circle, a regular expression that doesn't compile. <see cref="Code"/> is the same for every engine
/// (the conformance cases compare it); the message is for people.</summary>
public sealed class QueryError(string code, string message, int position) : Exception(message)
{
    public string Code { get; } = code;

    /// <summary>Where <see cref="Position"/> counts in: the expression (a filter, a column, a formula) that fails, once known.</summary>
    public string? Expression { get; init; }

    public int Position { get; } = position;

    /// <summary>The error <paramref name="compile"/> throws, told in which expression it is; one already told keeps its own.</summary>
    internal static T In<T>(string expression, Func<T> compile)
    {
        try
        {
            return compile();
        }
        catch (QuerySyntaxError e)
        {
            throw new QueryError("syntax", e.Message, e.Position) { Expression = expression };
        }
        catch (QueryError e) when (e.Expression is null)
        {
            throw new QueryError(e.Code, e.Message, e.Position) { Expression = expression };
        }
    }
}
