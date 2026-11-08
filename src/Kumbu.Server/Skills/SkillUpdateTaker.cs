// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Attachments;
using Kumbu.Core.Diffs;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Skills;

/// <summary>A copy's source as it is now: the <c>kumbu-source</c> Take would write, its parts for a GitHub copy, the
/// <c>kumbu-hash</c> of its files and the files themselves, as received.</summary>
internal sealed record SkillUpstream(string Source, RepoName? Repo, string? RepoPath, string? Commit, string Hash, IReadOnlyList<(string Path, byte[] Bytes)> Files);

/// <summary>A file of the copy against the new version: <c>added</c>, <c>removed</c>, <c>changed</c> or <c>same</c>, with the
/// hunks of a unified diff for a text file; a binary file has none.</summary>
internal sealed record SkillFileChange(string Path, string Change, IReadOnlyList<string> Hunks);

/// <summary>
/// Review, Take update and Skip (skills spec §3.10, Decisions 25, 33), the session's alone. Each starts from the origin
/// sealed in the vault's record, never from the frontmatter: a copy whose <c>kumbu-source</c> or <c>kumbu-hash</c>
/// disagrees with it is refused, 409 <see cref="SkillUpdates.OriginEdited"/>, before anything is fetched. Review diffs her
/// copy as it is now, her own edits included, against the files Take would write. Take writes exactly the version reviewed
/// (the client names its hash; a source that moved on since is refused, 409 <see cref="MovedOn"/>) through her writers, so
/// rights, quota and history apply, sends files the new version lacks to the trash, and only then rewrites the origin,
/// in <c>SKILL.md</c> and sealed. The share code of a link copy is only read from the record: it reaches no answer and no log.
/// </summary>
internal sealed partial class SkillUpdateTaker(
    EncryptedNoteStore store, NoteWriter writer, MarketplaceFetcher github, PublicationStore publishing, AccountLimits limits, RequestCaller caller, bool marketplaces)
{
    /// <summary>The <see cref="ApiError.Type"/> of a Take whose source changed since it was reviewed.</summary>
    public const string MovedOn = "moved-on";

    /// <summary>The <see cref="ApiError.Type"/> of a source withdrawn or vanished.</summary>
    public const string Gone = "gone";

    private sealed record Copy(VaultAccess Access, VaultPath Folder, string Stored, PublishingRecord Record, SkillOrigin Origin);

    /// <summary>A file of her copy now: a note's or text file's text, or an attachment's bytes, and its version.</summary>
    private sealed record Held(VaultPath Path, string Version, string? Text, byte[]? Bytes);

    public async Task<(SkillUpstream Upstream, IReadOnlyList<SkillFileChange> Files)> ReviewAsync(string? path, CancellationToken ct)
    {
        var counts = store.CountsUse;
        store.CountsUse = false;
        try
        {
            var copy = await CopyAsync(path, ct);
            var upstream = await UpstreamAsync(copy, ct);
            var held = await HeldAsync(copy.Folder, ct);
            var changes = new List<SkillFileChange>();
            foreach (var (relative, bytes) in upstream.Files)
            {
                var at = SkillCopier.Inside(copy.Folder, relative);
                var next = relative == SkillPaths.Manifest ? Encoding.UTF8.GetBytes(Manifest(bytes, upstream)) : bytes;
                changes.Add(held.TryGetValue(relative, out var mine) ? Compare(relative, at, mine, next) : Added(relative, at, next));
            }

            foreach (var (relative, mine) in held.Where(h => !upstream.Files.Any(f => f.Path == h.Key)))
            {
                changes.Add(new SkillFileChange(relative, "removed", IsText(mine.Path) ? TextDiff.Hunks(TextOf(mine), "", words: false) : []));
            }

            return (upstream, [.. changes.OrderBy(c => c.Path != SkillPaths.Manifest).ThenBy(c => c.Path, StringComparer.Ordinal)]);
        }
        finally
        {
            store.CountsUse = counts;
        }
    }

    public async Task<SkillUpstream> TakeAsync(string? path, string? hash, CancellationToken ct)
    {
        var copy = await CopyAsync(path, ct);
        RequireWritable(copy.Access.Name);
        var upstream = await UpstreamAsync(copy, ct);
        if (!string.Equals(upstream.Hash, hash, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status409Conflict, $"The source of {copy.Folder.Value} changed since you reviewed it; review it again.", type: MovedOn);
        }

        var attachments = writer.ForAttachments() ?? throw new InvalidOperationException("The server's store keeps attachments.");
        var held = await HeldAsync(copy.Folder, ct);

        // SKILL.md last: should a write fail, her copy keeps its origin and the update stays on offer.
        foreach (var (relative, bytes) in upstream.Files.Where(f => f.Path != SkillPaths.Manifest))
        {
            var at = SkillCopier.Inside(copy.Folder, relative);
            var version = held.GetValueOrDefault(relative)?.Version;
            var result = at.IsNote
                ? await writer.WriteAsync(at.Value, Encoding.UTF8.GetString(bytes), version, ct)
                : (await attachments.WriteAsync(at.Value, bytes, SkillCopier.Dimensions(at, bytes), version, ct)).Result;
            if (result.IsError) throw SkillCopier.Failed(result);
        }

        foreach (var (relative, mine) in held.Where(h => h.Key != SkillPaths.Manifest && !upstream.Files.Any(f => f.Path == h.Key)))
        {
            var removed = mine.Path.IsNote ? await writer.RemoveAsync(mine.Path, ct, mine.Version) : await attachments.RemoveAsync(mine.Path.Value, ct, mine.Version);
            if (removed.IsError) throw new ApiException(StatusCodes.Status409Conflict, removed.Output);
        }

        var manifest = upstream.Files.First(f => f.Path == SkillPaths.Manifest).Bytes;
        var written = await writer.WriteAsync(copy.Folder.Child(SkillPaths.Manifest).Value, Manifest(manifest, upstream), held.GetValueOrDefault(SkillPaths.Manifest)?.Version, ct);
        if (written.IsError) throw SkillCopier.Failed(written);

        var stored = copy.Stored;
        await publishing.ChangeAsync(copy.Access, r => r with
        {
            Origins = [.. r.Origins.Where(o => o.Path != stored), new SkillOrigin(stored, upstream.Source, upstream.Hash)],
            Skipped = [.. r.Skipped.Where(s => s.Path != stored)],
            Updates = [.. r.Updates.Where(u => u != stored)],
        }, ct);
        return upstream;
    }

    /// <summary>The source's state <paramref name="hash"/> as one she doesn't want: the check calls it skipped until the
    /// source hashes otherwise. Nothing is fetched.</summary>
    public async Task SkipAsync(string? path, string? hash, CancellationToken ct)
    {
        if (hash is null || !HashPattern().IsMatch(hash)) throw ApiErrors.BadRequest("Give the hash of the version to skip, as the review names it.");
        var copy = await CopyAsync(path, ct);
        RequireWritable(copy.Access.Name);
        var stored = copy.Stored;
        await publishing.ChangeAsync(copy.Access, r => r with
        {
            Skipped = [.. r.Skipped.Where(s => s.Path != stored), new SkippedUpdate(stored, hash)],
            Updates = [.. r.Updates.Where(u => u != stored)],
        }, ct);
    }

    /// <summary>The copy at <paramref name="path"/> with its sealed origin, which its frontmatter agrees with.</summary>
    private async Task<Copy> CopyAsync(string? path, CancellationToken ct)
    {
        var open = await store.UnlockedVaultsAsync(ct);
        var marked = open.FirstOrDefault(v => v.IsSkillsVault)?.Name;
        if (!VaultPath.TryParse((path ?? "").TrimEnd('/'), out var folder) || folder.Vault is not { } vault || SkillPaths.FolderOf(folder, marked) != folder
            || open.FirstOrDefault(a => a.Name == vault) is not { } access)
        {
            throw ApiErrors.NotFound($"There is no skill at {path}.");
        }

        var stored = access.ToVault(folder).Value;
        var record = (await publishing.ReadAsync(null, access.Vault, ct)).Record;
        var origin = record.Origins.FirstOrDefault(o => o.Path == stored) ?? throw ApiErrors.NotFound($"{folder.Value} has no source to take updates from.");
        var manifest = SkillManifest.Read(folder.Name, await store.ReadAsync(folder.Child(SkillPaths.Manifest), ct));
        if (!string.Equals(manifest.Source, origin.Source, StringComparison.Ordinal) || !string.Equals(manifest.Hash, origin.Hash, StringComparison.Ordinal))
        {
            throw new ApiException(StatusCodes.Status409Conflict,
                $"The origin in {folder.Child(SkillPaths.Manifest).Value} was edited: Kumbu offers no update for it.", type: SkillUpdates.OriginEdited);
        }

        return new Copy(access, folder, stored, record, origin);
    }

    /// <summary>The source as it is now: a link's publication opened with the sealed code, or the GitHub skill at the commit
    /// the default branch points to now, which counts against her GitHub limit; with marketplaces switched off, a GitHub
    /// copy answers as the marketplace routes do and nothing is fetched (skills spec §3.9).</summary>
    private async Task<SkillUpstream> UpstreamAsync(Copy copy, CancellationToken ct)
    {
        if (copy.Origin.Source.StartsWith("link:", StringComparison.Ordinal))
        {
            var code = copy.Record.SkillCodes.FirstOrDefault(c => c.Path == copy.Stored)?.Code;
            if (code is null || await publishing.OpenSkillAsync(code, ct) is not { } opened) throw SourceGone(copy);
            var files = new List<(string, byte[])>(opened.Copy.Files.Count);
            foreach (var file in opened.Copy.Files.OrderBy(f => f.Index))
            {
                files.Add((file.Path, (await publishing.OpenSkillFileAsync(opened, file.Index, ct))?.Bytes ?? throw SourceGone(copy)));
            }

            return Upstream(copy, copy.Origin.Source, null, null, null, files);
        }

        if (!marketplaces) throw MarketplaceApi.SwitchedOff();
        if (SkillUpdates.GitHubSource(copy.Origin.Source) is not var (repo, path, _)) throw SourceGone(copy);
        if (limits.GitHubFetch(caller.User!.Id) is { } retryAfter)
            throw new ApiException(StatusCodes.Status429TooManyRequests, "Too many requests to GitHub; try again in a minute.", retryAfter, type: MarketplaceApi.Busy);
        try
        {
            var (commit, archive) = await MarketplaceApi.GitHubAsync(async () =>
            {
                var now = await github.ResolveCommitAsync(repo, ct);
                return (now, await github.ArchiveAsync(repo, now, caller.User!.Id, ct));
            });
            var files = MarketplaceIndex.FilesOf(archive, path) ?? throw SourceGone(copy);
            return Upstream(copy, $"github:{repo}/{path}@{commit}", repo, path, commit, files.Files);
        }
        catch (ApiException e) when (e.Type == "not-found")
        {
            throw SourceGone(copy);
        }
    }

    private static SkillUpstream Upstream(Copy copy, string source, RepoName? repo, string? path, string? commit, IReadOnlyList<(string Path, byte[] Bytes)> files)
    {
        if (!files.Any(f => f.Path == SkillPaths.Manifest)) throw SourceGone(copy);
        return new SkillUpstream(source, repo, path, commit, SkillHash.Of(files.Select(f => (f.Path, (ReadOnlyMemory<byte>)f.Bytes))), files);
    }

    private static ApiException SourceGone(Copy copy) =>
        new(StatusCodes.Status404NotFound, $"The source of {copy.Folder.Value} is gone: there is no update to take.", type: Gone);

    /// <summary>The new <c>SKILL.md</c> as Take writes it: with the new source and hash in its origin fields.</summary>
    private static string Manifest(byte[] bytes, SkillUpstream upstream) => SkillManifest.WithOrigin(Encoding.UTF8.GetString(bytes), upstream.Source, upstream.Hash);

    /// <summary>The files of her copy now, by their path in the folder; folder markers aren't the skill's.</summary>
    private async Task<Dictionary<string, Held>> HeldAsync(VaultPath folder, CancellationToken ct)
    {
        var prefix = folder.Value.Length + 1;
        var held = new Dictionary<string, Held>(StringComparer.Ordinal);
        foreach (var note in await store.ReadNotesUnderAsync(folder, ct)) held[note.Path.Value[prefix..]] = new Held(note.Path, VersionToken.Of(note.Content), note.Content, null);
        foreach (var attachment in await StoreAttachments.UnderAsync(store, folder, ct))
        {
            if (FolderMarker.Is(attachment.Path)) continue;
            var bytes = await StoreAttachments.ReadBytesAsync(store, attachment.Path, attachment.Current, ct)
                ?? throw new ContentMissingException($"The content of {attachment.Path.Value} is missing.");
            held[attachment.Path.Value[prefix..]] = new Held(attachment.Path, attachment.Version, null, bytes);
        }

        return held;
    }

    private static SkillFileChange Compare(string relative, VaultPath at, Held mine, byte[] next)
    {
        if (!IsText(at)) return new SkillFileChange(relative, mine.Bytes!.AsSpan().SequenceEqual(next) ? "same" : "changed", []);
        var hunks = TextDiff.Hunks(TextOf(mine), Encoding.UTF8.GetString(next), words: false);
        return new SkillFileChange(relative, hunks.Count == 0 ? "same" : "changed", hunks);
    }

    private static SkillFileChange Added(string relative, VaultPath at, byte[] next) =>
        new(relative, "added", IsText(at) ? TextDiff.Hunks("", Encoding.UTF8.GetString(next), words: false) : []);

    private static bool IsText(VaultPath path) => path.IsNote || AttachmentFormat.ForPath(path)?.Kind == AttachmentKind.Text;

    private static string TextOf(Held file) => file.Text ?? Encoding.UTF8.GetString(file.Bytes!);

    private void RequireWritable(string vault)
    {
        if (caller.CanWrite(vault)) return;
        var sentence = caller.ReadOnlySentence(vault);
        throw new ApiException(StatusCodes.Status403Forbidden, char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".", code: caller.ReadOnlyCode(vault) ?? Refusals.ReadOnly);
    }

    [GeneratedRegex("^sha256:[0-9a-f]{64}$")]
    private static partial Regex HashPattern();
}
