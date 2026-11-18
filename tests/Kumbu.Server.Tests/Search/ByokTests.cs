// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;

namespace Kumbu.Server.Tests.Search;

/// <summary>The admin's switch for people's own TensorX keys, and what a key may be (BYOK spec, "The three deployments").</summary>
public sealed class ByokTests
{
    [Theory]
    [InlineData(false, "internal", false)] // a) self-hosted only
    [InlineData(true, "tensorx", false)] // b) the operator pays: the switch is ignored
    [InlineData(true, "internal", true)] // c) self-hosted by default, own keys allowed
    [InlineData(false, "tensorx", false)]
    public void The_switch_counts_only_with_an_internal_default(bool byok, string @default, bool offered)
    {
        var options = new EmbeddingsOptions { Byok = byok, Default = @default };

        Assert.Equal(offered, options.ByokOffered);
    }

    [Theory]
    [InlineData("sk-abc12345", true)]
    [InlineData("abcdefgh", true)]
    [InlineData("abcdefg", false)]
    [InlineData("sk-abc 12345", false)]
    [InlineData("sk-abc12345\n", false)]
    [InlineData("sk-äbc12345", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_key_is_printable_ascii_of_8_to_512(string? key, bool valid)
    {
        Assert.Equal(valid, Byok.IsKey(key));
    }

    [Fact]
    public void A_key_of_512_is_taken_and_one_of_513_is_not()
    {
        Assert.True(Byok.IsKey(new string('k', 512)));
        Assert.False(Byok.IsKey(new string('k', 513)));
    }

    [Fact]
    public void The_preset_is_tensorx_with_qwen3_at_1024()
    {
        Assert.Equal("tensorx-byok", Byok.Provider);
        Assert.Equal("https://api.tensorx.ai/v1", Byok.Preset.BaseUrl);
        Assert.Equal("qwen/qwen3-embedding-8b", Byok.Preset.Model);
        Assert.Equal(1024, Byok.Preset.Dimensions);
        Assert.Equal(EmbeddingDrivers.OpenAiCompatible, Byok.Preset.Driver);
        Assert.Equal("TensorX (your own key)", Byok.Preset.Label);
        Assert.Null(Byok.Preset.ApiKeyFile);
    }

    [Fact]
    public void An_admin_provider_may_not_take_the_byok_name()
    {
        Assert.Contains(KumbuOptionsValidator.ProviderFailures(Byok.Provider, Byok.Preset), f => f.Contains("people's own keys", StringComparison.Ordinal));
    }
}
