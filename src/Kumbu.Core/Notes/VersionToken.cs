// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;
using System.Text;

namespace Kumbu.Core.Notes;

/// <summary>
/// Identifies the state of a note, like an HTTP ETag. Computed from the content, so it never needs
/// storing and the same text always has the same token.
/// </summary>
public static class VersionToken
{
    public const int Length = 12;

    public static string Of(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..Length];

    /// <summary>The token of raw bytes (an attachment): no newline normalisation.</summary>
    public static string Of(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes))[..Length];
}
