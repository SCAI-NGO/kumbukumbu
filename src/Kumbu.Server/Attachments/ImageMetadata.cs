// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.IO.Compression;

namespace Kumbu.Server.Attachments;

/// <summary>
/// Takes the metadata out of an image before it leaves the vault: EXIF (GPS position, device, names), XMP, IPTC,
/// comments, text and thumbnails. What is kept is listed, not what goes, so anything unknown goes too. An orientation
/// other than 1 is kept as a minimal EXIF, so the image doesn't turn; an ICC profile is kept for its colours, with the
/// header fields that say who made it, on what and when zeroed. The pixels are never decoded: the walk copies the
/// segments, chunks or blocks it keeps byte for byte.
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

    /// <summary>The ICC header's date and time, device manufacturer and model, creator and profile ID.</summary>
    private static readonly (int Start, int End)[] IccNames = [(24, 36), (48, 56), (80, 100)];

    /// <summary>Zeroes <see cref="IccNames"/> in a piece of a profile that starts at byte <paramref name="at"/> of it.</summary>
    private static void BlankIcc(Span<byte> piece, int at)
    {
        foreach (var (start, end) in IccNames)
        {
            var from = Math.Max(start, at);
            var to = Math.Min(end, at + piece.Length);
            if (from < to)
            {
                piece[(from - at)..(to - at)].Clear();
            }
        }
    }

    private static byte[]? Jpeg(ReadOnlySpan<byte> h)
    {
        if (h.Length < 4 || h[0] != 0xFF || h[1] != 0xD8)
        {
            return null;
        }

        var lead = new List<byte[]>(); // the APP0 right after SOI, before which nothing else is kept
        var body = new List<byte[]>();
        var icc = new List<(byte Sequence, byte[] Segment)>();
        var orientation = 1;
        var frames = 0;
        var scans = 0;
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
                body.Add([0xFF, 0xD9]);
                break; // whatever follows EOI goes
            }

            if (marker is >= 0xD0 and <= 0xD7)
            {
                body.Add([0xFF, marker]);
                continue;
            }

            if (marker == 0x01)
            {
                continue; // TEM carries no length, and isn't listed
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

            // As ImageHeader reads it (the last Exif before the first scan), so the displayed size stays the same.
            if (scans == 0 && marker == 0xE1 && data.StartsWith("Exif\0\0"u8))
            {
                orientation = ImageHeader.ExifOrientation(data[6..]);
            }

            if (Kept(marker, data, segment) is { } kept)
            {
                (marker == 0xE0 && body.Count == 0 ? lead : body).Add(kept);
                if (marker == 0xE2)
                {
                    icc.Add((data[12], kept));
                }
            }

            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                frames++;
            }

            if (marker == 0xDA)
            {
                // The entropy-coded data runs to the next marker that is neither a stuffed FF 00 nor a restart.
                scans++;
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

                body.Add(h[start..pos].ToArray());
            }
        }

        if (frames == 0 || scans == 0)
        {
            return null;
        }

        // A profile may be split over several APP2s, in the order of their sequence numbers; its header is in the first.
        var offset = 0;
        foreach (var (_, chunk) in icc.OrderBy(c => c.Sequence))
        {
            var piece = chunk.AsSpan(4 + 14); // marker, length, ICC_PROFILE\0, sequence number and count
            BlankIcc(piece, offset);
            offset += piece.Length;
        }

        using var output = new MemoryStream();
        output.Write([0xFF, 0xD8]);
        lead.ForEach(b => output.Write(b));
        if (Turns(orientation))
        {
            output.Write([0xFF, 0xE1, 0x00, 2 + 6 + 26]);
            output.Write("Exif\0\0"u8);
            output.Write(MinimalExif(orientation));
        }

        body.ForEach(b => output.Write(b));
        return output.ToArray();
    }

    /// <summary>The segment as it is copied, or null if it goes.</summary>
    private static byte[]? Kept(byte marker, ReadOnlySpan<byte> data, ReadOnlySpan<byte> segment) => marker switch
    {
        // JFIF's fixed part, without its thumbnail; JFXX holds only thumbnails.
        0xE0 when data.Length >= 14 && data.StartsWith("JFIF\0"u8) => [0xFF, 0xE0, 0x00, 16, .. data[..12], 0, 0],
        0xE2 when data.Length >= 14 && data.StartsWith("ICC_PROFILE\0"u8) => segment.ToArray(),
        // Adobe's version, flags and colour transform.
        0xEE when data.Length >= 12 && data.StartsWith("Adobe"u8) => [0xFF, 0xEE, 0x00, 14, .. data[..12]],
        // SOFn but JPG, DHT, DAC; SOS, DQT, DNL, DRI, EXP.
        >= 0xC0 and <= 0xCF and not 0xC8 or 0xDA or 0xDB or 0xDC or 0xDD or 0xDF => segment.ToArray(),
        _ => null,
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

    /// <summary>The most an iCCP chunk may inflate to.</summary>
    private const int MaxProfile = 16 * 1024 * 1024;

    private static byte[]? Png(ReadOnlySpan<byte> h)
    {
        if (!h.StartsWith(PngSignature))
        {
            return null;
        }

        var kept = new List<(int Start, int Length, bool Idat, byte[]? Iccp)>();
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

            var data = h.Slice(pos + 8, (int)length);
            if (BinaryPrimitives.ReadUInt32BigEndian(h[(pos + 8 + (int)length)..]) != Crc32.Of(typeBytes, data))
            {
                return null;
            }

            var type = System.Text.Encoding.ASCII.GetString(typeBytes);
            if ((pos == PngSignature.Length) != (type == "IHDR"))
            {
                return null; // IHDR comes first, and only once
            }

            if (typeBytes[0] is >= (byte)'A' and <= (byte)'Z' && !PngCritical.Contains(type))
            {
                return null; // a decoder may not skip a critical chunk it doesn't know, nor may we
            }

            if (type == "eXIf")
            {
                orientation = ImageHeader.ExifOrientation(data);
            }

            if (PngKept.Contains(type))
            {
                byte[]? iccp = null;
                if (type == "iCCP" && (iccp = Iccp(data)) is null)
                {
                    return null;
                }

                kept.Add((pos, (int)length + 12, type == "IDAT", iccp));
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
        foreach (var (start, length, idat, iccp) in kept)
        {
            if (idat && !exifWritten)
            {
                WritePngChunk(output, "eXIf"u8, MinimalExif(orientation));
                exifWritten = true;
            }

            if (iccp is not null)
            {
                WritePngChunk(output, "iCCP"u8, iccp);
            }
            else
            {
                output.Write(h.Slice(start, length));
            }
        }

        return output.ToArray();
    }

    /// <summary>An iCCP chunk's data under the name <c>ICC profile</c>, its profile blanked; null if it doesn't inflate.</summary>
    private static byte[]? Iccp(ReadOnlySpan<byte> data)
    {
        var name = data[..Math.Min(data.Length, 80)].IndexOf((byte)0);
        if (name < 1 || name + 2 > data.Length || data[name + 1] != 0)
        {
            return null; // a name of 1 to 79 bytes, then compression method 0 (zlib)
        }

        byte[] profile;
        try
        {
            using var z = new ZLibStream(new MemoryStream(data[(name + 2)..].ToArray()), CompressionMode.Decompress);
            using var plain = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = z.Read(buffer)) > 0)
            {
                if (plain.Length + n > MaxProfile)
                {
                    return null;
                }

                plain.Write(buffer, 0, n);
            }

            profile = plain.ToArray();
        }
        catch (InvalidDataException)
        {
            return null;
        }

        BlankIcc(profile, 0);
        using var output = new MemoryStream();
        output.Write("ICC profile\0\0"u8);
        using (var z = new ZLibStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            z.Write(profile);
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

    /// <summary>The RIFF chunks from <paramref name="pos"/> to the end of <paramref name="h"/>, as (start, data
    /// length); null if one runs past the end.</summary>
    private static List<(int Start, int Length)>? RiffChunks(ReadOnlySpan<byte> h, int pos)
    {
        var chunks = new List<(int, int)>();
        while (pos < h.Length)
        {
            if (pos + 8 > h.Length)
            {
                return null;
            }

            var length = BinaryPrimitives.ReadUInt32LittleEndian(h[(pos + 4)..]);
            var padded = (long)length + (length & 1);
            if (padded > h.Length - pos - 8)
            {
                return null;
            }

            chunks.Add((pos, (int)length));
            pos += 8 + (int)padded;
        }

        return chunks;
    }

    /// <summary>A RIFF chunk with its pad byte, if odd.</summary>
    private static byte[] RiffChunk(ReadOnlySpan<byte> fourcc, ReadOnlySpan<byte> data)
    {
        var chunk = new byte[8 + data.Length + (data.Length & 1)];
        fourcc.CopyTo(chunk);
        BinaryPrimitives.WriteInt32LittleEndian(chunk.AsSpan(4), data.Length);
        data.CopyTo(chunk.AsSpan(8));
        return chunk;
    }

    private static bool IsFrame(ReadOnlySpan<byte> fourcc) => fourcc.SequenceEqual("VP8 "u8) || fourcc.SequenceEqual("VP8L"u8);

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
        if (RiffChunks(riff, 12) is not { } chunks)
        {
            return null;
        }

        var kept = new List<byte[]>();
        var vp8x = -1;
        var frame = false;
        var orientation = 1;
        foreach (var (start, length) in chunks)
        {
            var fourcc = riff.Slice(start, 4);
            var data = riff.Slice(start + 8, length);
            if (fourcc.SequenceEqual("VP8X"u8))
            {
                if (length != 10 || start != 12)
                {
                    return null; // VP8X is 10 bytes and comes first
                }

                vp8x = kept.Count;
                kept.Add(RiffChunk(fourcc, [data[0], 0, 0, 0, .. data[4..]]));
            }
            else if (fourcc.SequenceEqual("EXIF"u8))
            {
                orientation = ImageHeader.ExifOrientation(data.StartsWith("Exif\0\0"u8) ? data[6..] : data);
            }
            else if (fourcc.SequenceEqual("ICCP"u8))
            {
                var profile = data.ToArray();
                BlankIcc(profile, 0);
                kept.Add(RiffChunk(fourcc, profile));
            }
            else if (fourcc.SequenceEqual("ANMF"u8))
            {
                // The frame's 16-byte header, then its own chunks, of which only the image's are kept.
                if (length < 16 || RiffChunks(data, 16) is not { } inner)
                {
                    return null;
                }

                using var anmf = new MemoryStream();
                anmf.Write(data[..16]);
                foreach (var (innerStart, innerLength) in inner)
                {
                    var innerFourcc = data.Slice(innerStart, 4);
                    if (IsFrame(innerFourcc) || innerFourcc.SequenceEqual("ALPH"u8))
                    {
                        frame |= IsFrame(innerFourcc);
                        anmf.Write(RiffChunk(innerFourcc, data.Slice(innerStart + 8, innerLength)));
                    }
                }

                kept.Add(RiffChunk(fourcc, anmf.ToArray()));
            }
            else if (IsFrame(fourcc) || fourcc.SequenceEqual("ALPH"u8) || fourcc.SequenceEqual("ANIM"u8))
            {
                frame |= IsFrame(fourcc);
                kept.Add(riff.Slice(start, 8 + length + (length & 1)).ToArray());
            }
        }

        if (!frame)
        {
            return null;
        }

        // Without VP8X no flag can say there is EXIF, so the orientation can't be kept; an EXIF chunk there is invalid anyway.
        var exif = vp8x >= 0 && Turns(orientation);
        if (vp8x >= 0)
        {
            // ICC, alpha and animation stay; EXIF only if written; XMP and the reserved bits go.
            kept[vp8x][8] = (byte)((kept[vp8x][8] & 0x32) | (exif ? 0x08 : 0));
        }

        using var output = new MemoryStream();
        output.Write("RIFF\0\0\0\0WEBP"u8);
        kept.ForEach(b => output.Write(b));
        if (exif)
        {
            output.Write(RiffChunk("EXIF"u8, MinimalExif(orientation)));
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
        var images = 0;
        while (true)
        {
            if (pos >= h.Length)
            {
                return null;
            }

            int end;
            switch (h[pos])
            {
                case 0x3B:
                    if (images == 0)
                    {
                        return null;
                    }

                    output.WriteByte(0x3B);
                    return output.ToArray(); // whatever follows the trailer goes
                case 0x2C:
                    if (pos + 10 > h.Length)
                    {
                        return null;
                    }

                    end = SubBlocks(h, pos + 10 + ColourTable(h[pos + 9]) + 1); // and the LZW minimum code size
                    if (end < 0)
                    {
                        return null;
                    }

                    images++;
                    output.Write(h[pos..end]);
                    break;
                case 0x21:
                    end = pos + 2 > h.Length ? -1 : SubBlocks(h, pos + 2);
                    if (end < 0)
                    {
                        return null;
                    }

                    if (h[pos + 1] == 0xF9 && end == pos + 8 && h[pos + 2] == 4)
                    {
                        output.Write(h[pos..end]); // a graphic control: delay, disposal, transparency
                    }
                    else if (h[pos + 1] == 0xFF && h[pos + 2] == 11 && end >= pos + 19 && h[pos + 14] == 3 && h[pos + 15] == 1
                        && (h.Slice(pos + 3, 11).SequenceEqual("NETSCAPE2.0"u8) || h.Slice(pos + 3, 11).SequenceEqual("ANIMEXTS1.0"u8)))
                    {
                        output.Write(h[pos..(pos + 18)]); // the loop count alone
                        output.WriteByte(0);
                    }

                    break;
                default:
                    return null;
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
