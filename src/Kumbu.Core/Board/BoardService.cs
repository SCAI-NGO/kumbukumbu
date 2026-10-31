// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Board;

/// <summary>An argument that is either given (possibly as null, which removes) or left out.</summary>
public readonly record struct Optional<T>
{
    public Optional(T value)
    {
        IsGiven = true;
        Value = value;
    }

    public bool IsGiven { get; }

    public T Value { get; }

    public static implicit operator Optional<T>(T value) => new(value);
}

/// <summary>
/// The board's write actions, for the MCP tool and the REST API alike. A move or update reads the card, changes only the
/// server's own frontmatter keys and writes it back with compare-and-swap, retrying on the new state: it needs no version
/// token and never loses what someone wrote into the card meanwhile.
/// </summary>
public sealed class BoardService(INoteStore store, NoteWriter writer, NoteMover mover, TimeProvider time, ICaller caller)
{
    private const string Tool = "board";
    private const int Attempts = 3;
    private const int MaxReason = 2000;
    private const int MaxTags = 20;
    private const int MaxTagLength = 64;

    public async Task<ShellResult> AddAsync(
        string vault, string title, string column, string? text, string? position, DateOnly? due, IReadOnlyList<string>? tags,
        CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (Refuse("board add", vaultPath) is { } refused) return refused;
        if (!TryTags(tags, out var tagList, out error)) return error;
        title = title.Normalize(NormalizationForm.FormC);
        var folder = BoardPaths.Folder(vaultPath.Name);
        if (TitleProblem(folder, title) is { } problem) return problem;
        var path = folder.Child(title + ".md");

        var content = (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (board, boardError) = await ReadBoardAsync(vaultPath.Name, ct);
            if (board is null) return boardError!;
            if (string.Equals(title, TitleOf(board.Definition), StringComparison.OrdinalIgnoreCase))
                return Error($"'{title}' is the board's own note (Board.md); choose another title");
            // Ignoring case: on a case-insensitive file system (Obsidian's, often) "old.md" and "Old.md" are one note.
            if (KnownCards(board).FirstOrDefault(p => string.Equals(TitleOf(p), title, StringComparison.OrdinalIgnoreCase)) is { } taken)
                return Error($"'{title}' exists already ({taken.Value}); move or update it instead", ShellErrorKind.Conflict);
            if (await store.GetAsync(path, ct) is not null) return Error($"'{title}' exists already ({path.Value}); move or update it instead", ShellErrorKind.Conflict);
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message(Tool, path)) with { Kind = ShellErrorKind.Conflict };
            if (!TryColumn(board, column, out var target, out error)) return error;
            if (target.NeedsReason) return Error($"{target.Name} needs a reason; add '{title}' elsewhere and move it there with one");

            var lane = LaneOf(board, target, except: null);
            if (!TryIndex(board, lane, target, position, null, out var index, out error)) return error;
            var (rank, respread) = Place(lane, index);

            List<(string Key, string? Yaml)> changes = [("column", Scalar(target.Name)), ("rank", Scalar(rank))];
            if (due is { } day) changes.Add(("due", Day(day)));
            if (tagList is { Count: > 0 }) changes.Add(("tags", FlowList(tagList)));
            changes.Add(("moved", FlowMap([(target.Name, Day(Today))])));
            if (!TryOwnedKeys(path, content, changes, out var written, out error)) return error;

            var changeset = await writer.ChangesetAsync(path, ct);
            var note = new StoredNote(path, written, 1, caller.Writer, writer.Now, [], Changeset: changeset, Ref: caller.Reference);
            var batch = new NoteBatch { Changeset = changeset, Creates = { note } };
            var (ready, refusal) = await AddRespreadAsync(batch, respread, changeset, ct);
            if (refusal is not null) return refusal;
            if (!ready) continue;
            if ((await store.TryCommitAsync(batch, caller.Writer, caller.Reference, writer.Now, ct)).Count > 0) continue;

            var (archived, kept) = await ArchiveAsync(vaultPath.Name, target, note, ct);
            return Written($"added '{title}' to {target.Name} ({index + 1} of {lane.Count + 1}){Respread(respread, target, lane.Count + 1)}{archived}", kept);
        }

        return Error($"the board kept changing while adding '{title}'; try again", ShellErrorKind.Conflict);
    }

    public async Task<ShellResult> MoveAsync(string vault, string card, string column, string? position, string? reason, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (Refuse("board move", vaultPath) is { } refused) return refused;
        reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        if (reason is { Length: > MaxReason }) return Error($"a reason has at most {MaxReason} characters; this one has {reason.Length}");

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (board, boardError) = await ReadBoardAsync(vaultPath.Name, ct);
            if (board is null) return boardError!;
            var (current, values, cardError) = await FindCardAsync(board, card, ct);
            if (current is null || values is null) return cardError!;
            var title = TitleOf(current.Path);
            if (!TryColumn(board, column, out var target, out error)) return error;

            var from = values.GetValueOrDefault("column") is string written ? board.Columns.Find(written.Trim()) : null;
            var entering = from != target;
            if (entering && target.NeedsReason && reason is null)
                return Error($"{target.Name} needs a reason; move '{title}' there with one");

            var lane = LaneOf(board, target, except: current.Path);
            if (!TryIndex(board, lane, target, position, current.Path, out var index, out error)) return error;
            var (rank, respread) = Place(lane, index);

            List<(string Key, string? Yaml)> changes = [("column", Scalar(target.Name)), ("rank", Scalar(rank))];
            if (entering) changes.Add(("moved", Moved(board.Columns, values.GetValueOrDefault("moved"), target.Name)));
            // The reason belongs to the stay in a reason column: given, it is written (or replaced); leaving, it goes.
            if (target.NeedsReason && reason is not null) changes.Add(("reason", Scalar(reason)));
            else if (!target.NeedsReason && entering && values.ContainsKey("reason")) changes.Add(("reason", null));
            if (!TryOwnedKeys(current.Path, current.Content, changes, out var content, out error)) return error;
            var changeset = await writer.ChangesetAsync(current.Path, ct);
            var next = NoteWriter.Next(current, content, caller.Writer, caller.Reference, writer.Now, changeset);

            if (respread.Count == 0)
            {
                if (!await store.TryReplaceAsync(current, next, ct)) continue;
            }
            else
            {
                var batch = new NoteBatch { Changeset = changeset, Replaces = { (current, next) } };
                var (ready, refusal) = await AddRespreadAsync(batch, respread, changeset, ct);
                if (refusal is not null) return refusal;
                if (!ready) continue;
                if ((await store.TryCommitAsync(batch, caller.Writer, caller.Reference, writer.Now, ct)).Count > 0) continue;
            }

            var (archived, kept) = await ArchiveAsync(vaultPath.Name, target, next, ct);
            var unused = reason is not null && !target.NeedsReason ? $"; {target.Name} takes no reason, so it was not written" : "";
            return Written($"moved '{title}' to {target.Name} ({index + 1} of {lane.Count + 1}){Respread(respread, target, lane.Count + 1)}{unused}{archived}", kept);
        }

        return Error($"'{card}' kept changing while moving it ({Attempts} tries); try again", ShellErrorKind.Conflict);
    }

    public async Task<ShellResult> UpdateAsync(
        string vault, string card, Optional<DateOnly?> due, Optional<IReadOnlyList<string>?> tags, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (Refuse("board update", vaultPath) is { } refused) return refused;
        if (!due.IsGiven && !tags.IsGiven) return Error("update needs at least one of due or tags, to set or remove (given as null, it removes)");
        IReadOnlyList<string>? tagList = null;
        if (tags.IsGiven && !TryTags(tags.Value, out tagList, out error)) return error;

        List<(string Key, string? Yaml)> changes = [];
        List<string> said = [];
        if (due.IsGiven)
        {
            changes.Add(("due", due.Value is { } day ? Day(day) : null));
            said.Add(due.Value is { } d ? $"due {Day(d)}" : "no due date");
        }

        if (tags.IsGiven)
        {
            changes.Add(("tags", tagList is { Count: > 0 } ? FlowList(tagList) : null));
            said.Add(tagList is { Count: > 0 } ? $"tags {string.Join(", ", tagList)}" : "no tags");
        }

        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            var (board, boardError) = await ReadBoardAsync(vaultPath.Name, ct);
            if (board is null) return boardError!;
            var (current, _, cardError) = await FindCardAsync(board, card, ct);
            if (current is null) return cardError!;

            if (!TryOwnedKeys(current.Path, current.Content, changes, out var content, out error)) return error;
            var next = NoteWriter.Next(current, content, caller.Writer, caller.Reference, writer.Now, await writer.ChangesetAsync(current.Path, ct));
            if (!await store.TryReplaceAsync(current, next, ct)) continue;
            return Written($"updated '{TitleOf(current.Path)}': {string.Join(", ", said)}", next);
        }

        return Error($"'{card}' kept changing while updating it ({Attempts} tries); try again", ShellErrorKind.Conflict);
    }

    /// <summary>Renames a column in <c>Board.md</c> and in every card's <c>column</c> and <c>moved</c>, the archive's included,
    /// in one commit: all or nothing.</summary>
    public async Task<ShellResult> RenameColumnAsync(string vault, string from, string to, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (Refuse("board rename_column", vaultPath) is { } refused) return refused;
        var (board, boardError) = await ReadBoardAsync(vaultPath.Name, ct);
        if (board is null) return boardError!;
        if (!TryColumn(board, from, out var column, out error)) return error;

        var name = (to ?? "").Trim().Normalize(NormalizationForm.FormC);
        if (name.Length is 0 or > BoardDefinition.MaxNameLength || name.Contains('/'))
            return Error($"'{to}' is not a column name; a name has 1 to {BoardDefinition.MaxNameLength} characters and no '/'");
        if (name == column.Name) return Error($"the column is called {name} already");
        if (board.Columns.Find(name) is { } other && other != column) return Error($"there is a column '{other.Name}' already; choose another name", ShellErrorKind.Conflict);

        // The columns come from the revision the commit checks, so a Board.md edited since the read can't be overwritten.
        var definition = await store.GetAsync(board.Definition, ct);
        var (current, _) = definition is null ? (null, null) : BoardDefinition.Parse(definition.Content);
        if (definition is null || current is null || current.Find(column.Name) != column)
            return Error($"{board.Definition.Value} changed meanwhile, so nothing was renamed; try again", ShellErrorKind.Conflict);
        var changeset = await writer.ChangesetAsync(board.Definition, ct);
        var columns = current.Columns.Select(c => c == column ? c with { Name = name } : c);
        var batch = new NoteBatch { Changeset = changeset };

        // Cards first: a card changed meanwhile is the likely conflict, and the batch fails as a whole either way.
        var folder = BoardPaths.Folder(vaultPath.Name);
        // The cards and the archive's notes; a note in another sub-folder is not the board's to change.
        var archive = BoardPaths.Archive(vaultPath.Name);
        var notes = (await store.ListNotesUnderAsync(folder, ct))
            .Where(p => p != board.Definition && p.IsNote && (DirectlyIn(p, folder) || DirectlyIn(p, archive)))
            .Order(Comparer<VaultPath>.Create((a, b) => string.CompareOrdinal(a.Value, b.Value)));
        foreach (var path in notes)
        {
            if (await store.GetAsync(path, ct) is not { } card) continue;
            var read = Frontmatter.Read(card.Content);
            if (read.Problem is not null) continue;

            List<(string Key, string? Yaml)> changes = [];
            if (read.Values.GetValueOrDefault("column") is string written && Same(written, column.Name)) changes.Add(("column", Scalar(name)));
            if (read.Values.GetValueOrDefault("moved") is IReadOnlyDictionary<string, object?> moved && moved.Keys.Any(k => Same(k, column.Name)))
                changes.Add(("moved", Renamed(moved, column.Name, name)));
            if (changes.Count == 0) continue;
            if (!TryOwnedKeys(card.Path, card.Content, changes, out var renamed, out error)) return error;
            batch.Replaces.Add((card, NoteWriter.Next(card, renamed, caller.Writer, caller.Reference, writer.Now, changeset)));
        }

        var cards = batch.Replaces.Count;
        var content = Frontmatter.Set(definition.Content, [("columns", FlowList(columns.Select(ColumnYaml), quote: false))]);
        batch.Replaces.Add((definition, NoteWriter.Next(definition, content, caller.Writer, caller.Reference, writer.Now, changeset)));

        var conflicts = await store.TryCommitAsync(batch, caller.Writer, caller.Reference, writer.Now, ct);
        if (conflicts.Count > 0)
            return Error($"{string.Join(", ", conflicts.Select(c => c.Value))} changed meanwhile, so nothing was renamed; try again", ShellErrorKind.Conflict);
        // The Instructions are the user's text: the rename leaves them alone, but says when they name the old column.
        var stale = current.Instructions is { } instructions
            && Regex.IsMatch(instructions, $@"(?<!\w){Regex.Escape(column.Name)}(?!\w)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return ShellResult.Ok($"{Tool}: renamed column {column.Name} to {name} in Board.md and {Count(cards, "card")}"
            + (stale ? $"; the Instructions in Board.md still name {column.Name}: edit them if they should follow" : ""));

        static bool DirectlyIn(VaultPath path, VaultPath dir) =>
            path.Segments.Count == dir.Segments.Count + 1 && path.Value.StartsWith(dir.Value + "/", StringComparison.Ordinal);

        static bool Same(string written, string column) => string.Equals(written.Trim(), column, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes <c>Board.md</c> from the template; only when there is none.</summary>
    public async Task<ShellResult> CreateAsync(string vault, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (Refuse("board create", vaultPath) is { } refused) return refused;
        var path = BoardPaths.Definition(vaultPath.Name);
        if (await store.GetAsync(path, ct) is not null) return Error($"{path.Value} exists already; 'board {Quote(vaultPath)}' shows it", ShellErrorKind.Conflict);

        var result = await writer.WriteAsync(path.Value, BoardTemplate.Text(vaultPath.Name), null, ct);
        if (result.IsError) return Error(result.Output.StartsWith("write: ", StringComparison.Ordinal) ? result.Output["write: ".Length..] : result.Output, ShellErrorKind.Conflict);
        return ShellResult.Ok($"board: created {path.Value} from the template; 'board {Quote(vaultPath)}' shows it") with { Version = result.Version };
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private async Task<(Board? Board, ShellResult? Error)> ReadBoardAsync(string vault, CancellationToken ct)
    {
        var (board, problem) = await BoardReader.ReadAsync(store, vault, ct);
        if (board is not null) return (board, null);
        var root = VaultPath.Root.Child(vault);
        return (null, problem == "no board"
            ? Error($"no board in {root.Value}; the board tool's create makes one when the user asks for it", ShellErrorKind.NotFound)
            : Error($"{BoardPaths.Definition(vault).Value} can't be read, so the board can't change: {problem}", ShellErrorKind.Conflict));
    }

    /// <summary>A card by its title (exactly, else ignoring case when that is unique) or its path, directly in the board's folder.</summary>
    private async Task<(StoredNote? Note, IReadOnlyDictionary<string, object?>? Values, ShellResult? Error)> FindCardAsync(
        Board board, string card, CancellationToken ct)
    {
        var folder = BoardPaths.Folder(board.Definition.Vault!);
        var path = ResolveCard(board, card);
        if (path is null || path.Segments.Count != folder.Segments.Count + 1 || !path.Value.StartsWith(folder.Value + "/", StringComparison.Ordinal)
            || !path.IsNote || path == board.Definition)
        {
            return (null, null, card.StartsWith('/')
                ? Error($"{card} is not a card; cards are the notes directly in {folder.Value}", ShellErrorKind.NotFound)
                : Error($"no card '{card}' in {folder.Value} ('board {Quote(VaultPath.Root.Child(board.Definition.Vault!))}' lists them)", ShellErrorKind.NotFound));
        }

        var note = await store.GetAsync(path, ct);
        if (note is null)
        {
            return (null, null, await store.IsDamagedAsync(path, ct)
                ? ShellResult.Error(Damage.Message(Tool, path)) with { Kind = ShellErrorKind.Conflict }
                : Error($"no card '{card}' in {folder.Value} ('board {Quote(VaultPath.Root.Child(board.Definition.Vault!))}' lists them)", ShellErrorKind.NotFound));
        }

        var read = Frontmatter.Read(note.Content);
        if (read.Problem is not null)
            return (null, null, Error($"{path.Value}: the frontmatter does not parse, so the board leaves it alone (fix it with edit): {read.Problem}", ShellErrorKind.Conflict));
        return (note, read.Values, null);
    }

    private static VaultPath? ResolveCard(Board board, string card)
    {
        card = card.Trim().Normalize(NormalizationForm.FormC);
        if (card.StartsWith('/')) return VaultPath.TryParse(card, out var given) ? given : null;
        if (card.Length == 0 || card.Contains('/')) return null;

        var folder = BoardPaths.Folder(board.Definition.Vault!);
        var exact = folder.Child(card + ".md");
        var known = KnownCards(board);
        if (known.Contains(exact)) return exact;
        var alike = known.Where(p => string.Equals(TitleOf(p), card, StringComparison.OrdinalIgnoreCase)).ToList();
        return alike.Count == 1 ? alike[0] : exact;
    }

    /// <summary>The notes directly in the board's folder: its cards and the ones it can't place.</summary>
    private static List<VaultPath> KnownCards(Board board)
    {
        var folder = BoardPaths.Folder(board.Definition.Vault!);
        return [.. board.Lanes.SelectMany(l => l.Cards).Select(c => c.Path).Concat(board.Problems.Select(p => p.Path))
            .Where(p => p.Segments.Count == folder.Segments.Count + 1)];
    }

    private static string TitleOf(VaultPath path) => path.Name.EndsWith(".md", StringComparison.Ordinal) ? path.Name[..^".md".Length] : path.Name;

    private static ShellResult? TitleProblem(VaultPath folder, string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return Error("a card needs a title (it becomes the note's name)");
        if (title.Contains('/')) return Error($"{folder.Value}/{title}.md: a card's title can't contain '/' (cards are the notes directly in {folder.Value})");
        if (title.StartsWith(' ')) return Error($"{folder.Value}/{title}.md: '{title}' starts with a space");
        if (title.EndsWith('.') || title.EndsWith(' '))
            return Error($"{folder.Value}/{title}.md: '{title}' ends with a dot or space, which Windows can't store");
        return NoteNames.Problem(folder.Child(title + ".md")) is { } problem ? Error($"{folder.Child(title + ".md").Value}: {problem}") : null;
    }

    private static bool TryVault(string vault, out VaultPath path, out ShellResult error)
    {
        error = ShellResult.Ok("");
        if (VaultPath.TryParse("/" + vault.Trim().Trim('/'), out var parsed) && parsed.Segments.Count == 1 && !vault.Trim().Trim('/').Contains('/'))
        {
            path = parsed;
            return true;
        }

        path = VaultPath.Root;
        error = Error($"'{vault}' is not a vault (ls / lists them)", ShellErrorKind.NotFound);
        return false;
    }

    private static bool TryColumn(Board board, string name, out BoardColumn column, out ShellResult error)
    {
        error = ShellResult.Ok("");
        column = board.Columns.Find((name ?? "").Trim())!;
        if (column is not null) return true;
        error = Error($"there is no column '{name}' (columns: {string.Join(", ", board.Columns.Columns.Select(c => c.Name))})");
        return false;
    }

    private static bool TryTags(IReadOnlyList<string>? tags, out IReadOnlyList<string>? result, out ShellResult error)
    {
        error = ShellResult.Ok("");
        result = null;
        if (tags is null) return true;
        var list = new List<string>();
        foreach (var raw in tags)
        {
            var tag = (raw ?? "").Trim().TrimStart('#');
            if (tag.Length == 0) continue;
            if (tag.Any(char.IsWhiteSpace))
            {
                error = Error($"tag '{raw}' contains a space; a tag is one word (Obsidian reads it so)");
                return false;
            }

            if (tag.Length > MaxTagLength)
            {
                error = Error($"tag '{raw}' is longer than {MaxTagLength} characters");
                return false;
            }

            if (!list.Contains(tag, StringComparer.Ordinal)) list.Add(tag);
        }

        if (list.Count > MaxTags)
        {
            error = Error($"a card has at most {MaxTags} tags; these are {list.Count}");
            return false;
        }

        result = list;
        return true;
    }

    /// <summary>The cards of <paramref name="column"/> in order, without the one being moved.</summary>
    private static List<Card> LaneOf(Board board, BoardColumn column, VaultPath? except) =>
        [.. board.Lanes.Single(l => l.Column == column).Cards.Where(c => c.Path != except)];

    private static bool TryIndex(Board board, List<Card> lane, BoardColumn column, string? position, VaultPath? moving, out int index, out ShellResult error)
    {
        error = ShellResult.Ok("");
        index = lane.Count;
        var text = position?.Trim() ?? "";
        if (text.Length == 0 || text == "bottom") return true;
        if (text == "top")
        {
            index = 0;
            return true;
        }

        var colon = text.IndexOf(':');
        var kind = colon < 0 ? text : text[..colon].Trim();
        if (colon < 0 || kind is not ("before" or "after"))
        {
            error = Error($"position '{position}' is top, bottom, before:<card> or after:<card>");
            return false;
        }

        var name = text[(colon + 1)..].Trim();
        var path = ResolveCard(board, name);
        if (path is not null && path == moving)
        {
            error = Error($"'{name}' is the card being moved; before: and after: name another card");
            return false;
        }

        var at = lane.FindIndex(c => c.Path == path);
        if (at < 0)
        {
            error = Error($"'{name}' is not in {column.Name}; before: and after: name a card of the column the card goes to");
            return false;
        }

        index = kind == "before" ? at : at + 1;
        return true;
    }

    /// <summary>The rank for a card at <paramref name="index"/> of <paramref name="lane"/>: one between its neighbours, or,
    /// when that would be too long or a neighbour has no valid rank, the whole lane spread anew (the other cards' new ranks).</summary>
    private static (string Rank, IReadOnlyList<(Card Card, string Rank)> Respread) Place(List<Card> lane, int index)
    {
        var before = index > 0 ? lane[index - 1] : null;
        var after = index < lane.Count ? lane[index] : null;
        if (before is not { Rank: null } && after is not { Rank: null })
        {
            try
            {
                var rank = Rank.Between(before?.Rank, after?.Rank);
                if (rank.Length <= Rank.MaxLength) return (rank, []);
            }
            catch (ArgumentException)
            {
                // Two cards with the same rank: spread the lane.
            }
        }

        var keys = Rank.Spread(lane.Count + 1);
        var others = lane.Select((card, i) => (card, keys[i < index ? i : i + 1])).Where(x => x.card.Rank != x.Item2).ToList();
        return (keys[index], others);
    }

    /// <summary>Adds the new ranks of the other cards to <paramref name="batch"/>; not ready when one of them is gone or no
    /// longer parses (the board changed: try again), refused when one can't take its new rank.</summary>
    private async Task<(bool Ready, ShellResult? Refused)> AddRespreadAsync(
        NoteBatch batch, IReadOnlyList<(Card Card, string Rank)> respread, Changeset changeset, CancellationToken ct)
    {
        foreach (var (card, rank) in respread)
        {
            var current = await store.GetAsync(card.Path, ct);
            if (current is null || Frontmatter.Read(current.Content).Problem is not null) return (false, null);
            if (!TryOwnedKeys(current.Path, current.Content, [("rank", Scalar(rank))], out var content, out var error)) return (false, error);
            batch.Replaces.Add((current, NoteWriter.Next(current, content, caller.Writer, caller.Reference, writer.Now, changeset)));
        }

        return (true, null);
    }

    private static string Respread(IReadOnlyList<(Card Card, string Rank)> respread, BoardColumn column, int count) =>
        respread.Count == 0 ? "" : $"; re-ranked the {count} cards of {column.Name}";

    /// <summary>The card's <c>moved</c> map with today for <paramref name="column"/>; keys that name a column (in any case)
    /// take the board's spelling, others stay as they are.</summary>
    private string Moved(BoardDefinition definition, object? existing, string column)
    {
        var entries = new List<(string Key, string Value)>();
        if (existing is IReadOnlyDictionary<string, object?> map)
        {
            foreach (var (key, value) in map)
            {
                if (value is not string day) continue;
                var name = definition.Find(key.Trim())?.Name ?? key;
                var at = entries.FindIndex(e => e.Key == name);
                if (at < 0) entries.Add((name, day));
                else if (string.CompareOrdinal(day, entries[at].Value) > 0) entries[at] = (name, day);
            }
        }

        var today = Day(Today);
        var index = entries.FindIndex(e => e.Key == column);
        if (index < 0) entries.Add((column, today));
        else entries[index] = (column, today);
        return FlowMap([.. entries.Select(e => (e.Key, Scalar(e.Value)))]);
    }

    /// <summary>After an add or move into an archivable column: while it holds more than the rule's <c>after</c>, its oldest
    /// cards by the day they entered it, among those there at least <c>minDays</c>, go to the archive with <c>mv</c> (links
    /// follow) and get <c>archived</c>. Never fails the add or move: a move that fails stops it and says why.</summary>
    /// <returns>What to add to the message, and <paramref name="written"/> as it is now (archived, perhaps).</returns>
    private async Task<(string Message, StoredNote Written)> ArchiveAsync(string vault, BoardColumn column, StoredNote written, CancellationToken ct)
    {
        if (!column.Archivable) return ("", written);
        var (board, _) = await BoardReader.ReadAsync(store, vault, ct);
        if (board is null) return ("", written);
        var rule = board.Columns.Archive;
        // The column may have gone or been renamed since the add or move; then there is nothing to tidy.
        var found = board.Lanes.SingleOrDefault(l => string.Equals(l.Column.Name, column.Name, StringComparison.OrdinalIgnoreCase));
        if (found.Cards is not { } lane || lane.Count <= rule.After) return ("", written);

        var latest = Today.AddDays(-rule.MinDays);
        var candidates = lane
            .Select(card => (card, Entered: card.Moved.Where(m => string.Equals(m.Key.Trim(), column.Name, StringComparison.OrdinalIgnoreCase))
                .Select(m => (DateOnly?)m.Value).Max()))
            .Where(c => c.Entered is { } day && day <= latest)
            .OrderBy(c => c.Entered)
            .Take(lane.Count - rule.After)
            .Select(c => c.card)
            .ToList();

        var archived = new List<string>();
        var unmarked = new List<string>();
        var stopped = "";
        foreach (var card in candidates)
        {
            var target = await ArchivePathAsync(vault, card.Title, ct);
            var result = await mover.MoveAsync(card.Path, target, rewriteLinks: true, ct);
            if (result.IsError)
            {
                stopped = $"; archiving stopped at '{card.Title}': {result.Output}";
                break;
            }

            var note = await MarkArchivedAsync(target, ct);
            if (note is null) unmarked.Add(card.Title);
            else archived.Add(target.Name[..^".md".Length]);
            if (card.Path == written.Path) written = note ?? await store.GetAsync(target, ct) ?? written;
        }

        // Named, so the agent can tell the user which cards went and cat them there.
        const int named = 5;
        var names = string.Join(", ", archived.Take(named).Select(t => $"'{t}'")) + (archived.Count > named ? $" and {archived.Count - named} more" : "");
        var message = archived.Count == 0 ? "" : $"; archived {names} to {BoardPaths.Archive(vault).Value}/";
        message += string.Concat(unmarked.Select(t => $"; '{t}' is in the archive but 'archived' could not be set"));
        return (message + stopped, written);
    }

    /// <summary><c>/Board/Archive/&lt;Title&gt;.md</c>, or with " 2", " 3", … when that name is taken: nothing is overwritten.</summary>
    private async Task<VaultPath> ArchivePathAsync(string vault, string title, CancellationToken ct)
    {
        var archive = BoardPaths.Archive(vault);
        for (var n = 1; ; n++)
        {
            var path = archive.Child((n == 1 ? title : $"{title} {n}") + ".md");
            if (await store.GetAsync(path, ct) is null && !await store.IsDamagedAsync(path, ct) && (await store.ListNotesUnderAsync(path, ct)).Count == 0)
                return path;
        }
    }

    /// <summary>Sets <c>archived</c> on the card at its new place, retrying on the new state as a move does.</summary>
    private async Task<StoredNote?> MarkArchivedAsync(VaultPath path, CancellationToken ct)
    {
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (await store.GetAsync(path, ct) is not { } current || Frontmatter.Read(current.Content).Problem is not null) return null;
            if (!TryOwnedKeys(path, current.Content, [("archived", Day(Today))], out var content, out _)) return null;
            var next = NoteWriter.Next(current, content, caller.Writer, caller.Reference, writer.Now, await writer.ChangesetAsync(path, ct));
            if (await store.TryReplaceAsync(current, next, ct)) return next;
        }

        return null;
    }

    /// <summary>A card's <c>moved</c> map with the keys that name <paramref name="from"/> (in any case) renamed; when two keys meet,
    /// the later day stays.</summary>
    private static string Renamed(IReadOnlyDictionary<string, object?> moved, string from, string to)
    {
        var entries = new List<(string Key, string Value)>();
        foreach (var (key, value) in moved)
        {
            if (value is not string day) continue;
            var name = string.Equals(key.Trim(), from, StringComparison.OrdinalIgnoreCase) ? to : key;
            var at = entries.FindIndex(e => e.Key == name);
            if (at < 0) entries.Add((name, day));
            else if (string.CompareOrdinal(day, entries[at].Value) > 0) entries[at] = (name, day);
        }

        return FlowMap([.. entries.Select(e => (e.Key, Scalar(e.Value)))]);
    }

    /// <summary>The note's content with the server's own keys changed, when that leaves frontmatter that parses, reads each of
    /// those keys back as written and fits the note limit; otherwise why not, and nothing is to be written.</summary>
    private static bool TryOwnedKeys(
        VaultPath path, string content, IReadOnlyList<(string Key, string? Yaml)> changes, out string written, out ShellResult error)
    {
        error = ShellResult.Ok("");
        written = Frontmatter.Set(content, changes);
        var read = Frontmatter.Read(written);
        if (read.Problem is not null)
        {
            error = Error($"{path.Value}: changing {string.Join(", ", changes.Select(c => c.Key))} would leave frontmatter that does not parse, "
                + $"so nothing was written; fix the note with edit: {read.Problem}", ShellErrorKind.Conflict);
            return false;
        }

        foreach (var (key, yaml) in changes)
        {
            var wanted = yaml is null ? null : Frontmatter.Read($"---\n{key}: {yaml}\n---\n").Values.GetValueOrDefault(key);
            var found = read.Values.TryGetValue(key, out var value);
            if (yaml is null ? !found : found && Same(wanted, value)) continue;
            error = Error($"{path.Value}: {key} would not read back as written, so nothing was written; fix the note's frontmatter with edit",
                ShellErrorKind.Conflict);
            return false;
        }

        var bytes = Encoding.UTF8.GetByteCount(written);
        if (bytes <= NoteWriter.MaxBytes) return true;
        error = Error($"{path.Value} would be {bytes} bytes; the limit is {NoteWriter.MaxBytes} (1 MiB)");
        return false;

        static bool Same(object? a, object? b) => (a, b) switch
        {
            (IReadOnlyList<object?> x, IReadOnlyList<object?> y) => x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second)),
            (IReadOnlyDictionary<string, object?> x, IReadOnlyDictionary<string, object?> y) =>
                x.Count == y.Count && x.All(p => y.TryGetValue(p.Key, out var v) && Same(p.Value, v)),
            _ => Equals(a, b),
        };
    }

    /// <summary>A column as <c>Board.md</c> lists it: its name, or <c>{ Name: [flags] }</c>.</summary>
    private static string ColumnYaml(BoardColumn column)
    {
        List<string> flags = [];
        if (column.Archivable) flags.Add("archivable");
        if (column.NeedsReason) flags.Add("reason");
        return flags.Count == 0 ? Scalar(column.Name) : $"{{ {Scalar(column.Name)}: {FlowList(flags)} }}";
    }

    private static string Count(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FlowList(IEnumerable<string> items, bool quote = true) => $"[{string.Join(", ", quote ? items.Select(Scalar) : items)}]";

    private static string FlowMap(IReadOnlyList<(string Key, string Yaml)> entries) =>
        $"{{ {string.Join(", ", entries.Select(e => $"{Scalar(e.Key)}: {e.Yaml}"))} }}";

    private static readonly HashSet<string> YamlWords =
        new(["null", "~", "true", "false", "yes", "no", "on", "off", "y", "n"], StringComparer.OrdinalIgnoreCase);

    /// <summary>A one-line YAML scalar: plain when it reads back as the same string, otherwise double-quoted. A value that
    /// starts with a digit is quoted unless it is a day, so that no reader (Obsidian's) takes a rank or tag for a number.</summary>
    private static string Scalar(string value)
    {
        var plain = value.Length > 0 && char.IsLetterOrDigit(value[0]) && value[^1] != ' ' && !YamlWords.Contains(value)
            && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')
            && (!char.IsAsciiDigit(value[0]) || DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _));
        if (plain) return value;

        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            quoted.Append(c switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\t' => "\\t",
                < ' ' => $"\\x{(int)c:X2}",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    private static string Quote(VaultPath path) => path.Value.Contains(' ') ? $"\"{path.Value}\"" : path.Value;

    /// <summary>The caller's refusal of <paramref name="operation"/> (<c>board create</c>, <c>board add</c>, …): named
    /// apart, since creating a board and renaming its columns change the vault's structure, the cards don't.</summary>
    private ShellResult? Refuse(string operation, VaultPath vaultPath) =>
        caller.Refuse(operation, vaultPath) is { } refused ? refused with { Kind = ShellErrorKind.Refused } : null;

    private static ShellResult Error(string message, ShellErrorKind kind = ShellErrorKind.Invalid) =>
        ShellResult.Error($"{Tool}: {message}") with { Kind = kind };

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok($"{Tool}: {message}") with { Version = new NoteVersion(note.Path.Value, note.Version) };
}
