// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using Kumbu.Core.Attachments;
using Kumbu.Server.Attachments;

namespace Kumbu.Server.Tests.Attachments;

/// <summary>
/// The PDF sidecar without the sidecar: canned analyses and solid-colour PNGs of the size a request asks for. It counts
/// its calls, and can be switched down (every call unavailable), slow, or to answer with an error code.
/// </summary>
public sealed class FakePdfSidecar : IPdfSidecar
{
    private int _analyses;
    private int _renders;

    /// <summary>Every call fails with <c>unavailable</c>, as with the sidecar stopped.</summary>
    public bool Down { get; set; }

    /// <summary>How long each call takes before it answers.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>A code an analysis fails with (e.g. <c>encrypted</c>); null to succeed.</summary>
    public string? FailWith { get; set; }

    /// <summary>The analysis returned for a PDF; by default <see cref="Canned"/> with three pages.</summary>
    public Func<ReadOnlyMemory<byte>, SidecarAnalysis> Analysis { get; set; } = _ => Canned(3);

    /// <summary>The size of each page in points, as displayed (A4 by default).</summary>
    public (double Width, double Height) PageSize { get; set; } = (595, 842);

    public int Analyses => Volatile.Read(ref _analyses);

    public int Renders => Volatile.Read(ref _renders);

    public List<(int Page, StepLimits Target, Crop? Crop)> PageRequests { get; } = [];

    public List<(StepLimits Target, Crop? Crop)> ImageRequests { get; } = [];

    public async Task<SidecarAnalysis> AnalyseAsync(ReadOnlyMemory<byte> pdf, int maxPages, CancellationToken ct)
    {
        Interlocked.Increment(ref _analyses);
        await AnswerAsync(ct);
        if (FailWith is { } code) throw new SidecarException(code, $"The analysis failed ({code}).");
        return Analysis(pdf);
    }

    public async Task<Picture> RenderPageAsync(ReadOnlyMemory<byte> pdf, int page, StepLimits target, Crop? crop, CancellationToken ct)
    {
        Interlocked.Increment(ref _renders);
        lock (PageRequests) PageRequests.Add((page, target, crop));
        await AnswerAsync(ct);
        var (w, h) = crop is null ? PageSize : (crop.Width, crop.Height);
        var (width, height) = Fit(w * 600 / 72, h * 600 / 72, target);
        return new Picture(SolidPng(width, height), "image/png", width, height, PageSize.Width, PageSize.Height);
    }

    public async Task<Picture> RenderImageAsync(ReadOnlyMemory<byte> image, StepLimits target, Crop? crop, CancellationToken ct)
    {
        Interlocked.Increment(ref _renders);
        lock (ImageRequests) ImageRequests.Add((target, crop));
        await AnswerAsync(ct);
        var original = ImageHeader.Read(image.Span, out _) ?? (640, 480);
        var (w, h) = crop is null ? (original.Width, original.Height) : ((int)crop.Width, (int)crop.Height);
        var (width, height) = Fit(w, h, target);
        return new Picture(SolidPng(width, height), "image/png", width, height, original.Width, original.Height);
    }

    private async Task AnswerAsync(CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        if (Down) throw new SidecarException(SidecarException.Unavailable, PdfSidecar.UnavailableMessage);
    }

    /// <summary>Scaled down to fit the step's long edge and pixel count, never up.</summary>
    private static (int Width, int Height) Fit(double width, double height, StepLimits target)
    {
        var scale = Math.Min(1, Math.Min(target.LongEdge / Math.Max(width, height), Math.Sqrt(target.Pixels / (width * height))));
        return (Math.Max(1, (int)Math.Floor(width * scale)), Math.Max(1, (int)Math.Floor(height * scale)));
    }

    /// <summary>An analysis of <paramref name="pages"/> text pages, page n reading "Page n text".</summary>
    public static SidecarAnalysis Canned(int pages, Func<int, string>? text = null)
    {
        text ??= n => $"Page {n} text";
        var list = Enumerable.Range(1, pages)
            .Select(n => new PdfPage(n, 595, 842, 0, PageType.Text, text(n).Length, 0.2, 0, 0, 0, []))
            .ToList();
        return new SidecarAnalysis(new PdfAnalysis(PdfAnalyses.Analyser, pages, pages, "A title", null, list), [.. Enumerable.Range(1, pages).Select(text)]);
    }

    /// <summary>A real PNG of one colour: signature, IHDR, one IDAT of deflated rows, IEND.</summary>
    public static byte[] SolidPng(int width, int height, byte red = 200, byte green = 120, byte blue = 40)
    {
        var raw = new byte[height * (1 + width * 3)];
        for (var y = 0; y < height; y++)
        {
            var row = y * (1 + width * 3);
            for (var x = 0; x < width; x++) (raw[row + 1 + x * 3], raw[row + 2 + x * 3], raw[row + 3 + x * 3]) = (red, green, blue);
        }

        using var deflated = new MemoryStream();
        using (var z = new ZLibStream(deflated, CompressionLevel.Fastest, leaveOpen: true)) z.Write(raw);
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        using var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Chunk(png, "IHDR"u8, ihdr);
        Chunk(png, "IDAT"u8, deflated.ToArray());
        Chunk(png, "IEND"u8, []);
        return png.ToArray();
    }

    private static void Chunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> four = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(four, data.Length);
        s.Write(four);
        s.Write(type);
        s.Write(data);
        var crc = Crc32(Crc32(0xFFFFFFFF, type), data) ^ 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(four, crc);
        s.Write(four);
    }

    private static uint Crc32(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
        }

        return crc;
    }
}
