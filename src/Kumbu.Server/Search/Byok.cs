// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Search;

/// <summary>
/// The one provider people bring their own key for (BYOK spec): TensorX with Qwen3-Embedding 8B at 1024 values, the
/// model the README's example configures. Built in rather than configured, so that people only ever hand in a key and
/// never an address the server would call (no SSRF), and because each new upstream needs curating anyway.
/// </summary>
public static class Byok
{
    /// <summary>How a vault's meta names it; an admin's provider may not take the name.</summary>
    public const string Provider = "tensorx-byok";

    public static readonly EmbeddingProviderOptions Preset = new()
    {
        Driver = EmbeddingDrivers.OpenAiCompatible,
        BaseUrl = "https://api.tensorx.ai/v1",
        Model = "qwen/qwen3-embedding-8b",
        Dimensions = 1024,
        QueryPrompt = "Instruct: Given a search query, retrieve the note sections that answer it\nQuery: ",
        Label = "TensorX (your own key)",
        Description = "As TensorX states: EU (Ireland), zero data retention",
    };

    /// <summary>Printable ASCII without spaces, 8 to 512 characters: every key TensorX hands out, and nothing that could
    /// break a header.</summary>
    public static bool IsKey(string? key) => key is { Length: >= 8 and <= 512 } && key.All(c => c is >= '!' and <= '~');
}
