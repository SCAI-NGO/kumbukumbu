// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using Kumbu.Server.Attachments;

namespace Kumbu.Server.Tests.Attachments;

public class ImageMetadataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The TIFF block the server writes for an orientation N: IFD0 with the orientation alone.</summary>
    private static byte[] MinimalExif(int orientation) =>
        [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    internal static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];

    internal static byte[] Be32(int v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];

    internal static byte[] Le32(int v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];

    /// <summary>A big-endian TIFF block as a camera writes it: the maker (<c>PixelCam</c>), the orientation if given, the
    /// artist (<c>Alice</c>) and a pointer to a GPS IFD holding the latitude's reference and the string <c>GPS48N</c>.</summary>
    internal static byte[] CameraTiff(int? orientation)
    {
        var entries = orientation is null ? 3 : 4;
        var ifd0End = 8 + 2 + entries * 12 + 4;
        byte[] make = [.. "PixelCam"u8, 0];
        byte[] artist = [.. "Alice"u8, 0];
        byte[] area = [.. "GPS48N"u8, 0];
        var makeAt = ifd0End;
        var artistAt = makeAt + make.Length;
        var gpsAt = artistAt + artist.Length;
        var areaAt = gpsAt + 2 + 2 * 12 + 4;

        var tiff = new List<byte>();
        tiff.AddRange([.. "MM"u8, 0, 42, .. Be32(8)]);
        tiff.AddRange(Be16(entries));
        tiff.AddRange([.. Be16(0x010F), .. Be16(2), .. Be32(make.Length), .. Be32(makeAt)]);
        if (orientation is { } o)
        {
            tiff.AddRange([.. Be16(0x0112), .. Be16(3), .. Be32(1), .. Be16(o), 0, 0]);
        }

        tiff.AddRange([.. Be16(0x013B), .. Be16(2), .. Be32(artist.Length), .. Be32(artistAt)]);
        tiff.AddRange([.. Be16(0x8825), .. Be16(4), .. Be32(1), .. Be32(gpsAt)]);
        tiff.AddRange(Be32(0)); // no next IFD
        tiff.AddRange(make);
        tiff.AddRange(artist);
        tiff.AddRange(Be16(2));
        tiff.AddRange([.. Be16(0x0001), .. Be16(2), .. Be32(2), (byte)'N', 0, 0, 0]); // GPSLatitudeRef
        tiff.AddRange([.. Be16(0x001C), .. Be16(2), .. Be32(area.Length), .. Be32(areaAt)]); // GPSAreaInformation
        tiff.AddRange(Be32(0));
        tiff.AddRange(area);
        return [.. tiff];
    }

    /// <summary>An ICC profile of 132 bytes (its header and an empty tag table) whose header names its maker and model
    /// (<c>PixelCam</c>), its creator (<c>Alic</c>), the time it was made and a profile ID.</summary>
    internal static byte[] IccProfile()
    {
        var icc = Enumerable.Repeat((byte)0x11, 132).ToArray();
        Be32(132).CopyTo(icc, 0);
        "lcms"u8.CopyTo(icc.AsSpan(4));
        "mntrRGB XYZ "u8.CopyTo(icc.AsSpan(12));
        byte[] date = [0x07, 0xEA, 0, 11, 0, 5, 0, 14, 0, 32, 0, 7];
        date.CopyTo(icc, 24);
        "acspAPPL"u8.CopyTo(icc.AsSpan(36));
        "PixelCam"u8.CopyTo(icc.AsSpan(48));
        "Alic"u8.CopyTo(icc.AsSpan(80));
        Enumerable.Range(1, 16).Select(i => (byte)i).ToArray().CopyTo(icc, 84);
        icc.AsSpan(100, 28).Clear();
        icc.AsSpan(128, 4).Clear(); // no tags
        return icc;
    }

    /// <summary><see cref="IccProfile"/> as the server leaves it: date, maker, model, creator and profile ID zeroed.</summary>
    private static byte[] BlankedIccProfile()
    {
        var icc = IccProfile();
        icc.AsSpan(24, 12).Clear();
        icc.AsSpan(48, 8).Clear();
        icc.AsSpan(80, 20).Clear();
        return icc;
    }

    internal static byte[] Segment(int marker, ReadOnlySpan<byte> data) => [0xFF, (byte)marker, .. Be16(data.Length + 2), .. data];

    /// <summary>A JPEG of 40 by 20 as stored, with everything a phone and a photo editor leave in it: JFIF, the camera's
    /// Exif (<see cref="CameraTiff"/>, if given), XMP, Photoshop's IPTC, a comment, an ICC profile, the tables, one scan,
    /// EOI and a trailer after it.</summary>
    internal static byte[] Jpeg(byte[]? exif)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        jpeg.AddRange(Segment(0xE0, [.. "JFIF\0"u8, 1, 1, 0, 0, 1, 0, 1, 0, 0]));
        if (exif is not null)
        {
            jpeg.AddRange(Segment(0xE1, [.. "Exif\0\0"u8, .. exif]));
        }

        jpeg.AddRange(Segment(0xE1, [.. "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>Alice</x:xmpmeta>"u8]));
        jpeg.AddRange(Segment(0xED, [.. "Photoshop 3.0\08BIM"u8, 4, 4, 0, 0, 0, 0, 0, 10, 0x1C, 2, 0x50, 0, 5, .. "Alice"u8]));
        jpeg.AddRange(Segment(0xFE, "Alice"u8));
        jpeg.AddRange(Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 1, .. IccProfile()]));
        jpeg.AddRange(Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]));
        jpeg.AddRange(Segment(0xC0, [8, .. Be16(20), .. Be16(40), 1, 1, 0x11, 0]));
        jpeg.AddRange(Segment(0xC4, [0, .. new byte[16]]));
        jpeg.AddRange(Segment(0xDA, [1, 1, 0, 0, 63, 0]));
        jpeg.AddRange([0x12, 0xFF, 0x00, 0x34, 0xFF, 0xD0, 0x56, 0x78, 0xFF, 0xD9]); // scan bytes, a stuffed FF and a restart marker, EOI
        jpeg.AddRange("Alice"u8.ToArray());
        return [.. jpeg];
    }

    /// <summary>A camera's JPEG whose Exif holds GPS data (the string <c>GPS48N</c>) and the given orientation.</summary>
    internal static byte[] JpegWithGps(int orientation) => Jpeg(CameraTiff(orientation));

    /// <summary>The segments before the first scan, as (marker, data).</summary>
    private static List<(byte Marker, byte[] Data)> Segments(byte[] jpeg)
    {
        var segments = new List<(byte, byte[])>();
        var pos = 2;
        while (jpeg[pos + 1] != 0xDA)
        {
            var length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(pos + 2));
            segments.Add((jpeg[pos + 1], jpeg[(pos + 4)..(pos + 2 + length)]));
            pos += 2 + length;
        }

        return segments;
    }

    private static readonly byte[] Sof0 = [0xFF, 0xC0];
    private static readonly byte[] Eoi = [0xFF, 0xD9];

    private static bool Holds(byte[] bytes, string text) => bytes.AsSpan().IndexOf(Encoding.ASCII.GetBytes(text)) >= 0;

    [Fact]
    public void Jpeg_loses_exif_xmp_iptc_and_comments()
    {
        var input = JpegWithGps(1);
        foreach (var leak in new[] { "Alice", "PixelCam", "GPS48N", "xap", "Photoshop" })
        {
            Assert.True(Holds(input, leak));
        }

        var output = ImageMetadata.Strip(input, "image/jpeg", Ct);

        Assert.NotNull(output);
        foreach (var leak in new[] { "Alice", "PixelCam", "GPS48N", "xap", "Photoshop" })
        {
            Assert.False(Holds(output, leak), leak);
        }

        Assert.True(Holds(output, "ICC_PROFILE"));
        Assert.True(Holds(output, "JFIF"));
        Assert.Equal([0xFF, 0xD9], output[^2..]);
        var sof = input.AsSpan().IndexOf(Sof0);
        var eoi = input.AsSpan().LastIndexOf(Eoi);
        Assert.Equal(input[sof..(eoi + 2)], output[output.AsSpan().IndexOf(Sof0)..]);
        Assert.Equal((40, 20), ImageHeader.Read(output, out _));
    }

    [Fact]
    public void Jpeg_keeps_its_orientation()
    {
        var input = JpegWithGps(6);

        var output = ImageMetadata.Strip(input, "image/jpeg", Ct)!;

        var segments = Segments(output);
        Assert.Equal(0xE0, segments[0].Marker);
        Assert.Equal(0xE1, segments[1].Marker);
        var app1 = Assert.Single(segments, s => s.Marker == 0xE1);
        Assert.Equal([.. "Exif\0\0"u8, .. MinimalExif(6)], app1.Data);
        Assert.Equal((20, 40), ImageHeader.Read(input, out _));
        Assert.Equal(ImageHeader.Read(input, out _), ImageHeader.Read(output, out _));
    }

    [Fact]
    public void Jpeg_without_orientation_gets_no_exif()
    {
        foreach (var input in new[] { JpegWithGps(1), Jpeg(CameraTiff(null)), Jpeg(null) })
        {
            var output = ImageMetadata.Strip(input, "image/jpeg", Ct)!;
            Assert.DoesNotContain(Segments(output), s => s.Marker == 0xE1);
            Assert.Equal((40, 20), ImageHeader.Read(output, out _));
        }
    }

    /// <summary>A CRC-32 of the test's own, bit by bit, to check the server's table.</summary>
    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
            {
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
            }
        }

        return ~crc;
    }

    internal static byte[] PngChunk(string type, ReadOnlySpan<byte> data)
    {
        byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
        return [.. Be32(data.Length), .. typed, .. Be32((int)Crc32(typed))];
    }

    internal static byte[] Png(params byte[][] chunks) => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. chunks.SelectMany(c => c)];

    internal static byte[] Ihdr => PngChunk("IHDR", [.. Be32(40), .. Be32(20), 8, 6, 0, 0, 0]);

    private static List<(string Type, byte[] Data, uint Crc)> PngChunks(byte[] png)
    {
        var chunks = new List<(string, byte[], uint)>();
        var pos = 8;
        while (pos < png.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos));
            chunks.Add((Encoding.ASCII.GetString(png, pos + 4, 4), png[(pos + 8)..(pos + 8 + length)], BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos + 8 + length))));
            pos += 12 + length;
        }

        return chunks;
    }

    [Fact]
    public void Png_loses_text_and_exif_chunks()
    {
        var input = Png(
            Ihdr,
            PngChunk("tEXt", "Author\0Alice"u8),
            PngChunk("iCCP", [.. "PixelCam profile\0"u8, 0, .. Deflated(IccProfile())]),
            PngChunk("eXIf", CameraTiff(6)),
            PngChunk("pHYs", [.. Be32(2835), .. Be32(2835), 1]),
            PngChunk("zTXt", [.. "Comment\0"u8, 0, 0x78, 0x9C, .. "Alice"u8]),
            PngChunk("iTXt", "XML:com.adobe.xmp\0\0\0\0\0<x:xmpmeta>Alice</x:xmpmeta>"u8),
            PngChunk("prVt", "Alice"u8),
            PngChunk("IDAT", [0x78, 0x9C, 1, 2]),
            PngChunk("tIME", [0x07, 0xEA, 11, 5, 14, 32, 0]),
            PngChunk("IDAT", [3, 4]),
            PngChunk("IEND", []));

        var output = ImageMetadata.Strip(input, "image/png", Ct);

        Assert.NotNull(output);
        var chunks = PngChunks(output);
        Assert.Equal(["IHDR", "iCCP", "pHYs", "eXIf", "IDAT", "IDAT", "IEND"], chunks.Select(c => c.Type));
        Assert.Equal(MinimalExif(6), chunks[3].Data);
        Assert.All(chunks, c => Assert.Equal(Crc32([.. Encoding.ASCII.GetBytes(c.Type), .. c.Data]), c.Crc));
        Assert.False(Holds(output, "Alice"));
        Assert.False(Holds(output, "GPS48N"));
        Assert.Equal(input[^12..], output[^12..]);
    }

    [Fact]
    public void Png_with_an_unknown_critical_chunk_is_refused()
    {
        var input = Png(Ihdr, PngChunk("ABCD", [1, 2, 3]), PngChunk("IDAT", [0x78, 0x9C]), PngChunk("IEND", []));

        Assert.Null(ImageMetadata.Strip(input, "image/png", Ct));
    }

    internal static byte[] WebpChunk(string fourcc, ReadOnlySpan<byte> data) =>
        [.. Encoding.ASCII.GetBytes(fourcc), .. Le32(data.Length), .. data, .. data.Length % 2 == 1 ? new byte[] { 0 } : []];

    internal static byte[] Riff(params byte[][] chunks)
    {
        byte[] body = [.. "WEBP"u8, .. chunks.SelectMany(c => c)];
        return [.. "RIFF"u8, .. Le32(body.Length), .. body];
    }

    private static List<(string FourCc, byte[] Data)> WebpChunks(byte[] webp)
    {
        var chunks = new List<(string, byte[])>();
        var pos = 12;
        while (pos < webp.Length)
        {
            var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(pos + 4));
            chunks.Add((Encoding.ASCII.GetString(webp, pos, 4), webp[(pos + 8)..(pos + 8 + length)]));
            pos += 8 + length + length % 2;
        }

        Assert.Equal(webp.Length, pos);
        return chunks;
    }

    /// <summary>An extended WebP of 300 by 144 with an ICC profile (odd in length), a lossy frame, the camera's Exif and XMP;
    /// its flags say ICC, Exif and XMP.</summary>
    private static byte[] Webp(int orientation) => Riff(
        WebpChunk("VP8X", [0x2C, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]),
        WebpChunk("ICCP", [1, 2, 3, 4, 5]),
        WebpChunk("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, 0x2C, 0x01, 0x90, 0x00]),
        WebpChunk("EXIF", CameraTiff(orientation)),
        WebpChunk("XMP ", "<x:xmpmeta>Alice</x:xmpmeta>"u8));

    [Fact]
    public void Webp_loses_exif_and_xmp_and_their_flags()
    {
        foreach (var (orientation, flags) in new[] { (1, 0x00), (6, 0x08) })
        {
            var output = ImageMetadata.Strip(Webp(orientation), "image/webp", Ct);

            Assert.NotNull(output);
            Assert.Equal(output.Length - 8, (int)BinaryPrimitives.ReadUInt32LittleEndian(output.AsSpan(4)));
            var chunks = WebpChunks(output);
            Assert.Equal(flags, chunks[0].Data[0] & 0x0C);
            Assert.Equal(0x20, chunks[0].Data[0] & 0x20);
            Assert.Equal([1, 2, 3, 4, 5], chunks.Single(c => c.FourCc == "ICCP").Data);
            Assert.DoesNotContain(chunks, c => c.FourCc == "XMP ");
            var exif = chunks.Where(c => c.FourCc == "EXIF").ToList();
            if (orientation == 1)
            {
                Assert.Empty(exif);
            }
            else
            {
                Assert.Equal(MinimalExif(6), Assert.Single(exif).Data);
            }

            Assert.False(Holds(output, "Alice"));
            Assert.False(Holds(output, "GPS48N"));
            Assert.Equal((300, 144), ImageHeader.Read(output, out _));
        }
    }

    [Fact]
    public void Gif_loses_comments_and_xmp()
    {
        byte[] head = [.. "GIF89a"u8, 2, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF];
        byte[] netscape = [0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 0];
        byte[] comment = [0x21, 0xFE, 5, .. "Alice"u8, 0];
        byte[] xmp = [0x21, 0xFF, 11, .. "XMP DataXMP"u8, 5, .. "Alice"u8, 0];
        byte[] control = [0x21, 0xF9, 4, 0, 0, 0, 0, 0];
        byte[] image = [0x2C, 0, 0, 0, 0, 2, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0];
        byte[] input = [.. head, .. netscape, .. comment, .. xmp, .. control, .. image, 0x3B];

        var output = ImageMetadata.Strip(input, "image/gif", Ct);

        Assert.Equal([.. head, .. netscape, .. control, .. image, 0x3B], output);
    }

    [Fact]
    public void Bytes_that_dont_parse_are_refused()
    {
        var jpeg = JpegWithGps(6);
        Assert.Null(ImageMetadata.Strip(jpeg.AsSpan(0, jpeg.AsSpan().LastIndexOf(Eoi)), "image/jpeg", Ct));

        var png = Png(Ihdr, PngChunk("IDAT", [0x78, 0x9C, 1, 2, 3, 4]), PngChunk("IEND", []));
        Assert.NotNull(ImageMetadata.Strip(png, "image/png", Ct));
        Assert.Null(ImageMetadata.Strip(png.AsSpan(0, 8 + 25 + 10), "image/png", Ct));

        var webp = Webp(6);
        Assert.NotNull(ImageMetadata.Strip(webp, "image/webp", Ct));
        byte[] overlong = [.. webp[..4], .. Le32(webp.Length), .. webp[8..]];
        Assert.Null(ImageMetadata.Strip(overlong, "image/webp", Ct));

        Assert.Null(ImageMetadata.Strip("%PDF-1.7\n%Alice"u8, "application/pdf", Ct));
        Assert.Null(ImageMetadata.Strip(png, "image/jpeg", Ct));
    }

    internal static byte[] Deflated(byte[] data)
    {
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal))
        {
            z.Write(data);
        }

        return compressed.ToArray();
    }

    private static byte[] Inflated(byte[] data)
    {
        using var z = new ZLibStream(new MemoryStream(data), CompressionMode.Decompress);
        using var plain = new MemoryStream();
        z.CopyTo(plain);
        return plain.ToArray();
    }

    private static readonly byte[] Tables =
    [
        .. Segment(0xDB, [0, .. Enumerable.Repeat((byte)1, 64)]),
        .. Segment(0xC0, [8, .. Be16(20), .. Be16(40), 1, 1, 0x11, 0]),
        .. Segment(0xC4, [0, .. new byte[16]]),
    ];

    private static readonly byte[] Scan = [.. Segment(0xDA, [1, 1, 0, 0, 63, 0]), 0x12, 0xFF, 0x00, 0x34];

    [Fact]
    public void Jpeg_keeps_only_the_listed_markers()
    {
        byte[] input =
        [
            0xFF, 0xD8,
            .. Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 2, 1, .. "Alice!"u8]), // a thumbnail of 2 by 1
            .. Segment(0xE0, [.. "JFXX\0"u8, 0x10, .. "Alice"u8]),
            .. Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1, .. "Alice"u8]),
            0xFF, 0x01, // TEM
            .. Segment(0xC8, "Alice"u8),
            .. Segment(0xF0, "Alice"u8),
            .. Segment(0x02, "Alice"u8),
            .. Segment(0xDE, "Alice"u8),
            .. Tables,
            .. Scan,
            0xFF, 0xD9,
        ];

        var output = ImageMetadata.Strip(input, "image/jpeg", Ct);

        Assert.Equal(
        [
            0xFF, 0xD8,
            .. Segment(0xE0, [.. "JFIF\0"u8, 1, 2, 1, 0, 72, 0, 72, 0, 0]),
            .. Segment(0xEE, [.. "Adobe"u8, 0, 100, 0, 0, 0, 0, 1]),
            .. Tables,
            .. Scan,
            0xFF, 0xD9,
        ], output);
    }

    [Fact]
    public void Jpeg_without_a_frame_or_a_scan_is_refused()
    {
        byte[] withoutFrame = [0xFF, 0xD8, .. Segment(0xDB, [0, .. new byte[64]]), .. Scan, 0xFF, 0xD9];
        byte[] withoutScan = [0xFF, 0xD8, .. Tables, 0xFF, 0xD9];

        Assert.Null(ImageMetadata.Strip(withoutFrame, "image/jpeg", Ct));
        Assert.Null(ImageMetadata.Strip(withoutScan, "image/jpeg", Ct));
    }

    [Fact]
    public void Jpeg_loses_segments_between_scans()
    {
        byte[] head = [0xFF, 0xD8, .. Segment(0xDB, [0, .. new byte[64]]), .. Segment(0xC2, [8, .. Be16(20), .. Be16(40), 1, 1, 0x11, 0])];
        byte[] dht = Segment(0xC4, [0, .. new byte[16]]);
        byte[] first = [.. Segment(0xDA, [1, 1, 0, 0, 0, 0]), 0x12, 0xFF, 0x00, 0x34];
        byte[] between = [.. Segment(0xE1, "http://ns.adobe.com/xap/1.0/\0<x:xmpmeta>Alice</x:xmpmeta>"u8), .. Segment(0xFE, "Alice"u8)];
        byte[] second = [.. Segment(0xDA, [1, 1, 0, 1, 63, 0]), 0x56, 0xFF, 0xD3, 0x78];
        byte[] input = [.. head, .. dht, .. first, .. dht, .. between, .. second, 0xFF, 0xD9];

        var output = ImageMetadata.Strip(input, "image/jpeg", Ct);

        Assert.Equal([.. head, .. dht, .. first, .. dht, .. second, 0xFF, 0xD9], output);
    }

    [Fact]
    public void Icc_profiles_keep_their_colours_but_lose_names_and_dates()
    {
        // JPEG: the profile in two APP2 chunks, its header across both.
        var icc = IccProfile();
        byte[] jpeg =
        [
            0xFF, 0xD8,
            .. Segment(0xE2, [.. "ICC_PROFILE\0"u8, 1, 2, .. icc[..60]]),
            .. Segment(0xE2, [.. "ICC_PROFILE\0"u8, 2, 2, .. icc[60..]]),
            .. Tables, .. Scan, 0xFF, 0xD9,
        ];
        var stripped = ImageMetadata.Strip(jpeg, "image/jpeg", Ct)!;
        var chunks = Segments(stripped).Where(s => s.Marker == 0xE2).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(BlankedIccProfile(), chunks[0].Data[14..].Concat(chunks[1].Data[14..]).ToArray());
        Assert.False(Holds(stripped, "PixelCam"));

        // PNG: the profile inflated, blanked, deflated, under a name of no one's.
        var png = ImageMetadata.Strip(Png(Ihdr, PngChunk("iCCP", [.. "PixelCam profile\0"u8, 0, .. Deflated(icc)]), PngChunk("IDAT", [0x78, 0x9C]), PngChunk("IEND", [])), "image/png", Ct)!;
        var iccp = PngChunks(png).Single(c => c.Type == "iCCP");
        Assert.Equal([.. "ICC profile\0"u8, 0], iccp.Data[..13]);
        Assert.Equal(BlankedIccProfile(), Inflated(iccp.Data[13..]));
        Assert.Equal(Crc32([.. "iCCP"u8, .. iccp.Data]), iccp.Crc);

        // WebP.
        var webp = ImageMetadata.Strip(Riff(
            WebpChunk("VP8X", [0x20, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]),
            WebpChunk("ICCP", icc),
            WebpChunk("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, 0x2C, 0x01, 0x90, 0x00])), "image/webp", Ct)!;
        Assert.Equal(BlankedIccProfile(), WebpChunks(webp).Single(c => c.FourCc == "ICCP").Data);
    }

    private static readonly byte[] Vp8L = WebpChunk("VP8L", [0x2F, 0x2B, 0xC1, 0x23, 0x00]);

    [Fact]
    public void Webp_animation_keeps_only_its_frames()
    {
        byte[] frameHeader = [0, 0, 0, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0, 100, 0, 0, 0];
        var input = Riff(
            WebpChunk("VP8X", [0xC3, 0xFF, 0xFF, 0xFF, 0x2B, 0x01, 0, 0x8F, 0, 0]), // animated, and every reserved bit set
            WebpChunk("ANIM", [0, 0, 0, 0, 0, 0]),
            WebpChunk("ANMF", [.. frameHeader, .. WebpChunk("ABCD", "Alice"u8), .. Vp8L, .. WebpChunk("XMP ", "<x:xmpmeta>Alice</x:xmpmeta>"u8)]));

        var output = ImageMetadata.Strip(input, "image/webp", Ct)!;

        Assert.Equal(Riff(
            WebpChunk("VP8X", [0x02, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]),
            WebpChunk("ANIM", [0, 0, 0, 0, 0, 0]),
            WebpChunk("ANMF", [.. frameHeader, .. Vp8L])), output);
    }

    [Fact]
    public void Webp_with_a_misplaced_or_odd_vp8x_or_no_frame_is_refused()
    {
        byte[] vp8x = WebpChunk("VP8X", [0, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]);
        Assert.NotNull(ImageMetadata.Strip(Riff(vp8x, Vp8L), "image/webp", Ct));

        Assert.Null(ImageMetadata.Strip(Riff(Vp8L, vp8x), "image/webp", Ct));
        Assert.Null(ImageMetadata.Strip(Riff(WebpChunk("VP8X", [0, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0, 0]), Vp8L), "image/webp", Ct));
        Assert.Null(ImageMetadata.Strip(Riff(vp8x, WebpChunk("ICCP", IccProfile())), "image/webp", Ct));
        Assert.Null(ImageMetadata.Strip(Riff(vp8x, WebpChunk("ANMF", new byte[16])), "image/webp", Ct));
    }

    [Fact]
    public void Gif_keeps_only_frame_controls_and_the_loop()
    {
        byte[] head = [.. "GIF89a"u8, 2, 0, 1, 0, 0x80, 0, 0, 0, 0, 0, 0xFF, 0xFF, 0xFF];
        byte[] text = [0x21, 0x01, 12, .. new byte[12], 5, .. "Alice"u8, 0];
        byte[] unknown = [0x21, 0x99, 5, .. "Alice"u8, 0];
        byte[] longControl = [0x21, 0xF9, 9, .. "Alice!!!!"u8, 0];
        byte[] control = [0x21, 0xF9, 4, 0, 0, 0, 0, 0];
        byte[] image = [0x2C, 0, 0, 0, 0, 2, 0, 1, 0, 0, 2, 2, 0x44, 0x01, 0];
        byte[] input =
        [
            .. head,
            0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 5, .. "Alice"u8, 0,
            .. text, .. unknown, .. longControl, .. control, .. image,
            0x21, 0xFF, 11, .. "ANIMEXTS1.0"u8, 5, .. "Alice"u8, 0,
            0x3B,
        ];

        var output = ImageMetadata.Strip(input, "image/gif", Ct);

        Assert.Equal([.. head, 0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 0, .. control, .. image, 0x3B], output);
        Assert.Null(ImageMetadata.Strip([.. head, .. control, 0x3B], "image/gif", Ct));
    }

    [Fact]
    public void Png_with_a_wrong_crc_or_a_second_header_is_refused()
    {
        byte[] idat = PngChunk("IDAT", [0x78, 0x9C]);
        byte[] text = PngChunk("tEXt", "Author\0Alice"u8);
        text[^1] ^= 1;

        Assert.Null(ImageMetadata.Strip(Png(Ihdr, text, idat, PngChunk("IEND", [])), "image/png", Ct));
        Assert.Null(ImageMetadata.Strip(Png(Ihdr, Ihdr, idat, PngChunk("IEND", [])), "image/png", Ct));
    }

    /// <summary>An SVG as Inkscape saves it: a DTD, comments, its own namespaces with the file's name and the
    /// export folder on the author's disk, and RDF metadata naming the author.</summary>
    internal const string InkscapeSvg = """
        <?xml version="1.0" encoding="UTF-8" standalone="no"?>
        <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
        <!-- Created with Inkscape by Alice -->
        <?xml-stylesheet href="style.css"?>
        <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink"
             xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape" xmlns:sodipodi="http://sodipodi.sourceforge.net/DTD/sodipodi-0.dtd"
             xmlns:rdf="http://www.w3.org/1999/02/22-rdf-syntax-ns#" xmlns:dc="http://purl.org/dc/elements/1.1/"
             width="200" height="100" viewBox="0 0 200 100" sodipodi:docname="plan-alice.svg" inkscape:export-filename="/home/alice/plans/plan.png">
          <sodipodi:namedview id="view" inkscape:zoom="2"/>
          <metadata><rdf:RDF><dc:creator>Alice Example</dc:creator></rdf:RDF></metadata>
          <title>Pump plan</title>
          <style>rect { fill: #36c; }</style>
          <defs><linearGradient id="g"><stop offset="0" stop-color="red"/></linearGradient></defs>
          <g inkscape:label="Layer 1" inkscape:groupmode="layer"><rect width="200" height="100" xml:space="preserve"/><use xlink:href="#g"/></g>
          <text x="10" y="50">Pump <tspan>A</tspan></text>
        </svg>
        """;

    [Fact]
    public void Svg_keeps_its_drawing_and_loses_comments_metadata_and_foreign_namespaces()
    {
        var stripped = Encoding.UTF8.GetString(ImageMetadata.Strip(Encoding.UTF8.GetBytes(InkscapeSvg), "image/svg+xml", Ct)!);

        foreach (var gone in new[] { "Alice", "alice", "DOCTYPE", "<!--", "xml-stylesheet", "inkscape", "sodipodi", "rdf", "metadata", "purl.org" })
        {
            Assert.DoesNotContain(gone, stripped, StringComparison.Ordinal);
        }

        foreach (var kept in new[] { "<title>Pump plan</title>", "rect { fill: #36c; }", "viewBox=\"0 0 200 100\"", "xlink:href=\"#g\"", "stop-color=\"red\"", "xml:space=\"preserve\"", "Pump <tspan>A</tspan>" })
        {
            Assert.Contains(kept, stripped, StringComparison.Ordinal);
        }

        Assert.Null(Kumbu.Core.Attachments.SvgImage.Problem("x.svg", Encoding.UTF8.GetBytes(stripped)));
        Assert.Equal((200, 100), Kumbu.Core.Attachments.SvgImage.Size(Encoding.UTF8.GetBytes(stripped)));
    }

    [Fact]
    public void Svg_without_a_namespace_keeps_its_elements()
    {
        var stripped = Encoding.UTF8.GetString(ImageMetadata.Strip("<svg width=\"4\" height=\"4\"><!-- x --><rect width=\"4\" height=\"4\"/></svg>"u8, "image/svg+xml", Ct)!);

        Assert.Equal("<svg width=\"4\" height=\"4\"><rect width=\"4\" height=\"4\" /></svg>", stripped);
    }

    [Fact]
    public void Svg_keeps_only_references_inside_itself_and_strips_the_images_it_embeds()
    {
        var photo = Convert.ToBase64String(JpegWithGps(6));
        var inner = Convert.ToBase64String(Encoding.UTF8.GetBytes(InkscapeSvg));
        var svg = $"""
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink">
              <image id="linked" xlink:href="/home/alice/plans/photo.png" width="4" height="4"/>
              <image id="file" href="file:///home/alice/photo.png"/>
              <image id="photo" href="data:image/jpeg;base64,{photo}"/>
              <image id="drawing" href="data:image/svg+xml;base64,{inner}"/>
              <image id="broken" href="data:image/png;base64,AAAA"/>
              <image id="plain" href="data:image/svg+xml,%3Csvg%2F%3E"/>
              <filter id="f"><feImage href="http://example.org/a.png"/></filter>
              <use id="local" xlink:href="#linked"/>
            </svg>
            """;

        var stripped = XDocument.Parse(Encoding.UTF8.GetString(ImageMetadata.Strip(Encoding.UTF8.GetBytes(svg), "image/svg+xml", Ct)!));

        string? Href(string id) => stripped.Descendants().Single(e => (string?)e.Attribute("id") == id).Attributes().SingleOrDefault(a => a.Name.LocalName == "href")?.Value;
        foreach (var gone in new[] { "linked", "file", "broken", "plain" }) Assert.Null(Href(gone));
        Assert.Null(stripped.Descendants().Single(e => e.Name.LocalName == "feImage").Attribute("href"));
        Assert.Equal("#linked", Href("local"));
        Assert.Equal(ImageMetadata.Strip(JpegWithGps(6), "image/jpeg", Ct), Convert.FromBase64String(Href("photo")!["data:image/jpeg;base64,".Length..]));
        Assert.Equal(ImageMetadata.Strip(Encoding.UTF8.GetBytes(InkscapeSvg), "image/svg+xml", Ct), Convert.FromBase64String(Href("drawing")!["data:image/svg+xml;base64,".Length..]));
    }

    [Fact]
    public void Svg_keeps_its_shapes_in_order_with_comments_and_editor_elements_between_them()
    {
        var svg = """<svg xmlns="http://www.w3.org/2000/svg"><rect id="a"/><!--c--><inkscape:x xmlns:inkscape="http://www.inkscape.org/namespaces/inkscape"/><circle id="b"/><metadata>m</metadata><g><!--d--><path id="c"/><?pi x?></g></svg>""";

        var stripped = Encoding.UTF8.GetString(ImageMetadata.Strip(Encoding.UTF8.GetBytes(svg), "image/svg+xml", Ct)!);

        Assert.Equal("""<svg xmlns="http://www.w3.org/2000/svg"><rect id="a" /><circle id="b" /><g><path id="c" /></g></svg>""", stripped);
    }

    public static TheoryData<string, string> Drawings => new()
    {
        {
            InkscapeSvg,
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"200\" height=\"100\" viewBox=\"0 0 200 100\">\n"
            + "  \n  \n  <title>Pump plan</title>\n  <style>rect { fill: #36c; }</style>\n"
            + "  <defs><linearGradient id=\"g\"><stop offset=\"0\" stop-color=\"red\" /></linearGradient></defs>\n"
            + "  <g><rect width=\"200\" height=\"100\" xml:space=\"preserve\" /><use xlink:href=\"#g\" /></g>\n"
            + "  <text x=\"10\" y=\"50\">Pump <tspan>A</tspan></text>\n</svg>"
        },
        {
            """
            <svg:svg xmlns:svg="http://www.w3.org/2000/svg" width="10" height="10">
              <svg:style><![CDATA[ .a > b { fill: red } ]]></svg:style>
              <svg:g>
                <!-- one -->
                <svg:text xml:space="preserve">Tom &amp; Jerry <!-- two -->&lt;3</svg:text>
                <svg:metadata><svg:g/></svg:metadata>
                <svg:g><svg:g><?editor keep?><svg:rect width="1" height="1"/></svg:g></svg:g>
              </svg:g>
            </svg:svg>
            """,
            "<svg:svg xmlns:svg=\"http://www.w3.org/2000/svg\" width=\"10\" height=\"10\">\n"
            + "  <svg:style><![CDATA[ .a > b { fill: red } ]]></svg:style>\n  <svg:g>\n    \n"
            + "    <svg:text xml:space=\"preserve\">Tom &amp; Jerry &lt;3</svg:text>\n    \n"
            + "    <svg:g><svg:g><svg:rect width=\"1\" height=\"1\" /></svg:g></svg:g>\n  </svg:g>\n</svg:svg>"
        },
        {
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 4 4\">\n"
            + "\t<defs>\n\t\t<path id=\"p\" d=\"M0 0h4v4z\"/>\n\t</defs>\n\t<use xlink:href=\"#p\" href=\"#p\"/>\n"
            + "\t<image href=\"data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjwhLS0geCAtLT48cmVjdC8+PC9zdmc+\"/>\n"
            + "\t<foreignObject width=\"4\" height=\"4\"><div xmlns=\"http://www.w3.org/1999/xhtml\">Hi</div></foreignObject>\n"
            + "\t<x:extra xmlns:x=\"urn:x\"><rect/></x:extra>\n</svg>",
            "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" viewBox=\"0 0 4 4\">\n"
            + "\t<defs>\n\t\t<path id=\"p\" d=\"M0 0h4v4z\" />\n\t</defs>\n\t<use xlink:href=\"#p\" href=\"#p\" />\n"
            + "\t<image href=\"data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciPjxyZWN0IC8+PC9zdmc+\" />\n"
            + "\t<foreignObject width=\"4\" height=\"4\" />\n\t\n</svg>"
        },
        { """<svg xmlns="http://www.w3.org/2000/svg"><rect/>plain text</svg>""", """<svg xmlns="http://www.w3.org/2000/svg"><rect />plain text</svg>""" },
    };

    [Theory]
    [MemberData(nameof(Drawings))]
    public void Svg_strips_to_exactly_its_drawing(string svg, string expected)
    {
        Assert.Equal(expected, Encoding.UTF8.GetString(ImageMetadata.Strip(Encoding.UTF8.GetBytes(svg), "image/svg+xml", Ct)!));
    }

    [Fact]
    public void Stripping_an_svg_stops_when_it_is_given_up()
    {
        using var given = new CancellationTokenSource();
        given.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => ImageMetadata.Strip(Encoding.UTF8.GetBytes(InkscapeSvg), "image/svg+xml", given.Token));
    }

    [Fact]
    public void What_isnt_an_svg_strips_to_nothing()
    {
        Assert.Null(ImageMetadata.Strip("<html/>"u8, "image/svg+xml", Ct));
        Assert.Null(ImageMetadata.Strip("<svg"u8, "image/svg+xml", Ct));
    }
}
