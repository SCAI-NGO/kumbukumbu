// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;

namespace Kumbu.Core.Sh.Commands;

public sealed class Img(INoteReader notes, IAttachmentPixels pixels) : ICommand
{
    private static readonly Option[] Spec = [new(null, "size", TakesValue: true), new(null, "crop", TakesValue: true)];

    public string Name => "img";

    public string Usage => "img <path> [--size S] [--crop x,y,w,h]";

    public string Summary => "show an image attachment to the model";

    public string Help => $"""
        img <path> [--size small|medium|large] [--crop x,y,w,h]
          Show an image attachment (png, jpg, gif, webp): the result is a line of text naming the file,
          its version, the original size and what was delivered, followed by the image itself. The image
          is scaled down to the size step, never enlarged, and sent as PNG, or as JPEG if the PNG would
          be larger than the file size aim.
          --size S          the step, medium if omitted:
        {SizeSteps.Table}
          --crop x,y,w,h    look at a part at full resolution instead: x and y from the top left, all four in
                            pixels of the original image (as 'cat' reports its size, rotation by the camera
                            applied). The part is then scaled to the step like a whole image.
          A very tall or wide image is reduced a lot to fit a step; the text then suggests a crop.
          One call delivers one image, and img stands alone, not in a pipeline.
          A client may drop images; if none follows the text, say so to the user rather than guess.
          For the pages of a PDF see 'help pdf'; 'cat <path>' says what an attachment is.

          img /Work/Screens/shot.png
          img /Work/Screens/shot.png --size large
          img /Work/Screens/shot.png --crop 0,1200,1440,800
        """;

    public bool ReadsInput => false;

    public bool ProducesImages => true;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (!Options.TryParse(Name, args, Spec, out var options, out var optionError)) return optionError;
        if (options.Operands.Count == 0) return Invalid("img: missing operand");
        if (options.Operands.Count > 1) return Invalid($"img: too many arguments{CommandArgs.QuoteHint}");

        var step = SizeStep.Medium;
        if (options.Value("size") is { } size && !SizeSteps.TryParse(size, out step))
        {
            return Refused(ImgRefusal.BadSize, $"img: --size is small, medium or large, not '{size}' (see 'help img')");
        }

        Crop? crop = null;
        if (options.Value("crop") is { } cropText)
        {
            if (!Crop.TryParse(cropText, out var parsed, out var cropError)) return Refused(ImgRefusal.BadCrop, $"img: {cropError}");
            crop = parsed;
        }

        if (!CommandArgs.TryParsePath(Name, options.Operands[0], out var path, out var pathError)) return pathError! with { Kind = ShellErrorKind.Invalid };
        if (await StoreAttachments.GetAsync(notes, path, ct) is not { } attachment) return await NotAnAttachmentAsync(path, ct);

        var state = attachment.Current;
        if (AttachmentText.IsPdf(state))
        {
            return Refused(ImgRefusal.Pdf, $"img: {path.Value} is a PDF; look at its pages with pdf {path.Value} <pages> --images (see 'help pdf')");
        }

        // The pixels would answer the same, but need a round trip to find out.
        if (crop is not null && state is { Width: { } width, Height: { } height }
            && (crop.X + crop.Width > width + 1e-6 || crop.Y + crop.Height > height + 1e-6))
        {
            return Refused(ImgRefusal.BadCrop, $"img: the crop {crop} lies outside the image ({width}×{height} px)");
        }

        Picture picture;
        try
        {
            picture = await pixels.ImageAsync(path, state, step, crop, ct);
        }
        catch (PixelsUnavailableException e)
        {
            pixels.Count(new ImgRefused(e.Problem switch
            {
                PixelsProblem.TooLarge => ImgRefusal.TooLarge,
                PixelsProblem.BadCrop => ImgRefusal.BadCrop,
                _ => ImgRefusal.Unavailable,
            }));
            return ShellResult.Error($"img: {e.Message}");
        }

        pixels.Count(new ImgCalled(step, crop is not null));

        var head = $"{path.Value} (version {state.Version})";
        var original = $"image ({AttachmentText.FormatName(state)}), original {picture.OriginalWidth:0}×{picture.OriginalHeight:0} px";
        var text = PictureText.For(head, original, step, crop, picture);
        return ShellResult.Ok(text) with { Parts = [new TextPart(text), new ImagePart(picture.Data, picture.MediaType)] };
    }

    private static ShellResult Invalid(string message) => ShellResult.Error(message) with { Kind = ShellErrorKind.Invalid };

    private ShellResult Refused(ImgRefusal reason, string message)
    {
        pixels.Count(new ImgRefused(reason));
        return Invalid(message);
    }

    /// <summary>What <c>cat</c> would say about a path that is no attachment, in the words of <c>img</c>.</summary>
    private async Task<ShellResult> NotAnAttachmentAsync(VaultPath path, CancellationToken ct)
    {
        var read = await CommandArgs.ReadNoteAsync(Name, notes, path, ct);
        if (!read.IsError) return Refused(ImgRefusal.Note, $"img: {path.Value} is a note; cat reads notes");
        var missing = read.Output.Contains(": No such file or directory", StringComparison.Ordinal);
        if (missing) pixels.Count(new ImgRefused(ImgRefusal.NotFound));
        return read with { Kind = missing ? ShellErrorKind.NotFound : ShellErrorKind.Invalid };
    }
}
