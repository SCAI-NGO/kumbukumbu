// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Server.Notes;

namespace Kumbu.Server.Tests.Notes;

public sealed class SeedScannerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("kumbu-seed-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Write(string relative, string content) => Write(relative, new UTF8Encoding(false).GetBytes(content));

    private string Write(string relative, byte[] bytes)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    [Fact]
    public void Collects_markdown_files_with_vault_paths()
    {
        Write("Demo/a.md", "a");
        Write("Demo/sub/b.md", "b");
        var scan = SeedScanner.Scan(_root);
        Assert.Equal(["/Demo/a.md", "/Demo/sub/b.md"], scan.Files.Select(f => f.Path.Value).Order(StringComparer.Ordinal));
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public void Skips_files_outside_a_vault_with_a_warning()
    {
        Write("loose.md", "x");
        var scan = SeedScanner.Scan(_root);
        Assert.Empty(scan.Files);
        var warning = Assert.Single(scan.Warnings);
        Assert.Contains("loose.md", warning);
    }

    [Fact]
    public void Skips_non_markdown_files_silently()
    {
        Write("Demo/x.txt", "x");
        var scan = SeedScanner.Scan(_root);
        Assert.Empty(scan.Files);
        Assert.Empty(scan.Warnings);
    }

    [Fact]
    public void Skips_hidden_directories()
    {
        Write("Demo/.obsidian/c.md", "c");
        Write(".git/d.md", "d");
        var scan = SeedScanner.Scan(_root);
        Assert.Empty(scan.Files);
    }

    [Fact]
    public void Normalizes_crlf_to_lf()
    {
        Write("Demo/a.md", "one\r\ntwo\r\n");
        Assert.Equal("one\ntwo\n", Assert.Single(SeedScanner.Scan(_root).Files).Content);
    }

    [Fact]
    public void Strips_utf8_bom()
    {
        Write("Demo/a.md", [0xEF, 0xBB, 0xBF, .. "# Title"u8.ToArray()]);
        var content = Assert.Single(SeedScanner.Scan(_root).Files).Content;
        Assert.Equal("# Title", content);
        Assert.False(content.StartsWith('﻿'));
    }

    [Fact]
    public void Normalizes_file_names_to_nfc()
    {
        var nfd = "Überlegungen.md";
        Write("Demo/" + nfd, "x");
        var path = Assert.Single(SeedScanner.Scan(_root).Files).Path;
        Assert.Equal("/Demo/Überlegungen.md", path.Value);
        Assert.True(path.Value.IsNormalized(NormalizationForm.FormC));
    }

    [Fact]
    public void Skips_oversized_files_with_a_warning()
    {
        Write("Demo/big.md", new byte[SeedScanner.MaxBytes + 1]);
        var scan = SeedScanner.Scan(_root);
        Assert.Empty(scan.Files);
        Assert.Contains("big.md", Assert.Single(scan.Warnings));
    }
}
