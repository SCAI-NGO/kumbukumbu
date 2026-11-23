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

public sealed record DateValue(DateOnly Date) : Value;

/// <summary>A date with a time and no zone, as Obsidian keeps a datetime property.</summary>
public sealed record DateTimeValue(DateTime DateTime) : Value;

public sealed record ListValue(IReadOnlyList<Value> Items) : Value;

public sealed record ObjectValue(IReadOnlyDictionary<string, Value> Values) : Value;
