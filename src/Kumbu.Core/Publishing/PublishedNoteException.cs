// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Publishing;

/// <summary>A removal or move would withdraw a publication, and its caller may not withdraw (publish spec §3.7): the
/// batch aborts and nothing moves or goes. A final refusal, <see cref="Code"/> <c>published</c>: 403 over REST, never a
/// retryable answer.</summary>
public sealed class PublishedNoteException(string sentence) : Exception(sentence)
{
    public string Code => Notes.Refusals.Published;
}
