// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Attachments;

public enum SizeStep { Small, Medium, Large }

/// <summary>What an image delivered at a step must fit: long edge and pixel count in pixels, a byte target for the encoded file.</summary>
public sealed record StepLimits(int LongEdge, int Pixels, int Bytes);

/// <summary>The size steps of <c>img</c> and <c>pdf</c>, in one place; the sidecar gets the numbers with each request.</summary>
public static class SizeSteps
{
    private const int KB = 1024;

    public static StepLimits Of(SizeStep step) => step switch
    {
        SizeStep.Small => new(768, 400_000, 150 * KB),
        SizeStep.Medium => new(1568, 1_200_000, 400 * KB),
        SizeStep.Large => new(2576, 3_700_000, 1024 * KB),
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    public static string Name(SizeStep step) => step switch
    {
        SizeStep.Small => "small",
        SizeStep.Medium => "medium",
        SizeStep.Large => "large",
        _ => throw new ArgumentOutOfRangeException(nameof(step)),
    };

    public static bool TryParse(string text, out SizeStep step)
    {
        foreach (var candidate in Enum.GetValues<SizeStep>())
        {
            if (string.Equals(text, Name(candidate), StringComparison.OrdinalIgnoreCase))
            {
                step = candidate;
                return true;
            }
        }

        step = default;
        return false;
    }

    // Approximate visual tokens for the table; the budget and the byte target decide, not this.
    private static int Tokens(SizeStep step) => step switch
    {
        SizeStep.Small => 500,
        SizeStep.Medium => 1_550,
        _ => 4_750,
    };

    private static string Bytes(int bytes) => bytes >= 1024 * KB ? $"{bytes / (1024 * KB)} MB" : $"{bytes / KB} KB";

    /// <summary>The table <c>help img</c> and <c>help pdf</c> print, for a monospace reader.</summary>
    public static string Table { get; } = Build();

    private static string Build()
    {
        var rows = new List<string[]> { new[] { "step", "long edge", "pixels", "~Claude tokens", "file size aim" } };
        foreach (var step in Enum.GetValues<SizeStep>())
        {
            var limits = Of(step);
            rows.Add([
                Name(step) + (step == SizeStep.Medium ? " (default)" : ""),
                $"{limits.LongEdge} px",
                $"{(limits.Pixels / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture)} MP",
                $"~{Tokens(step).ToString("N0", CultureInfo.InvariantCulture)}",
                Bytes(limits.Bytes),
            ]);
        }

        var widths = Enumerable.Range(0, 5).Select(i => rows.Max(r => r[i].Length)).ToArray();
        return string.Join('\n', rows.Select(r => "  " + string.Join("  ", r.Select((c, i) => c.PadRight(widths[i]))).TrimEnd()));
    }
}
