// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;

namespace Kumbu.Core.Sh;

/// <summary>The text block that goes with each image <c>img</c> and <c>pdf</c> deliver.</summary>
internal static class PictureText
{
    /// <summary>A page counts as very tall or wide beyond this ratio of its long to its short side.</summary>
    private const double ExtremeRatio = 4;

    /// <summary>
    /// An image shown below this share of its width gets a warning. A phone screenshot at medium comes to about half,
    /// and models misread its digits there without noticing, as the client tests of 2026-11-01 showed.
    /// </summary>
    private const double ReadableShare = 0.75;

    /// <summary>
    /// "/Paper.pdf (version …), page 4 of 12 (mixed): page 595×842 pt; medium: 1109×1568 px PNG, 312 KB", then a hint
    /// for a very tall or wide page, and always the line for a client that dropped the image.
    /// </summary>
    /// <param name="head">What was looked at: the path and version, for a page also "page 4 of 12 (mixed)".</param>
    /// <param name="original">What it was before scaling: "page 595×842 pt".</param>
    public static string For(string head, string original, SizeStep step, Crop? crop, Picture picture)
    {
        // A crop is already the closer look the hint would ask for.
        var extreme = crop is null && Math.Max(picture.Width, picture.Height) > ExtremeRatio * Math.Min(picture.Width, picture.Height);
        return Block(head, original, step, crop, picture, extreme ? ["Very tall/wide: details may be too small to read; look at a part with --crop x,y,w,h."] : []);
    }

    /// <summary>
    /// "/Screens/shot.png (version …): image (PNG), original 1440×3120 px; medium: 724×1568 px PNG, 312 KB", then what
    /// the picture doesn't show of the image, and always the line for a client that dropped the image.
    /// </summary>
    public static string ForImage(string head, string original, SizeStep step, Crop? crop, Picture picture, bool gif)
    {
        var notes = new List<string>();
        var share = picture.Width / (crop?.Width ?? picture.OriginalWidth);
        if (share < ReadableShare)
        {
            notes.Add($"Reduced to {share * 100:0} %: small text and digits may be misread; look at {(crop is null ? "a part" : "a smaller part")} with --crop x,y,w,h before quoting them.");
        }

        if (gif) notes.Add("A GIF is shown as a still: if it is animated, only its first frame.");
        if (picture.Transparent) notes.Add("Transparent areas are shown on white, so a white shape on them can't be seen.");
        return Block(head, original, step, crop, picture, notes);
    }

    private static string Block(string head, string original, SizeStep step, Crop? crop, Picture picture, IEnumerable<string> notes)
    {
        var name = SizeSteps.Name(step);
        var size = AttachmentText.Size(picture.Data.Length);
        var target = SizeSteps.Of(step).Bytes;
        var over = picture.Data.Length > target ? $" (over the {AttachmentText.Size(target)} target for {name})" : "";
        var cropped = crop is null ? "" : $"; crop {crop}";
        var lines = new List<string> { $"{head}: {original}{cropped}; {name}: {picture.Width}×{picture.Height} px {Format(picture.MediaType)}, {size}{over}" };
        lines.AddRange(notes);
        lines.Add("If no image follows, your client dropped it: tell the user so instead of guessing what it shows.");
        return string.Join('\n', lines);
    }

    private static string Format(string mediaType) => mediaType == "image/jpeg" ? "JPEG" : "PNG";
}
