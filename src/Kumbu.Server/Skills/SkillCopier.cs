// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;
using Kumbu.Core.Skills;
using Kumbu.Server.Api;
using Kumbu.Server.Attachments;
using Kumbu.Server.Auth;
using Kumbu.Server.Publishing;
using Kumbu.Server.Vaults;

namespace Kumbu.Server.Skills;

/// <summary><c>From</c>: a skill's folder ("/Skills/x", "/Work/_skills/x") or a system skill ("system:create-skill"); <c>To</c>:
/// the name of a vault; <c>Name</c>: the folder's name there, by default the source folder's.</summary>
public sealed record SkillCopyRequest(string From, string To, string? Name = null);

/// <summary>The copy's folder, in the caller's view.</summary>
public sealed record SkillCopiedView(string Path);

/// <summary>
/// Copy to… and Move to… of the Skills screen (skills design §5, Decision 21): a skill into the skill location of another
/// vault the caller may write, also one shared with her (Decision 34). The files are read from the one vault and written
/// to the other through the request's writers, so rights, the storage quota and the history apply as to any write; a
/// write that fails removes what this request wrote. A link copy's sealed code and origin go into the target vault's
/// record, and with a move leave the source's. The folder's name is the source folder's, never the manifest's.
/// </summary>
internal sealed class SkillCopier(EncryptedNoteStore store, NoteWriter writer, PublicationStore publishing, RequestCaller caller, ILogger? log = null)
{
    public const string SystemPrefix = "system:";

    /// <summary>The <see cref="ApiError.Type"/> of a target folder that exists: the web app asks for another name.</summary>
    public const string NameTaken = "name-taken";

    /// <summary>The <see cref="ApiError.Type"/> of a move of a skill shared by link: its link is withdrawn first.</summary>
    public const string Shared = "shared";

    public Task<VaultPath> CopyAsync(SkillCopyRequest request, CancellationToken ct) => RunAsync(request, move: false, ct);

    public Task<VaultPath> MoveAsync(SkillCopyRequest request, CancellationToken ct) => RunAsync(request, move: true, ct);

    /// <summary>The skill's folder in <paramref name="target"/>: <c>/&lt;vault&gt;/&lt;name&gt;</c> in the marked vault
    /// Skills, else <c>/&lt;vault&gt;/_skills/&lt;name&gt;</c>. A name with a <see cref="NameProblem"/> is refused here, 400,
    /// so no caller makes a folder of one: <see cref="VaultPath.Child"/> takes any text as one segment.</summary>
    public static VaultPath TargetOf(VaultAccess target, string folderName)
    {
        if (NameProblem(folderName) is { } problem) throw ApiErrors.BadRequest($"Choose another name: {problem}.");
        var vault = VaultPath.Root.Child(target.Name);
        return (target.IsSkillsVault ? vault : vault.Child(SkillPaths.LocationFolder)).Child(folderName);
    }

    /// <summary>Why a skill's folder can't take the name; null if it can.</summary>
    public static string? NameProblem(string name) =>
        string.IsNullOrWhiteSpace(name) ? "a skill's name isn't empty"
        : name.Contains('/', StringComparison.Ordinal) ? "a skill's name has no '/'"
        : name.Contains('\\', StringComparison.Ordinal) ? "a skill's name has no '\\'"
        : name.StartsWith('.') ? "a skill's name never starts with '.'"
        : NoteNames.NameProblem(VaultPath.Root.Child("x").Child(name));

    internal sealed record Source(VaultAccess? Access, VaultPath? Folder, string Name, IReadOnlyList<SourceFile> Files);

    /// <summary>A file relative to the skill's folder: a note's or text file's text, or an attachment's bytes.</summary>
    internal sealed record SourceFile(string Path, string? Text, byte[]? Bytes, (int W, int H)? Dimensions);

    private async Task<VaultPath> RunAsync(SkillCopyRequest request, bool move, CancellationToken ct)
    {
        var command = move ? "move" : "copy";
        var open = await caller.OpenVaultsAsync(ct);
        var marked = open.FirstOrDefault(v => v.IsSkillsVault)?.Name;
        var source = await SourceAsync(store, request.From, open, marked, ct);
        var target = open.FirstOrDefault(v => string.Equals(v.Name, request.To, StringComparison.Ordinal))
            ?? throw ApiErrors.NotFound($"There is no vault '{request.To}'.");
        var name = request.Name ?? source.Name;
        var folder = TargetOf(target, name);

        if (source.Folder is { } from && SkillPaths.LocationOf(from, marked) == SkillPaths.LocationOf(folder, marked))
        {
            throw ApiErrors.BadRequest($"{from.Value} is in {target.Name} already; choose another vault.");
        }

        if (move)
        {
            if (source.Folder is not { } moved) throw ApiErrors.BadRequest("A system skill stays where it is; copy it instead.");
            RequireWritable(moved.Vault!);
            if (((ICaller)caller).RefuseFolder("mv", moved) is { } refused) throw NoteApi.Refused(refused);
            // A skill shared by link stays where its link reads it from; a copy leaves it there.
            var stored = source.Access!.ToVault(moved).Value;
            if ((await publishing.ReadAsync(null, source.Access.Vault, ct)).Record.Publications.Any(p => p.Kind == PublicationKinds.Skill && string.Equals(p.Note, stored, StringComparison.Ordinal)))
            {
                throw new ApiException(StatusCodes.Status409Conflict, "Withdraw the link first: a shared skill stays where its link reads it.", type: Shared);
            }
        }

        await RequireFreeAsync(target, folder, command, ct);
        var attachments = await WriteAsync(folder, source.Files, async () =>
        {
            if (source is { Access: { } access, Folder: { } at }) await CarryStateAsync(access, at, target, folder, ct);
        }, ct);
        if (move) await LeaveAsync(attachments, source, folder, ct);
        return folder;
    }

    /// <summary>
    /// Add to my skills (skills design §3.12, Decision 6): the copy behind a link, <paramref name="opened"/> with
    /// <paramref name="code"/>, into the skill location of <paramref name="vault"/>, under the publication's folder name
    /// or <paramref name="name"/>. <c>kumbu-hash</c> covers the files as received, before <c>SKILL.md</c> gets its
    /// origin; <c>kumbu-source</c> names the link by its lookup id. The code itself is kept only sealed, in the target
    /// vault's record, beside the sealed origin (Decisions 16, 33, 34).
    /// </summary>
    public async Task<VaultPath> AddAsync(string code, OpenedSkill opened, string vault, string? name, CancellationToken ct)
    {
        var target = (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => string.Equals(v.Name, vault, StringComparison.Ordinal))
            ?? throw ApiErrors.NotFound($"There is no vault '{vault}'.");
        name ??= opened.Copy.Name;
        var folder = TargetOf(target, name);
        await RequireFreeAsync(target, folder, "copy", ct);

        var received = new List<(string Path, byte[] Bytes)>(opened.Copy.Files.Count);
        foreach (var file in opened.Copy.Files.OrderBy(f => f.Index))
        {
            received.Add((file.Path, (await publishing.OpenSkillFileAsync(opened, file.Index, ct))?.Bytes ?? throw ApiErrors.NotFound(SkillPageApi.NotFoundSentence)));
        }

        if (!received.Any(f => f.Path == SkillPaths.Manifest)) throw ApiErrors.NotFound(SkillPageApi.NotFoundSentence);
        await ReceiveAsync(target, folder, received, "link:" + Convert.ToHexStringLower(ShareCode.Lookup(code)), code, ct);
        return folder;
    }

    /// <summary>
    /// Install from a marketplace (skills spec §3.9): <paramref name="files"/>, the ones of the skill that Kumbu stores, into
    /// the skill location of <paramref name="vault"/> under <paramref name="name"/>, the listing's folder name unless the
    /// caller chose another. <c>kumbu-hash</c> covers these files as received; <c>kumbu-source</c> is
    /// <paramref name="source"/>, which names the commit; the sealed origin goes into the target vault's record (Decisions 33, 34).
    /// </summary>
    public async Task<VaultPath> InstallAsync(string vault, string name, IReadOnlyList<(string Path, byte[] Bytes)> files, string source, CancellationToken ct)
    {
        var target = (await caller.OpenVaultsAsync(ct)).FirstOrDefault(v => string.Equals(v.Name, vault, StringComparison.Ordinal))
            ?? throw ApiErrors.NotFound($"There is no vault '{vault}'.");
        var folder = TargetOf(target, name);
        await RequireFreeAsync(target, folder, "copy", ct);
        if (!files.Any(f => f.Path == SkillPaths.Manifest)) throw ApiErrors.BadRequest($"The skill has no {SkillPaths.Manifest} Kumbu can store.");
        await ReceiveAsync(target, folder, files, source, null, ct);
        return folder;
    }

    /// <summary>The files received, into <paramref name="folder"/>: <c>SKILL.md</c> with its origin, the rest as they came;
    /// then the sealed origin, and the code of a link copy, in the target vault's record.</summary>
    private async Task ReceiveAsync(VaultAccess target, VaultPath folder, IReadOnlyList<(string Path, byte[] Bytes)> received, string origin, string? code, CancellationToken ct)
    {
        var hash = SkillHash.Of(received.Select(f => (f.Path, (ReadOnlyMemory<byte>)f.Bytes)));
        var files = received.Select(f =>
            f.Path == SkillPaths.Manifest ? new SourceFile(f.Path, SkillManifest.WithOrigin(Encoding.UTF8.GetString(f.Bytes), origin, hash), null, null)
            : Inside(folder, f.Path).IsNote ? new SourceFile(f.Path, Encoding.UTF8.GetString(f.Bytes), null, null)
            : new SourceFile(f.Path, null, f.Bytes, Dimensions(Inside(folder, f.Path), f.Bytes)));

        var stored = target.ToVault(folder).Value;
        await WriteAsync(folder, [.. Ordered(files)], () => publishing.ChangeAsync(target, r => r with
        {
            SkillCodes = code is null ? [.. r.SkillCodes.Where(c => !Under(c.Path, stored))] : [.. r.SkillCodes.Where(c => !Under(c.Path, stored)), new SkillCode(stored, code)],
            Origins = [.. r.Origins.Where(o => !Under(o.Path, stored)), new SkillOrigin(stored, origin, hash)],
        }, ct), ct);
    }

    /// <summary>An image's width and height as its bytes say, never as anyone else told; null for another file, and for
    /// a header that doesn't read: the writer then stores the image without them.</summary>
    private static (int W, int H)? Dimensions(VaultPath path, byte[] bytes) =>
        AttachmentFormat.ForPath(path) is { Kind: AttachmentKind.Image } ? ImageHeader.Read(bytes, out _) : null;

    /// <summary>The target folder is hers to write and holds nothing yet.</summary>
    private async Task RequireFreeAsync(VaultAccess target, VaultPath folder, string command, CancellationToken ct)
    {
        RequireWritable(target.Name);
        if (((ICaller)caller).Refuse(command, folder) is { } refused) throw NoteApi.Refused(refused);
        if ((await store.ListNotesUnderAsync(folder, ct)).Count > 0 || (await StoreAttachments.UnderAsync(store, folder, ct)).Count > 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, $"{folder.Value} is taken; choose another name.", type: NameTaken);
        }
    }

    /// <summary>Every file into <paramref name="folder"/> through the request's writers, then <paramref name="state"/>
    /// (the sealed state of the copy); should any of it fail, what this wrote goes again.</summary>
    private async Task<AttachmentWriter> WriteAsync(VaultPath folder, IReadOnlyList<SourceFile> files, Func<Task> state, CancellationToken ct)
    {
        var attachments = writer.ForAttachments() ?? throw new InvalidOperationException("The server's store keeps attachments.");
        var written = new List<VaultPath>();
        try
        {
            foreach (var file in files)
            {
                var path = Inside(folder, file.Path);
                var result = file.Bytes is { } bytes
                    ? (await attachments.WriteAsync(path.Value, bytes, file.Dimensions, null, ct)).Result
                    : await writer.WriteAsync(path.Value, file.Text!, null, ct);
                if (result.IsError) throw Failed(result);
                written.Add(path);
            }

            await state();
        }
        catch
        {
            // RemoveAsync never throws: the error that stopped the copy (a 507 among them) is the answer.
            await RemoveAsync(attachments, written);
            throw;
        }

        return attachments;
    }

    /// <summary>The skill at <paramref name="from"/> as <paramref name="store"/> reads it: its folder's name and every file, <c>SKILL.md</c> first.</summary>
    internal static async Task<Source> SourceAsync(EncryptedNoteStore store, string from, IReadOnlyList<VaultAccess> open, string? marked, CancellationToken ct)
    {
        if (from.StartsWith(SystemPrefix, StringComparison.Ordinal))
        {
            var system = SystemSkills.Find(from[SystemPrefix.Length..]) ?? throw ApiErrors.NotFound($"There is no system skill '{from[SystemPrefix.Length..]}'.");
            return new Source(null, null, system.Name, [.. Ordered(system.Files.Select(f => new SourceFile(f.Path, f.Content, null, null)))]);
        }

        if (!VaultPath.TryParse(from.TrimEnd('/'), out var folder) || SkillPaths.FolderOf(folder, marked) != folder
            || open.FirstOrDefault(v => string.Equals(v.Name, folder.Vault, StringComparison.Ordinal)) is not { } access)
        {
            throw ApiErrors.NotFound($"There is no skill at {from}.");
        }

        var prefix = folder.Value.Length + 1;
        var files = new List<SourceFile>();
        foreach (var note in await store.ReadNotesUnderAsync(folder, ct)) files.Add(new SourceFile(note.Path.Value[prefix..], note.Content, null, null));
        foreach (var attachment in await StoreAttachments.UnderAsync(store, folder, ct))
        {
            var bytes = await StoreAttachments.ReadBytesAsync(store, attachment.Path, attachment.Current, ct)
                ?? throw new ContentMissingException($"The content of {attachment.Path.Value} is missing.");
            var dimensions = attachment.Current is { Width: { } w, Height: { } h } ? (w, h) : ((int W, int H)?)null;
            files.Add(new SourceFile(attachment.Path.Value[prefix..], null, bytes, dimensions));
        }

        if (files.Count == 0) throw ApiErrors.NotFound($"There is no skill at {from}.");
        return new Source(access, folder, folder.Name, [.. Ordered(files)]);
    }

    /// <summary><c>SKILL.md</c> first, so a copy that stops short has its manifest, then by path.</summary>
    private static IEnumerable<SourceFile> Ordered(IEnumerable<SourceFile> files) =>
        files.OrderBy(f => f.Path != SkillPaths.Manifest).ThenBy(f => f.Path, TextOrder.Default);

    private static VaultPath Inside(VaultPath folder, string relative) => relative.Split('/').Aggregate(folder, (path, segment) => path.Child(segment));

    private void RequireWritable(string vault)
    {
        if (caller.CanWrite(vault)) return;
        var sentence = caller.ReadOnlySentence(vault);
        throw new ApiException(StatusCodes.Status403Forbidden, char.ToUpperInvariant(sentence[0]) + sentence[1..] + ".", code: caller.ReadOnlyCode(vault) ?? Refusals.ReadOnly);
    }

    private static ApiException Failed(ShellResult result) => result.Kind switch
    {
        ShellErrorKind.Refused => NoteApi.Refused(result),
        ShellErrorKind.Conflict => new ApiException(StatusCodes.Status409Conflict, result.Output),
        ShellErrorKind.NotFound => ApiErrors.NotFound(result.Output),
        _ => ApiErrors.BadRequest(result.Output),
    };

    /// <summary>The source's sealed code and origin of the skill, in the target vault's record under the new folder.</summary>
    private async Task CarryStateAsync(VaultAccess source, VaultPath from, VaultAccess target, VaultPath to, CancellationToken ct)
    {
        var (stored, into) = (source.ToVault(from).Value, target.ToVault(to).Value);
        var record = (await publishing.ReadAsync(null, source.Vault, ct)).Record;
        var codes = record.SkillCodes.Where(c => Under(c.Path, stored)).Select(c => c with { Path = into + c.Path[stored.Length..] }).ToList();
        var origins = record.Origins.Where(o => Under(o.Path, stored)).Select(o => o with { Path = into + o.Path[stored.Length..] }).ToList();
        if (codes.Count == 0 && origins.Count == 0) return;
        await publishing.ChangeAsync(target, r => r with
        {
            SkillCodes = [.. r.SkillCodes.Where(c => !Under(c.Path, into)), .. codes],
            Origins = [.. r.Origins.Where(o => !Under(o.Path, into)), .. origins],
        }, ct);
    }

    /// <summary>
    /// A move's end: the source's sealed state out of its record first, so nothing restored from the trash takes it up, then
    /// its files to its vault's trash, <c>SKILL.md</c> first. Should the record not change, the source stays whole, as
    /// after a copy; should <c>SKILL.md</c> stay, its state goes back with it.
    /// </summary>
    private async Task LeaveAsync(AttachmentWriter attachments, Source source, VaultPath copied, CancellationToken ct)
    {
        var (access, folder) = (source.Access!, source.Folder!);
        var stored = access.ToVault(folder).Value;
        List<SkillCode> codes = [];
        List<SkillOrigin> origins = [];
        try
        {
            await publishing.ChangeAsync(access, r =>
            {
                codes = [.. r.SkillCodes.Where(c => Under(c.Path, stored))];
                origins = [.. r.Origins.Where(o => Under(o.Path, stored))];
                return codes.Count > 0 || origins.Count > 0
                    ? r with { SkillCodes = [.. r.SkillCodes.Where(c => !Under(c.Path, stored))], Origins = [.. r.Origins.Where(o => !Under(o.Path, stored))] }
                    : null;
            }, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.LogWarning("A skill move kept its source: its vault's record did not change ({Kind})", e.GetType().Name);
            throw new ApiException(StatusCodes.Status409Conflict, $"Copied to {copied.Value}, but {folder.Value} stayed where it was; remove it by hand if you meant to move it.");
        }

        var stayed = new List<string>();
        foreach (var file in source.Files)
        {
            var path = Inside(folder, file.Path);
            var removed = path.IsNote ? await writer.RemoveAsync(path, ct) : await attachments.RemoveAsync(path.Value, ct);
            if (removed.IsError) stayed.Add(path.Value);
        }

        if (stayed.Contains(folder.Child(SkillPaths.Manifest).Value) && (codes.Count > 0 || origins.Count > 0))
        {
            try
            {
                await publishing.ChangeAsync(access, r => r with { SkillCodes = [.. r.SkillCodes, .. codes], Origins = [.. r.Origins, .. origins] }, CancellationToken.None);
            }
            catch (Exception e)
            {
                log?.LogWarning("A skill move could not give its source back its sealed state ({Kind})", e.GetType().Name);
            }
        }

        if (stayed.Count > 0)
        {
            throw new ApiException(StatusCodes.Status409Conflict, $"Copied to {copied.Value}, but these stayed where they were: {string.Join(", ", stayed)}.");
        }
    }

    /// <summary>
    /// What a failed copy wrote, to the target's trash (there it stays for the trash's days, in the owner's vault for a
    /// member's copy): never cancelled, so a client gone away leaves nothing half-copied, and never throwing, so the
    /// failure the caller hears is the one that stopped the copy. What stays is logged by count, never by name or content.
    /// </summary>
    private async Task RemoveAsync(AttachmentWriter attachments, IReadOnlyList<VaultPath> written)
    {
        var stayed = 0;
        foreach (var path in written)
        {
            try
            {
                var removed = path.IsNote ? await writer.RemoveAsync(path, CancellationToken.None) : await attachments.RemoveAsync(path.Value, CancellationToken.None);
                if (removed.IsError) stayed++;
            }
            catch (Exception)
            {
                stayed++;
            }
        }

        if (stayed > 0) log?.LogWarning("A failed skill copy left {Count} of {Written} files it wrote", stayed, written.Count);
    }

    private static bool Under(string path, string folder) =>
        string.Equals(path, folder, StringComparison.Ordinal) || path.StartsWith(folder + "/", StringComparison.Ordinal);
}
