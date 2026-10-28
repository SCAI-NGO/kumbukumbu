// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;

namespace Kumbu.Server.Attachments;

/// <summary>
/// Reads an image's size from its header, never its pixels, so a small file claiming a huge canvas is refused before
/// anything is allocated for it. A JPEG's frame header may come after large segments (EXIF, ICC profiles, XMP): the
/// walk skips them by their lengths through the whole file (at most 15 MB, held in memory already), allocating and
/// decoding nothing.
/// </summary>
public static class ImageHeader
{
    public const long MaxPixels = 50_000_000;
    public const int MaxSide = 16_384;

    private const string Unreadable = "This image can't be read";

    /// <summary>As <see cref="Read(ReadOnlySpan{byte}, out string?)"/>, for the rest of a stream (a memory stream's
    /// own buffer; any other stream read to its end, up to the attachments' limit).</summary>
    public static (int Width, int Height)? Read(Stream s, out string? problem)
    {
        if (s is MemoryStream memory && memory.TryGetBuffer(out var buffer))
        {
            return Read(buffer.AsSpan((int)memory.Position), out problem);
        }

        using var copy = new MemoryStream();
        var piece = new byte[64 * 1024];
        int n;
        while (copy.Length <= Kumbu.Core.Attachments.AttachmentFormat.MaxBytes && (n = s.Read(piece, 0, piece.Length)) > 0)
        {
            copy.Write(piece, 0, n);
        }

        return Read(copy.GetBuffer().AsSpan(0, (int)copy.Length), out problem);
    }

    /// <summary>The dimensions as displayed (swapped for EXIF orientations 5 to 8), or null with the reason
    /// the image is refused or unreadable. <paramref name="file"/> is the whole file. Never throws for malformed
    /// content.</summary>
    public static (int Width, int Height)? Read(ReadOnlySpan<byte> file, out string? problem)
    {
        var size = Parse(file);
        if (size is not var (width, height) || width <= 0 || height <= 0)
        {
            problem = Unreadable;
            return null;
        }

        if (width > MaxSide || height > MaxSide)
        {
            problem = $"This image is too large: at most {MaxSide.ToString("N0", CultureInfo.InvariantCulture)} px on each side";
            return null;
        }

        if ((long)width * height > MaxPixels)
        {
            problem = $"This image is too large: at most {MaxPixels / 1_000_000} megapixels";
            return null;
        }

        problem = null;
        return (width, height);
    }

    private static (int Width, int Height)? Parse(ReadOnlySpan<byte> h)
    {
        if (h.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }))
        {
            return Png(h);
        }

        if (h.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF }))
        {
            return Jpeg(h);
        }

        if (h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8))
        {
            return h.Length < 10 ? null : (BinaryPrimitives.ReadUInt16LittleEndian(h[6..]), BinaryPrimitives.ReadUInt16LittleEndian(h[8..]));
        }

        if (h.Length >= 16 && h[..4].SequenceEqual("RIFF"u8) && h.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebP(h);
        }

        return null;
    }

    private static (int, int)? Png(ReadOnlySpan<byte> h)
    {
        // Signature, then the IHDR chunk: length, type, width, height.
        if (h.Length < 24 || !h.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            return null;
        }

        var width = BinaryPrimitives.ReadUInt32BigEndian(h[16..]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(h[20..]);
        return width > int.MaxValue || height > int.MaxValue ? (int.MaxValue, int.MaxValue) : ((int)width, (int)height);
    }

    private static (int, int)? WebP(ReadOnlySpan<byte> h)
    {
        var chunk = h.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            // Frame tag (3 bytes), start code 9D 01 2A, then 14-bit width and height.
            if (h.Length < 30 || h[23] != 0x9D || h[24] != 0x01 || h[25] != 0x2A)
            {
                return null;
            }

            return (BinaryPrimitives.ReadUInt16LittleEndian(h[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(h[28..]) & 0x3FFF);
        }

        if (chunk.SequenceEqual("VP8L"u8))
        {
            // Signature 0x2F, then 14-bit width-1 and height-1.
            if (h.Length < 25 || h[20] != 0x2F)
            {
                return null;
            }

            var bits = BinaryPrimitives.ReadUInt32LittleEndian(h[21..]);
            return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (chunk.SequenceEqual("VP8X"u8))
        {
            // Flags (4 bytes), then 24-bit canvas width-1 and height-1.
            if (h.Length < 30)
            {
                return null;
            }

            return (h[24] | h[25] << 8 | h[26] << 16, h[27] | h[28] << 8 | h[29] << 16) is var (w, ht)
                ? (w + 1, ht + 1)
                : null;
        }

        return null;
    }

    private static (int, int)? Jpeg(ReadOnlySpan<byte> h)
    {
        (int, int)? size = null;
        var orientation = 1;
        var pos = 2;
        while (pos < h.Length)
        {
            if (h[pos] != 0xFF)
            {
                return null;
            }

            while (pos < h.Length && h[pos] == 0xFF)
            {
                pos++;
            }

            if (pos >= h.Length)
            {
                break;
            }

            var marker = h[pos++];
            if (marker is 0x01 or >= 0xD0 and <= 0xD8)
            {
                continue; // standalone markers carry no length
            }

            if (marker is 0xD9 or 0xDA)
            {
                break; // end of image / start of scan: the headers are over
            }

            if (pos + 2 > h.Length)
            {
                break;
            }

            var segment = BinaryPrimitives.ReadUInt16BigEndian(h[pos..]);
            if (segment < 2 || pos + segment > h.Length)
            {
                break; // garbled or cut off
            }

            var data = h.Slice(pos + 2, segment - 2);
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (data.Length < 5)
                {
                    return null;
                }

                size = (BinaryPrimitives.ReadUInt16BigEndian(data[3..]), BinaryPrimitives.ReadUInt16BigEndian(data[1..]));
            }
            else if (marker == 0xE1 && data.StartsWith("Exif\0\0"u8))
            {
                orientation = ExifOrientation(data[6..]);
            }

            pos += segment;
        }

        if (size is not var (w, ht))
        {
            return null;
        }

        return orientation is >= 5 and <= 8 ? (ht, w) : (w, ht);
    }

    /// <summary>IFD0's orientation tag (0x0112) in a TIFF block, 1 when absent or malformed.</summary>
    private static int ExifOrientation(ReadOnlySpan<byte> tiff)
    {
        if (tiff.Length < 8)
        {
            return 1;
        }

        bool little;
        if (tiff[0] == 'I' && tiff[1] == 'I')
        {
            little = true;
        }
        else if (tiff[0] == 'M' && tiff[1] == 'M')
        {
            little = false;
        }
        else
        {
            return 1;
        }

        var ifd = U32(tiff, 4, little);
        if (ifd > tiff.Length - 2)
        {
            return 1;
        }

        var offset = (int)ifd;
        var count = U16(tiff, offset, little);
        for (var i = 0; i < count; i++)
        {
            var entry = offset + 2 + i * 12;
            if (entry + 12 > tiff.Length)
            {
                break;
            }

            if (U16(tiff, entry, little) == 0x0112)
            {
                return U16(tiff, entry + 2, little) == 3 ? U16(tiff, entry + 8, little) : 1; // type SHORT
            }
        }

        return 1;
    }

    private static ushort U16(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt16LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt16BigEndian(b[at..]);

    private static uint U32(ReadOnlySpan<byte> b, int at, bool little) =>
        little ? BinaryPrimitives.ReadUInt32LittleEndian(b[at..]) : BinaryPrimitives.ReadUInt32BigEndian(b[at..]);
}
