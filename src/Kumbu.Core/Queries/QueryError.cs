// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Queries;

/// <summary>A query that no note can make right: a function, method or formula that doesn't exist, formulas that use
/// each other in a circle, a regular expression that doesn't compile. <see cref="Code"/> is the same for every engine
/// (the conformance cases compare it); the message is for people.</summary>
public sealed class QueryError(string code, string message, int position) : Exception(message)
{
    public string Code { get; } = code;

    public int Position { get; } = position;
}
