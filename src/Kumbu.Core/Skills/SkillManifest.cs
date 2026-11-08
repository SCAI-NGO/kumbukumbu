// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using System.Text.RegularExpressions;
using Kumbu.Core.Board;
using Kumbu.Core.Sections;
using Kumbu.Core.Sh;

namespace Kumbu.Core.Skills;

/// <summary>What a skill's <c>SKILL.md</c> says about itself, with the rules it breaks.
/// Kumbu's own fields live under <c>metadata</c> with a <c>kumbu-</c> prefix.</summary>
public sealed partial record SkillManifest(string? Name, string? Description, string? License, string? Compatibility, string? AllowedTools,
    IReadOnlyDictionary<string, string> Metadata, IReadOnlyList<string> Problems)
{
    public const int MaxName = 64, MaxDescription = 1024;
    public const string SourceKey = "kumbu-source", HashKey = "kumbu-hash";

    private static readonly IReadOnlyDictionary<string, string> NoMetadata = new Dictionary<string, string>();

    public string? Source => Metadata.GetValueOrDefault(SourceKey);
    public string? Hash => Metadata.GetValueOrDefault(HashKey);

    public static SkillManifest Read(string folderName, string? content)
    {
        if (content is null) return Failed("SKILL.md is missing");
        content = Normalise(content);
        if (Outline.FrontmatterEnd(Lines.Split(content)) == 0)
        {
            return Failed("SKILL.md has no frontmatter: it starts with ---, then name and description");
        }

        var read = Frontmatter.Read(content);
        if (read.Problem is not null) return Failed($"the frontmatter doesn't parse: {read.Problem}");

        var problems = new List<string>();
        var name = Text(read.Values, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            problems.Add("name is missing");
        }
        else if (!ValidName().IsMatch(name))
        {
            problems.Add($"name '{name}' must be lowercase letters, digits and hyphens, at most {MaxName} characters");
        }
        else if (name != folderName)
        {
            problems.Add($"name '{name}' must be the folder's name, '{folderName}'");
        }

        var description = Text(read.Values, "description");
        if (string.IsNullOrWhiteSpace(description)) problems.Add("description is missing");
        else if (description.Length > MaxDescription) problems.Add($"description has {description.Length} characters; at most {MaxDescription}");

        var metadata = NoMetadata;
        if (read.Values.TryGetValue("metadata", out var raw) && raw is not null)
        {
            if (raw is IReadOnlyDictionary<string, object?> map && map.Values.All(v => v is null or string)) metadata = map.ToDictionary(p => p.Key, p => (string?)p.Value ?? "");
            else problems.Add("metadata must be a map of keys and values");
        }

        return new SkillManifest(name, description, Text(read.Values, "license"), Text(read.Values, "compatibility"), Text(read.Values, "allowed-tools"), metadata, problems);
    }

    /// <summary>Writes the origin under <c>metadata</c>, replacing older origin fields; every other line stays as it was
    /// (the other metadata entries are written out again, quoted).</summary>
    public static string WithOrigin(string content, string source, string hash)
    {
        var existing = Frontmatter.Read(Normalise(content)).Values.GetValueOrDefault("metadata") is IReadOnlyDictionary<string, object?> map
            ? map.Where(p => p.Key is not (SourceKey or HashKey) && p.Value is null or string).Select(p => (p.Key, (string?)p.Value ?? ""))
            : [];
        var block = new StringBuilder();
        foreach (var (key, value) in existing.Append((SourceKey, source)).Append((HashKey, hash)))
        {
            block.Append("\n  ").Append(Key(key)).Append(": ").Append(Quote(value));
        }

        return Frontmatter.Set(content, [("metadata", block.ToString())]);
    }

    /// <summary>What follows the frontmatter; the whole text when there is none.</summary>
    public static string Body(string content)
    {
        content = Normalise(content);
        var lines = Lines.Split(content);
        var end = Outline.FrontmatterEnd(lines);
        if (end == 0) return content;
        var body = Lines.Join(lines.Skip(end));
        return body.Length > 0 && content.EndsWith('\n') ? body + "\n" : body;
    }

    private static SkillManifest Failed(string problem) => new(null, null, null, null, null, NoMetadata, [problem]);

    private static string Normalise(string content) => content.TrimStart('﻿').Replace("\r\n", "\n");

    private static string? Text(IReadOnlyDictionary<string, object?> values, string key) => values.GetValueOrDefault(key) as string;

    private static string Key(string key) => PlainKey().IsMatch(key) ? key : Quote(key);

    private static string Quote(string value)
    {
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
        {
            quoted.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' or '\u007f' or '\u0085' or '\u2028' or '\u2029' => $"\\u{(int)c:x4}",
                _ => c.ToString(),
            });
        }

        return quoted.Append('"').ToString();
    }

    [GeneratedRegex("^[a-z0-9-]{1,64}$")]
    private static partial Regex ValidName();

    [GeneratedRegex(@"^[A-Za-z0-9_.-]+$")]
    private static partial Regex PlainKey();
}
