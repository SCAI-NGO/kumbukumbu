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
    /// <summary>Every file as <c>&lt;folderName&gt;/&lt;path&gt;</c>, each entry dated <paramref name="at"/>. A folder
    /// name or path that could unpack outside that root (an empty, <c>.</c> or <c>..</c> segment, a leading <c>/</c>, a
    /// backslash, a drive prefix) or holds a control character throws, though the stored paths are checked where they are
    /// written.</summary>
    internal static byte[] Of(string folderName, IReadOnlyList<(string Path, byte[] Bytes)> files, DateTime at)
    {
        if (folderName.Contains('/') || !Plain(folderName)) throw new ArgumentException("The folder name isn't a plain name.", nameof(folderName));
        foreach (var (path, _) in files)
        {
            if (!Plain(path)) throw new ArgumentException("A file path leaves the skill's folder.", nameof(files));
        }

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

    /// <summary>No backslash, no empty, <c>.</c> or <c>..</c> segment, no <c>:</c> (a drive prefix such as <c>C:</c>, or an
    /// alternate data stream) and no control character, which some unzip tools on Windows mishandle.</summary>
    private static bool Plain(string path) =>
        !path.Contains('\\') && !path.Contains(':') && !path.Any(char.IsControl) && path.Split('/').All(s => s is not ("" or "." or ".."));

    /// <summary>The download's <c>Content-Disposition</c> (RFC 6266): an ASCII fallback, in which anything outside
    /// <c>[A-Za-z0-9._-]</c> is <c>_</c>, and the name itself in UTF-8.</summary>
    internal static string Disposition(string folderName)
    {
        var fallback = new StringBuilder(folderName.Length);
        foreach (var c in folderName) fallback.Append(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '_');
        return $"attachment; filename=\"{fallback}.zip\"; filename*=UTF-8''{Uri.EscapeDataString(folderName)}.zip";
    }
}
