// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Queries;

namespace Kumbu.Core.Tests.Queries;

// What a person sees of typing is in the conformance cases; these are the limits on what anyone with write access can plant.
public class PropertiesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"types\": [\"date\"]}")]
    [InlineData("{\"types\": {\"a\": 5}}")]
    public void A_types_file_that_is_not_one_declares_nothing(string json)
    {
        Assert.Empty(PropertyTypes.Read(json));
    }

    [Fact]
    public void An_unknown_type_name_declares_nothing_for_its_property()
    {
        var types = PropertyTypes.Read("{\"types\": {\"a\": \"date\", \"b\": \"colour\"}}");

        Assert.Equal(PropertyType.Date, types["a"]);
        Assert.False(types.ContainsKey("b"));
    }

    [Fact]
    public void A_types_file_nested_too_deeply_declares_nothing()
    {
        var json = string.Concat(Enumerable.Repeat("{\"a\":", 200)) + "1" + new string('}', 200);

        Assert.Empty(PropertyTypes.Read(json));
    }

    [Fact]
    public void Anchors_that_expand_tenfold_per_level_are_a_problem_not_a_hang()
    {
        var yaml = "a: &a [x, x, x, x, x, x, x, x, x, x]\n"
                   + string.Concat(Enumerable.Range(0, 8).Select(i => $"{(char)('b' + i)}: &{(char)('b' + i)} [{string.Join(", ", Enumerable.Repeat($"*{(char)('a' + i)}", 10))}]\n"));

        var read = Properties.Read($"---\n{yaml}---\n", PropertyTypes.None);

        Assert.NotNull(read.Problem);
        Assert.Empty(read.Values);
    }

    // A query reads every note's frontmatter; parsed once per content, a vault of thousands runs in milliseconds (ADR 0027).
    [Fact]
    public void A_note_s_properties_are_parsed_once_for_its_content_and_its_vault_s_types()
    {
        var content = string.Concat("---\nbirthday: 1980-11-23\n---\n", "# Ann");
        var types = PropertyTypes.Read("{\"types\": {\"birthday\": \"date\"}}");

        var first = Properties.Read(content, types);

        Assert.Same(first, Properties.Read(content, types));
        Assert.Same(first, Properties.Read(content, PropertyTypes.Read("{\"types\": {\"birthday\": \"date\"}}")));
        Assert.IsType<TextValue>(Properties.Read(content, PropertyTypes.Read("{\"types\": {\"birthday\": \"text\"}}")).Values["birthday"]);
        Assert.IsType<DateValue>(Properties.Read(content, types).Values["birthday"]);
    }
}
