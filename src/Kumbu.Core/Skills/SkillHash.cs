// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Core.Skills;

/// <summary>The <c>kumbu-hash</c> of a skill: SHA-256 over its files, so a changed byte, name or file shows.</summary>
public static class SkillHash
{
    /// <summary>"sha256:" and lower-case hex of SHA-256 over the files ordered ordinally by NFC relative path,
    /// each as UTF-8 path bytes, a 0 byte, the length as 8 bytes big-endian, then the bytes.</summary>
    public static string Of(IEnumerable<(string RelativePath, ReadOnlyMemory<byte> Bytes)> files)
    {
        var ordered = files.Select(f => (Path: f.RelativePath.Normalize(NormalizationForm.FormC), f.Bytes)).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        for (var i = 1; i < ordered.Count; i++)
        {
            if (ordered[i].Path == ordered[i - 1].Path) throw new ArgumentException($"Two files have the path '{ordered[i].Path}' once normalised.", nameof(files));
        }

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[8];
        foreach (var (path, bytes) in ordered)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(path));
            sha.AppendData([0]);
            BinaryPrimitives.WriteInt64BigEndian(length, bytes.Length);
            sha.AppendData(length);
            sha.AppendData(bytes.Span);
        }

        return "sha256:" + Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
