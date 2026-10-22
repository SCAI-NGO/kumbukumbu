// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Microsoft.Extensions.Options;

namespace Kumbu.Server.Notes;

/// <summary>
/// Loads <c>Kumbu:Seed:Path</c> at startup. Since step 3 the database is the source of truth: a file
/// creates a missing note, and updates a note only while nobody changed it through MCP (see <see cref="SeedDecision"/>).
/// </summary>
public sealed class Seeder(IOptions<KumbuOptions> options, MongoNoteStore store, TimeProvider time, ILogger<Seeder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var seedPath = options.Value.Seed.Path;
        if (string.IsNullOrEmpty(seedPath))
        {
            return;
        }

        // A typo in the mount should not take the server down: it runs, just without seeded notes.
        if (!Directory.Exists(seedPath))
        {
            log.LogError("Kumbu:Seed:Path does not exist: {Path}", seedPath);
            return;
        }

        await store.EnsureIndexesAsync(cancellationToken);
        var scan = SeedScanner.Scan(seedPath);
        var counts = new Dictionary<SeedAction, int>();
        foreach (var file in scan.Files)
        {
            var action = await SeedAsync(file, cancellationToken);
            counts[action] = counts.GetValueOrDefault(action) + 1;
        }

        foreach (var warning in scan.Warnings)
        {
            log.LogWarning("{Warning}", warning);
        }

        var vaults = scan.Files.Select(f => f.Path.Vault).Distinct().Count();
        log.LogInformation(
            "Seeded {Notes} notes in {Vaults} vaults: {Created} created, {Updated} updated, {Kept} kept as changed through MCP, {Trashed} left in the trash ({Skipped} files skipped)",
            scan.Files.Count, vaults, counts.GetValueOrDefault(SeedAction.Create), counts.GetValueOrDefault(SeedAction.Update),
            counts.GetValueOrDefault(SeedAction.Conflict), counts.GetValueOrDefault(SeedAction.SkipTrashed), scan.Warnings.Count);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task<SeedAction> SeedAsync(SeedFile file, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var note = await store.GetAsync(file.Path, ct);
        var inTrash = note is null && (await store.ListTrashAsync(file.Path, ct)).Any(t => t.Note.Path == file.Path);
        var action = SeedDecision.Decide(file.Content, note, inTrash);

        switch (action)
        {
            case SeedAction.Create:
                await store.TryCreateAsync(new StoredNote(file.Path, file.Content, 1, Writers.Seed, now, [], file.Content), ct);
                break;
            case SeedAction.Update:
                // Never coalesced: two restarts in ten minutes must not lose what the first one loaded.
                var next = NoteWriter.Next(note!, file.Content, Writers.Seed, now, keepCurrent: true) with { SeedContent = file.Content };
                await store.TryReplaceAsync(note!, next, ct);
                break;
            case SeedAction.Remember:
                await store.TryReplaceAsync(note!, note! with { SeedContent = file.Content }, ct);
                break;
            case SeedAction.Conflict:
                log.LogWarning(
                    "Kept {Path}: it changed on disk and through MCP since the last seed; the file was not loaded (history {Path} shows the versions)",
                    file.Path.Value, file.Path.Value);
                break;
            case SeedAction.SkipTrashed:
                log.LogInformation("Left {Path} in the trash; restore it to bring it back", file.Path.Value);
                break;
        }

        return action;
    }
}
