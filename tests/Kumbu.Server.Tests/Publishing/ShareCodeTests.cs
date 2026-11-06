// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text.RegularExpressions;
using Kumbu.Server.Publishing;

namespace Kumbu.Server.Tests.Publishing;

/// <summary>Share codes (publish spec §4): 160 random bits in Crockford Base32, and the two keys derived from them.</summary>
public sealed partial class ShareCodeTests
{
    private const string Crockford = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{32}$")]
    private static partial Regex Normalised();

    [GeneratedRegex("^([0-9A-HJKMNP-TV-Z]{4}-){7}[0-9A-HJKMNP-TV-Z]{4}$")]
    private static partial Regex Formatted();

    [Fact]
    public void A_code_is_32_crockford_characters()
    {
        for (var i = 0; i < 1000; i++)
        {
            Assert.Matches(Normalised(), ShareCode.New());
        }
    }

    [Fact]
    public void Codes_differ_and_use_the_whole_alphabet()
    {
        var codes = Enumerable.Range(0, 10_000).Select(_ => ShareCode.New()).ToList();

        Assert.Equal(codes.Count, codes.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(Crockford, new string([.. codes.Select(c => c[0]).Distinct().Order()]));
        Assert.Equal(Crockford, new string([.. codes.Select(c => c[^1]).Distinct().Order()]));
    }

    [Fact]
    public void Format_groups_in_fours()
    {
        var formatted = ShareCode.Format(ShareCode.New());

        Assert.Equal(39, formatted.Length);
        Assert.Matches(Formatted(), formatted);
    }

    [Fact]
    public void Parse_reads_as_pairing_codes_do()
    {
        // Every 1 and 0 typed as I, L or O, the whole in lower case, with spaces between the groups.
        var code = "10" + ShareCode.New()[2..];
        var ones = 0;
        var typed = new string([.. ShareCode.Format(code).Select(c => c switch
        {
            '1' => ones++ % 2 == 0 ? 'I' : 'L',
            '0' => 'O',
            '-' => ' ',
            _ => c,
        })]).ToLowerInvariant();

        Assert.Equal(code, ShareCode.Parse(typed));
        Assert.Equal(code, ShareCode.Parse(ShareCode.Format(code)));
        Assert.Null(ShareCode.Parse(code[..31]));
        Assert.Null(ShareCode.Parse(code + "0"));
        Assert.Null(ShareCode.Parse("U" + code[1..]));
        Assert.Null(ShareCode.Parse(""));
        Assert.Null(ShareCode.Parse(string.Join(new string(' ', 30), code.Select(c => c.ToString()))));
    }

    [Fact]
    public void Lookup_and_reader_key_differ_and_are_stable()
    {
        var code = ShareCode.New();
        var lookup = ShareCode.Lookup(code);
        var reader = ShareCode.ReaderKey(code);

        Assert.Equal(32, lookup.Length);
        Assert.Equal(32, reader.Length);
        Assert.NotEqual(lookup, reader);
        Assert.Equal(lookup, ShareCode.Lookup(ShareCode.Parse(ShareCode.Format(code).ToLowerInvariant())!));
        Assert.Equal(reader, ShareCode.ReaderKey(code));
        Assert.NotEqual(lookup, ShareCode.Lookup(ShareCode.New()));
    }

    [Fact]
    public void Link_puts_the_code_in_the_fragment()
    {
        var code = ShareCode.New();
        var link = ShareCode.Link("https://kumbu.example.org/", code);

        Assert.Equal("https://kumbu.example.org/s#" + ShareCode.Format(code), link);
        Assert.Equal("/s", new Uri(link).PathAndQuery);
        Assert.Equal(link, ShareCode.Link("https://kumbu.example.org", code));
    }
}
