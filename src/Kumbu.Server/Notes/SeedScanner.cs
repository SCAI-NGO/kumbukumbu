// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Core.Attachments;
using Kumbu.Core.Paths;

namespace Kumbu.Server.Notes;

public sealed record SeedFile(VaultPath Path, string Content);

/// <param name="Vaults">Every vault folder, also one without notes.</param>
/// <param name="Folders">The folders a <c>.vaultkeep</c> file names, so that empty ones are kept.</param>
public sealed record SeedScan(
    IReadOnlyList<SeedFile> Files, IReadOnlyList<string> Warnings, IReadOnlyList<string> Vaults, IReadOnlyList<VaultPath> Folders);

public static class SeedScanner
{
    public const int MaxBytes = 1_048_576;

    public static SeedScan Scan(string root)
    {
        var files = new List<SeedFile>();
        var warnings = new List<string>();
        var folders = new List<VaultPath>();
        var utf8 = new UTF8Encoding(false);

        // Hidden entries are filtered below by their dot segments; AttributesToSkip = 0 keeps the enumerator
        // from making a second, platform-specific decision. Unreadable directories are skipped, not fatal.
        var enumeration = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = 0,
        };

        foreach (var full in Directory.EnumerateFiles(root, "*", enumeration).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
            var segments = relative.Split('/');
            // A marker names its folder whatever it holds; one directly in a vault folder is skipped, as a vault needs none.
            if (segments[^1] == FolderMarker.Name)
            {
                if (segments.Length > 2 && !segments[..^1].Any(s => s.StartsWith('.'))
                    && VaultPath.TryParse("/" + string.Join('/', segments[..^1]), out var folder))
                {
                    folders.Add(folder);
                }

                continue;
            }

            if (segments.Any(s => s.StartsWith('.')) || !relative.EndsWith(".md", StringComparison.Ordinal))
            {
                continue;
            }

            if (segments.Length < 2)
            {
                warnings.Add($"Skipped {relative}: notes must live inside a vault directory");
                continue;
            }

            if (!VaultPath.TryParse("/" + relative, out var path))
            {
                warnings.Add($"Skipped {relative}: invalid path");
                continue;
            }

            byte[] bytes;
            try
            {
                if (new FileInfo(full).Length > MaxBytes)
                {
                    warnings.Add($"Skipped {relative}: larger than {MaxBytes} bytes");
                    continue;
                }

                bytes = File.ReadAllBytes(full);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Skipped {relative}: {ex.Message}");
                continue;
            }

            var text = utf8.GetString(bytes);
            if (text.StartsWith('\uFEFF'))
            {
                text = text[1..];
            }

            files.Add(new SeedFile(path, text.Replace("\r\n", "\n", StringComparison.Ordinal)));
        }

        var vaults = Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => !name.StartsWith('.') && VaultPath.TryParse("/" + name, out _))
            .Select(name => name.Normalize(NormalizationForm.FormC))
            .Union(files.Select(f => f.Path.Vault!))
            .Order(StringComparer.Ordinal)
            .ToList();
        return new SeedScan(files, warnings, vaults, folders);
    }
}
