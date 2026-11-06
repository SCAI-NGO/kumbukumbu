// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Sh;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Publishing;

/// <summary><c>sh publish</c> for the request's caller (publish spec §3.9), over <see cref="Publications"/>, which
/// makes every check. Paths are in the caller's view.</summary>
internal sealed class ShellPublisher(
    Publications publications, RequestCaller caller, EncryptedNoteStore store, IHttpContextAccessor http, IOptions<KumbuOptions> options) : IPublisher
{
    public async Task<IReadOnlyList<PublicationLine>> ListAsync(VaultPath under, CancellationToken ct)
    {
        var vaults = await store.UnlockedVaultsAsync(ct);
        IEnumerable<(VaultAccess Access, VaultPath Under)> scopes = under.IsRoot
            ? vaults.Select(a => (a, VaultPath.Root.Child(a.Name)))
            : [(vaults.FirstOrDefault(a => a.Name == under.Vault) ?? throw new PublishRefusedException($"{under}: no such vault", ShellErrorKind.NotFound), under)];
        var lines = new List<PublicationLine>();
        foreach (var (access, scope) in scopes)
        {
            lines.AddRange((await publications.ListAsync(access, store, scope, null, ct)).Select(s => new PublicationLine(s.Note, s.SnapshotAt, s.Older)));
        }

        return lines;
    }

    public async Task<PublishOutcome> AddAsync(VaultPath note, CancellationToken ct)
    {
        var access = await AccessAsync(note, ct);
        // The dev token has no user; its access may never publish, so Publications refuses it before the id is used.
        var (by, byRef) = Writer(access);
        var published = await publications.PublishAsync(access, store, note, caller.User?.Id ?? "", by, byRef, BaseUrl, ct);
        return new PublishOutcome(published.Link, published.SnapshotAt, published.Plain);
    }

    public async Task<PublishOutcome> UpdateAsync(VaultPath note, CancellationToken ct)
    {
        var access = await AccessAsync(note, ct);
        var (by, byRef) = Writer(access);
        var updated = await publications.UpdateAsync(access, store, note, by, byRef, BaseUrl, ct);
        return new PublishOutcome(null, updated.SnapshotAt, updated.Plain);
    }

    public async Task WithdrawAsync(VaultPath note, CancellationToken ct)
    {
        var access = await AccessAsync(note, ct);
        var (by, byRef) = Writer(access);
        await publications.WithdrawAsync(access, store, note, by, byRef, ct);
    }

    /// <summary>Who the record names. A member is refused before anything is written, so her writer id isn't needed
    /// (and a member without one would fail here instead of being refused).</summary>
    private (string By, WriterRef ByRef) Writer(VaultAccess access) =>
        access.Membership is null ? (caller.WriterIn(access.Name), caller.ReferenceIn(access.Name)) : ("", caller.Reference);

    private string BaseUrl => Publications.BaseUrl(http.HttpContext!, options.Value);

    /// <summary>The caller's access to the vault the note lies in; a path outside every vault it opens is no note.</summary>
    private async Task<VaultAccess> AccessAsync(VaultPath note, CancellationToken ct) =>
        (note.Segments.Count < 2 ? null : (await store.UnlockedVaultsAsync(ct)).FirstOrDefault(a => a.Name == note.Vault))
        ?? throw new PublishRefusedException($"{note}: no such note", ShellErrorKind.NotFound);
}
