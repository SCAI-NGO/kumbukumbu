// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Kumbu.Core.Paths;

/// <summary>
/// An absolute, normalized path inside the virtual vault tree ("/" or "/Work/a/b.md").
/// Values are NFC-normalized, never end in a slash, and compare ordinally.
/// </summary>
public sealed record VaultPath
{
    public static VaultPath Root { get; } = new([]);

    private VaultPath(IReadOnlyList<string> segments)
    {
        Segments = segments;
        Value = segments.Count == 0 ? "/" : "/" + string.Join('/', segments);
    }

    public string Value { get; }

    public IReadOnlyList<string> Segments { get; }

    public bool IsRoot => Segments.Count == 0;

    public string? Vault => IsRoot ? null : Segments[0];

    public string Name => IsRoot ? "/" : Segments[^1];

    public bool IsNote => !IsRoot && Name.EndsWith(".md", StringComparison.Ordinal);

    /// <summary>Parses an absolute path. Returns false only for relative or blank input.</summary>
    public static bool TryParse(string input, [NotNullWhen(true)] out VaultPath? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(input) || !input.StartsWith('/'))
        {
            return false;
        }

        var segments = new List<string>();
        foreach (var part in input.Normalize(NormalizationForm.FormC).Split('/'))
        {
            if (part.Length == 0 || part == ".")
            {
                continue;
            }

            if (part == "..")
            {
                if (segments.Count > 0)
                {
                    segments.RemoveAt(segments.Count - 1);
                }

                continue;
            }

            segments.Add(part);
        }

        path = segments.Count == 0 ? Root : new VaultPath(segments);
        return true;
    }

    public VaultPath Child(string name) => new([.. Segments, name.Normalize(NormalizationForm.FormC)]);

    public bool Equals(VaultPath? other) => other is not null && string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;
}
