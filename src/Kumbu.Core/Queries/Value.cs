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

/// <summary>What an expression gives where it can't give a value for the note: an unknown function or method, a
/// calculation between types that don't fit, and so on; a value in the cell, with Obsidian's message, never an error of
/// the query (bases spec 2026-11-25 decision 9). It taints whatever uses it.</summary>
public sealed record ErrorValue(string Message) : Value;

/// <summary>A span of time as Moment keeps one, which Obsidian's durations are: calendar months, days and milliseconds,
/// added to a date in that order from the smallest (bases spec 2026-11-25 decision 13). Until probe 3 it shows and
/// compares as <see cref="AsMilliseconds"/>.</summary>
public sealed record DurationValue(double Months, double Days, double Milliseconds) : Value
{
    /// <summary>The whole span in milliseconds, a month counted as Moment's average one (146,097 days in 4,800 months).</summary>
    public double AsMilliseconds => Milliseconds + (Days + Months * 146097 / 4800) * 86_400_000;
}
