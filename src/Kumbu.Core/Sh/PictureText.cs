// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Sh;

/// <summary>The text block that goes with each image <c>img</c> and <c>pdf</c> deliver.</summary>
internal static class PictureText
{
    /// <summary>A picture counts as very tall or wide beyond this ratio of its long to its short side.</summary>
    private const double ExtremeRatio = 4;

    /// <summary>
    /// "/Screens/shot.png (version …): image (PNG), original 1440×3120 px; medium: 724×1568 px PNG, 312 KB", then a hint
    /// for a very tall or wide picture, and always the line for a client that dropped the image.
    /// </summary>
    /// <param name="head">What was looked at: the path and version, for a page also "page 4 of 12 (mixed)".</param>
    /// <param name="original">What it was before scaling: "image (PNG), original 1440×3120 px" or "page 595×842 pt".</param>
    public static string For(string head, string original, SizeStep step, Crop? crop, Picture picture)
    {
        var name = SizeSteps.Name(step);
        var size = AttachmentText.Size(picture.Data.Length);
        var target = SizeSteps.Of(step).Bytes;
        var over = picture.Data.Length > target ? $" (over the {AttachmentText.Size(target)} target for {name})" : "";
        var cropped = crop is null ? "" : $"; crop {crop}";
        var lines = new List<string> { $"{head}: {original}{cropped}; {name}: {picture.Width}×{picture.Height} px {Format(picture.MediaType)}, {size}{over}" };
        if (Math.Max(picture.Width, picture.Height) > ExtremeRatio * Math.Min(picture.Width, picture.Height))
        {
            lines.Add("Very tall/wide: details may be too small to read; look at a part with --crop x,y,w,h.");
        }

        lines.Add("If no image follows, your client dropped it: tell the user so instead of guessing what it shows.");
        return string.Join('\n', lines);
    }

    private static string Format(string mediaType) => mediaType == "image/jpeg" ? "JPEG" : "PNG";
}
