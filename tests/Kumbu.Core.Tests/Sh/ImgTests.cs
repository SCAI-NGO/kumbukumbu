// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Tests.Fakes;
using Microsoft.Extensions.Time.Testing;

namespace Kumbu.Core.Tests.Sh;

/// <summary><c>img</c>: an image attachment shown to the model, at a size step, whole or cropped.</summary>
public class ImgTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Delivered = [9, 8, 7, 6];

    private sealed class FakePixels : IAttachmentPixels
    {
        public List<(VaultPath Path, SizeStep Step, Crop? Crop)> Requests { get; } = [];

        public List<int> Rotations { get; } = [];

        public List<int?> Frames { get; } = [];

        public List<AttachmentUse> Used { get; } = [];

        /// <summary>The picture returned; null scales the original down to the step's long edge.</summary>
        public Func<SizeStep, Crop?, AttachmentState, Picture>? Answer { get; set; }

        public Exception? Fail { get; set; }

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, int rotate, int? frame, CancellationToken ct)
        {
            Requests.Add((path, step, crop));
            Rotations.Add(rotate);
            Frames.Add(frame);
            if (Fail is not null) throw Fail;
            return Task.FromResult(Answer?.Invoke(step, crop, state) ?? new Picture(Delivered, "image/png", 724, 1568, state.Width!.Value, state.Height!.Value));
        }

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, int rotate, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<PageTextBoxes>> TextBoxesAsync(VaultPath path, AttachmentState state, IReadOnlyList<int> pages, CancellationToken ct) => throw new NotSupportedException();

        public void Count(AttachmentUse use) => Used.Add(use);
    }

    private readonly InMemoryNoteStore _store = new(("/Work/plan.md", "a note\n"));
    private readonly FakeTimeProvider _time = new(new DateTime(2026, 10, 28, 11, 0, 0, DateTimeKind.Utc));
    private readonly FakePixels _pixels = new();
    private readonly AttachmentWriter _files;
    private readonly Shell _sh;

    public ImgTests()
    {
        var caller = new FixedCaller(Writers.Dev);
        _files = new AttachmentWriter(_store, _store, _time, caller);
        _sh = new Shell(_store, new NoteWriter(_store, _time, caller), new NoteMover(_store, _time, caller), pixels: _pixels);
    }

    private static CancellationToken Ct => CancellationToken.None;

    private static VaultPath P(string path) => VaultPath.TryParse(path, out var parsed) ? parsed : throw new ArgumentException(path);

    private Task<ShellResult> Run(string line) => _sh.RunAsync(line, Ct);

    private async Task<string> Upload(string path, byte[] bytes, (int, int)? size)
    {
        var result = await _files.WriteAsync(path, bytes, size, null, Ct);
        Assert.False(result.Result.IsError, result.Result.Output);
        return result.Attachment!.Version;
    }

    private static byte[] Png(int size) => [.. PngSignature, .. new byte[size - PngSignature.Length]];

    private static byte[] Jpeg() => [0xFF, 0xD8, 0xFF, 0xE0, .. new byte[996]];

    private static string Text(ShellResult result) => Assert.IsType<TextPart>(result.Parts![0]).Text;

    private const string Dropped = "If no image follows, your client dropped it: tell the user so instead of guessing what it shows.";

    private static string Reduced(int percent, string part = "a part") =>
        $"Reduced to {percent} %: small text and digits may be misread; look at {part} with --crop x,y,w,h before quoting them.";

    [Fact]
    public async Task The_image_follows_a_text_block_that_says_what_it_is()
    {
        var version = await Upload("/Work/Screens/shot.png", Png(500_000), (1440, 3120));
        _pixels.Answer = (_, _, s) => new Picture(new byte[312 * 1024], "image/png", 724, 1568, 1440, 3120);

        var result = await Run("img /Work/Screens/shot.png");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Equal($"/Work/Screens/shot.png (version {version}): image (PNG), original 1440×3120 px; medium: 724×1568 px PNG, 312 KB\n{Reduced(50)}\n{Dropped}", Text(result));
        var image = Assert.IsType<ImagePart>(result.Parts[1]);
        Assert.Equal("image/png", image.MediaType);
        Assert.Equal(312 * 1024, image.Data.Length);
        Assert.Equal(Text(result), result.Output);
        Assert.Null(result.Version);
    }

    [Theory]
    [InlineData("", SizeStep.Medium, "medium")]
    [InlineData("--size small", SizeStep.Small, "small")]
    [InlineData("--size=medium", SizeStep.Medium, "medium")]
    [InlineData("--size large", SizeStep.Large, "large")]
    public async Task Each_step_reaches_the_pixels_and_medium_is_the_default(string option, SizeStep expected, string name)
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));

        var result = await Run($"img /Work/Screens/shot.png {option}");

        Assert.False(result.IsError, result.Output);
        Assert.Equal((P("/Work/Screens/shot.png"), expected, (Crop?)null), Assert.Single(_pixels.Requests));
        Assert.Contains($"; {name}: ", Text(result));
    }

    [Fact]
    public async Task A_crop_is_parsed_passed_on_and_named()
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));
        _pixels.Answer = (_, c, _) => new Picture(new byte[100_000], "image/png", 1440, 800, 1440, 3120);

        var result = await Run("img /Work/Screens/shot.png --crop 0,1200,1440,800 --size large");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(new Crop(0, 1200, 1440, 800), Assert.Single(_pixels.Requests).Crop);
        Assert.Contains("original 1440×3120 px; crop 0,1200,1440,800; large: 1440×800 px PNG, 98 KB", Text(result));
    }

    [Fact]
    public async Task A_picture_over_the_byte_target_says_so()
    {
        await Upload("/Work/Screens/photo.png", Png(1000), (4000, 3000));
        _pixels.Answer = (_, _, _) => new Picture(new byte[(int)(1.2 * 1024 * 1024)], "image/jpeg", 1568, 1176, 4000, 3000);

        var text = Text(await Run("img /Work/Screens/photo.png"));

        Assert.Contains("medium: 1568×1176 px JPEG, 1.2 MB (over the 400 KB target for medium)", text);
    }

    [Theory]
    [InlineData(1440, 6000, 376, 1568, 26)]
    [InlineData(6000, 1440, 1568, 376, 26)]
    [InlineData(1440, 3120, 723, 1568, 50)]
    [InlineData(1000, 1000, 749, 749, 75)]
    [InlineData(1000, 1000, 750, 750, null)]
    [InlineData(800, 600, 800, 600, null)]
    public async Task An_image_reduced_a_lot_warns_about_small_text(int ow, int oh, int w, int h, int? percent)
    {
        await Upload("/Work/Screens/long.png", Png(1000), (ow, oh));
        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", w, h, ow, oh);

        var lines = Text(await Run("img /Work/Screens/long.png")).Split('\n');

        Assert.Equal(percent is null ? 2 : 3, lines.Length);
        if (percent is { } shown) Assert.Equal(Reduced(shown), lines[1]);
        Assert.Equal(Dropped, lines[^1]);
    }

    [Fact]
    public async Task A_narrow_crop_at_full_size_gets_no_warning_but_a_reduced_one_does()
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));

        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", 1000, 70, 1440, 3120);
        var full = Text(await Run("img /Work/Screens/shot.png --crop 0,2060,1000,70"));
        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", 500, 500, 1440, 3120);
        var reduced = Text(await Run("img /Work/Screens/shot.png --crop 0,0,1000,1000"));

        Assert.Equal(2, full.Split('\n').Length);
        Assert.Equal(Reduced(50, "a smaller part"), reduced.Split('\n')[1]);
    }

    [Fact]
    public async Task A_gif_says_it_is_shown_as_a_still_and_another_image_does_not()
    {
        byte[] gif = [.. "GIF89a"u8.ToArray(), .. new byte[100]];
        await Upload("/Work/Screens/moving.gif", gif, (600, 400));
        await Upload("/Work/Screens/still.png", Png(1000), (600, 400));
        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", 600, 400, 600, 400);

        Assert.Contains("A GIF is shown as a still: if it is animated, only its first frame.", Text(await Run("img /Work/Screens/moving.gif")));
        Assert.DoesNotContain("GIF", Text(await Run("img /Work/Screens/still.png")));
    }

    [Fact]
    public async Task A_transparent_image_says_it_is_shown_on_white()
    {
        await Upload("/Work/Screens/logo.png", Png(1000), (600, 400));

        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", 600, 400, 600, 400, Transparent: true);
        Assert.Contains("Transparent areas are shown on white, so a white shape on them can't be seen.", Text(await Run("img /Work/Screens/logo.png")));

        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", 600, 400, 600, 400);
        Assert.DoesNotContain("Transparent", Text(await Run("img /Work/Screens/logo.png")));
    }

    [Fact]
    public async Task The_pointer_to_pdf_quotes_a_path_with_spaces()
    {
        await Upload("/Work/My Papers/a b.pdf", [.. "%PDF-1.7"u8.ToArray(), .. new byte[100]], null);

        var result = await Run("img \"/Work/My Papers/a b.pdf\"");

        Assert.Equal("img: /Work/My Papers/a b.pdf is a PDF; look at its pages with pdf \"/Work/My Papers/a b.pdf\" <pages> --images (see 'help pdf')", result.Output);
    }

    [Fact]
    public async Task A_note_a_pdf_and_a_missing_path_are_refused()
    {
        await Upload("/Work/a.pdf", [.. "%PDF-1.7"u8.ToArray(), .. new byte[100]], null);

        var note = await Run("img /Work/plan.md");
        var pdf = await Run("img /Work/a.pdf");
        var missing = await Run("img /Work/Screens/nothing.png");
        var directory = await Run("img /Work");

        Assert.Equal(ShellResult.Error("img: /Work/plan.md is a note; cat reads notes") with { Kind = ShellErrorKind.Invalid }, note);
        Assert.Equal(ShellResult.Error("img: /Work/a.pdf is a PDF; look at its pages with pdf /Work/a.pdf <pages> --images (see 'help pdf')") with { Kind = ShellErrorKind.Invalid }, pdf);
        Assert.Equal(ShellErrorKind.NotFound, missing.Kind);
        Assert.StartsWith("img: /Work/Screens/nothing.png: No such file or directory", missing.Output);
        Assert.Equal("img: /Work: Is a directory", directory.Output);
        Assert.Empty(_pixels.Requests);
    }

    [Theory]
    [InlineData("img /Work/Screens/shot.png --size huge", "img: --size is small, medium or large, not 'huge' (see 'help img')")]
    [InlineData("img /Work/Screens/shot.png --size", "img: option '--size' requires an argument")]
    [InlineData("img /Work/Screens/shot.png --crop 1,2,3", "img: '1,2,3' is not a crop")]
    [InlineData("img /Work/Screens/shot.png --crop 0,0,0,5", "img: '0,0,0,5' is not a crop")]
    [InlineData("img", "img: missing operand")]
    [InlineData("img /Work/a.png /Work/b.png", "img: too many arguments")]
    [InlineData("img /Work/Screens/shot.png --frobnicate", "img: unrecognized option '--frobnicate'")]
    [InlineData("img a.png", "img: a.png: paths must be absolute")]
    public async Task Wrong_use_is_refused_before_any_pixels_are_made(string line, string start)
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));

        var result = await Run(line);

        Assert.True(result.IsError);
        Assert.StartsWith(start, result.Output);
        Assert.Empty(_pixels.Requests);
    }

    [Fact]
    public async Task A_crop_outside_the_image_is_refused_without_asking_the_pixels()
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));

        var result = await Run("img /Work/Screens/shot.png --crop 10,10,5000,5");

        Assert.Equal(ShellResult.Error("img: the crop 10,10,5000,5 lies outside the image (1440×3120 px)") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Empty(_pixels.Requests);
        Assert.False((await Run("img /Work/Screens/shot.png --crop 0,0,1440,3120")).IsError);
    }

    [Fact]
    public async Task Pixels_that_cannot_be_made_are_the_error_with_their_sentence()
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));
        _pixels.Fail = new PixelsUnavailableException("image rendering is unavailable right now; try again later");

        var result = await Run("img /Work/Screens/shot.png");

        Assert.Equal(ShellResult.Error("img: image rendering is unavailable right now; try again later"), result);
    }

    [Fact]
    public async Task It_cannot_be_part_of_a_pipeline()
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));

        var result = await Run("img /Work/Screens/shot.png | head");

        Assert.Equal("sh: img shows images and can't be part of a pipeline; run it on its own", result.Output);
        Assert.Empty(_pixels.Requests);
    }

    [Fact]
    public async Task Help_gives_the_steps_the_crop_and_the_limits()
    {
        var help = (await Run("help img")).Output;

        Assert.StartsWith("img <path>", help);
        Assert.Contains(SizeSteps.Table, help);
        Assert.Contains("--crop x,y,w,h", help);
        Assert.Contains("original", help);
        Assert.Contains("One call delivers one image", help);
        Assert.DoesNotContain("At most 4 images", help);
        Assert.Contains("A client may drop images", help);
        Assert.Contains("'help pdf'", help);
        Assert.Contains("img /Work/Screens/shot.png --crop", help);
        Assert.Contains("img ", (await Run("help attachments")).Output);
    }

    private const string TurnedImage = "With --rotate 90, --crop counts from the top left of this turned image, in its pixels.";

    [Theory]
    [InlineData("--rotate=-90", 270)]
    [InlineData("--rotate -90", 270)]
    [InlineData("--rotate=-270", 90)]
    public async Task A_minus_turns_the_other_way(string option, int clockwise)
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));

        var result = await Run($"img /Work/Scans/sheet.jpg {option}");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(clockwise, Assert.Single(_pixels.Rotations));
    }

    [Fact]
    public async Task Rotate_goes_to_the_pixels()
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));

        var result = await Run("img /Work/Scans/sheet.jpg --rotate 270");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(270, Assert.Single(_pixels.Rotations));
        Assert.Contains(new PictureTurned(TurnCommand.Img), _pixels.Used);
    }

    [Fact]
    public async Task The_last_rotate_wins()
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));

        var result = await Run("img /Work/Scans/sheet.jpg --rotate 90 --rotate 180");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(180, Assert.Single(_pixels.Rotations));
    }

    [Theory]
    [InlineData("--rotate 90 --size large --crop 0,0,300,200")]
    [InlineData("--crop 0,0,300,200 --rotate 90 --size large")]
    [InlineData("--size large --crop 0,0,300,200 --rotate=90")]
    public async Task Rotate_goes_with_size_and_crop_in_any_order(string options)
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));

        var result = await Run($"img /Work/Scans/sheet.jpg {options}");

        Assert.False(result.IsError, result.Output);
        Assert.Equal((P("/Work/Scans/sheet.jpg"), SizeStep.Large, new Crop(0, 0, 300, 200)), Assert.Single(_pixels.Requests));
        Assert.Equal(90, Assert.Single(_pixels.Rotations));
    }

    [Theory]
    [InlineData("45")]
    [InlineData("0")]
    [InlineData("-45")]
    public async Task A_bad_rotate_is_refused_with_its_sentence(string angle)
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));

        var result = await Run($"img /Work/Scans/sheet.jpg --rotate={angle}");

        Assert.Equal(ShellResult.Error($"img: --rotate turns clockwise by 90, 180 or 270 degrees (or back with -90, -180 or -270), not '{angle}' (leave it out for no turn)") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(new ImgRefused(ImgRefusal.BadRotate), Assert.Single(_pixels.Used));
        Assert.Empty(_pixels.Requests);
    }

    [Fact]
    public async Task A_bad_rotate_is_refused_before_the_path_is_opened()
    {
        var result = await Run("img /Work/Scans/nothing.jpg --rotate 45");

        Assert.Equal("img: --rotate turns clockwise by 90, 180 or 270 degrees (or back with -90, -180 or -270), not '45' (leave it out for no turn)", result.Output);
        Assert.Equal(new ImgRefused(ImgRefusal.BadRotate), Assert.Single(_pixels.Used));
    }

    [Fact]
    public async Task The_text_block_names_the_turn()
    {
        var version = await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));
        _pixels.Answer = (_, _, _) => new Picture(Delivered, "image/png", 894, 1341, 3000, 2000);

        var lines = Text(await Run("img /Work/Scans/sheet.jpg --rotate 90")).Split('\n');

        Assert.Equal(
            [
                $"/Work/Scans/sheet.jpg (version {version}): image (JPEG), original 3000×2000 px, turned 90° clockwise to 2000×3000 px; medium: 894×1341 px PNG, {AttachmentText.Size(4)}",
                Reduced(45),
                TurnedImage,
                Dropped,
            ],
            lines);
    }

    [Fact]
    public async Task A_half_turn_keeps_the_sizes()
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));
        _pixels.Answer = (_, _, _) => new Picture(Delivered, "image/png", 1341, 894, 3000, 2000);

        var lines = Text(await Run("img /Work/Scans/sheet.jpg --rotate 180")).Split('\n');

        Assert.Contains("original 3000×2000 px, turned 180° clockwise to 3000×2000 px; medium: 1341×894 px PNG", lines[0]);
        Assert.Equal(Reduced(45), lines[1]);
        Assert.Equal("With --rotate 180, --crop counts from the top left of this turned image, in its pixels.", lines[2]);
    }

    [Fact]
    public async Task A_crop_says_it_is_of_the_turned_image()
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));
        _pixels.Answer = (_, _, _) => new Picture(Delivered, "image/png", 300, 200, 3000, 2000);

        var text = Text(await Run("img /Work/Scans/sheet.jpg --rotate 90 --crop 0,0,300,200"));

        Assert.Contains("turned 90° clockwise to 2000×3000 px; crop 0,0,300,200 of the turned image; medium: 300×200 px PNG", text);
        Assert.Contains(TurnedImage, text);
    }

    [Fact]
    public async Task A_crop_outside_the_turned_image_is_refused_without_a_round_trip()
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (2000, 3000));

        var result = await Run("img /Work/Scans/sheet.jpg --rotate 90 --crop 0,2500,800,600");

        Assert.Equal(ShellResult.Error("img: the crop 0,2500,800,600 lies outside the image as turned by --rotate 90 (3000×2000 px)") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Empty(_pixels.Requests);
        Assert.Equal(new ImgRefused(ImgRefusal.BadCrop), Assert.Single(_pixels.Used));
    }

    [Fact]
    public async Task A_crop_inside_the_turned_image_passes_though_outside_the_stored_one()
    {
        await Upload("/Work/Scans/sheet.jpg", Jpeg(), (2000, 3000));

        var result = await Run("img /Work/Scans/sheet.jpg --rotate 90 --crop 2500,0,400,400");

        Assert.False(result.IsError, result.Output);
        Assert.Single(_pixels.Requests);
    }

    [Fact]
    public async Task Without_rotate_the_text_block_is_as_it_was()
    {
        var version = await Upload("/Work/Scans/sheet.jpg", Jpeg(), (3000, 2000));
        _pixels.Answer = (_, _, _) => new Picture(Delivered, "image/png", 300, 200, 3000, 2000);

        var text = Text(await Run("img /Work/Scans/sheet.jpg --crop 0,0,300,200"));

        Assert.Equal($"/Work/Scans/sheet.jpg (version {version}): image (JPEG), original 3000×2000 px; crop 0,0,300,200; medium: 300×200 px PNG, {AttachmentText.Size(4)}\n{Dropped}", text);
        Assert.DoesNotContain("turned", text);
        Assert.DoesNotContain("With --rotate", text);
        Assert.Equal(0, Assert.Single(_pixels.Rotations));
        Assert.DoesNotContain(_pixels.Used, u => u is PictureTurned);
    }

    [Fact]
    public async Task The_help_names_rotate()
    {
        var help = (await Run("help img")).Output;

        Assert.StartsWith("img <path> [--size small|medium|large] [--rotate 90|180|270] [--crop x,y,w,h] [--frame N]\n", help);
        Assert.Contains("  --rotate D        turn the picture clockwise by 90, 180 or 270 degrees, on top of the camera's rotation,", help);
        Assert.Contains("for a sheet lying on its side. With it, --crop counts in the turned picture, from its", help);
        Assert.Equal("img <path> [--size S] [--rotate D] [--crop x,y,w,h] [--frame N]", new Kumbu.Core.Sh.Commands.Img(_store, _pixels).Usage);
    }

    private static byte[] Gif() => [.. "GIF89a"u8.ToArray(), .. new byte[100]];

    private static Picture Still(int? frames) => new(Delivered, "image/png", 600, 400, 600, 400, Frames: frames);

    private const string Animated3 = "An animated GIF of 3 frames, shown as a still: frame 1. --frame N shows another.";

    [Fact]
    public async Task A_plain_gif_of_three_frames_says_so()
    {
        await Upload("/Work/Clips/animated.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(3);

        var lines = Text(await Run("img /Work/Clips/animated.gif")).Split('\n');

        Assert.Equal([Animated3, Dropped], lines[1..]);
    }

    [Fact]
    public async Task A_plain_gif_of_one_frame_has_no_note()
    {
        await Upload("/Work/Clips/still.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(1);

        var lines = Text(await Run("img /Work/Clips/still.gif")).Split('\n');

        Assert.Equal([Dropped], lines[1..]);
    }

    [Fact]
    public async Task A_plain_gif_of_unknown_count_says_todays_sentence()
    {
        await Upload("/Work/Clips/old.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(null);

        var lines = Text(await Run("img /Work/Clips/old.gif")).Split('\n');

        Assert.Equal(["A GIF is shown as a still: if it is animated, only its first frame.", Dropped], lines[1..]);
    }

    [Fact]
    public async Task A_frame_goes_to_the_pixels()
    {
        await Upload("/Work/Clips/animated.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(3);

        var result = await Run("img /Work/Clips/animated.gif --frame 2");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(2, Assert.Single(_pixels.Frames));
        Assert.Contains(_pixels.Used, u => u is ImgCalled);
        Assert.Contains(_pixels.Used, u => u is ImgFrameShown);
        _pixels.Used.Clear();
        await Run("img /Work/Clips/animated.gif");
        Assert.DoesNotContain(_pixels.Used, u => u is ImgFrameShown);
        Assert.Null(_pixels.Frames[^1]);
    }

    [Fact]
    public async Task The_head_names_the_frame_of_the_count()
    {
        var version = await Upload("/Work/Clips/animated.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(3);

        var text = Text(await Run("img /Work/Clips/animated.gif --frame 2"));

        Assert.Equal($"/Work/Clips/animated.gif (version {version}), frame 2 of 3: image (GIF), original 600×400 px; medium: 600×400 px PNG, 4 B\n{Dropped}", text);
    }

    [Fact]
    public async Task The_head_names_the_frame_with_the_count_unknown()
    {
        var version = await Upload("/Work/Clips/animated.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(null);

        var text = Text(await Run("img /Work/Clips/animated.gif --frame 2"));

        Assert.StartsWith($"/Work/Clips/animated.gif (version {version}), frame 2: image (GIF), ", text);
        Assert.DoesNotContain("A GIF is shown", text);
    }

    [Theory]
    [InlineData("1-3")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("2.5")]
    [InlineData("1000001")]
    public async Task A_bad_frame_is_refused_with_its_sentence(string frame)
    {
        var result = await Run($"img /Work/Clips/nothing.gif --frame={frame}");

        Assert.Equal(ShellResult.Error($"img: --frame is one frame, counting from 1, not '{frame}'") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(new ImgRefused(ImgRefusal.BadFrame), Assert.Single(_pixels.Used));
        Assert.Empty(_pixels.Requests);
    }

    [Fact]
    public async Task A_frame_of_anything_but_a_gif_is_refused()
    {
        await Upload("/Work/Screens/shot.png", Png(1000), (1440, 3120));

        var result = await Run("img /Work/Screens/shot.png --frame 2");

        Assert.Equal(ShellResult.Error("img: --frame is for GIFs; /Work/Screens/shot.png is a PNG") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(new ImgRefused(ImgRefusal.BadFrame), Assert.Single(_pixels.Used));
        Assert.Empty(_pixels.Requests);
    }

    [Fact]
    public async Task A_frame_past_the_end_says_so()
    {
        await Upload("/Work/Clips/moving.gif", Gif(), (600, 400));
        _pixels.Fail = new PixelsUnavailableException("/Work/Clips/moving.gif has no frame 7; img without --frame says how many it has", PixelsProblem.BadFrame);

        var result = await Run("img /Work/Clips/moving.gif --frame 7");

        Assert.Equal(ShellResult.Error("img: /Work/Clips/moving.gif has no frame 7; img without --frame says how many it has") with { Kind = ShellErrorKind.Invalid }, result);
        Assert.Equal(new ImgRefused(ImgRefusal.BadFrame), Assert.Single(_pixels.Used));
    }

    [Fact]
    public async Task Frame_goes_with_rotate_crop_and_size()
    {
        await Upload("/Work/Clips/animated.gif", Gif(), (600, 400));
        _pixels.Answer = (_, _, _) => Still(3);

        var result = await Run("img /Work/Clips/animated.gif --size large --rotate 90 --frame 3 --crop 0,0,300,200");

        Assert.False(result.IsError, result.Output);
        Assert.Equal((P("/Work/Clips/animated.gif"), SizeStep.Large, new Crop(0, 0, 300, 200)), Assert.Single(_pixels.Requests));
        Assert.Equal(90, Assert.Single(_pixels.Rotations));
        Assert.Equal(3, Assert.Single(_pixels.Frames));
        Assert.Contains(", frame 3 of 3: ", Text(result));
    }

    [Fact]
    public async Task The_help_names_frame()
    {
        var help = (await Run("help img")).Output;

        Assert.StartsWith("img <path> [--size small|medium|large] [--rotate 90|180|270] [--crop x,y,w,h] [--frame N]\n", help);
        Assert.Contains("  --frame N         a GIF only: frame N, from 1, as it looks when played. One frame a call; a plain img", help);
        Assert.Contains("A GIF is shown as a still, its first frame unless --frame says otherwise.", help);
        Assert.Equal("img <path> [--size S] [--rotate D] [--crop x,y,w,h] [--frame N]", new Kumbu.Core.Sh.Commands.Img(_store, _pixels).Usage);
    }

    [Fact]
    public async Task Without_pixels_the_shell_has_no_img()
    {
        var plain = TestShell.Of();

        Assert.StartsWith("command not found: img", (await plain.RunAsync("img /a.png", Ct)).Output);
        Assert.DoesNotContain("\n  img ", (await plain.RunAsync("help", Ct)).Output);
    }
}
