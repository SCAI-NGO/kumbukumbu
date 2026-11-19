// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;

namespace Kumbu.Core.Attachments;

/// <summary>A file larger than the admin's bound per file (<c>Kumbu:Vaults:MaxFileBytes</c>, files spec decision 11):
/// nothing was stored. The ways in answer it with 413, with this sentence, which names <see cref="Limit"/>.</summary>
public sealed class FileTooLargeException(long limit)
    : Exception($"A file can have at most {Shown(limit)}.")
{
    public long Limit { get; } = limit;

    /// <summary>A bound as the admin likely set it: "1 GiB", "20 MiB", else its bytes.</summary>
    public static string Shown(long bytes)
    {
        const long mib = 1024 * 1024, gib = 1024 * mib;
        if (bytes % gib == 0) return $"{bytes / gib} GiB";
        if (bytes % mib == 0) return $"{bytes / mib} MiB";
        return $"{bytes.ToString("N0", CultureInfo.InvariantCulture)} bytes";
    }
}

/// <summary>A file larger than 15 MB (<see cref="AttachmentFormat.MaxBytes"/>) asked for whole: Kumbu² keeps it, streams
/// it, but doesn't read it into memory (files spec, decision 12).</summary>
public sealed class TooLargeToReadException()
    : Exception($"The file is {AttachmentFormat.NotRead}.");
