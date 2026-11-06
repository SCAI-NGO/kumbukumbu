// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Kumbu.Server.Attachments;

namespace Kumbu.Server.Tests.Attachments;

public class ImageMetadataTests
{
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

        var output = ImageMetadata.Strip(input, "image/jpeg");

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

        var output = ImageMetadata.Strip(input, "image/jpeg")!;

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
            var output = ImageMetadata.Strip(input, "image/jpeg")!;
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

        var output = ImageMetadata.Strip(input, "image/png");

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

        Assert.Null(ImageMetadata.Strip(input, "image/png"));
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
            var output = ImageMetadata.Strip(Webp(orientation), "image/webp");

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

        var output = ImageMetadata.Strip(input, "image/gif");

        Assert.Equal([.. head, .. netscape, .. control, .. image, 0x3B], output);
    }

    [Fact]
    public void Bytes_that_dont_parse_are_refused()
    {
        var jpeg = JpegWithGps(6);
        Assert.Null(ImageMetadata.Strip(jpeg.AsSpan(0, jpeg.AsSpan().LastIndexOf(Eoi)), "image/jpeg"));

        var png = Png(Ihdr, PngChunk("IDAT", [0x78, 0x9C, 1, 2, 3, 4]), PngChunk("IEND", []));
        Assert.NotNull(ImageMetadata.Strip(png, "image/png"));
        Assert.Null(ImageMetadata.Strip(png.AsSpan(0, 8 + 25 + 10), "image/png"));

        var webp = Webp(6);
        Assert.NotNull(ImageMetadata.Strip(webp, "image/webp"));
        byte[] overlong = [.. webp[..4], .. Le32(webp.Length), .. webp[8..]];
        Assert.Null(ImageMetadata.Strip(overlong, "image/webp"));

        Assert.Null(ImageMetadata.Strip("%PDF-1.7\n%Alice"u8, "application/pdf"));
        Assert.Null(ImageMetadata.Strip(png, "image/jpeg"));
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

        var output = ImageMetadata.Strip(input, "image/jpeg");

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

        Assert.Null(ImageMetadata.Strip(withoutFrame, "image/jpeg"));
        Assert.Null(ImageMetadata.Strip(withoutScan, "image/jpeg"));
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

        var output = ImageMetadata.Strip(input, "image/jpeg");

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
        var stripped = ImageMetadata.Strip(jpeg, "image/jpeg")!;
        var chunks = Segments(stripped).Where(s => s.Marker == 0xE2).ToList();
        Assert.Equal(2, chunks.Count);
        Assert.Equal(BlankedIccProfile(), chunks[0].Data[14..].Concat(chunks[1].Data[14..]).ToArray());
        Assert.False(Holds(stripped, "PixelCam"));

        // PNG: the profile inflated, blanked, deflated, under a name of no one's.
        var png = ImageMetadata.Strip(Png(Ihdr, PngChunk("iCCP", [.. "PixelCam profile\0"u8, 0, .. Deflated(icc)]), PngChunk("IDAT", [0x78, 0x9C]), PngChunk("IEND", [])), "image/png")!;
        var iccp = PngChunks(png).Single(c => c.Type == "iCCP");
        Assert.Equal([.. "ICC profile\0"u8, 0], iccp.Data[..13]);
        Assert.Equal(BlankedIccProfile(), Inflated(iccp.Data[13..]));
        Assert.Equal(Crc32([.. "iCCP"u8, .. iccp.Data]), iccp.Crc);

        // WebP.
        var webp = ImageMetadata.Strip(Riff(
            WebpChunk("VP8X", [0x20, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]),
            WebpChunk("ICCP", icc),
            WebpChunk("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, 0x2C, 0x01, 0x90, 0x00])), "image/webp")!;
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

        var output = ImageMetadata.Strip(input, "image/webp")!;

        Assert.Equal(Riff(
            WebpChunk("VP8X", [0x02, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]),
            WebpChunk("ANIM", [0, 0, 0, 0, 0, 0]),
            WebpChunk("ANMF", [.. frameHeader, .. Vp8L])), output);
    }

    [Fact]
    public void Webp_with_a_misplaced_or_odd_vp8x_or_no_frame_is_refused()
    {
        byte[] vp8x = WebpChunk("VP8X", [0, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]);
        Assert.NotNull(ImageMetadata.Strip(Riff(vp8x, Vp8L), "image/webp"));

        Assert.Null(ImageMetadata.Strip(Riff(Vp8L, vp8x), "image/webp"));
        Assert.Null(ImageMetadata.Strip(Riff(WebpChunk("VP8X", [0, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0, 0]), Vp8L), "image/webp"));
        Assert.Null(ImageMetadata.Strip(Riff(vp8x, WebpChunk("ICCP", IccProfile())), "image/webp"));
        Assert.Null(ImageMetadata.Strip(Riff(vp8x, WebpChunk("ANMF", new byte[16])), "image/webp"));
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

        var output = ImageMetadata.Strip(input, "image/gif");

        Assert.Equal([.. head, 0x21, 0xFF, 11, .. "NETSCAPE2.0"u8, 3, 1, 0, 0, 0, .. control, .. image, 0x3B], output);
        Assert.Null(ImageMetadata.Strip([.. head, .. control, 0x3B], "image/gif"));
    }

    [Fact]
    public void Png_with_a_wrong_crc_or_a_second_header_is_refused()
    {
        byte[] idat = PngChunk("IDAT", [0x78, 0x9C]);
        byte[] text = PngChunk("tEXt", "Author\0Alice"u8);
        text[^1] ^= 1;

        Assert.Null(ImageMetadata.Strip(Png(Ihdr, text, idat, PngChunk("IEND", [])), "image/png"));
        Assert.Null(ImageMetadata.Strip(Png(Ihdr, Ihdr, idat, PngChunk("IEND", [])), "image/png"));
    }
}
