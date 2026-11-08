// SPDX-License-Identifier: AGPL-3.0-or-later
using System.IO.Compression;
using System.Text;

namespace Kumbu.Server.Skills;

/// <summary>
/// A skill's folder as a <c>.zip</c> (skills design §3.12): its files under one root named after the folder, the Agent
/// Skills layout, so the archive unpacks into a skill elsewhere. Root and file name are always the folder's name, never
/// the manifest's, which its writer chose.
/// </summary>
internal static class SkillZip
{
    /// <summary>Every file as <c>&lt;folderName&gt;/&lt;path&gt;</c>, each entry dated <paramref name="at"/>.</summary>
    internal static byte[] Of(string folderName, IReadOnlyList<(string Path, byte[] Bytes)> files, DateTime at)
    {
        // A zip keeps a time of day without a zone: the UTC one, as it is.
        var stamp = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Unspecified), TimeSpan.Zero);
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, bytes) in files)
            {
                var entry = archive.CreateEntry($"{folderName}/{path}", CompressionLevel.Optimal);
                entry.LastWriteTime = stamp;
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return buffer.ToArray();
    }

    /// <summary>The download's <c>Content-Disposition</c> (RFC 6266): an ASCII fallback, in which anything outside
    /// <c>[A-Za-z0-9._-]</c> is <c>_</c>, and the name itself in UTF-8.</summary>
    internal static string Disposition(string folderName)
    {
        var fallback = new StringBuilder(folderName.Length);
        foreach (var c in folderName) fallback.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        return $"attachment; filename=\"{fallback}.zip\"; filename*=UTF-8''{Uri.EscapeDataString(folderName)}.zip";
    }
}
