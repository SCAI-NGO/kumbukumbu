// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Attachments;

/// <summary>A rectangle from the top left: pixels of the original for an image, points of the displayed page for a PDF.</summary>
public sealed record Crop(double X, double Y, double Width, double Height)
{
    /// <summary>Far beyond any image or page: what lies within it is written without an exponent.</summary>
    public const double MaxValue = 1_000_000;

    private const string Expected = "a crop is four numbers x,y,w,h from the top left (x and y at least 0, w and h above 0, none above 1,000,000), e.g. 0,120,600,400";

    public static bool TryParse(string text, out Crop crop, out string error)
    {
        crop = new Crop(0, 0, 0, 0);
        var parts = text.Split(',');
        var numbers = new double[4];
        if (parts.Length != 4
            || !Enumerable.Range(0, 4).All(i => double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) && double.IsFinite(numbers[i])))
        {
            error = $"'{text}' is not a crop: {Expected}";
            return false;
        }

        // Kept to the four places ToString writes, so what is checked is what is sent; + 0.0 turns -0 into 0.
        for (var i = 0; i < 4; i++) numbers[i] = Math.Round(numbers[i], 4, MidpointRounding.AwayFromZero) + 0.0;
        if (numbers[0] < 0 || numbers[1] < 0 || numbers[2] <= 0 || numbers[3] <= 0 || numbers.Any(n => n > MaxValue))
        {
            error = $"'{text}' is not a crop: {Expected}";
            return false;
        }

        crop = new Crop(numbers[0], numbers[1], numbers[2], numbers[3]);
        error = "";
        return true;
    }

    /// <summary>"x,y,w,h" with a decimal point and at most four places, never an exponent, as <c>--crop</c> and the sidecar take it.</summary>
    public override string ToString() => string.Join(',', new[] { X, Y, Width, Height }.Select(n => n.ToString("0.####", CultureInfo.InvariantCulture)));
}
