// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Queries;

/// <summary>A parsed expression of the Bases language; <see cref="Position"/> is where it starts in the text, for messages.</summary>
public abstract record Expr(int Position);

public sealed record LiteralExpr(Value Value, int Position) : Expr(Position);

/// <summary>A bare name: a property (<c>status</c>), or one of <c>file</c>, <c>note</c>, <c>formula</c>, <c>this</c>, or a function's.</summary>
public sealed record NameExpr(string Name, int Position) : Expr(Position);

public sealed record MemberExpr(Expr Target, string Name, int Position) : Expr(Position);

public sealed record IndexExpr(Expr Target, Expr Index, int Position) : Expr(Position);

/// <summary>A call: of a function (<c>now()</c>, callee a <see cref="NameExpr"/>) or a method (<c>file.hasTag("a")</c>, callee a <see cref="MemberExpr"/>).</summary>
public sealed record CallExpr(Expr Callee, IReadOnlyList<Expr> Arguments, int Position) : Expr(Position);

public sealed record UnaryExpr(Operator Operator, Expr Operand, int Position) : Expr(Position);

public sealed record BinaryExpr(Operator Operator, Expr Left, Expr Right, int Position) : Expr(Position);

public sealed record ListExpr(IReadOnlyList<Expr> Items, int Position) : Expr(Position);

public sealed record ObjectExpr(IReadOnlyList<KeyValuePair<string, Expr>> Entries, int Position) : Expr(Position);

public sealed record RegexExpr(string Pattern, string Flags, int Position) : Expr(Position);

public enum Operator
{
    Or,
    And,
    Not,
    Equal,
    NotEqual,
    Less,
    LessOrEqual,
    Greater,
    GreaterOrEqual,
    Add,
    Subtract,
    Multiply,
    Divide,
    Remainder,
    Negate,
}

/// <summary>An expression that doesn't parse: what is wrong, and where (<see cref="Position"/>, from 0).</summary>
public sealed class QuerySyntaxError(string message, int position) : Exception(message)
{
    public int Position { get; } = position;
}
