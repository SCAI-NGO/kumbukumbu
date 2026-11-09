// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

/// <summary>
/// A write that adds data reached a vault whose owner's storage is full (WebDAV design §3.10): nothing was written.
/// The message is all anybody is told; <see cref="Owner"/> says whether the caller is the vault's owner, who alone is
/// pointed further (<c>df</c>, the web app's notice). A member learns nothing about the owner's figures.
/// </summary>
public sealed class QuotaExceededException(bool owner) : Exception(Sentence)
{
    public const string Sentence = "This vault is out of space.";

    /// <summary>The caller is the vault's owner (her session, key, share or connection).</summary>
    public bool Owner { get; } = owner;
}
