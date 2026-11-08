// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Search;
using Kumbu.Core.Skills;

namespace Kumbu.Core.Sh.Commands;

/// <summary>
/// <c>skills ls|find|cat|check|copy</c>: the skills the caller can see, found for a request, read and checked. Nothing here fetches, shares,
/// installs, moves a skill between vaults or takes an update; those are the user's, in the Skills screen. The one write
/// is <c>copy</c> of a built-in skill, through the caller's <see cref="NoteWriter"/>, so only where she may write.
/// Paths are taken as given: <see cref="Skill.Line"/> cleans what it shows, never what is looked up.
/// </summary>
public sealed class SkillsCommand(SkillDirectory skills, INoteStore notes, NoteWriter writer, ISearchIndex search, ISkillUpdates updates, WriterMarks? marks = null) : ICommand
{
    private const string Subcommands = "ls, find, cat, copy, check";

    /// <summary>How a built-in skill is named when one of the caller's has its name.</summary>
    private const string SystemPrefix = "system:";

    private const string NotInAFolder = "is not in a skill's folder (/Skills/<name>/ or /<vault>/_skills/<name>/)";

    public string Name => "skills";

    public string Usage => "skills ls|find|cat|copy|check";

    public string Summary => "find, read and check the user's skills";

    public string Help => """
        skills ls [/<vault>]                 every skill you can see: name, where it is, who last changed it, its description
        skills find "<what the user wants>"  the skills that fit a request, best first
        skills cat <name|path>[/<file>]      a skill's SKILL.md, or one of its files
        skills cat system:<name>[/<file>]    a built-in skill, also when a skill of yours has its name
        skills copy <system-skill> [/<vault>]  copy a built-in skill to /Skills/ (default) or to a vault's _skills/
        skills check [<path>]                check skills against the rules of SKILL.md
        A skill is a folder with SKILL.md and its files: /Skills/<name>/ for the user's own skills,
        /<vault>/_skills/<name>/ for a vault's, and built-in ones (system). Before a task, run skills find
        with the user's request and follow a skill whose description fits it; what the user says comes first.
        A skill last changed by another user is theirs: check that it fits before you follow it.
        skills only reads and finds: bringing skills in, sharing them, moving them between vaults and taking
        updates are the user's, in Kumbu²'s Skills screen. Change a skill's files with write and edit.

        skills find "write a release post"
        skills cat write-as-alice
        skills cat write-as-alice/examples/post.md
        skills copy create-skill
        """;

    public bool ReadsInput => false;

    /// <summary>The index <c>skills find</c> ranks by meaning with.</summary>
    internal ISearchIndex Search { get; } = search;

    public async Task<ShellResult> RunAsync(IReadOnlyList<string> args, string? input, CancellationToken ct)
    {
        if (args.Count == 0) return Invalid($"skills: give {Subcommands} (help skills)");
        var command = $"skills {args[0]}";
        var operands = args.Skip(1).ToList();
        if (args[0] is "ls" or "cat" or "check" or "copy" && CommandArgs.CheckOptions(command, operands) is { } optionError) return optionError with { Kind = ShellErrorKind.Invalid };
        return args[0] switch
        {
            "ls" when operands.Count <= 1 => await ListAsync(operands.FirstOrDefault(), ct),
            "cat" when operands.Count == 1 => await CatAsync(operands[0], ct),
            "find" when operands.Count == 1 => await FindSkillsAsync(operands[0], ct),
            "find" => Invalid(operands.Count == 0 ? "skills find: missing operand (help skills)" : "skills find: give the request as one quoted argument"),
            "check" when operands.Count <= 1 => await CheckAsync(operands.FirstOrDefault(), ct),
            "copy" when operands.Count is 1 or 2 => await CopyAsync(operands[0], operands.ElementAtOrDefault(1), ct),
            "ls" or "check" => Invalid($"{command}: too many arguments{CommandArgs.QuoteHint}"),
            "cat" or "copy" => Invalid(operands.Count == 0 ? $"{command}: missing operand (help skills)" : $"{command}: too many arguments{CommandArgs.QuoteHint}"),
            _ => Invalid($"skills: no subcommand '{args[0]}'; skills reads and finds ({Subcommands}). Bringing skills in, sharing and updates are the user's, in the Skills screen"),
        };
    }

    private async Task<ShellResult> ListAsync(string? vaultText, CancellationToken ct)
    {
        string? vault = null;
        if (vaultText is not null)
        {
            if (!VaultPath.TryParse(vaultText.TrimEnd('/'), out var path) || path.Segments.Count != 1) return Invalid($"skills ls: {vaultText}: give a vault, e.g. /Work");
            vault = path.Vault;
        }

        var list = await skills.ListAsync(vault, ct);
        if (list.Count == 0) return ShellResult.Ok("") with { NoMatchHint = "no skills yet; 'skills copy create-skill' adds the one that writes skills" };
        var marks = await updates.MarksAsync(ct);
        return ShellResult.Ok(string.Join('\n', list.Select(s => marks.HasUpdate(s) ? marks.Apply(s).Line() + " [update available: Skills screen]" : marks.Apply(s).Line())));
    }

    private async Task<ShellResult> FindSkillsAsync(string request, CancellationToken ct)
    {
        var marks = await updates.MarksAsync(ct);
        var visible = (await skills.ListAsync(null, ct)).Select(marks.Apply).ToList();
        var manifests = new List<(Skill, string?)>(visible.Count);
        foreach (var skill in visible) manifests.Add((skill, await skills.ManifestAsync(skill, ct)));
        var hits = await SkillSearch.RankAsync(manifests, request, Search, ct);
        if (hits.Count == 0) return ShellResult.Ok("") with { NoMatchHint = "no skill fits; skills ls lists them all" };
        return ShellResult.Ok(string.Join('\n', hits.Select(h => $"{h.Rank}. {h.Skill.Line()}  score {h.Score.ToString("0.0000", CultureInfo.InvariantCulture)}")));
    }

    private async Task<ShellResult> CatAsync(string address, CancellationToken ct)
    {
        const string command = "skills cat";
        var (skill, file, error) = await FindAsync(command, address, ct);
        if (skill is null) return error!;

        if (skill.Folder is not { } folder)
        {
            var system = SystemSkills.Find(skill.Name)!.Files.Where(f => f.Path == file).ToList();
            return system.Count == 1 ? ShellResult.Ok(system[0].Content) : NotFound($"{command}: {skill.Name} (system) has no file {file}");
        }

        // As cat reads it: a note with its version block, a text file as it is, an image or a PDF as one line.
        var read = await new Cat(notes, marks: marks).RunAsync([Inside(folder, file)!.Value], null, ct);
        return read.IsError && read.Output.StartsWith("cat:", StringComparison.Ordinal) ? read with { Output = command + read.Output["cat".Length..] } : read;
    }

    /// <summary>
    /// The skill an address names, among those the caller sees, and the file in it (<c>SKILL.md</c> when none is given): a
    /// path at or below a skill's folder, or a name with an optional file after a '/'.
    /// </summary>
    private async Task<(Skill? Skill, string File, ShellResult? Error)> FindAsync(string command, string address, CancellationToken ct)
    {
        var visible = await skills.ListAsync(null, ct);
        if (address.StartsWith('/'))
        {
            if (!CommandArgs.TryParsePath(command, address, out var path, out var pathError)) return (null, "", pathError! with { Kind = ShellErrorKind.Invalid });
            if (SkillPaths.FolderOf(path, await notes.SkillsVaultAsync(ct)) is not { } folder)
            {
                return (null, "", Invalid($"{command}: {path.Value} {NotInAFolder}"));
            }

            if (visible.FirstOrDefault(s => s.Folder == folder) is not { } found) return (null, "", NotFound($"{command}: there is no skill at {folder.Value} ('skills ls' lists them)"));
            var rest = string.Join('/', path.Segments.Skip(folder.Segments.Count));
            return (found, rest.Length == 0 ? SkillPaths.Manifest : rest, null);
        }

        var builtIn = address.StartsWith(SystemPrefix, StringComparison.Ordinal);
        var named = builtIn ? address[SystemPrefix.Length..] : address;
        var slash = named.IndexOf('/');
        var name = slash < 0 ? named : named[..slash];
        var file = slash < 0 || slash == named.Length - 1 ? SkillPaths.Manifest : named[(slash + 1)..];
        // A name's file stays in its folder: no '.' or '..' segment leads out of it.
        if (Inside(VaultPath.Root, file) is null) return (null, "", Invalid($"{command}: {address} {NotInAFolder}"));
        var matches = visible.Where(s => s.Name == name && (!builtIn || s.IsSystem)).ToList();
        return matches.Count switch
        {
            0 => (null, "", NotFound(builtIn ? $"{command}: there is no built-in skill '{name}'" : $"{command}: no skill named '{name}' ('skills ls' lists them)")),
            1 => (matches[0], file, null),
            _ => (null, "", Invalid($"{command}: '{name}' names {matches.Count} skills: {string.Join(", ", matches.Select(s => s.Folder?.Value ?? SystemPrefix + s.Name))}; give the path")),
        };
    }

    private async Task<ShellResult> CheckAsync(string? pathText, CancellationToken ct)
    {
        const string command = "skills check";
        var checking = await skills.ListAsync(null, ct);
        // The root holds every skill: as without a path.
        if (pathText?.TrimEnd('/').Length == 0) pathText = null;
        if (pathText is not null)
        {
            if (!CommandArgs.TryParsePath(command, pathText.TrimEnd('/'), out var path, out var pathError)) return pathError! with { Kind = ShellErrorKind.Invalid };
            // The skills at or below the path, or the one it lies in.
            checking = [.. checking.Where(s => s.Folder is { } folder && (Below(folder, path) || Below(path, folder)))];
            if (checking.Count == 0) return NotFound($"{command}: there is no skill at {path.Value} ('skills ls' lists them)");
        }

        var lines = checking.Select(s => $"{s.Folder?.Value ?? s.Name}: {(s.Warnings.Count == 0 ? "ok" : string.Join("; ", s.Warnings.Select(Skill.Clean)))}");
        var output = string.Join('\n', lines);
        return checking.Any(s => s.Warnings.Count > 0) ? Invalid(output) : ShellResult.Ok(output);
    }

    private async Task<ShellResult> CopyAsync(string name, string? vaultText, CancellationToken ct)
    {
        const string command = "skills copy";
        if (name.StartsWith(SystemPrefix, StringComparison.Ordinal))
        {
            name = name[SystemPrefix.Length..];
            if (SystemSkills.Find(name) is null) return NotFound($"{command}: there is no built-in skill '{name}'");
        }

        if (SystemSkills.Find(name) is not { } system)
        {
            return name.StartsWith('/') || (await skills.ListAsync(null, ct)).Any(s => s.Name == name)
                ? Invalid($"{command}: only built-in skills are copied here; moving a user's skill between vaults is for the user, in the Skills screen")
                : NotFound($"{command}: there is no built-in skill '{name}' ('skills ls' lists them as system)");
        }

        var vaults = await notes.ListVaultsAsync(ct);
        var marked = await notes.SkillsVaultAsync(ct) is { } m && vaults.Contains(m, StringComparer.Ordinal) ? m : null;
        VaultPath location;
        if (vaultText is null)
        {
            if (marked is null) return Invalid($"{command}: there is no vault Skills here; name a vault: skills copy {name} /Work");
            location = VaultPath.Root.Child(marked);
        }
        else
        {
            if (!VaultPath.TryParse(vaultText.TrimEnd('/'), out var path) || path.Segments.Count != 1) return Invalid($"{command}: {vaultText}: give a vault, e.g. /Work");
            if (!vaults.Contains(path.Vault!, StringComparer.Ordinal)) return NotFound($"{command}: there is no vault {path.Vault}");
            location = path.Vault == marked ? path : path.Child(SkillPaths.LocationFolder);
        }

        var target = location.Child(system.Name);
        if (writer.Caller.Refuse("skills", target) is { } refused) return refused with { Kind = ShellErrorKind.Refused };
        if ((await notes.ListNotesUnderAsync(target, ct)).Count > 0 || (await StoreAttachments.UnderAsync(notes, target, ct)).Count > 0)
        {
            return ShellResult.Error($"{command}: {target.Value} already exists; rm it first, or copy to another vault") with { Kind = ShellErrorKind.Conflict };
        }

        foreach (var (file, content) in system.Files)
        {
            // write takes a skill's text files too, through the attachment writer.
            var written = await writer.WriteAsync(Inside(target, file)!.Value, content, null, ct);
            if (written.IsError)
            {
                return written.Output.StartsWith("write:", StringComparison.Ordinal) ? written with { Output = command + written.Output["write".Length..] } : written;
            }
        }

        var files = system.Files.Count == 1 ? "1 file" : $"{system.Files.Count} files";
        return ShellResult.Ok($"copied {system.Name} to {target.Value}/ ({files}); it is yours now, and changes to the built-in one don't reach it");
    }

    /// <summary>The file at <paramref name="relative"/> in <paramref name="folder"/>; null when a segment is empty, '.' or '..'.</summary>
    private static VaultPath? Inside(VaultPath folder, string relative)
    {
        var path = folder;
        foreach (var segment in relative.Split('/'))
        {
            if (segment is "" or "." or "..") return null;
            path = path.Child(segment);
        }

        return path;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or lies below it.</summary>
    private static bool Below(VaultPath path, VaultPath folder) =>
        folder.IsRoot || path == folder || path.Value.StartsWith(folder.Value + "/", StringComparison.Ordinal);

    private static ShellResult Invalid(string message) => ShellResult.Error(message) with { Kind = ShellErrorKind.Invalid };

    private static ShellResult NotFound(string message) => ShellResult.Error(message) with { Kind = ShellErrorKind.NotFound };
}
