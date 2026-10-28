// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using Kumbu.Server.Attachments;

namespace Kumbu.Server.Tests.Attachments;

public class ImageHeaderTests
{
    /// <summary>A PNG that is only signature and IHDR: the header claims a size, no pixel data follows.</summary>
    private static MemoryStream PngClaiming(int width, int height)
    {
        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(0), width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), height);
        ihdr[8] = 8; // bit depth
        ihdr[9] = 6; // RGBA

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        WriteChunk(png, "IHDR"u8, ihdr);
        WriteChunk(png, "IEND"u8, []);
        png.Position = 0;
        return png;
    }

    private static void WriteChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        s.Write(length);
        s.Write(type);
        s.Write(data);
        Span<byte> checksum = stackalloc byte[4]; // the header reader doesn't verify it
        s.Write(checksum);
    }

    [Fact]
    public void A_header_claiming_100000_by_100000_is_refused_without_decoding()
    {
        using var png = PngClaiming(100_000, 100_000);
        Assert.True(png.Length < 100);

        Assert.Null(ImageHeader.Read(png, out var problem));
        Assert.Contains("16,384 px", problem);
    }

    [Fact]
    public void A_50_megapixel_limit_counts_width_times_height()
    {
        using var over = PngClaiming(10_000, 5_001);
        Assert.Null(ImageHeader.Read(over, out var problem));
        Assert.Contains("50 megapixels", problem);

        using var exactly = PngClaiming(10_000, 5_000);
        Assert.Equal((10_000, 5_000), ImageHeader.Read(exactly, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void A_plain_image_reports_its_size()
    {
        using var png = PngClaiming(640, 480);
        Assert.Equal((640, 480), ImageHeader.Read(png, out var problem));
        Assert.Null(problem);
    }

    private static byte[] Be16(int v) => [(byte)(v >> 8), (byte)v];

    /// <summary>SOI, an Exif APP1 with the given orientation, SOF0 and EOI: all a header reader looks at.</summary>
    private static MemoryStream Jpeg(int width, int height, int orientation, bool littleEndian)
    {
        byte[] U16(int v) => littleEndian ? [(byte)v, (byte)(v >> 8)] : Be16(v);
        byte[] U32(int v) => littleEndian ? [(byte)v, (byte)(v >> 8), 0, 0] : [0, 0, (byte)(v >> 8), (byte)v];

        var tiff = new List<byte>();
        tiff.AddRange(littleEndian ? "II"u8.ToArray() : "MM"u8.ToArray());
        tiff.AddRange(U16(42));
        tiff.AddRange(U32(8));
        tiff.AddRange(U16(1)); // one entry
        tiff.AddRange(U16(0x0112));
        tiff.AddRange(U16(3)); // SHORT
        tiff.AddRange(U32(1));
        tiff.AddRange(U16(orientation));
        tiff.AddRange(new byte[] { 0, 0 });
        tiff.AddRange(U32(0)); // no next IFD

        var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8]);
        jpeg.Write([0xFF, 0xE1]);
        jpeg.Write(Be16(2 + 6 + tiff.Count));
        jpeg.Write("Exif\0\0"u8);
        jpeg.Write(tiff.ToArray());
        jpeg.Write([0xFF, 0xC4, 0x00, 0x04, 0xFF, 0xFF]); // a DHT-like segment that is not a frame header
        jpeg.Write([0xFF, 0xC0]);
        jpeg.Write(Be16(11));
        jpeg.Write([8]);
        jpeg.Write(Be16(height));
        jpeg.Write(Be16(width));
        jpeg.Write([1, 1, 0x11, 0]);
        jpeg.Write([0xFF, 0xD9]);
        jpeg.Position = 0;
        return jpeg;
    }

    [Theory]
    [InlineData(1, 40, 20, true)]
    [InlineData(3, 40, 20, false)]
    [InlineData(5, 20, 40, true)]
    [InlineData(6, 20, 40, true)]
    [InlineData(6, 20, 40, false)]
    [InlineData(7, 20, 40, false)]
    [InlineData(8, 20, 40, true)]
    public void An_exif_orientation_decides_the_displayed_dimensions(int orientation, int width, int height, bool littleEndian)
    {
        using var jpeg = Jpeg(40, 20, orientation, littleEndian);
        Assert.Equal((width, height), ImageHeader.Read(jpeg, out var problem));
        Assert.Null(problem);
    }

    [Fact]
    public void A_jpeg_whose_frame_header_follows_300_KB_of_segments_reports_its_size()
    {
        // A segment holds at most 64 KB: an ICC profile or XMP that large comes in several (APP2, APP1), as from
        // Photoshop; the frame header follows them all.
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        var payload = 0;
        while (payload < 300 * 1024)
        {
            var data = new byte[65_533];
            jpeg.AddRange([0xFF, (byte)(payload == 0 ? 0xE1 : 0xE2), .. Be16(data.Length + 2), .. data]);
            payload += data.Length;
        }

        jpeg.AddRange([0xFF, 0xC0, .. Be16(11), 8, .. Be16(3_000), .. Be16(4_000), 1, 1, 0x11, 0, 0xFF, 0xD9]);
        Assert.True(jpeg.Count > 300 * 1024);

        Assert.Equal((4_000, 3_000), ImageHeader.Read(jpeg.ToArray(), out var problem));
        Assert.Null(problem);
        // A stream that isn't in memory is read to its end too.
        using var stream = new BufferedStream(new MemoryStream(jpeg.ToArray()));
        Assert.Equal((4_000, 3_000), ImageHeader.Read(stream, out _));
    }

    [Fact]
    public void A_jpeg_cut_off_before_its_frame_header_is_unreadable()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE1, .. Be16(65_535), .. new byte[1_000]];
        Assert.Null(ImageHeader.Read(jpeg, out var problem));
        Assert.Equal("This image can't be read", problem);
    }

    [Fact]
    public void A_jpeg_without_exif_reports_the_stored_size()
    {
        using var jpeg = new MemoryStream([0xFF, 0xD8, 0xFF, 0xC2, 0, 11, 8, 0, 20, 0, 40, 1, 1, 0x11, 0, 0xFF, 0xD9]);
        Assert.Equal((40, 20), ImageHeader.Read(jpeg, out _));
    }

    [Fact]
    public void A_gif_reports_its_logical_screen()
    {
        using var gif = new MemoryStream([.. "GIF89a"u8, 0x2C, 0x01, 0x90, 0x00, 0, 0, 0]);
        Assert.Equal((300, 144), ImageHeader.Read(gif, out _));
    }

    [Fact]
    public void A_webp_reports_its_size_in_each_flavour()
    {
        byte[] Riff(string fourcc, byte[] payload) =>
            [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBP"u8, .. System.Text.Encoding.ASCII.GetBytes(fourcc), (byte)payload.Length, 0, 0, 0, .. payload];

        using var lossy = new MemoryStream(Riff("VP8 ", [0, 0, 0, 0x9D, 0x01, 0x2A, 0x2C, 0x01, 0x90, 0x00]));
        Assert.Equal((300, 144), ImageHeader.Read(lossy, out _));

        // VP8L: signature, then 14 bits of width-1 (299) and 14 bits of height-1 (143).
        var bits = 299u | 143u << 14;
        using var lossless = new MemoryStream(Riff("VP8L", [0x2F, (byte)bits, (byte)(bits >> 8), (byte)(bits >> 16), (byte)(bits >> 24)]));
        Assert.Equal((300, 144), ImageHeader.Read(lossless, out _));

        using var extended = new MemoryStream(Riff("VP8X", [0, 0, 0, 0, 0x2B, 0x01, 0, 0x8F, 0, 0]));
        Assert.Equal((300, 144), ImageHeader.Read(extended, out _));
    }

    [Fact]
    public void Truncated_or_garbled_headers_are_unreadable_not_exceptions()
    {
        foreach (var bytes in new byte[][]
        {
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0],
            [0xFF, 0xD8, 0xFF],
            [0xFF, 0xD8, 0xFF, 0xE1, 0x00, 0x20, .. "Exif\0\0MM"u8],
            [0xFF, 0xD8, 0xFF, 0xC0, 0x00, 0x0B, 8, 0],
            [.. "GIF89a"u8, 1],
            [.. "RIFF"u8, 0, 0, 0, 0, .. "WEBPVP8X"u8, 0, 0],
            [],
        })
        {
            using var stream = new MemoryStream(bytes);
            Assert.Null(ImageHeader.Read(stream, out var problem));
            Assert.Equal("This image can't be read", problem);
        }
    }

    [Fact]
    public void A_malformed_exif_block_falls_back_to_the_stored_size()
    {
        // The IFD offset points far outside the block.
        byte[] exif = [.. "Exif\0\0MM"u8, 0, 42, 0xFF, 0xFF, 0xFF, 0xFF];
        using var jpeg = new MemoryStream([0xFF, 0xD8, 0xFF, 0xE1, 0, (byte)(2 + exif.Length), .. exif,
            0xFF, 0xC0, 0, 11, 8, 0, 20, 0, 40, 1, 1, 0x11, 0, 0xFF, 0xD9]);
        Assert.Equal((40, 20), ImageHeader.Read(jpeg, out _));
    }

    [Fact]
    public void A_jpeg_whose_frame_header_never_comes_is_unreadable()
    {
        // Endless comment segments: the walk goes through the whole file, by the segments' lengths, and finds none.
        var jpeg = new MemoryStream();
        jpeg.Write([0xFF, 0xD8, 0xFF]);
        while (jpeg.Length < 1024 * 1024)
        {
            jpeg.Write([0xFE, 0xFF, 0xFF]);
            jpeg.Write(new byte[0xFFFD]);
            jpeg.Write([0xFF]);
        }

        jpeg.Position = 0;
        Assert.Null(ImageHeader.Read(jpeg, out var problem));
        Assert.Equal("This image can't be read", problem);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_are_unreadable()
    {
        using var junk = new MemoryStream("not an image at all"u8.ToArray());
        Assert.Null(ImageHeader.Read(junk, out var problem));
        Assert.Equal("This image can't be read", problem);
    }
}
