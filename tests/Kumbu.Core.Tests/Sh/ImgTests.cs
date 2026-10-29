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

        /// <summary>The picture returned; null scales the original down to the step's long edge.</summary>
        public Func<SizeStep, Crop?, AttachmentState, Picture>? Answer { get; set; }

        public Exception? Fail { get; set; }

        public Task<Picture> ImageAsync(VaultPath path, AttachmentState state, SizeStep step, Crop? crop, CancellationToken ct)
        {
            Requests.Add((path, step, crop));
            if (Fail is not null) throw Fail;
            return Task.FromResult(Answer?.Invoke(step, crop, state) ?? new Picture(Delivered, "image/png", 724, 1568, state.Width!.Value, state.Height!.Value));
        }

        public Task<Picture> PageAsync(VaultPath path, AttachmentState state, int page, SizeStep step, Crop? crop, CancellationToken ct) => throw new NotSupportedException();

        public Task<PdfAnalysis?> AnalysisAsync(VaultPath path, AttachmentState state, bool runIfPending, CancellationToken ct) => throw new NotSupportedException();

        public Task<IReadOnlyList<string>?> PageTextAsync(VaultPath path, AttachmentState state, int first, int last, CancellationToken ct) => throw new NotSupportedException();

        public void Count(AttachmentUse use)
        {
        }
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

    private static string Text(ShellResult result) => Assert.IsType<TextPart>(result.Parts![0]).Text;

    private const string Dropped = "If no image follows, your client dropped it: tell the user so instead of guessing what it shows.";

    [Fact]
    public async Task The_image_follows_a_text_block_that_says_what_it_is()
    {
        var version = await Upload("/Work/Screens/shot.png", Png(500_000), (1440, 3120));
        _pixels.Answer = (_, _, s) => new Picture(new byte[312 * 1024], "image/png", 724, 1568, 1440, 3120);

        var result = await Run("img /Work/Screens/shot.png");

        Assert.False(result.IsError, result.Output);
        Assert.Equal(2, result.Parts!.Count);
        Assert.Equal($"/Work/Screens/shot.png (version {version}): image (PNG), original 1440×3120 px; medium: 724×1568 px PNG, 312 KB\n{Dropped}", Text(result));
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
    [InlineData(1440, 6000, 376, 1568, "Very tall/wide: details may be too small to read; look at a part with --crop x,y,w,h.")]
    [InlineData(6000, 1440, 1568, 376, "Very tall/wide: details may be too small to read; look at a part with --crop x,y,w,h.")]
    [InlineData(1440, 3120, 724, 1568, null)]
    public async Task A_very_tall_or_wide_picture_gets_a_hint_to_crop(int ow, int oh, int w, int h, string? hint)
    {
        await Upload("/Work/Screens/long.png", Png(1000), (ow, oh));
        _pixels.Answer = (_, _, _) => new Picture(new byte[10_000], "image/png", w, h, ow, oh);

        var lines = Text(await Run("img /Work/Screens/long.png")).Split('\n');

        Assert.Equal(hint is null ? 2 : 3, lines.Length);
        if (hint is not null) Assert.Equal(hint, lines[1]);
        Assert.Equal(Dropped, lines[^1]);
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

    [Fact]
    public async Task Without_pixels_the_shell_has_no_img()
    {
        var plain = TestShell.Of();

        Assert.StartsWith("command not found: img", (await plain.RunAsync("img /a.png", Ct)).Output);
        Assert.DoesNotContain("\n  img ", (await plain.RunAsync("help", Ct)).Output);
    }
}
