// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Publishing;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Skills;

/// <summary>A skill for the Skills screen. <c>Location</c> is "system" or the vault's name; <c>Path</c> its folder, null for a
/// system skill; <c>Source</c> a copy's source where its sealed origin agrees, else null, with <c>SourceUnverified</c>
/// when the frontmatter claims one that nothing backs (Decision 33); <c>CanWrite</c> whether the caller may change it;
/// <c>Shared</c> whether it is shared by link, and <c>WithdrawnBefore</c> whether a link of it was withdrawn since it was
/// last shared (Decision 15).</summary>
public sealed record SkillView(
    string Name, string Description, string Location, string? Path, string? Source, string? LastEditor, bool ByOther, string[] Warnings,
    string? Update = null, bool Shared = false, bool CanWrite = false, bool OriginEdited = false, bool WithdrawnBefore = false, bool SourceUnverified = false);

/// <summary>The skills the caller sees, and her live agent keys and connections that lack the vault Skills (by name).</summary>
public sealed record SkillsView(SkillView[] Skills, string[] WithoutSkills);

/// <summary>The skill to share by link: its folder ("/Skills/x", "/Work/_skills/x").</summary>
public sealed record SkillShareRequest(string Path);

/// <summary>A skill's new link, the code in its fragment: this answer is the only one that ever holds it (Decision 15).</summary>
public sealed record SkillLinkView(string Link, DateTime SnapshotAt)
{
    public override string ToString() => $"SkillLinkView {{ SnapshotAt = {SnapshotAt:O} }}";
}

/// <summary>One of her skills shared by link: <c>Changed</c> when a file of the folder changed since the copy. Never the link.</summary>
public sealed record SharedSkillView(string Id, string Path, DateTime SnapshotAt, bool Changed);

/// <summary>When the copy behind a skill's link was taken.</summary>
public sealed record SkillSnapshotView(DateTime SnapshotAt);

/// <summary>Add to my skills: the code from the skill's link, the name of a vault she may write, and the folder's name
/// there, by default the publication's. Not logged: <see cref="ToString"/> leaves out the code.</summary>
public sealed record SkillAddRequest(string Code, string Vault, string? Name = null)
{
    public override string ToString() => $"SkillAddRequest {{ Vault = {Vault}, Name = {Name} }}";
}

/// <summary>A copy's state after Check for updates (<see cref="SkillUpdates"/>): its folder, <c>current</c>, <c>available</c>,
/// <c>gone</c>, <c>skipped</c> or <c>origin-edited</c>, its sealed source and, for a GitHub copy, the commit asked.</summary>
public sealed record SkillUpdateView(string Path, string State, string? Source = null, string? Commit = null);

/// <summary>A skill that fits a request: its place (1 first) and its fused score.</summary>
public sealed record SkillHitView(int Rank, double Score, SkillView Skill);

/// <summary>
/// The Skills screen's list and find, and its Copy to… and Move to… (<see cref="SkillCopier"/>) (skills design §5), for a
/// session only: only a session reaches both vaults. <c>as</c> shows what one of her agent keys or
/// connections would find: its vaults alone, besides the system skills. Listing reads every vault she reaches, which is
/// no use of any (<see cref="EncryptedNoteStore.CountsUse"/>), and the credential asked about acts on nothing, so its
/// last use stays.
/// </summary>
public static class SkillsApi
{
    public static void MapSkillsApi(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/skills").AddEndpointFilter(ApiErrors.Filter).WithMetadata(SessionOnlyMetadata.Instance);
        group.MapGet("", ListAsync).Errors(401, 403, 503);
        group.MapGet("/find", FindAsync).Errors(400, 401, 403, 404, 503);
        group.MapPost("/copy", CopyAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        group.MapPost("/move", MoveAsync).Errors(400, 401, 403, 404, 409, 503, 507);
        group.MapPost("/add", AddAsync).Errors(400, 401, 403, 404, 409, 429, 503, 507);
        group.MapPost("/updates/check", CheckUpdatesAsync).Errors(401, 403, 429, 503);
        group.MapGet("/zip", ZipAsync).Produces(StatusCodes.Status200OK, typeof(byte[]), "application/zip").WithMetadata(FileContentMetadata.Instance).Errors(400, 401, 403, 404, 503);
        MapSharing(group);
    }

    /// <summary>
    /// Sharing by link (skills design §3.7, Decision 15): her session alone, though <see cref="Publications"/> would take
    /// any of her credentials that may publish. Sharing and updating put content out, so they need the password window,
    /// as a note's publishing does; withdrawing only reduces what is out. The link is in the answer that makes it, never
    /// in another.
    /// </summary>
    private static void MapSharing(RouteGroupBuilder group)
    {
        group.MapGet("/shared", SharedAsync).Errors(401, 403, 503);
        group.MapPost("/shared", ShareAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 409, 503);
        group.MapPost("/shared/{id}/update", UpdateSharedAsync).WithMetadata(PasswordWindowMetadata.Instance).Errors(400, 401, 403, 404, 409, 503);
        group.MapDelete("/shared/{id}", WithdrawSharedAsync).Errors(401, 403, 404, 409, 503);
    }

    private static async Task<Ok<SkillCopiedView>> CopyAsync(
        SkillCopyRequest body, RequestCaller caller, EncryptedNoteStore store, NoteWriter writer, Publishing.PublicationStore publishing, ILoggerFactory logs, CancellationToken ct) =>
        TypedResults.Ok(new SkillCopiedView((await new SkillCopier(store, writer, publishing, caller, logs.CreateLogger<SkillCopier>()).CopyAsync(body, ct)).Value));

    private static async Task<Ok<SkillCopiedView>> MoveAsync(
        SkillCopyRequest body, RequestCaller caller, EncryptedNoteStore store, NoteWriter writer, Publishing.PublicationStore publishing, ILoggerFactory logs, CancellationToken ct) =>
        TypedResults.Ok(new SkillCopiedView((await new SkillCopier(store, writer, publishing, caller, logs.CreateLogger<SkillCopier>()).MoveAsync(body, ct)).Value));

    /// <summary>
    /// Add to my skills (skills design §3.12): the code opens the skill as on its page, under the same limits, so a session
    /// guesses no faster than a stranger, and a wrong one answers the page's 404. The answer names the new folder, never
    /// the code.
    /// </summary>
    private static async Task<Created<SkillCopiedView>> AddAsync(
        SkillAddRequest body, HttpContext http, RequestCaller caller, EncryptedNoteStore store, NoteWriter writer, PublicationStore publishing, UserCatalog users,
        AccountLimits limits, IOptions<KumbuOptions> options, ILoggerFactory logs, CancellationToken ct)
    {
        var (code, opened) = await SkillPageApi.OpenCodeAsync(body.Code ?? "", http, publishing, users, limits, options, logs, ct)
            ?? throw ApiErrors.NotFound(SkillPageApi.NotFoundSentence);
        var folder = await new SkillCopier(store, writer, publishing, caller, logs.CreateLogger<SkillCopier>()).AddAsync(code, opened, body.Vault ?? "", body.Name, ct);
        return TypedResults.Created((string?)null, new SkillCopiedView(folder.Value));
    }

    /// <summary>Check for updates (skills spec §3.10): asked only here, by her session, when the Skills screen opens or
    /// she presses the button; never in the background, never by <c>sh</c>.</summary>
    private static async Task<Ok<SkillUpdateView[]>> CheckUpdatesAsync(
        RequestCaller caller, EncryptedNoteStore store, MarketplaceFetcher github, PublicationStore publishing, AccountLimits limits, IOptions<KumbuOptions> options,
        CancellationToken ct) =>
        TypedResults.Ok((await new SkillUpdates(store, github, publishing, limits, caller, options.Value.Skills.Marketplaces.Enabled).CheckAsync(ct))
            .Select(s => new SkillUpdateView(s.Folder.Value, s.State, s.Source, s.Commit)).ToArray());

    /// <summary>
    /// A skill she can read (its folder, or <c>system:&lt;name&gt;</c>) as a <c>.zip</c> under its folder's name. A vault's
    /// skill is taken out as the files ZIP takes files out (<see cref="ExportApi"/>): it needs a ticket while she asks for
    /// the password for downloads or the password window is closed, a member's download is an event for the owner, and
    /// each is logged with ids and counts. Within a skill's limits, checked before its content is read
    /// (<see cref="SkillCopier.SourceAsync"/>). A system skill is the server's own and needs none of this.
    /// </summary>
    private static async Task<FileContentHttpResult> ZipAsync(
        [FromQuery] string? path, [FromQuery] string? ticket, EncryptedNoteStore store, UserCatalog users, [AsParameters] ExportApi.Export export)
    {
        var (caller, http, ct) = (export.Caller, export.Http, export.Ct);
        if (string.IsNullOrWhiteSpace(path)) throw ApiErrors.NotFound("Give the skill's folder, as path.");
        var open = await caller.OpenVaultsAsync(ct);
        var system = path.StartsWith(SkillCopier.SystemPrefix, StringComparison.Ordinal);
        // Not marked PasswordWindowMetadata: the browser's download can't answer a 401 with the password dialog, so a
        // closed window asks for the ticket instead, as the preference does.
        var withPassword = !system && (await users.AsksPasswordForDownloadsAsync(caller.User!.Id, ct) || !caller.PasswordWindowOpen);
        if (withPassword) export.RequireTicket(ticket);
        var source = await SkillCopier.SourceAsync(store, path, open, open.FirstOrDefault(v => v.IsSkillsVault)?.Name, ct);
        var files = source.Files.Select(f => (f.Path, f.Bytes ?? Encoding.UTF8.GetBytes(f.Text!))).ToList();
        var zip = SkillZip.Of(source.Name, files, export.Time.GetUtcNow().UtcDateTime);
        if (source.Folder?.Vault is { } vault)
        {
            await export.RecordMemberExportsAsync([vault]);
            export.Log.LogInformation(
                "User {UserId} downloaded the skill of {Files} files ({Bytes} bytes) of vault {VaultId} as a ZIP, {Password}",
                caller.User!.Id, files.Count, files.Sum(f => (long)f.Item2.Length), await export.VaultIdsAsync([vault]),
                withPassword ? "with the password" : "without the password (turned off)");
        }

        http.Response.Headers.ContentDisposition = SkillZip.Disposition(source.Name);
        return TypedResults.File(zip, "application/zip");
    }

    private static async Task<Ok<SkillsView>> ListAsync(
        RequestCaller caller, EncryptedNoteStore store, IWriterLabels labels, ApiKeys keys, Connections connections, PublicationStore publishing, TimeProvider time,
        CancellationToken ct)
    {
        var open = await caller.OpenVaultsAsync(ct);
        var skillsVault = open.FirstOrDefault(v => v.IsSkillsVault)?.Id;
        var skills = await VisibleAsync(store, labels, null, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var without = new List<string>();
        if (skillsVault is not null)
        {
            // What covers only hidden vaults isn't named, as the lists of keys and connections don't list it.
            var onlyHidden = await AccountApi.OnlyHiddenAsync(caller, ct);
            without.AddRange((await keys.ListAsync(caller.User!, caller.UserKey!, ct))
                .Where(k => k.Kind == ApiKeyKind.Agent && k.RevokedAt is null && (k.ExpiresAt is null || k.ExpiresAt > now) && !k.Vaults.ContainsKey(skillsVault)
                    && !onlyHidden(k.Vaults.Keys))
                .Select(k => k.Name));
            without.AddRange((await connections.ListAsync(caller.User!, caller.UserKey!, ct))
                .Where(c => c.State is ConnectionState.Waiting or ConnectionState.Connected or ConnectionState.NeedsReconnect && !c.Vaults.ContainsKey(skillsVault)
                    && !onlyHidden(c.Vaults.Keys))
                .Select(c => c.Name));
        }

        return TypedResults.Ok(new SkillsView([.. await ViewsAsync(store, publishing, skills, ct)], [.. without]));
    }

    private static async Task<Ok<SkillHitView[]>> FindAsync(
        [FromQuery] string? q, [FromQuery(Name = "as")] string? credential, RequestCaller caller, EncryptedNoteStore store, IWriterLabels labels, ApiKeys keys, Connections connections,
        PublicationStore publishing, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) throw ApiErrors.BadRequest("Give what to look for, as q.");
        IReadOnlySet<string>? vaults = null;
        if (credential is not null)
        {
            vaults = (await keys.ListAsync(caller.User!, caller.UserKey!, ct)).FirstOrDefault(k => k.Id == credential && k.Kind == ApiKeyKind.Agent && k.RevokedAt is null) is { } key
                ? key.Vaults.Keys.ToHashSet(StringComparer.Ordinal)
                : (await connections.ListAsync(caller.User!, caller.UserKey!, ct)).FirstOrDefault(c => c.Id == credential && c.State is not (ConnectionState.Revoked or ConnectionState.Expired)) is { } connection
                    ? connection.Vaults.Keys.ToHashSet(StringComparer.Ordinal)
                    : null;
            // One on hidden vaults only answers as one she doesn't have, as the lists leave it out.
            if (vaults is null || (await AccountApi.OnlyHiddenAsync(caller, ct))(vaults)) throw ApiErrors.NotFound("You have no such agent key or connection.");
        }

        var open = await caller.OpenVaultsAsync(ct);
        var allowed = vaults is null ? null : open.Where(v => vaults.Contains(v.Id)).Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        var skills = await VisibleAsync(store, labels, allowed, ct);
        var manifests = new List<(Skill, string?)>(skills.Count);
        var directory = new SkillDirectory(store, labels);
        var counts = store.CountsUse;
        store.CountsUse = false;
        try
        {
            foreach (var skill in skills) manifests.Add((skill, await directory.ManifestAsync(skill, ct)));
            var hits = await SkillSearch.RankAsync(manifests, q, store, ct);
            var views = (await ViewsAsync(store, publishing, [.. hits.Select(h => h.Skill)], ct)).ToList();
            return TypedResults.Ok(hits.Select((h, i) => new SkillHitView(h.Rank, h.Score, views[i])).ToArray());
        }
        finally
        {
            store.CountsUse = counts;
        }
    }

    /// <summary>The skills of the caller, or with <paramref name="vaults"/> (the names of one credential's vaults) the system
    /// skills and those of these vaults alone.</summary>
    private static async Task<IReadOnlyList<Skill>> VisibleAsync(EncryptedNoteStore store, IWriterLabels labels, IReadOnlySet<string>? vaults, CancellationToken ct)
    {
        var counts = store.CountsUse;
        store.CountsUse = false;
        try
        {
            var all = await new SkillDirectory(store, labels).ListAsync(null, ct);
            return vaults is null ? all : [.. all.Where(s => s.Vault is not { } vault || vaults.Contains(vault))];
        }
        finally
        {
            store.CountsUse = counts;
        }
    }

    private static async Task<List<SkillView>> ViewsAsync(EncryptedNoteStore store, PublicationStore publishing, IReadOnlyList<Skill> skills, CancellationToken ct)
    {
        var (shared, withdrawn, origins, updates) = await SharedFoldersAsync(store, publishing, skills, ct);
        var views = new List<SkillView>(skills.Count);
        foreach (var s in skills)
        {
            var canWrite = s.Vault is { } vault && await store.VaultInfoAsync(vault, ct) is { Level: >= AccessLevel.Write };
            var sealedOrigin = s.Folder is { } copy && origins.TryGetValue(copy.Value, out var origin) ? origin : null;
            var edited = s.OriginEdited || sealedOrigin is not null && await OriginEditedAsync(store, s.Folder!, sealedOrigin, ct);
            var update = !edited && s.Folder is { } checkedFolder && updates.Contains(checkedFolder.Value) ? SkillUpdates.Available : null;
            // "From" a source only where the sealed origin agrees: anyone who may write the folder can write a kumbu-source.
            var verified = sealedOrigin is not null && !edited;
            views.Add(new SkillView(
                s.Name, s.Description, s.Vault ?? "system", s.Folder?.Value, verified ? s.Source : null, s.LastEditor, s.ByOther, [.. s.Warnings],
                Update: update, Shared: s.Folder is { } folder && shared.Contains(folder.Value), CanWrite: canWrite, OriginEdited: edited,
                WithdrawnBefore: s.Folder is { } at && withdrawn.Contains(at.Value), SourceUnverified: s.Source is not null && !verified && !edited));
        }

        return views;
    }

    /// <summary>Whether the frontmatter's <c>kumbu-source</c> or <c>kumbu-hash</c> disagrees with the sealed origin (Decision 33).</summary>
    private static async Task<bool> OriginEditedAsync(EncryptedNoteStore store, VaultPath folder, SkillOrigin origin, CancellationToken ct)
    {
        var counts = store.CountsUse;
        store.CountsUse = false;
        try
        {
            return SkillUpdates.IsOriginEdited(folder, await store.ReadAsync(folder.Child(SkillPaths.Manifest), ct), origin);
        }
        finally
        {
            store.CountsUse = counts;
        }
    }

    /// <summary>The folders of <paramref name="skills"/> shared by link, those whose link was withdrawn since they were last
    /// shared, the sealed origins of copies and the folders the last check found an update for, in the caller's view:
    /// whether, not how (no link, no code).</summary>
    private static async Task<(HashSet<string> Shared, HashSet<string> Withdrawn, Dictionary<string, SkillOrigin> Origins, HashSet<string> Updates)> SharedFoldersAsync(
        EncryptedNoteStore store, PublicationStore publishing, IReadOnlyList<Skill> skills, CancellationToken ct)
    {
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var withdrawn = new HashSet<string>(StringComparer.Ordinal);
        var origins = new Dictionary<string, SkillOrigin>(StringComparer.Ordinal);
        var updates = new HashSet<string>(StringComparer.Ordinal);
        var vaults = skills.Select(s => s.Vault).OfType<string>().ToHashSet(StringComparer.Ordinal);
        if (vaults.Count == 0) return (folders, withdrawn, origins, updates);
        foreach (var access in (await store.UnlockedVaultsAsync(ct)).Where(a => vaults.Contains(a.Name)))
        {
            var record = (await publishing.ReadAsync(null, access.Vault, ct)).Record;
            foreach (var entry in record.Publications.Where(p => p.Kind == PublicationKinds.Skill))
            {
                if (VaultPath.TryParse(entry.Note, out var stored)) folders.Add(access.ToCaller(stored).Value);
            }

            foreach (var folder in record.Withdrawn)
            {
                if (VaultPath.TryParse(folder, out var stored)) withdrawn.Add(access.ToCaller(stored).Value);
            }

            foreach (var origin in record.Origins)
            {
                if (VaultPath.TryParse(origin.Path, out var stored)) origins[access.ToCaller(stored).Value] = origin;
            }

            foreach (var folder in record.Updates)
            {
                if (VaultPath.TryParse(folder, out var stored)) updates.Add(access.ToCaller(stored).Value);
            }
        }

        return (folders, withdrawn, origins, updates);
    }

    /// <summary>The skills of her own vaults shared by link.</summary>
    private static async Task<Ok<SharedSkillView[]>> SharedAsync(Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var views = new List<SharedSkillView>();
        foreach (var access in (await store.UnlockedVaultsAsync(ct)).Where(Owned))
        {
            views.AddRange((await publications.SkillStatesAsync(access, store, ct)).Select(s => new SharedSkillView(s.Id, s.Folder.Value, s.SnapshotAt, s.Changed)));
        }

        return TypedResults.Ok(views.OrderBy(v => v.Path, StringComparer.Ordinal).ToArray());
    }

    private static async Task<Created<SkillLinkView>> ShareAsync(
        SkillShareRequest body, HttpContext http, RequestCaller caller, Publications publications, EncryptedNoteStore store, IOptions<KumbuOptions> options, CancellationToken ct)
    {
        if (!VaultPath.TryParse((body.Path ?? "").TrimEnd('/'), out var folder) || folder.Vault is not { } vault
            || (await store.UnlockedVaultsAsync(ct)).FirstOrDefault(a => a.Name == vault) is not { } access)
        {
            throw ApiErrors.NotFound($"There is no skill at {body.Path}.");
        }

        var (by, byRef) = Writer(caller, access);
        try
        {
            var shared = await publications.PublishSkillAsync(access, store, folder, caller.User!.Id, by, byRef, Publications.BaseUrl(http, options.Value), ct);
            return TypedResults.Created((string?)null, new SkillLinkView(shared.Link, shared.SnapshotAt));
        }
        // One link per skill: the Shared tab withdraws it, then Share makes a new one.
        catch (PublishRefusedException e) when (e.Code == Refusals.SkillShared)
        {
            throw new ApiException(StatusCodes.Status409Conflict, $"{folder.Value} is shared already: withdraw its link first to make a new one.", code: Refusals.SkillShared);
        }
    }

    private static async Task<Ok<SkillSnapshotView>> UpdateSharedAsync(
        string id, RequestCaller caller, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var (access, state) = await HolderAsync(id, publications, store, ct);
        var (by, byRef) = Writer(caller, access);
        return TypedResults.Ok(new SkillSnapshotView(await publications.UpdateSkillAsync(access, store, state.Folder, by, byRef, ct)));
    }

    private static async Task<NoContent> WithdrawSharedAsync(
        string id, RequestCaller caller, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        var (access, _) = await HolderAsync(id, publications, store, ct);
        var (by, byRef) = Writer(caller, access);
        await publications.WithdrawSkillAsync(access, store, id, by, byRef, ct);
        return TypedResults.NoContent();
    }

    /// <summary>Her own vault holding the skill link <paramref name="id"/>, and the link's state; a member's alias holds none.</summary>
    private static async Task<(VaultAccess Access, SkillPublicationState State)> HolderAsync(string id, Publications publications, EncryptedNoteStore store, CancellationToken ct)
    {
        foreach (var access in (await store.UnlockedVaultsAsync(ct)).Where(Owned))
        {
            if ((await publications.SkillStatesAsync(access, store, ct)).FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal)) is { } state) return (access, state);
        }

        throw ApiErrors.NotFound("You have no such shared skill.");
    }

    private static bool Owned(VaultAccess access) => access.IsOwner && access.Membership is null;

    /// <summary>As <see cref="PublicationApi"/>'s: a member is refused before anything is written, so her writer id isn't needed.</summary>
    private static (string By, WriterRef ByRef) Writer(RequestCaller caller, VaultAccess access) =>
        access.Membership is null ? (caller.WriterIn(access.Name), caller.ReferenceIn(access.Name)) : ("", caller.Reference);
}
