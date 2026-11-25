// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Queries;

/// <summary>A query that can't run: an expression that doesn't parse, nests too deeply or builds too much for a note, a
/// base that can't be read, a call that doesn't fit the query. <see cref="Code"/> is the same for every engine
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

/// <summary>What a query names that doesn't exist (a function, method or formula) or formulas that use each other in a
/// circle: the query still runs, with an error value in the cells it touches, and <c>query check</c> tells it.
/// <see cref="Position"/> counts in <see cref="Expression"/>.</summary>
public sealed record QueryWarning(string Code, string Message, string Expression, int Position);
