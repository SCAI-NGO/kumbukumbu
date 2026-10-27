// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
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

    public async Task<ShellResult> AddAsync(
        string vault, string title, string column, string? text, string? position, DateOnly? due, IReadOnlyList<string>? tags,
        CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (caller.Refuse(Tool, vaultPath) is { } refused) return refused;
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
            if (path == board.Definition) return Error($"'{title}' is the board's own note (Board.md); choose another title");
            if (await store.GetAsync(path, ct) is not null) return Error($"'{title}' exists already ({path.Value}); move or update it instead");
            if (await store.IsDamagedAsync(path, ct)) return ShellResult.Error(Damage.Message(Tool, path));
            if (!TryColumn(board, column, out var target, out error)) return error;
            if (target.NeedsReason) return Error($"{target.Name} needs a reason; add '{title}' elsewhere and move it there with one");

            var lane = LaneOf(board, target, except: null);
            if (!TryIndex(board, lane, target, position, null, out var index, out error)) return error;
            var (rank, respread) = Place(lane, index);

            List<(string Key, string? Yaml)> changes = [("column", Scalar(target.Name)), ("rank", Scalar(rank))];
            if (due is { } day) changes.Add(("due", Day(day)));
            if (tagList is { Count: > 0 }) changes.Add(("tags", FlowList(tagList)));
            changes.Add(("moved", FlowMap([(target.Name, Day(Today))])));
            var written = Frontmatter.Set(content, changes);
            if (Encoding.UTF8.GetByteCount(written) > NoteWriter.MaxBytes)
                return Error($"the card would be {Encoding.UTF8.GetByteCount(written)} bytes; the limit is {NoteWriter.MaxBytes} (1 MiB)");

            var changeset = await writer.ChangesetAsync(path, ct);
            var note = new StoredNote(path, written, 1, caller.Writer, writer.Now, [], Changeset: changeset);
            var batch = new NoteBatch { Changeset = changeset, Creates = { note } };
            if (!await AddRespreadAsync(batch, respread, changeset, ct)) continue;
            if ((await store.TryCommitAsync(batch, caller.Writer, writer.Now, ct)).Count > 0) continue;

            var (archived, kept) = await ArchiveAsync(vaultPath.Name, target, note, ct);
            return Written($"added '{title}' to {target.Name} ({index + 1} of {lane.Count + 1}){Respread(respread, target, lane.Count + 1)}{archived}", kept);
        }

        return Error($"the board kept changing while adding '{title}'; try again");
    }

    public async Task<ShellResult> MoveAsync(string vault, string card, string column, string? position, string? reason, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (caller.Refuse(Tool, vaultPath) is { } refused) return refused;

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
            if (entering && target.NeedsReason && string.IsNullOrWhiteSpace(reason))
                return Error($"{target.Name} needs a reason; move '{title}' there with one");

            var lane = LaneOf(board, target, except: current.Path);
            if (!TryIndex(board, lane, target, position, current.Path, out var index, out error)) return error;
            var (rank, respread) = Place(lane, index);

            List<(string Key, string? Yaml)> changes = [("column", Scalar(target.Name)), ("rank", Scalar(rank))];
            if (entering) changes.Add(("moved", Moved(board.Columns, values.GetValueOrDefault("moved"), target.Name)));
            if (!string.IsNullOrWhiteSpace(reason)) changes.Add(("reason", Scalar(reason.Trim())));
            var changeset = await writer.ChangesetAsync(current.Path, ct);
            var next = NoteWriter.Next(current, Frontmatter.Set(current.Content, changes), caller.Writer, writer.Now, changeset);

            if (respread.Count == 0)
            {
                if (!await store.TryReplaceAsync(current, next, ct)) continue;
            }
            else
            {
                var batch = new NoteBatch { Changeset = changeset, Replaces = { (current, next) } };
                if (!await AddRespreadAsync(batch, respread, changeset, ct)) continue;
                if ((await store.TryCommitAsync(batch, caller.Writer, writer.Now, ct)).Count > 0) continue;
            }

            var (archived, kept) = await ArchiveAsync(vaultPath.Name, target, next, ct);
            return Written($"moved '{title}' to {target.Name} ({index + 1} of {lane.Count + 1}){Respread(respread, target, lane.Count + 1)}{archived}", kept);
        }

        return Error($"'{card}' kept changing while moving it ({Attempts} tries); try again");
    }

    public async Task<ShellResult> UpdateAsync(
        string vault, string card, Optional<DateOnly?> due, Optional<IReadOnlyList<string>?> tags, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (caller.Refuse(Tool, vaultPath) is { } refused) return refused;
        if (!due.IsGiven && !tags.IsGiven) return Error("update needs due or tags (null removes them)");
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

            var next = NoteWriter.Next(current, Frontmatter.Set(current.Content, changes), caller.Writer, writer.Now,
                await writer.ChangesetAsync(current.Path, ct));
            if (!await store.TryReplaceAsync(current, next, ct)) continue;
            return Written($"updated '{TitleOf(current.Path)}': {string.Join(", ", said)}", next);
        }

        return Error($"'{card}' kept changing while updating it ({Attempts} tries); try again");
    }

    /// <summary>Renames a column in <c>Board.md</c> and in every card's <c>column</c> and <c>moved</c>, the archive's included,
    /// in one commit: all or nothing.</summary>
    public async Task<ShellResult> RenameColumnAsync(string vault, string from, string to, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (caller.Refuse(Tool, vaultPath) is { } refused) return refused;
        var (board, boardError) = await ReadBoardAsync(vaultPath.Name, ct);
        if (board is null) return boardError!;
        if (!TryColumn(board, from, out var column, out error)) return error;

        var name = (to ?? "").Trim().Normalize(NormalizationForm.FormC);
        if (name.Length is 0 or > BoardDefinition.MaxNameLength || name.Contains('/'))
            return Error($"'{to}' is not a column name; a name has 1 to {BoardDefinition.MaxNameLength} characters and no '/'");
        if (name == column.Name) return Error($"the column is called {name} already");
        if (board.Columns.Find(name) is { } other && other != column) return Error($"there is a column '{other.Name}' already; choose another name");

        // The columns come from the revision the commit checks, so a Board.md edited since the read can't be overwritten.
        var definition = await store.GetAsync(board.Definition, ct);
        var (current, _) = definition is null ? (null, null) : BoardDefinition.Parse(definition.Content);
        if (definition is null || current is null || current.Find(column.Name) != column)
            return Error($"{board.Definition.Value} changed meanwhile, so nothing was renamed; try again");
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
            if (changes.Count > 0) batch.Replaces.Add((card, NoteWriter.Next(card, Frontmatter.Set(card.Content, changes), caller.Writer, writer.Now, changeset)));
        }

        var cards = batch.Replaces.Count;
        var content = Frontmatter.Set(definition.Content, [("columns", FlowList(columns.Select(ColumnYaml), quote: false))]);
        batch.Replaces.Add((definition, NoteWriter.Next(definition, content, caller.Writer, writer.Now, changeset)));

        var conflicts = await store.TryCommitAsync(batch, caller.Writer, writer.Now, ct);
        if (conflicts.Count > 0)
            return Error($"{string.Join(", ", conflicts.Select(c => c.Value))} changed meanwhile, so nothing was renamed; try again");
        return ShellResult.Ok($"{Tool}: renamed column {column.Name} to {name} in Board.md and {Count(cards, "card")}");

        static bool DirectlyIn(VaultPath path, VaultPath dir) =>
            path.Segments.Count == dir.Segments.Count + 1 && path.Value.StartsWith(dir.Value + "/", StringComparison.Ordinal);

        static bool Same(string written, string column) => string.Equals(written.Trim(), column, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Writes <c>Board.md</c> from the template; only when there is none.</summary>
    public async Task<ShellResult> CreateAsync(string vault, CancellationToken ct)
    {
        if (!TryVault(vault, out var vaultPath, out var error)) return error;
        if (caller.Refuse(Tool, vaultPath) is { } refused) return refused;
        var path = BoardPaths.Definition(vaultPath.Name);
        if (await store.GetAsync(path, ct) is not null) return Error($"{path.Value} exists already; 'board {Quote(vaultPath)}' shows it");

        var result = await writer.WriteAsync(path.Value, BoardTemplate.Text(vaultPath.Name), null, ct);
        if (result.IsError) return Error(result.Output.StartsWith("write: ", StringComparison.Ordinal) ? result.Output["write: ".Length..] : result.Output);
        return ShellResult.Ok($"board: created {path.Value} from the template; 'board {Quote(vaultPath)}' shows it") with { Version = result.Version };
    }

    private DateOnly Today => DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime);

    private async Task<(Board? Board, ShellResult? Error)> ReadBoardAsync(string vault, CancellationToken ct)
    {
        var (board, problem) = await BoardReader.ReadAsync(store, vault, ct);
        if (board is not null) return (board, null);
        var root = VaultPath.Root.Child(vault);
        return (null, problem == "no board"
            ? Error($"no board in {root.Value}; the board tool's create makes one when the user asks for it")
            : Error($"{BoardPaths.Definition(vault).Value} can't be read, so the board can't change: {problem}"));
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
                ? Error($"{card} is not a card; cards are the notes directly in {folder.Value}")
                : Error($"no card '{card}' in {folder.Value} ('board {Quote(VaultPath.Root.Child(board.Definition.Vault!))}' lists them)"));
        }

        var note = await store.GetAsync(path, ct);
        if (note is null)
        {
            return (null, null, await store.IsDamagedAsync(path, ct)
                ? ShellResult.Error(Damage.Message(Tool, path))
                : Error($"no card '{card}' in {folder.Value} ('board {Quote(VaultPath.Root.Child(board.Definition.Vault!))}' lists them)"));
        }

        var read = Frontmatter.Read(note.Content);
        if (read.Problem is not null)
            return (null, null, Error($"{path.Value}: the frontmatter does not parse, so the board leaves it alone (fix it with edit): {read.Problem}"));
        return (note, read.Values, null);
    }

    private static VaultPath? ResolveCard(Board board, string card)
    {
        card = card.Trim().Normalize(NormalizationForm.FormC);
        if (card.StartsWith('/')) return VaultPath.TryParse(card, out var given) ? given : null;
        if (card.Length == 0 || card.Contains('/')) return null;

        var folder = BoardPaths.Folder(board.Definition.Vault!);
        var exact = folder.Child(card + ".md");
        var known = board.Lanes.SelectMany(l => l.Cards).Select(c => c.Path).Concat(board.Problems.Select(p => p.Path))
            .Where(p => p.Segments.Count == folder.Segments.Count + 1).ToList();
        if (known.Contains(exact)) return exact;
        var alike = known.Where(p => string.Equals(TitleOf(p), card, StringComparison.OrdinalIgnoreCase)).ToList();
        return alike.Count == 1 ? alike[0] : exact;
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
        error = Error($"'{vault}' is not a vault (ls / lists them)");
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

            if (!list.Contains(tag, StringComparer.Ordinal)) list.Add(tag);
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

    /// <summary>Adds the new ranks of the other cards to <paramref name="batch"/>; false when one of them is gone or no longer parses.</summary>
    private async Task<bool> AddRespreadAsync(NoteBatch batch, IReadOnlyList<(Card Card, string Rank)> respread, Changeset changeset, CancellationToken ct)
    {
        foreach (var (card, rank) in respread)
        {
            var current = await store.GetAsync(card.Path, ct);
            if (current is null || Frontmatter.Read(current.Content).Problem is not null) return false;
            batch.Replaces.Add((current, NoteWriter.Next(current, Frontmatter.Set(current.Content, [("rank", Scalar(rank))]), caller.Writer, writer.Now, changeset)));
        }

        return true;
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

        var archived = 0;
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
            else archived++;
            if (card.Path == written.Path) written = note ?? await store.GetAsync(target, ct) ?? written;
        }

        var message = archived == 0 ? "" : $"; archived {Count(archived, "card")} to {BoardPaths.Archive(vault).Value}/";
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
            var next = NoteWriter.Next(current, Frontmatter.Set(current.Content, [("archived", Day(Today))]), caller.Writer, writer.Now,
                await writer.ChangesetAsync(path, ct));
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

    /// <summary>A one-line YAML scalar: plain when it reads back as the same string, otherwise double-quoted.</summary>
    private static string Scalar(string value)
    {
        var plain = value.Length > 0 && char.IsLetterOrDigit(value[0]) && value[^1] != ' ' && !YamlWords.Contains(value)
            && value.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.');
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

    private static ShellResult Error(string message) => ShellResult.Error($"{Tool}: {message}");

    private static ShellResult Written(string message, StoredNote note) =>
        ShellResult.Ok($"{Tool}: {message}") with { Version = new NoteVersion(note.Path.Value, note.Version) };
}
