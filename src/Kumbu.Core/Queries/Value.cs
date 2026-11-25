// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Queries;

/// <summary>A typed value as a query sees it: a property, a parameter or what an expression gives.</summary>
public abstract record Value;

public sealed record NullValue : Value
{
    public static readonly NullValue Instance = new();
}

public sealed record TextValue(string Text) : Value;

public sealed record NumberValue(double Number) : Value;

public sealed record BooleanValue(bool Boolean) : Value;

/// <summary>A date, with a time or without one, and no zone, as Obsidian keeps it: one type, as in Obsidian. A date
/// without a time is its midnight, so it equals only midnight.</summary>
public sealed record DateValue(DateTime Moment, bool HasTime) : Value
{
    public DateValue(DateOnly date) : this(date.ToDateTime(TimeOnly.MinValue), false)
    {
    }

    public DateOnly Date => DateOnly.FromDateTime(Moment);
}

public sealed record ListValue(IReadOnlyList<Value> Items) : Value;

public sealed record ObjectValue(IReadOnlyDictionary<string, Value> Values) : Value;
