// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;

namespace Kumbu.Server.Accounts;

/// <summary>
/// Users that a backup restore brought back: on the <see cref="DeletionList"/>, kept outside the database, and in
/// the <c>users</c> collection again. Never the primary admin (who can't be deleted, so is never listed).
/// </summary>
public sealed class RestoredDeletions(IMongoDatabase db, DeletionList list)
{
    private IMongoCollection<UserDocument> Users { get; } = db.GetCollection<UserDocument>("users");

    public bool Configured => list.Configured;

    /// <summary>Every listed user that exists again, in username order.</summary>
    public async Task<IReadOnlyList<User>> FindAsync(CancellationToken ct)
    {
        var ids = await list.ReadAsync(ct);
        if (ids.Count == 0) return [];
        var found = await Users.Find(Builders<UserDocument>.Filter.In(u => u.Id, ids) & Builders<UserDocument>.Filter.Ne(u => u.Role, Roles.PrimaryAdmin))
            .SortBy(u => u.Username).ToListAsync(ct);
        return found.Select(u => u.ToUser()).ToList();
    }

    /// <summary><see cref="FindAsync"/>, for an admin only, and without the admin: they can't delete themselves
    /// here (another admin can, or they use Settings), so they aren't offered it.</summary>
    public async Task<IReadOnlyList<User>> ListAsync(User actor, CancellationToken ct)
    {
        var acting = await AdminRules.RequireAdminAsync(Users, actor, ct);
        return (await FindAsync(ct)).Where(u => u.Id != acting.Id).ToList();
    }
}

/// <summary>At start, warns the admins (in the log) about deleted users a restore brought back; ids only.</summary>
public sealed class RestoredDeletionsCheck(RestoredDeletions restored, ILogger<RestoredDeletionsCheck> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            var users = await restored.FindAsync(stoppingToken);
            if (users.Count > 0)
            {
                log.LogWarning(
                    "{Count} deleted users are back after a restore; an admin can delete them again on the users page ({UserIds})",
                    users.Count, string.Join(", ", users.Select(u => u.Id)));
            }
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A check, never a reason not to start.
            log.LogError("Could not check for deleted users a restore brought back ({Error})", e.Message);
        }
    }
}
