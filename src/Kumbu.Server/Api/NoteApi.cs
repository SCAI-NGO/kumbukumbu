// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.Json.Serialization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Links;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Core.Sh.Commands;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Kumbu.Server.Api;

/// <summary><c>embeddings</c>: the name of the embedding provider the vault's notes and searches go to: the one its
/// owner chose, or the server's default (<c>internal</c>, the model on this server, unless the admin set another). <c>pending</c>: the vault is being
/// re-encrypted; until that is done it answers 503 and its notes aren't counted (<c>notes</c> is 0). <c>canWrite</c>:
/// whether the caller may write to it (an API key or connection with only <c>read</c> may not).</summary>
public sealed record VaultView(string Name, int Notes, string Embeddings, bool Pending, bool CanWrite);

/// <summary>What would lose access if the vault were re-encrypted: the names of the API keys with access to it and of
/// the connected apps that list it.</summary>
public sealed record VaultAccessView(string[] ApiKeys, string[] Connections);

/// <summary>Re-encrypting one vault: the owner's current password.</summary>
public sealed record RekeyVaultRequest(string Password);

/// <summary>How many API keys were revoked and apps disconnected because they had the vault.</summary>
public sealed record RekeyVaultView(int RevokedApiKeys, int DisconnectedApps);

/// <summary>An embedding provider of this server. <c>external</c>: the text of every section and every search query of
/// a vault that uses it is sent to it. <c>available</c> false with a <c>reason</c> while it can't embed (still loading,
/// switched off, or failing). <c>default</c>: the provider of every vault whose owner never chose one.</summary>
public sealed record EmbeddingProviderView(string Name, string Label, string? Description, bool External, bool Available, string? Reason, bool Default);

/// <summary><c>provider</c>: the name of one of <c>GET /api/embeddings</c>, <c>internal</c> for the model on this server.</summary>
public sealed record SetEmbeddingsRequest(string Provider);

public sealed record CreateVaultRequest(string Name);

/// <summary>The owner's current password, and <c>confirm</c>: the vault's name, typed out exactly.</summary>
public sealed record DeleteVaultRequest(string Password, string Confirm);

/// <summary>A vault's notes and attachments as folders and files, each level folders first, then files, both in text order (as <c>ls</c>).
/// <c>damaged</c>: stored notes of the vault that don't open (their paths are what doesn't open, so they aren't in the tree).</summary>
public sealed record TreeView(TreeFolder Root, int Damaged);

/// <summary><c>path</c> is the folder's path from the root of the vault tree, e.g. <c>/Work/projects</c>; <c>notes</c>
/// holds its files, notes and attachments together.</summary>
public sealed record TreeFolder(string Name, string Path, IReadOnlyList<TreeFolder> Folders, IReadOnlyList<TreeNote> Notes);

/// <summary>A note or attachment; <c>size</c> in bytes (a note's UTF-8). <c>kind</c>: <c>note</c>, <c>image</c> or
/// <c>pdf</c>; <c>mediaType</c> <c>text/markdown</c> for a note, an attachment's from its checked format. An attachment
/// also has its <c>version</c> and <c>writtenAt</c> (the gallery orders by date, and trashes at the version it shows);
/// a note has neither, its entry is as before.</summary>
public sealed record TreeNote(
    string Name,
    string Path,
    int Size,
    string Kind = "note",
    string MediaType = NoteApi.NoteMediaType,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Version = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTime? WrittenAt = null);

/// <summary>A note; <c>path</c> from the root of the vault tree (<c>/Work/plan.md</c>), <c>version</c> what a write sends back.
/// Read with <c>?version=</c>, it is that version: <c>current</c> false for an earlier one (whose token a write
/// can't go on top of), with the writer and time of that version in <c>lastWriter</c> and <c>lastWriteAt</c>.
/// <c>lastWriterKind</c>, for sessions only: <c>user</c>, <c>device</c>, <c>agent</c>, <c>server</c>, or null.</summary>
public sealed record NoteView(string Path, string Content, string Version, string LastWriter, DateTime LastWriteAt, bool Current, string? LastWriterKind = null)
{
    public static async Task<NoteView> FromAsync(StoredNote note, WriterKinds kinds, CancellationToken ct) =>
        new(note.Path.Value, note.Content, note.Version, note.LastWriter, note.LastWriteAt, Current: true, await kinds.OfAsync(note.LastWriter, ct));
}

/// <summary>Without <c>version</c> a note is created; with it, the note is overwritten if it is still at that version.</summary>
public sealed record PutNoteRequest(string Content, string? Version = null);

public sealed record CurrentNoteView(string Content, string Version);

/// <summary>A write that lost against another one: the note as it is now, for the web app to show or merge.</summary>
public sealed record ConflictView(string Error, CurrentNoteView Current);

/// <summary>Paths from the root of the vault tree, of a note, an attachment or a folder. A <c>from</c> ending in
/// <c>/</c> moves the folder with every note and attachment below it; a <c>to</c> ending in <c>/</c> (or naming an existing folder) moves into it. Links that the move would
/// break are rewritten, unless <c>keepLinks</c>.</summary>
public sealed record MoveRequest(string From, string To, bool KeepLinks = false);

/// <summary><c>path</c> and <c>version</c> of the moved note or attachment; null for a folder move.</summary>
public sealed record MovedView(string Message, string? Path, string? Version);

/// <summary>A link as written, 1-based <c>line</c>. <c>kind</c>: <c>note</c> (<c>path</c> is where it leads), <c>self</c>
/// (<c>[[#Heading]]</c>), <c>ambiguous</c> (<c>candidates</c>), <c>attachment</c> (<c>path</c> is the vault's attachment it
/// leads to; null for a file the vault doesn't keep, e.g. a .docx) or <c>not_found</c>.</summary>
public sealed record LinkView(int Line, string Raw, string Target, string Kind, string? Path, IReadOnlyList<string> Candidates);

/// <summary>A line linking to the note, 1-based; <c>ambiguous</c>: its link matches several notes, this one among them.</summary>
public sealed record BacklinkView(string Path, int Line, string Text, bool Ambiguous);

/// <summary>A version of a note or attachment, newest first; <c>size</c> in bytes (a note's UTF-8), <c>changeset</c> the
/// short id of its changeset, if it is in one. <c>writerKind</c>, for sessions only, as <c>lastWriterKind</c> on a note.
/// <c>kind</c> and <c>mediaType</c> as in the tree; an attachment has no <c>lines</c> (0), and its version's bytes
/// come from <c>GET /api/files</c> with <c>?version=</c>.</summary>
public sealed record HistoryEntryView(
    string Version, DateTime WrittenAt, string Writer, int Lines, int Size, bool Current, string? Changeset, string? WriterKind = null,
    string Kind = "note", string MediaType = NoteApi.NoteMediaType);

/// <summary>A section found; lines 1-based and inclusive. <c>score</c>: comparable within one search, 1.0 for every
/// exact match, null when ranked by words only. <c>reason</c>: <c>title</c> (the query is in the note's name),
/// <c>exact</c> (the whole query is in the section, ignoring case), <c>all words</c> (every word of it is) or
/// <c>meaning</c>. <c>snippet</c>: the first line of the section with a word of the query, else its start.</summary>
public sealed record SearchResultView(string Path, string Address, int StartLine, int EndLine, string Snippet, double? Score, string Reason);

/// <summary><c>notIndexed</c>: notes in the scope not indexed for meaning yet (they rank by words only, or not at
/// all with <c>mode=meaning</c>). <c>notice</c>: a sentence when the ranking isn't what was asked for.</summary>
public sealed record SearchView(IReadOnlyList<SearchResultView> Results, int NotIndexed, string? Notice);

/// <summary>A section of an earlier version: <c>version</c> opens with <c>?version=</c> on the note; lines as a search
/// result's. <c>before</c>: the version before it, null when there was none or it is no longer kept (<c>beforeLost</c>).
/// <c>writer</c> without the tool ("liz", not "liz (mv)"), <c>writerKind</c> as on a note.</summary>
public sealed record EarlierResultView(
    string Path, string Version, DateTime WrittenAt, string Writer, int StartLine, int EndLine, string Snippet, string? Before, bool BeforeLost, string? WriterKind);

/// <summary>A note whose last write is by one of the user's agents, or by a device back after a long pause (see <c>ChangesAsync</c>).</summary>
public sealed record ChangeView(string Path, string Writer, DateTime LastWriteAt, string Version, string WriterKind = WriterKinds.Agent, DateTime? QuietSince = null, DateTime? ResumedAt = null);

/// <summary>
/// The vault and note endpoints under <c>/api</c> (accounts design §4): the same code paths as the <c>sh</c>
/// commands and the write tools, so the web app sees the notes, links, history and search an agent sees. Every
/// caller may read (a session, an API key, the dev token) what its credential opens; writes follow
/// <see cref="ICaller.CanWrite"/>. Creating or deleting a vault and the agent banner's changes need the user key:
/// sessions only.
/// </summary>
public static class NoteApi
{
    private const int DefaultResults = 10;
    private const int MaxResults = 50;
    private const int MaxVaultName = 64;

    /// <summary>What a note is, beside the attachments' media types.</summary>
    public const string NoteMediaType = "text/markdown";

    public static void MapNoteApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(ApiErrors.Filter);
        api.MapGet("/vaults", ListVaultsAsync).Errors(401, 503);
        api.MapPost("/vaults", CreateVaultAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 409);
        api.MapPost("/vaults/{vault}/delete", DeleteVaultAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 429);
        api.MapGet("/vaults/{vault}/access", AccessAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(401, 403, 404);
        api.MapPost("/vaults/{vault}/rekey", RekeyVaultAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 429, 503);
        api.MapGet("/vaults/{vault}/tree", TreeAsync).Errors(400, 401, 404, 503);
        api.MapPut("/vaults/{vault}/embeddings", SetEmbeddingsAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 503);
        api.MapGet("/embeddings", ListEmbeddingProviders).Errors(401, 503);

        api.MapGet("/notes/{vault}/{**path}", ReadAsync).Errors(400, 401, 404, 503);
        api.MapPut("/notes/{vault}/{**path}", WriteAsync).Errors(400, 401, 403, 404, 413, 503);
        api.MapDelete("/notes/{vault}/{**path}", DeleteAsync).Errors(400, 401, 403, 404, 409, 503);
        api.MapPost("/move", MoveAsync).Errors(400, 401, 403, 404, 409, 503);

        api.MapGet("/links/{vault}/{**path}", LinksAsync).Errors(400, 401, 404, 503);
        api.MapGet("/backlinks/{vault}/{**path}", BacklinksAsync).Errors(400, 401, 404, 503);
        api.MapGet("/history/{vault}/{**path}", HistoryAsync).Errors(400, 401, 404, 503);

        api.MapGet("/search", SearchAsync).Errors(400, 401, 404, 503);
        api.MapGet("/search/earlier", EarlierAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 404, 429, 503);
        api.MapGet("/changes", ChangesAsync).WithMetadata(SessionOnlyMetadata.Instance).Errors(400, 401, 403, 503);
    }

    private static async Task<Ok<VaultView[]>> ListVaultsAsync(EncryptedNoteStore store, RequestCaller caller, CancellationToken ct)
    {
        var vaults = (await store.UnlockedVaultsAsync(ct)).ToDictionary(v => v.Name, StringComparer.Ordinal);
        var views = new List<VaultView>();
        foreach (var name in (await store.ListVaultsAsync(ct)).Order(TextOrder.Default))
        {
            // A vault being re-encrypted has no notes to count yet; it is listed all the same.
            var notes = vaults[name].Pending ? 0 : (await store.ListNotesUnderAsync(VaultPath.Root.Child(name), ct)).Count;
            views.Add(new VaultView(name, notes, vaults[name].EmbeddingProvider, vaults[name].Pending, caller.CanWrite(name)));
        }

        return TypedResults.Ok(views.ToArray());
    }

    private static Ok<EmbeddingProviderView[]> ListEmbeddingProviders(SearchIndex index, VaultCatalog catalog) =>
        TypedResults.Ok(index.Providers.All
            .Select(p => new EmbeddingProviderView(
                p.Name, p.Label, p.Description, p.External, p.Driver.State == SemanticState.Ready, p.Driver.Reason, p.Name == catalog.DefaultEmbeddingProvider))
            .ToArray());

    /// <summary>
    /// The owner points a vault at another embedding provider (sessions only: a session opens its user's own vaults
    /// and nothing else). The vault is embedded again with it, in the background; until a note is, it is found by
    /// words. Choosing an external provider is what sends the vault's text there, so the web app asks first.
    /// </summary>
    private static async Task<Ok<VaultView>> SetEmbeddingsAsync(
        string vault, SetEmbeddingsRequest body, EncryptedNoteStore store, VaultCatalog catalog, SearchIndex index, RequestCaller caller, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        var unlocked = (await store.UnlockedVaultsAsync(ct)).FirstOrDefault(v => v.Name == root.Segments[0])
            ?? throw ApiErrors.NotFound($"There is no vault '{root.Segments[0]}'.");
        if (index.Providers.Get(body.Provider) is null)
        {
            throw ApiErrors.BadRequest($"This server has no embedding provider '{body.Provider}'; it has {string.Join(", ", index.Providers.All.Select(p => p.Name))}.");
        }

        if (unlocked.Pending || unlocked.EmbeddingProvider != body.Provider)
        {
            await catalog.SetEmbeddingProviderAsync(unlocked, body.Provider, ct);
            index.CatchUp(unlocked);
        }

        return TypedResults.Ok(new VaultView(unlocked.Name, unlocked.Notes.Count, unlocked.EmbeddingProvider, unlocked.Pending, caller.CanWrite(unlocked.Name)));
    }

    private static async Task<Created<VaultView>> CreateVaultAsync(CreateVaultRequest body, RequestCaller caller, VaultCatalog catalog, CancellationToken ct)
    {
        var name = body.Name.Normalize(NormalizationForm.FormC).Trim();
        if (name.Length is 0 or > MaxVaultName) throw ApiErrors.BadRequest($"A vault name has 1 to {MaxVaultName} characters.");
        if (name.Contains('/')) throw ApiErrors.BadRequest("A vault name can't contain '/'.");
        // A vault is the top folder of its notes, so its name follows the rules of every other path segment.
        if (NoteNames.Problem(VaultPath.Root.Child(name).Child("note.md")) is { } problem) throw ApiErrors.BadRequest($"That can't be a vault name: {problem}.");

        await catalog.CreateForUserAsync(name, caller.User!.Id, caller.UserKey!, ct, caller.CredentialReadAt);
        return TypedResults.Created($"/api/vaults/{Uri.EscapeDataString(name)}/tree", new VaultView(name, 0, catalog.DefaultEmbeddingProvider, Pending: false, CanWrite: true));
    }

    /// <summary>The vault with every note, its history, trash, checkpoints and embeddings, for good.</summary>
    private static async Task<NoContent> DeleteVaultAsync(
        string vault, DeleteVaultRequest body, RequestCaller caller, Deletion deletion, AccountLimits limits, CancellationToken ct)
    {
        // The same budget as the other password checks behind a session.
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        return await deletion.DeleteVaultAsync(caller.User!, caller.UserKey!, vault, body.Password, body.Confirm, ct) is not null
            ? TypedResults.NoContent()
            : throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
    }

    /// <summary>What re-encrypting the vault would take access from, for the owner to see before deciding.</summary>
    private static async Task<Ok<VaultAccessView>> AccessAsync(string vault, RequestCaller caller, Rekeying rekeying, CancellationToken ct)
    {
        var (keys, connections) = await rekeying.AccessAsync(caller.User!, caller.UserKey!, vault, ct)
            ?? throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
        return TypedResults.Ok(new VaultAccessView(keys, connections));
    }

    /// <summary>The vault under a new vault key; the answer waits for it. API keys with the vault are revoked and apps
    /// that list it disconnected: they can't be given the new key without their owner.</summary>
    private static async Task<Ok<RekeyVaultView>> RekeyVaultAsync(
        string vault, RekeyVaultRequest body, RequestCaller caller, Rekeying rekeying, AccountLimits limits, CancellationToken ct)
    {
        // The same budget as the other password checks behind a session.
        if (limits.PasswordCheck(caller.User!.Username) is { } retryAfter) throw ApiErrors.TooMany(retryAfter);
        var summary = await rekeying.RekeyVaultAsync(caller.User!, caller.UserKey!, vault, body.Password, ct)
            ?? throw ApiErrors.NotFound($"You have no vault named '{vault}'.");
        return TypedResults.Ok(new RekeyVaultView(summary.RevokedApiKeys.Count, summary.DisconnectedConnections.Count));
    }

    private static async Task<Ok<TreeView>> TreeAsync(string vault, INoteStore store, CancellationToken ct)
    {
        var root = ApiPaths.Parse(vault, null, note: false);
        await RequireVaultAsync(store, root, ct);
        var notes = await store.ReadNotesUnderAsync(root, ct);
        var attachments = await StoreAttachments.UnderAsync(store, root, ct);
        return TypedResults.Ok(new TreeView(TreeOf(root, notes, attachments), await store.CountDamagedAsync(root, ct)));
    }

    /// <summary>The folder <paramref name="dir"/> and everything below it as a tree; the checkpoints' tree is built the same way.</summary>
    internal static TreeFolder TreeOf(VaultPath dir, IEnumerable<Note> notes, IEnumerable<StoredAttachment> attachments) =>
        TreeOf(dir, notes.Select(n => (n.Path, new TreeNote(n.Path.Name, n.Path.Value, Encoding.UTF8.GetByteCount(n.Content))))
            .Concat(attachments.Select(a => (a.Path, TreeFile(a)))));

    private static TreeNote TreeFile(StoredAttachment file) =>
        new(file.Path.Name, file.Path.Value, (int)file.Current.Size, AttachmentFormat.KindOf(file.Path.Name), file.Current.MediaType, file.Current.Version, file.LastWriteAt);

    private static TreeFolder TreeOf(VaultPath dir, IEnumerable<(VaultPath Path, TreeNote File)> below)
    {
        var depth = dir.Segments.Count;
        var groups = below.GroupBy(n => n.Path.Segments.Count > depth + 1).ToDictionary(g => g.Key, g => g.ToList());
        var folders = groups.GetValueOrDefault(true, [])
            .GroupBy(n => n.Path.Segments[depth], StringComparer.Ordinal)
            .OrderBy(g => g.Key, TextOrder.Default)
            .Select(g => TreeOf(dir.Child(g.Key), g))
            .ToList();
        var files = groups.GetValueOrDefault(false, [])
            .OrderBy(n => n.Path.Name, TextOrder.Default)
            .Select(n => n.File)
            .ToList();
        return new TreeFolder(dir.Name, dir.Value, folders, files);
    }

    /// <summary><c>version</c>: a token from the note's history, to read that version instead of the current one
    /// (as <c>cat --version</c>); the web app diffs against it for the agent banner and the Activity sheet. A version
    /// of a note at this path that is in the trash now is found there too (never <c>current</c>), for a session only: the
    /// trash is the owner's, and keys never read a removed note's text.</summary>
    private static async Task<Ok<NoteView>> ReadAsync(
        string vault, string? path, string? version, RequestCaller caller, INoteStore store, WriterKinds kinds, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, note: true);
        var note = await store.GetAsync(notePath, ct);
        if (version is null) return TypedResults.Ok(await NoteView.FromAsync(note ?? throw await MissingAsync(store, notePath, ct), kinds, ct));

        // The same lookup as INoteReader.ReadVersionAsync (Versions.Find), with the version's writer and time.
        var found = note is null ? null : NoteHistory.Versions(note).FirstOrDefault(v => v.Version == version);
        if (found is null && caller.Kind == CallerKind.Session)
        {
            found = (await store.ListTrashAsync(notePath, ct))
                .Where(t => t.Note.Path == notePath)
                .SelectMany(t => NoteHistory.Versions(t.Note))
                .FirstOrDefault(v => v.Version == version) is { } trashed ? trashed with { Current = false } : null;
        }

        if (found is null)
        {
            throw note is null ? await MissingAsync(store, notePath, ct) : ApiErrors.NotFound($"{notePath.Value} has no version {version} (its history lists them).");
        }

        return TypedResults.Ok(new NoteView(notePath.Value, found.Content, found.Version, found.Writer, found.WrittenAt, found.Current, await kinds.OfAsync(found.Writer, ct)));
    }

    private static async Task<Results<Created<NoteView>, Ok<NoteView>, Conflict<ConflictView>>> WriteAsync(
        string vault, string? path, PutNoteRequest body, RequestCaller caller, INoteStore store, NoteWriter writer, WriterKinds kinds, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, note: true);
        await RequireVaultAsync(store, notePath, ct);
        Refuse(caller, "write", notePath);
        var bytes = Encoding.UTF8.GetByteCount(body.Content.Replace("\r\n", "\n", StringComparison.Ordinal));
        if (bytes > NoteWriter.MaxBytes)
        {
            throw new ApiException(StatusCodes.Status413PayloadTooLarge, $"A note can have at most {NoteWriter.MaxBytes} bytes (1 MiB); this one would have {bytes}.");
        }

        // Asked first, so that a conflict answers with the note as it is; the writer checks again when it writes.
        var existing = await store.GetAsync(notePath, ct);
        if (existing is not null && existing.Version != body.Version) return Conflict(existing, body.Version);
        if (existing is null && body.Version is not null) throw await MissingAsync(store, notePath, ct);

        var result = await writer.WriteAsync(notePath.Value, body.Content, body.Version, ct);
        var now = await store.GetAsync(notePath, ct);
        if (result.IsError)
        {
            // Another write came in between the check and this one.
            if (now is not null && now.Version != existing?.Version) return Conflict(now, body.Version);
            if (now is null && body.Version is not null) throw await MissingAsync(store, notePath, ct);
            throw ApiErrors.BadRequest(result.Output);
        }

        var view = await NoteView.FromAsync(now!, kinds, ct);
        return existing is null ? TypedResults.Created(ApiPaths.Url("notes", notePath), view) : TypedResults.Ok(view);
    }

    internal static Conflict<ConflictView> Conflict(StoredNote current, string? version) => TypedResults.Conflict(new ConflictView(
        version is null
            ? $"{current.Path.Value} exists already; to overwrite it, send its version."
            : $"{current.Path.Value} has changed since version {version} (it is now {current.Version}).",
        new CurrentNoteView(current.Content, current.Version)));

    /// <summary><c>version</c>: trash the note only if it is still at that version, else 409 with the note as it is
    /// now (as a stale <c>PUT</c>). Without it, whatever version the note is at.</summary>
    private static async Task<Results<NoContent, Conflict<ConflictView>>> DeleteAsync(
        string vault, string? path, string? version, RequestCaller caller, INoteStore store, NoteWriter writer, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, note: true);
        await RequireVaultAsync(store, notePath, ct);
        Refuse(caller, "rm", notePath);
        if (await store.GetAsync(notePath, ct) is null) throw await MissingAsync(store, notePath, ct);

        var result = await writer.RemoveAsync(notePath, ct, version);
        if (!result.IsError) return TypedResults.NoContent();
        if (version is not null)
        {
            // Changed since the version the caller saw (checked on the state that would have been trashed), or meanwhile.
            if (await store.GetAsync(notePath, ct) is { } now && now.Version != version) return Conflict(now, version);
            if (await store.GetAsync(notePath, ct) is null) throw await MissingAsync(store, notePath, ct);
        }

        throw new ApiException(StatusCodes.Status409Conflict, result.Output);
    }

    private static async Task<Ok<MovedView>> MoveAsync(MoveRequest body, RequestCaller caller, INoteStore store, NoteMover mover, CancellationToken ct)
    {
        if (!VaultPath.TryParse(body.From, out var source) || source.Segments.Count < 2)
        {
            throw ApiErrors.BadRequest("'from' is the path of a note or folder in a vault, e.g. /Work/plan.md or /Work/projects/.");
        }

        if (!VaultPath.TryParse(body.To, out var target) || target.IsRoot) throw ApiErrors.BadRequest("'to' is a path in the vault, e.g. /Work/archive/plan.md.");
        if (body.From.Contains('\\') || body.To.Contains('\\')) throw ApiErrors.BadRequest("Paths separate folders with '/', not '\\'.");
        // As in URL paths: a path names one place exactly, never one reached by stepping up.
        if (new[] { body.From, body.To }.SelectMany(p => p.Split('/')).Any(seg => seg is "." or ".."))
        {
            throw ApiErrors.BadRequest("Paths may not contain '.' or '..' segments.");
        }

        await RequireVaultAsync(store, source, ct);
        Refuse(caller, "mv", source);
        if (target.Vault != source.Vault) throw ApiErrors.BadRequest($"Notes can't move between vaults ({source.Vault} → {target.Vault}).");

        // The trailing '/' says "the folder", as in mv: a mistyped note name never moves a folder.
        var folder = body.From.EndsWith('/');
        if (!folder && body.To.EndsWith('/')) target = target.Child(source.Name);
        if (await store.GetAsync(source, ct) is null && await StoreAttachments.GetAsync(store, source, ct) is null
            && (await store.ListNotesUnderAsync(source, ct)).Count == 0 && (await StoreAttachments.UnderAsync(store, source, ct)).Count == 0)
        {
            throw await MissingAsync(store, source, ct);
        }

        // A name that contradicts the file's bytes is the request's fault, not the vault's state.
        if (!folder && AttachmentFormat.RenameProblem(source, target) is { } renameProblem) throw ApiErrors.BadRequest(renameProblem + ".");

        var result = folder
            ? await mover.MoveFolderAsync(source, target, !body.KeepLinks, ct)
            : await mover.MoveAsync(source, target, !body.KeepLinks, ct);
        // The source exists and the request is well-formed, so what's left is the vault's state in the way: a
        // target that exists, a folder into itself, a note where a folder is needed, a change meanwhile.
        if (result.IsError) throw new ApiException(StatusCodes.Status409Conflict, result.Output);
        return TypedResults.Ok(new MovedView(result.Output, result.Version?.Path, result.Version?.Token));
    }

    /// <summary>The links of a note; an attachment holds none (its <c>backlinks</c> are the notes that lead to it).</summary>
    private static async Task<Ok<LinkView[]>> LinksAsync(string vault, string? path, INoteStore store, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, PathKind.NoteOrAttachment);
        if (notePath.IsAttachment)
        {
            return await StoreAttachments.GetAsync(store, notePath, ct) is not null ? TypedResults.Ok(Array.Empty<LinkView>()) : throw await MissingAsync(store, notePath, ct);
        }

        var content = await store.ReadAsync(notePath, ct) ?? throw await MissingAsync(store, notePath, ct);
        return TypedResults.Ok(await LinkViewsAsync(store, notePath, content, ct));
    }

    /// <summary>The links of a note, resolved against <paramref name="notes"/>: the vault, or a checkpoint's view of it.</summary>
    internal static async Task<LinkView[]> LinkViewsAsync(INoteReader notes, VaultPath path, string content, CancellationToken ct) =>
        (await NoteLinks.OfAsync(notes, path, content, ct)).Select(l => new LinkView(
            l.Link.Line + 1,
            l.Link.Raw,
            l.Link.Target,
            KindText(l.Resolution.Kind),
            l.Resolution.Target?.Value,
            l.Resolution.Kind == LinkKind.Ambiguous ? [.. l.Resolution.Candidates.Select(c => c.Value).Order(TextOrder.Default)] : []))
            .ToArray();

    private static string KindText(LinkKind kind) => kind switch
    {
        LinkKind.Note => "note",
        LinkKind.Self => "self",
        LinkKind.Ambiguous => "ambiguous",
        LinkKind.Attachment => "attachment",
        _ => "not_found",
    };

    private static async Task<Ok<BacklinkView[]>> BacklinksAsync(string vault, string? path, INoteStore store, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, PathKind.NoteOrAttachment);
        var exists = notePath.IsAttachment ? await StoreAttachments.GetAsync(store, notePath, ct) is not null : await store.GetAsync(notePath, ct) is not null;
        if (!exists) throw await MissingAsync(store, notePath, ct);
        return TypedResults.Ok((await NoteLinks.ToAsync(store, notePath, ct))
            .Select(b => new BacklinkView(b.Source.Value, b.Line + 1, b.Text, b.Ambiguous))
            .ToArray());
    }

    private static async Task<Ok<HistoryEntryView[]>> HistoryAsync(string vault, string? path, INoteStore store, WriterKinds kinds, CancellationToken ct)
    {
        var notePath = ApiPaths.Parse(vault, path, PathKind.NoteOrAttachment);
        if (notePath.IsAttachment)
        {
            var attachment = await StoreAttachments.GetAsync(store, notePath, ct) ?? throw await MissingAsync(store, notePath, ct);
            var versions = new List<HistoryEntryView>();
            foreach (var v in NoteHistory.Versions(attachment))
            {
                versions.Add(new HistoryEntryView(
                    v.Version, v.WrittenAt, v.Writer, 0, (int)v.State.Size, v.Current, v.Changeset?.Short, await kinds.OfAsync(v.Writer, ct),
                    AttachmentFormat.KindOf(notePath.Name), v.State.MediaType));
            }

            return TypedResults.Ok(versions.ToArray());
        }

        var note = await store.GetAsync(notePath, ct) ?? throw await MissingAsync(store, notePath, ct);
        var entries = new List<HistoryEntryView>();
        foreach (var v in NoteHistory.Versions(note))
        {
            entries.Add(new HistoryEntryView(
                v.Version, v.WrittenAt, v.Writer, Lines.Split(v.Content).Count, Encoding.UTF8.GetByteCount(v.Content), v.Current, v.Changeset?.Short, await kinds.OfAsync(v.Writer, ct)));
        }

        return TypedResults.Ok(entries.ToArray());
    }

    /// <summary>
    /// <c>mode</c>: <c>hybrid</c> (default) and <c>meaning</c> rank as <c>semsearch</c> and <c>semsearch --meaning</c>
    /// do; <c>exact</c> finds the sections with a line containing the query, ignoring case (<c>rg -F -i</c>).
    /// <c>path</c> narrows the search to a vault, folder or note; <c>n</c> results, 1 to 50, default 10.
    /// </summary>
    /// <summary>
    /// Search in earlier versions: the sections of the notes' history (not their current text) that hold every word of
    /// <c>q</c>, ignoring case, skipping those still in the note today; newest first. <c>path</c> and <c>n</c> as for
    /// <c>/search</c>. Sessions only: old versions are the owner's to look through. Limited with a vault's activity
    /// (<see cref="AccountLimits.HistoryScansPerUser"/>): both read every note in their scope.
    /// </summary>
    private static async Task<Ok<EarlierResultView[]>> EarlierAsync(
        string? q, string? path, int? n, RequestCaller caller, AccountLimits limits, INoteStore store, WriterKinds kinds, CancellationToken ct)
    {
        var query = q?.Trim() ?? "";
        if (query.Length == 0) throw ApiErrors.BadRequest("Give a query (q).");
        var max = n ?? DefaultResults;
        if (max is < 1 or > MaxResults) throw ApiErrors.BadRequest($"n is 1 to {MaxResults}.");
        var scope = VaultPath.Root;
        if (path is not null && (!VaultPath.TryParse(path, out scope) || path.Contains('\\')))
        {
            throw ApiErrors.BadRequest("The path is a vault, folder or note, e.g. /Work or /Work/projects.");
        }

        if (limits.HistoryScan(caller.User!.Id) is { } retryAfter) throw ApiErrors.TooMany(retryAfter, AccountLimits.HistoryScansRefused);
        var notes = new List<StoredNote>();
        if (!scope.IsRoot && await store.GetAsync(scope, ct) is { } single) notes.Add(single);
        else
        {
            if (!scope.IsRoot) await RequireVaultAsync(store, scope, ct);
            foreach (var p in await store.ListNotesUnderAsync(scope, ct))
            {
                if (await store.GetAsync(p, ct) is { } note) notes.Add(note);
            }
        }

        var views = new List<EarlierResultView>();
        foreach (var h in EarlierSearch.Find(notes, query, max, ct))
        {
            views.Add(new EarlierResultView(h.Path.Value, h.Version, h.WrittenAt, h.Writer, h.StartLine, h.EndLine, h.Snippet, h.Before, h.BeforeLost, await kinds.OfAsync(h.Writer, ct)));
        }

        return TypedResults.Ok(views.ToArray());
    }

    private static async Task<Ok<SearchView>> SearchAsync(string? q, string? mode, string? path, int? n, INoteStore store, ISearchIndex index, CancellationToken ct)
    {
        var query = q?.Trim() ?? "";
        if (query.Length == 0) throw ApiErrors.BadRequest("Give a query (q).");
        var exact = mode is "exact";
        var searchMode = mode switch
        {
            null or "hybrid" or "exact" => SearchMode.Hybrid,
            "meaning" => SearchMode.Meaning,
            _ => throw ApiErrors.BadRequest("The mode is 'hybrid', 'meaning' or 'exact'."),
        };
        var max = n ?? DefaultResults;
        if (max is < 1 or > MaxResults) throw ApiErrors.BadRequest($"n is 1 to {MaxResults}.");

        var scope = VaultPath.Root;
        if (path is not null && (!VaultPath.TryParse(path, out scope) || path.Contains('\\')))
        {
            throw ApiErrors.BadRequest("The path is a vault, folder or note, e.g. /Work or /Work/projects.");
        }

        IReadOnlyList<Note> notes;
        if (!scope.IsRoot && await store.ReadAsync(scope, ct) is { } single) notes = [new Note(scope, single)];
        else
        {
            if (!scope.IsRoot) await RequireVaultAsync(store, scope, ct);
            notes = await store.ReadNotesUnderAsync(scope, ct);
        }

        if (exact)
        {
            return TypedResults.Ok(new SearchView(
                [.. SectionSearch.Exact(notes, query, max).Select(m => Result(m.Chunk, SectionSearch.Snippet(m.Chunk, query), 1.0, InTitle(query, m.Chunk) ? "title" : "exact"))],
                0,
                null));
        }

        var ranked = await SectionSearch.RankAsync(notes, scope, query, searchMode, max, index, ct);
        var reason = ranked.Semantic?.Reason ?? "search by meaning isn't available";
        var notice = ranked switch
        {
            { MeaningUnavailable: true } => $"Search by meaning isn't available: {reason}.",
            { Semantic.State: not SemanticState.Ready } => $"Ranked by words only: {reason}.",
            { FellBackToWords: true } => "Ranked by words only: nothing is indexed for meaning yet.",
            { Semantic: { State: SemanticState.Ready, WordsOnlyVaults.Count: > 0, Reason: { } partial } } when searchMode == SearchMode.Meaning =>
                $"Search by meaning isn't available for some vaults, so their notes aren't in these results ({partial}). Hybrid or exact search finds them by words.",
            { Semantic: { State: SemanticState.Ready, WordsOnlyVaults.Count: > 0, Reason: { } partial } } =>
                $"Search by meaning isn't available for some vaults, so their notes are ranked by words ({partial}).",
            _ => null,
        };
        return TypedResults.Ok(new SearchView(
            [.. ranked.Hits.Select(h => Result(h.Chunk, SectionSearch.Snippet(h.Chunk, query), h.Score, Reason(query, h.Chunk)))],
            ranked.Semantic?.Pending ?? 0,
            notice));

        static SearchResultView Result(Chunk c, string snippet, double? score, string reason) =>
            new(c.Path.Value, c.Address, c.StartLine + 1, c.EndLine, snippet, score, reason);
    }

    /// <summary>The reason tags of MOBILE_EDITOR §4, strongest first: the query in the note's name, the whole query
    /// in the section (as <c>mode=exact</c> would find it), every word of it there, or only the meaning.</summary>
    private static string Reason(string query, Chunk chunk)
    {
        if (InTitle(query, chunk)) return "title";
        var literal = Rg.Pattern([query], fixedStrings: true, wordRegexp: false, ignoreCase: true);
        if (chunk.Text.Split('\n').Any(literal.IsMatch)) return "exact";
        var terms = Bm25.QueryTerms(query);
        var words = Bm25.Terms(chunk.Text).ToHashSet(StringComparer.Ordinal);
        return terms.Count > 0 && terms.All(words.Contains) ? "all words" : "meaning";
    }

    private static bool InTitle(string query, Chunk chunk)
    {
        var name = chunk.Path.Name;
        if (name.EndsWith(".md", StringComparison.Ordinal)) name = name[..^3];
        return name.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The agent banner: notes in the caller's vaults whose last write is by one of the user's agents, API keys or
    /// connections (revoked ones too: their writes stay theirs), after <paramref name="since"/>, newest first. A
    /// device's writes are the user's own (spec §5a) and don't count, unless the device wrote again after a long pause
    /// since then (<see cref="WriterKinds.ResumedAsync"/>), so that a stolen device key's writes stand out: those come
    /// with <c>writerKind</c> <c>device</c>, the device having written nothing between <c>quietSince</c> and
    /// <c>resumedAt</c>. The names are sealed under the user key, hence sessions only.
    /// </summary>
    private static async Task<Ok<ChangeView[]>> ChangesAsync(DateTimeOffset since, INoteStore store, WriterKinds kinds, CancellationToken ct)
    {
        var from = since.UtcDateTime;
        var changes = new List<ChangeView>();
        foreach (var path in await store.ListNotesUnderAsync(VaultPath.Root, ct))
        {
            if (await store.GetAsync(path, ct) is not { } note || note.LastWriteAt <= from) continue;
            var writer = Writers.Identity(note.LastWriter);
            if (await kinds.OfAsync(writer, ct) == WriterKinds.Agent) changes.Add(new ChangeView(path.Value, writer, note.LastWriteAt, note.Version));
            else if (await kinds.ResumedAsync(writer, note.LastWriteAt, from, ct) is { } resumed)
            {
                changes.Add(new ChangeView(path.Value, writer, note.LastWriteAt, note.Version, WriterKinds.Device, resumed.QuietSince, resumed.At));
            }
        }

        return TypedResults.Ok(changes.OrderByDescending(c => c.LastWriteAt).ThenBy(c => c.Path, TextOrder.Default).ToArray());
    }

    /// <summary>A 404 for a vault the caller has no way into, before anything else is said about the path.</summary>
    internal static async Task RequireVaultAsync(INoteStore store, VaultPath path, CancellationToken ct)
    {
        if (!(await store.ListVaultsAsync(ct)).Contains(path.Segments[0])) throw ApiErrors.NotFound($"There is no vault '{path.Segments[0]}'.");
    }

    /// <summary>The refusal of a write to a vault the caller may only read: the same sentence the tools give.</summary>
    internal static void Refuse(ICaller caller, string command, VaultPath path)
    {
        if (caller.Refuse(command, path) is { } refused) throw new ApiException(StatusCodes.Status403Forbidden, refused.Output);
    }

    internal static async Task<ApiException> MissingAsync(INoteStore store, VaultPath path, CancellationToken ct) =>
        ApiErrors.NotFound(await store.IsDamagedAsync(path, ct)
            ? Damage.Message("read", path)
            : $"There is no {(path.IsAttachment ? "file" : "note")} at {path.Value}{await Moves.HintAsync(store, path, ct)} (if it was removed, it is in the trash).");
}

/// <summary>What a URL's path must name.</summary>
internal enum PathKind
{
    Folder,
    Note,
    Attachment,
    NoteOrAttachment,
}

/// <summary>Note, attachment and folder paths in URLs: <c>{vault}/{path}</c>, each segment URL-encoded.</summary>
internal static class ApiPaths
{
    public static VaultPath Parse(string vault, string? rest, bool note) => Parse(vault, rest, note ? PathKind.Note : PathKind.Folder);

    /// <summary>
    /// The path of <paramref name="rest"/> in <paramref name="vault"/> (a trailing '/' allowed for a folder). Empty,
    /// '.' and '..' segments and backslashes are refused rather than resolved: a URL names one path exactly, and
    /// one that steps out of its vault is a mistake or an attempt. Each segment is NFC-normalised (<see cref="VaultPath.Child"/>),
    /// so a name a phone sends decomposed names the same note or file as its composed spelling.
    /// </summary>
    public static VaultPath Parse(string vault, string? rest, PathKind kind)
    {
        var segments = new List<string> { vault };
        if (!string.IsNullOrEmpty(rest)) segments.AddRange((kind == PathKind.Folder ? rest.TrimEnd('/') : rest).Split('/'));
        if (segments.Any(s => s is "" or "." or ".." || s.Contains('\\')))
        {
            throw ApiErrors.BadRequest(Refused);
        }

        var path = segments.Aggregate(VaultPath.Root, (p, s) => p.Child(s));
        return kind switch
        {
            PathKind.Note when !path.IsNote => throw ApiErrors.BadRequest($"{path.Value} is no note: note paths end in .md."),
            PathKind.Attachment when path.IsNote => throw ApiErrors.BadRequest($"{path.Value} is a note: /api/notes reads and writes it."),
            PathKind.Attachment when !path.IsAttachment => throw ApiErrors.BadRequest($"{path.Value}: {AttachmentFormat.UnsupportedMessage}."),
            PathKind.NoteOrAttachment when !path.IsNote && !path.IsAttachment =>
                throw ApiErrors.BadRequest($"{path.Value} is neither a note (.md) nor a file Kumbu² keeps (PDF, PNG, JPEG, GIF, WebP)."),
            _ => path,
        };
    }

    private const string Refused = "A path may not contain empty, '.' or '..' segments or backslashes.";

    /// <summary>
    /// Kestrel resolves '.' and '..' segments, also sent as <c>%2E</c>, before routing: <c>/api/notes/V/a/../b.md</c>
    /// arrives as <c>/api/notes/V/b.md</c>, so <see cref="Parse"/> never sees them over HTTP. This middleware looks at
    /// the target as the client sent it and refuses an <c>/api</c> URL with such a segment, as Parse would.
    /// </summary>
    public static async Task RefuseDotSegmentsAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            && HasDotSegment(context.Features.Get<IHttpRequestFeature>()?.RawTarget))
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new ApiError(Refused));
            return;
        }

        await next(context);
    }

    /// <summary>Whether the path of a request target (origin or absolute form, the query left out) has a '.' or '..'
    /// segment, percent-encoded or not. An encoded slash separates no segments, for Kestrel neither.</summary>
    internal static bool HasDotSegment(string? target)
    {
        if (string.IsNullOrEmpty(target)) return false;
        var path = target.Split('?', '#')[0];
        if (!path.StartsWith('/'))
        {
            var scheme = path.IndexOf("://", StringComparison.Ordinal);
            var slash = scheme < 0 ? -1 : path.IndexOf('/', scheme + 3);
            if (slash < 0) return false;
            path = path[slash..];
        }

        return path.Split('/').Any(s => Uri.UnescapeDataString(s) is "." or "..");
    }

    public static string Url(string kind, VaultPath path) => $"/api/{kind}/{string.Join('/', path.Segments.Select(Uri.EscapeDataString))}";
}
