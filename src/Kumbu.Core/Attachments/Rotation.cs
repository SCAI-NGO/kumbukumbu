// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Attachments;

/// <summary>The turn a picture can be asked for (<c>--rotate</c>): a quarter, a half or three quarters, either way round.</summary>
public static class Rotation
{
    /// <summary>True for exactly <c>90</c>, <c>180</c> or <c>270</c>, written plainly, or the same with a minus for counter-clockwise: no plus,
    /// no leading zero, no space. <paramref name="degrees"/> is always the clockwise turn.</summary>
    public static bool TryParse(string text, out int degrees)
    {
        degrees = text switch { "90" or "-270" => 90, "180" or "-180" => 180, "270" or "-90" => 270, _ => 0 };
        return degrees != 0;
    }

    /// <summary>A quarter turn exchanges width and height.</summary>
    public static bool SwapsSides(int degrees) => degrees is 90 or 270;
}
