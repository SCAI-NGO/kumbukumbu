// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Core.Notes;

public static class Versions
{
    /// <summary>The content of <paramref name="version"/>: the current one or one in the history; null if neither.</summary>
    public static string? Find(StoredNote note, string version) =>
        note.Version == version ? note.Content : note.History.FirstOrDefault(h => VersionToken.Of(h.Content) == version)?.Content;
}
