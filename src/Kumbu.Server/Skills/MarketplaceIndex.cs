// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Unicode;
using Kumbu.Core.Attachments;
using Kumbu.Core.Notes;
using Kumbu.Core.Paths;
using Kumbu.Core.Skills;

namespace Kumbu.Server.Skills;

/// <summary>A skill a marketplace offers, by its folder's path in the repository. <see cref="Name"/> is the folder's name,
/// never the manifest's; <see cref="Reason"/> says why a skill that isn't <see cref="Installable"/> can't be installed.</summary>
public sealed record MarketplaceSkill(string Path, string Name, string Description, string? Plugin, bool Installable, string? Reason, bool HasScripts, IReadOnlyList<string> Warnings);

/// <summary>A plugin a <c>marketplace.json</c> lists; only one whose skills live in the same repository is installable.</summary>
public sealed record MarketplacePlugin(string Name, string? Source, bool Installable);

/// <summary>What a marketplace offers at a commit. <see cref="Note"/> says why its <c>marketplace.json</c> wasn't used.</summary>
public sealed record MarketplaceListing(string Commit, IReadOnlyList<MarketplaceSkill> Skills, IReadOnlyList<MarketplacePlugin> Plugins, string? Note);

/// <summary>A skill's files Kumbu can store, by their path inside the skill's folder, and the ones it can't, named.</summary>
public sealed record SkillFiles(IReadOnlyList<(string Path, byte[] Bytes)> Files, IReadOnlyList<string> Skipped);

/// <summary>
/// The skills in a marketplace's archive (spec §3.9, Decision 24): those of the plugins a
/// <c>.claude-plugin/marketplace.json</c> lists, else every folder holding a <c>SKILL.md</c> at most
/// <see cref="MaxDepth"/> levels deep. The archive is untrusted: every path it or its JSON names is checked here.
/// </summary>
public static class MarketplaceIndex
{
    public const int MaxSkillFiles = 200;
    public const long MaxSkillBytes = 10L * 1024 * 1024;
    public const int MaxDepth = 6;
    public const string MarketplaceJson = ".claude-plugin/marketplace.json";

    /// <summary>How deeply <c>marketplace.json</c> may nest; its shape needs four levels.</summary>
    private const int MaxJsonDepth = 16;

    private const string Unparsable = "marketplace.json doesn't parse; listing every folder with a SKILL.md";
    private const string TooLarge = "marketplace.json is over 1 MB; listing every folder with a SKILL.md";
    private const string BadManifest = "SKILL.md isn't UTF-8 text of at most 1 MiB";

    public static MarketplaceListing Of(RepoArchive archive)
    {
        var tree = Tree.Of(archive);
        string? note = null;
        if (tree.Kept.TryGetValue(MarketplaceJson, out var json))
        {
            if (json.Length > GitHubClient.MaxJsonBytes) note = TooLarge;
            else if (ReadPlugins(json) is { } plugins) return PluginLayout(archive.Commit, tree, plugins);
            else note = Unparsable;
        }

        var skills = tree.Kept.Keys
            .Where(p => p.EndsWith("/" + SkillPaths.Manifest, StringComparison.Ordinal))
            .Select(p => p[..^(SkillPaths.Manifest.Length + 1)])
            .Where(IsSkillPath)
            .Order(StringComparer.Ordinal)
            .Select(folder => Skill(tree, folder, plugin: null))
            .ToList();
        return new MarketplaceListing(archive.Commit, skills, [], note);
    }

    /// <summary>Whether a path the archive or its JSON names may be a skill's folder: relative, <c>/</c>-separated, no empty,
    /// <c>.</c>, <c>..</c> or dot-leading segment, no backslash or NUL, at most <see cref="MaxDepth"/> segments.</summary>
    public static bool IsSkillPath(string path)
    {
        if (path.Length == 0 || path.Contains('\\') || path.Contains('\0')) return false;
        var segments = path.Split('/');
        return segments.Length <= MaxDepth && segments.All(s => s.Length > 0 && !s.StartsWith('.'));
    }

    /// <summary>The skill's files at <paramref name="skillPath"/>; null unless it's a <see cref="IsSkillPath">skill path</see>
    /// with a <c>SKILL.md</c>.</summary>
    public static SkillFiles? FilesOf(RepoArchive archive, string skillPath)
    {
        if (Nfc(skillPath) is not { } folder || !IsSkillPath(folder)) return null;
        var tree = Tree.Of(archive);
        return tree.Kept.ContainsKey(folder + "/" + SkillPaths.Manifest) ? Collect(tree, folder) : null;
    }

    /// <summary>A listed plugin. <see cref="Root"/> is its folder, null unless <see cref="Local"/> and inside this repository.</summary>
    private sealed record Plugin(string Name, string? Source, bool Local, string? Root, IReadOnlyList<string>? Skills);

    private static MarketplaceListing PluginLayout(string commit, Tree tree, IReadOnlyList<Plugin> plugins)
    {
        var skills = new List<MarketplaceSkill>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        // Local plugins first, so a path a plugin of another repository names never hides one that can be installed.
        foreach (var plugin in plugins.Where(p => p.Root is not null))
        {
            var root = plugin.Root!;
            var folders = plugin.Skills is { } entries
                ? entries.Select(e => Join(root, e)).Where(f => f is not null && tree.Kept.ContainsKey(f + "/" + SkillPaths.Manifest))
                : tree.Kept.Keys
                    .Select(p => FolderUnder(p, Join(root, "skills")))
                    .Where(f => f is not null)
                    .Order(StringComparer.Ordinal);
            foreach (var folder in folders)
            {
                if (listed.Add(folder!)) skills.Add(Skill(tree, folder!, plugin.Name));
            }
        }

        foreach (var plugin in plugins.Where(p => !p.Local))
        {
            foreach (var path in plugin.Skills?.Select(e => Join("", e)).OfType<string>() ?? [])
            {
                if (!listed.Add(path)) continue;
                skills.Add(new MarketplaceSkill(path, path.Split('/')[^1], "", plugin.Name, false,
                    $"its plugin {plugin.Name} lives in another repository", false, []));
            }
        }

        return new MarketplaceListing(commit, skills, [.. plugins.Select(p => new MarketplacePlugin(p.Name, p.Source, p.Root is not null))], null);
    }

    /// <summary>The folder of <paramref name="path"/> when it's <c>&lt;skills&gt;/&lt;one folder&gt;/SKILL.md</c>.</summary>
    private static string? FolderUnder(string path, string? skills)
    {
        if (skills is null || !path.StartsWith(skills + "/", StringComparison.Ordinal) || !path.EndsWith("/" + SkillPaths.Manifest, StringComparison.Ordinal)) return null;
        var folder = path[..^(SkillPaths.Manifest.Length + 1)];
        return folder.IndexOf('/', skills.Length + 1) < 0 && IsSkillPath(folder) ? folder : null;
    }

    /// <summary>A path the JSON names (<c>./</c> once at its start, a <c>/</c> at its end allowed), below
    /// <paramref name="root"/>; null unless both it and the result are skill paths.</summary>
    private static string? Join(string root, string entry)
    {
        if (entry.StartsWith("./", StringComparison.Ordinal)) entry = entry[2..];
        if (entry.EndsWith('/')) entry = entry[..^1];
        if (Nfc(entry) is not { } relative || !IsSkillPath(relative)) return null;
        var path = root.Length == 0 ? relative : root + "/" + relative;
        return IsSkillPath(path) ? path : null;
    }

    /// <summary>The plugins of <c>{ "plugins": [{ "name", "source", "skills"? }] }</c>; null when the JSON doesn't parse or
    /// isn't of that shape. A plugin without a name is left out; one whose source is a string starting with <c>./</c> is local.</summary>
    private static List<Plugin>? ReadPlugins(byte[] json)
    {
        try
        {
            var bytes = json.AsMemory();
            if (bytes.Span.StartsWith("﻿"u8)) bytes = bytes[3..];
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = MaxJsonDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("plugins", out var list) || list.ValueKind != JsonValueKind.Array) return null;

            var plugins = new List<Plugin>();
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || String(item, "name") is not { } name) continue;
                string? source = null, root = null;
                var local = false;
                if (item.TryGetProperty("source", out var given))
                {
                    if (given.ValueKind == JsonValueKind.String)
                    {
                        source = given.GetString()!;
                        local = source.StartsWith("./", StringComparison.Ordinal);
                        root = source == "./" ? "" : local ? Join("", source) : null;
                    }
                    else if (given.ValueKind == JsonValueKind.Object)
                    {
                        source = String(given, "repo") ?? String(given, "url");
                    }
                }

                IReadOnlyList<string>? skills = item.TryGetProperty("skills", out var entries) && entries.ValueKind == JsonValueKind.Array
                    ? [.. entries.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!)]
                    : null;
                plugins.Add(new Plugin(name, source, local, root, skills));
            }

            return plugins;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static MarketplaceSkill Skill(Tree tree, string folder, string? plugin)
    {
        var name = folder.Split('/')[^1];
        var files = Collect(tree, folder);
        var manifest = files.Files.FirstOrDefault(f => f.Path == SkillPaths.Manifest).Bytes;
        string description = "";
        string? reason;
        IReadOnlyList<string> warnings;
        if (manifest is null)
        {
            reason = "its " + BadManifest;
            warnings = [BadManifest];
        }
        else
        {
            var read = SkillManifest.Read(name, Encoding.UTF8.GetString(manifest));
            description = read.Description ?? "";
            warnings = read.Problems;
            var bytes = files.Files.Sum(f => (long)f.Bytes.Length);
            reason = files.Files.Count > MaxSkillFiles ? $"it has {files.Files.Count} files; a skill has at most {MaxSkillFiles}"
                : bytes > MaxSkillBytes ? $"it has {(bytes / (1024.0 * 1024)).ToString("F1", CultureInfo.InvariantCulture)} MB; a skill has at most 10 MB"
                : null;
        }

        return new MarketplaceSkill(folder, name, description, plugin, reason is null, reason, SkillPageApi.HasScripts(files.Files.Select(f => f.Path)), warnings);
    }

    /// <summary>The files below <paramref name="folder"/>: the ones Kumbu can store there and the rest, in the archive's order.</summary>
    private static SkillFiles Collect(Tree tree, string folder)
    {
        var prefix = folder + "/";
        var files = new List<(string Path, byte[] Bytes)>();
        var skipped = new List<string>();
        foreach (var (path, bytes) in tree.Entries)
        {
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = path[prefix.Length..];
            if (bytes is not null && IsStorable(relative, bytes)) files.Add((relative, bytes));
            else skipped.Add(relative);
        }

        skipped.AddRange(tree.Skipped.Where(p => p.StartsWith(prefix, StringComparison.Ordinal)).Select(p => p[prefix.Length..]));
        return new SkillFiles(files, skipped);
    }

    /// <summary>Whether Kumbu keeps the file in a skill's folder: a note that is UTF-8 and at most 1 MiB, an image or PDF whose
    /// bytes match its name, or a skill's text file; every name one Kumbu creates.</summary>
    private static bool IsStorable(string relative, byte[] bytes)
    {
        if (relative.Contains('\\') || relative.Contains('\0')) return false;
        var path = VaultPath.Root.Child(SkillPaths.SkillsVault).Child("x");
        foreach (var segment in relative.Split('/'))
        {
            if (segment is "" or "." or "..") return false;
            path = path.Child(segment);
        }

        if (NoteNames.NameProblem(path) is not null) return false;
        if (path.IsNote) return bytes.Length <= NoteWriter.MaxBytes && Utf8.IsValid(bytes);
        return AttachmentFormat.ForPath(path) is { } format && format != AttachmentFormat.Marker
            && bytes.Length <= AttachmentFormat.MaxBytes && AttachmentFormat.Problem(path, bytes) is null;
    }

    private static string? Nfc(string text)
    {
        try
        {
            return text.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null;   // not valid Unicode
        }
    }

    /// <summary>The archive's files by NFC path, in its order. An entry whose path isn't valid Unicode or is a case twin of
    /// an earlier one has no bytes: it's skipped wherever it lies.</summary>
    private sealed class Tree
    {
        public List<(string Path, byte[]? Bytes)> Entries { get; } = [];
        public Dictionary<string, byte[]> Kept { get; } = new(StringComparer.Ordinal);
        public IReadOnlyList<string> Skipped { get; private init; } = [];

        public static Tree Of(RepoArchive archive)
        {
            var tree = new Tree { Skipped = archive.Skipped };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in archive.Files)
            {
                var path = Nfc(file.Path);
                if (path is null || !seen.Add(path))
                {
                    tree.Entries.Add((path ?? file.Path, null));
                    continue;
                }

                tree.Entries.Add((path, file.Bytes));
                tree.Kept[path] = file.Bytes;
            }

            return tree;
        }
    }
}
