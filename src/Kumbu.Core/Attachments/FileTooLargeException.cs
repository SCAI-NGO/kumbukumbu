// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Attachments;

/// <summary>A file larger than the admin's bound per file (<c>Kumbu:Vaults:MaxFileBytes</c>, files spec decision 11):
/// nothing was stored. The ways in answer it with 413, naming <see cref="Limit"/>.</summary>
public sealed class FileTooLargeException(long limit)
    : Exception($"A file holds at most {limit / (1024 * 1024)} MB on this server.")
{
    public long Limit { get; } = limit;
}

/// <summary>A file larger than 15 MB (<see cref="AttachmentFormat.MaxBytes"/>) asked for whole: Kumbu² keeps it, streams
/// it, but doesn't read it into memory (files spec, decision 12).</summary>
public sealed class TooLargeToReadException()
    : Exception("The file is larger than 15 MB: kept, but not read whole.");
