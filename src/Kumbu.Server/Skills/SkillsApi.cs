// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Skills;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;

namespace Kumbu.Server.Skills;

/// <summary>A skill for the Skills screen. <c>Location</c> is "system" or the vault's name; <c>Path</c> its folder, null for a
/// system skill; <c>CanWrite</c> whether the caller may change it.</summary>
public sealed record SkillView(
    string Name, string Description, string Location, string? Path, string? Source, string? LastEditor, bool ByOther, string[] Warnings,
    string? Update = null, bool Shared = false, bool CanWrite = false, bool OriginEdited = false);

/// <summary>The skills the caller sees, and her live agent keys and connections that lack the vault Skills (by name).</summary>
public sealed record SkillsView(SkillView[] Skills, string[] WithoutSkills);

/// <summary>A skill that fits a request: its place (1 first) and its fused score.</summary>
public sealed record SkillHitView(int Rank, double Score, SkillView Skill);

/// <summary>
/// The Skills screen's list and find (skills design §5), for a session only. <c>as</c> shows what one of her agent keys or
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
    }

    private static async Task<Ok<SkillsView>> ListAsync(
        RequestCaller caller, EncryptedNoteStore store, IWriterLabels labels, ApiKeys keys, Connections connections, TimeProvider time, CancellationToken ct)
    {
        var open = await caller.OpenVaultsAsync(ct);
        var skillsVault = open.FirstOrDefault(v => v.IsSkillsVault)?.Id;
        var skills = await VisibleAsync(store, labels, null, ct);
        var now = time.GetUtcNow().UtcDateTime;
        var without = new List<string>();
        if (skillsVault is not null)
        {
            without.AddRange((await keys.ListAsync(caller.User!, caller.UserKey!, ct))
                .Where(k => k.Kind == ApiKeyKind.Agent && k.RevokedAt is null && (k.ExpiresAt is null || k.ExpiresAt > now) && !k.Vaults.ContainsKey(skillsVault)).Select(k => k.Name));
            without.AddRange((await connections.ListAsync(caller.User!, caller.UserKey!, ct))
                .Where(c => c.State is ConnectionState.Waiting or ConnectionState.Connected or ConnectionState.NeedsReconnect && !c.Vaults.ContainsKey(skillsVault)).Select(c => c.Name));
        }

        return TypedResults.Ok(new SkillsView([.. await ViewsAsync(store, skills, ct)], [.. without]));
    }

    private static async Task<Ok<SkillHitView[]>> FindAsync(
        [FromQuery] string? q, [FromQuery(Name = "as")] string? credential, RequestCaller caller, EncryptedNoteStore store, IWriterLabels labels, ApiKeys keys, Connections connections,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(q)) throw ApiErrors.BadRequest("Give what to look for, as q.");
        IReadOnlySet<string>? vaults = null;
        if (credential is not null)
        {
            vaults = (await keys.ListAsync(caller.User!, caller.UserKey!, ct)).FirstOrDefault(k => k.Id == credential && k.Kind == ApiKeyKind.Agent && k.RevokedAt is null) is { } key
                ? key.Vaults.Keys.ToHashSet(StringComparer.Ordinal)
                : (await connections.ListAsync(caller.User!, caller.UserKey!, ct)).FirstOrDefault(c => c.Id == credential && c.State is not (ConnectionState.Revoked or ConnectionState.Expired)) is { } connection
                    ? connection.Vaults.Keys.ToHashSet(StringComparer.Ordinal)
                    : throw ApiErrors.NotFound("You have no such agent key or connection.");
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
            var views = (await ViewsAsync(store, [.. hits.Select(h => h.Skill)], ct)).ToList();
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

    private static async Task<List<SkillView>> ViewsAsync(EncryptedNoteStore store, IReadOnlyList<Skill> skills, CancellationToken ct)
    {
        var views = new List<SkillView>(skills.Count);
        foreach (var s in skills)
        {
            var canWrite = s.Vault is { } vault && await store.VaultInfoAsync(vault, ct) is { Level: >= AccessLevel.Write };
            views.Add(new SkillView(
                s.Name, s.Description, s.Vault ?? "system", s.Folder?.Value, s.Source, s.LastEditor, s.ByOther, [.. s.Warnings], CanWrite: canWrite, OriginEdited: s.OriginEdited));
        }

        return views;
    }
}
