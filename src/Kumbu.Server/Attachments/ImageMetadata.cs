// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;

namespace Kumbu.Server.Attachments;

/// <summary>
/// Takes the metadata out of an image before it leaves the vault: EXIF (GPS position, device, names), XMP, IPTC,
/// comments and text. What is kept is listed, not what goes, so anything unknown goes too. An orientation other than 1
/// is kept as a minimal EXIF, so the image doesn't turn. The pixels are never decoded: the walk copies the segments,
/// chunks or blocks it keeps byte for byte.
/// </summary>
public static class ImageMetadata
{
    /// <summary>The image without EXIF, XMP, IPTC, comments and text; an orientation other than 1 kept as a minimal
    /// EXIF. Null if the bytes don't parse as <paramref name="mediaType"/> (image/jpeg, image/png, image/gif,
    /// image/webp) or it is another type. Never throws for malformed content.</summary>
    public static byte[]? Strip(ReadOnlySpan<byte> file, string mediaType) => mediaType.ToLowerInvariant() switch
    {
        "image/jpeg" => Jpeg(file),
        "image/png" => Png(file),
        "image/gif" => Gif(file),
        "image/webp" => WebP(file),
        _ => null,
    };

    /// <summary>A big-endian TIFF block whose IFD0 holds the orientation alone.</summary>
    private static byte[] MinimalExif(int orientation) =>
        [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, 0x00, (byte)orientation, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static bool Turns(int orientation) => orientation is >= 2 and <= 8;

    private static byte[]? Jpeg(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4 || h[0] != 0xFF || h[1] != 0xD8)
        {
            return null;
        }

        using var lead = new MemoryStream(); // the APP0s right after SOI, before which nothing else is kept
        using var body = new MemoryStream();
        var orientation = 1;
        var scanned = false;
        var pos = 2;
        while (true)
        {
            if (pos >= h.Length || h[pos] != 0xFF)
            {
                return null;
            }

            while (pos < h.Length && h[pos] == 0xFF)
            {
                pos++; // fill bytes
            }

            if (pos >= h.Length)
            {
                return null;
            }

            var marker = h[pos++];
            if (marker == 0xD9)
            {
                body.Write([0xFF, 0xD9]);
                break; // whatever follows EOI goes
            }

            if (marker is 0x01 or >= 0xD0 and <= 0xD7)
            {
                body.Write([0xFF, marker]);
                continue;
            }

            if (marker is 0x00 or 0xD8 || pos + 2 > h.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(h[pos..]);
            if (length < 2 || pos + length > h.Length)
            {
                return null;
            }

            var data = h.Slice(pos + 2, length - 2);
            var segment = h.Slice(pos - 2, length + 2);
            pos += length;

            // As ImageHeader reads it, so the displayed size stays the same.
            if (!scanned && marker == 0xE1 && data.StartsWith("Exif\0\0"u8))
            {
                orientation = ImageHeader.ExifOrientation(data[6..]);
            }

            if (Keeps(marker, data))
            {
                (marker == 0xE0 && body.Length == 0 ? lead : body).Write(segment);
            }

            if (marker == 0xDA)
            {
                // The entropy-coded data runs to the next marker that is neither a stuffed FF 00 nor a restart.
                scanned = true;
                var start = pos;
                while (true)
                {
                    if (pos + 1 >= h.Length)
                    {
                        return null;
                    }

                    if (h[pos] == 0xFF && h[pos + 1] != 0x00 && h[pos + 1] is not (>= 0xD0 and <= 0xD7))
                    {
                        break;
                    }

                    pos++;
                }

                body.Write(h[start..pos]);
            }
        }

        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        lead.WriteTo(output);
        if (Turns(orientation))
        {
            output.Write([0xFF, 0xE1, 0x00, 2 + 6 + 26]);
            output.Write("Exif\0\0"u8);
            output.Write(MinimalExif(orientation));
        }

        body.WriteTo(output);
        return output.ToArray();
    }

    private static bool Keeps(byte marker, ReadOnlySpan<byte> data) => marker switch
    {
        0xE0 => data.StartsWith("JFIF\0"u8) || data.StartsWith("JFXX\0"u8),
        0xE2 => data.StartsWith("ICC_PROFILE\0"u8),
        0xEE => data.StartsWith("Adobe"u8),
        >= 0xE0 and <= 0xEF or 0xFE => false,
        _ => true,
    };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static readonly HashSet<string> PngCritical = ["IHDR", "PLTE", "IDAT", "IEND"];

    // cLLi as the plan names it, cLLI as PNG's third edition spells it.
    private static readonly HashSet<string> PngKept =
    [
        "IHDR", "PLTE", "IDAT", "IEND",
        "tRNS", "cHRM", "gAMA", "iCCP", "sBIT", "sRGB", "cICP", "mDCV", "cLLi", "cLLI", "bKGD", "pHYs", "hIST",
        "acTL", "fcTL", "fdAT",
    ];

    private static byte[]? Png(ReadOnlySpan<byte> h)
    {
        if (!h.StartsWith(PngSignature))
        {
            return null;
        }

        var kept = new List<(int Start, int Length, bool Idat)>();
        var orientation = 1;
        var ended = false;
        var pos = PngSignature.Length;
        while (pos < h.Length)
        {
            if (pos + 12 > h.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32BigEndian(h[pos..]);
            if (length > (uint)(h.Length - pos - 12))
            {
                return null;
            }

            var typeBytes = h.Slice(pos + 4, 4);
            foreach (var c in typeBytes)
            {
                if (c is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
                {
                    return null;
                }
            }

            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            if (kept.Count == 0 && type != "IHDR")
            {
                return null;
            }

            if (typeBytes[0] is >= (byte)'A' and <= (byte)'Z' && !PngCritical.Contains(type))
            {
                return null; // a decoder may not skip a critical chunk it doesn't know, nor may we
            }

            if (type == "eXIf")
            {
                orientation = ImageHeader.ExifOrientation(h.Slice(pos + 8, (int)length));
            }

            if (PngKept.Contains(type))
            {
                kept.Add((pos, (int)length + 12, type == "IDAT"));
            }

            pos += (int)length + 12;
            if (type == "IEND")
            {
                ended = true;
                break; // whatever follows IEND goes
            }
        }

        if (!ended || !kept.Exists(c => c.Idat))
        {
            return null;
        }

        using var output = new MemoryStream();
        output.Write(PngSignature);
        var exifWritten = !Turns(orientation);
        foreach (var (start, length, idat) in kept)
        {
            if (idat && !exifWritten)
            {
                WritePngChunk(output, "eXIf"u8, MinimalExif(orientation));
                exifWritten = true;
            }

            output.Write(h.Slice(start, length));
        }

        return output.ToArray();
    }

    private static void WritePngChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> number = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
        s.Write(number);
        s.Write(type);
        s.Write(data);
        BinaryPrimitives.WriteUInt32BigEndian(number, Crc32.Of(type, data));
        s.Write(number);
    }

    /// <summary>PNG's CRC-32 (ISO 3309, polynomial 0xEDB88320, reflected), over a chunk's type and data.</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = Build();

        private static uint[] Build()
        {
            var table = new uint[256];
            for (var n = 0u; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        public static uint Of(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data) => ~Update(Update(0xFFFFFFFFu, type), data);

        private static uint Update(uint crc, ReadOnlySpan<byte> bytes)
        {
            foreach (var b in bytes)
            {
                crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc;
        }
    }

    private static byte[]? WebP(ReadOnlySpan<byte> h)
    {
        if (h.Length < 12 || !h[..4].SequenceEqual("RIFF"u8) || !h.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return null;
        }

        var size = BinaryPrimitives.ReadUInt32LittleEndian(h[4..]);
        if (size < 4 || size > (uint)(h.Length - 8))
        {
            return null;
        }

        var riff = h[..(8 + (int)size)];
        var kept = new List<(int Start, int Length)>();
        var vp8x = -1;
        var orientation = 1;
        var pos = 12;
        while (pos < riff.Length)
        {
            if (pos + 8 > riff.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(riff[(pos + 4)..]);
            var padded = (long)length + (length & 1);
            if (padded > riff.Length - pos - 8)
            {
                return null;
            }

            var fourcc = riff.Slice(pos, 4);
            var data = riff.Slice(pos + 8, (int)length);
            if (fourcc.SequenceEqual("EXIF"u8))
            {
                orientation = ImageHeader.ExifOrientation(data.StartsWith("Exif\0\0"u8) ? data[6..] : data);
            }
            else if (fourcc.SequenceEqual("VP8X"u8))
            {
                if (length < 10 || vp8x >= 0)
                {
                    return null;
                }

                vp8x = kept.Count;
            }

            if (fourcc.SequenceEqual("VP8 "u8) || fourcc.SequenceEqual("VP8L"u8) || fourcc.SequenceEqual("VP8X"u8)
                || fourcc.SequenceEqual("ALPH"u8) || fourcc.SequenceEqual("ANIM"u8) || fourcc.SequenceEqual("ANMF"u8)
                || fourcc.SequenceEqual("ICCP"u8))
            {
                kept.Add((pos, 8 + (int)padded));
            }

            pos += 8 + (int)padded;
        }

        // Without VP8X no flag can say there is EXIF, so the orientation can't be kept; an EXIF chunk there is invalid anyway.
        var exif = vp8x >= 0 && Turns(orientation);
        using var output = new MemoryStream();
        output.Write("RIFF\0\0\0\0WEBP"u8);
        for (var i = 0; i < kept.Count; i++)
        {
            var chunk = riff.Slice(kept[i].Start, kept[i].Length);
            if (i == vp8x)
            {
                var copy = chunk.ToArray();
                copy[8] = (byte)((copy[8] & ~0x0C) | (exif ? 0x08 : 0)); // EXIF and XMP flags
                output.Write(copy);
            }
            else
            {
                output.Write(chunk);
            }
        }

        if (exif)
        {
            output.Write("EXIF\x1A\0\0\0"u8); // 26 bytes, even: no pad
            output.Write(MinimalExif(orientation));
        }

        var bytes = output.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), (uint)(bytes.Length - 8));
        return bytes;
    }

    private static byte[]? Gif(ReadOnlySpan<byte> h)
    {
        if (h.Length < 13 || !(h.StartsWith("GIF87a"u8) || h.StartsWith("GIF89a"u8)))
        {
            return null;
        }

        var pos = 13 + ColourTable(h[10]);
        if (pos > h.Length)
        {
            return null;
        }

        using var output = new MemoryStream();
        output.Write(h[..pos]);
        while (true)
        {
            if (pos >= h.Length)
            {
                return null;
            }

            int end;
            var keep = true;
            switch (h[pos])
            {
                case 0x3B:
                    output.WriteByte(0x3B);
                    return output.ToArray(); // whatever follows the trailer goes
                case 0x2C:
                    if (pos + 10 > h.Length)
                    {
                        return null;
                    }

                    end = SubBlocks(h, pos + 10 + ColourTable(h[pos + 9]) + 1); // and the LZW minimum code size
                    break;
                case 0x21:
                    if (pos + 2 > h.Length)
                    {
                        return null;
                    }

                    end = SubBlocks(h, pos + 2);
                    keep = h[pos + 1] switch
                    {
                        0xFE => false, // comment
                        0xFF => pos + 14 <= h.Length && h[pos + 2] == 11
                            && (h.Slice(pos + 3, 11).SequenceEqual("NETSCAPE2.0"u8) || h.Slice(pos + 3, 11).SequenceEqual("ANIMEXTS1.0"u8)),
                        _ => true,
                    };
                    break;
                default:
                    return null;
            }

            if (end < 0)
            {
                return null;
            }

            if (keep)
            {
                output.Write(h[pos..end]);
            }

            pos = end;
        }
    }

    private static int ColourTable(byte flags) => (flags & 0x80) != 0 ? 3 << ((flags & 7) + 1) : 0;

    /// <summary>Where a run of sub-blocks starting at <paramref name="pos"/> ends, after its terminator; -1 if cut off.</summary>
    private static int SubBlocks(ReadOnlySpan<byte> h, int pos)
    {
        while (pos < h.Length)
        {
            var n = h[pos++];
            if (n == 0)
            {
                return pos;
            }

            pos += n;
        }

        return -1;
    }
}
