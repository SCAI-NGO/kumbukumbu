// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;

namespace Kumbu.Core.Skills;

/// <summary>A skill built into the server: its folder name and files, <c>SKILL.md</c> first.</summary>
public sealed record SystemSkill(string Name, IReadOnlyList<(string Path, string Content)> Files);

/// <summary>The skills embedded in this assembly under <c>Skills/System/&lt;name&gt;/</c>.</summary>
public static class SystemSkills
{
    private const string Prefix = "Kumbu.Core.Skills.System/";

    public static IReadOnlyList<SystemSkill> All { get; } = Load();

    public static SystemSkill? Find(string name) => All.FirstOrDefault(s => s.Name == name);

    private static List<SystemSkill> Load()
    {
        var assembly = typeof(SystemSkills).Assembly;
        var files = new Dictionary<string, List<(string Path, string Content)>>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(r => r.StartsWith(Prefix, StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var relative = resource[Prefix.Length..].Replace('\\', '/');
            var slash = relative.IndexOf('/');
            if (slash < 0) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var name = relative[..slash];
            if (!files.TryGetValue(name, out var list)) files[name] = list = [];
            list.Add((relative[(slash + 1)..], reader.ReadToEnd()));
        }

        return files.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new SystemSkill(p.Key, [.. p.Value.OrderBy(f => f.Path != "SKILL.md").ThenBy(f => f.Path, StringComparer.Ordinal)]))
            .ToList();
    }
}
