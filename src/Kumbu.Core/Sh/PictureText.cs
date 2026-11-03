// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
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
    /// <param name="rotate">The turn by <c>--rotate</c>, 0 for none; <paramref name="picture"/>'s original size is the page's before it.</param>
    public static string For(string head, string original, SizeStep step, Crop? crop, Picture picture, int rotate = 0)
    {
        // A crop is already the closer look the hint would ask for.
        var extreme = crop is null && Math.Max(picture.Width, picture.Height) > ExtremeRatio * Math.Min(picture.Width, picture.Height);
        var notes = new List<string>();
        if (extreme) notes.Add("Very tall/wide: details may be too small to read; look at a part with --crop x,y,w,h.");
        if (rotate != 0) notes.Add($"With --rotate {rotate}, --crop counts from the top left of this turned page, in its points.");
        return Block(head, original + Turned(rotate, picture.OriginalWidth, picture.OriginalHeight, "pt"), step, crop, rotate == 0 ? "" : " of the turned page", picture, notes);
    }

    /// <summary>
    /// "/Screens/shot.png (version …): image (PNG), original 1440×3120 px; medium: 724×1568 px PNG, 312 KB", then what
    /// the picture doesn't show of the image, and always the line for a client that dropped the image.
    /// </summary>
    public static string ForImage(string head, string original, SizeStep step, Crop? crop, Picture picture, bool gif, int rotate = 0, int? frame = null)
    {
        var notes = new List<string>();
        var share = picture.Width / (crop?.Width ?? (Rotation.SwapsSides(rotate) ? picture.OriginalHeight : picture.OriginalWidth));
        if (share < ReadableShare)
        {
            notes.Add($"Reduced to {share * 100:0} %: small text and digits may be misread; look at {(crop is null ? "a part" : "a smaller part")} with --crop x,y,w,h before quoting them.");
        }

        if (gif && frame is null)
        {
            if (picture.Frames is null) notes.Add("A GIF is shown as a still: if it is animated, only its first frame.");
            else if (picture.Frames > 1) notes.Add($"An animated GIF of {picture.Frames} frames, shown as a still: frame 1. --frame N shows another.");
        }

        if (picture.Transparent) notes.Add("Transparent areas are shown on white, so a white shape on them can't be seen.");
        if (rotate != 0) notes.Add($"With --rotate {rotate}, --crop counts from the top left of this turned image, in its pixels.");
        return Block(head, original + Turned(rotate, picture.OriginalWidth, picture.OriginalHeight, "px"), step, crop, rotate == 0 ? "" : " of the turned image", picture, notes);
    }

    /// <summary>", turned 90° clockwise to 842×595 pt" for a page of 595×842 pt; nothing without a turn.</summary>
    internal static string Turned(int rotate, double width, double height, string unit)
    {
        if (rotate == 0) return "";
        var (w, h) = Rotation.SwapsSides(rotate) ? (height, width) : (width, height);
        return $", turned {rotate}° clockwise to {Whole(w)}×{Whole(h)} {unit}";
    }

    private static string Whole(double value) => Math.Round(value, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture);

    private static string Block(string head, string original, SizeStep step, Crop? crop, string ofCrop, Picture picture, IEnumerable<string> notes)
    {
        var name = SizeSteps.Name(step);
        var size = AttachmentText.Size(picture.Data.Length);
        var target = SizeSteps.Of(step).Bytes;
        var over = picture.Data.Length > target ? $" (over the {AttachmentText.Size(target)} target for {name})" : "";
        var cropped = crop is null ? "" : $"; crop {crop}{ofCrop}";
        var lines = new List<string> { $"{head}: {original}{cropped}; {name}: {picture.Width}×{picture.Height} px {Format(picture.MediaType)}, {size}{over}" };
        lines.AddRange(notes);
        lines.Add("If no image follows, your client dropped it: tell the user so instead of guessing what it shows.");
        return string.Join('\n', lines);
    }

    private static string Format(string mediaType) => mediaType == "image/jpeg" ? "JPEG" : "PNG";
}
