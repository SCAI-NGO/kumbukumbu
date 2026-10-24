// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;

namespace Kumbu.Server.Search;

/// <summary>Turns text into vectors whose dot product measures closeness of meaning.</summary>
public interface IEmbedder
{
    SemanticState State { get; }

    /// <summary>Why <see cref="State"/> isn't Ready, as a sentence for the user.</summary>
    string? Reason { get; }

    /// <summary>Completes when the model is loaded; never, if search by meaning is off.</summary>
    Task WhenReadyAsync(CancellationToken ct);

    /// <summary>Unit vectors, one per text. <paramref name="query"/> marks a search query, which the model
    /// embeds with its query prompt; notes are embedded as they are.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct);
}

/// <summary>The model (concept §6), pinned to a revision; each file is checked against its SHA-256.</summary>
public static class ModelFiles
{
    public const string Repository = "Snowflake/snowflake-arctic-embed-m-v2.0";
    public const string Revision = "95c2741480856aa9666782eb4afe11959938017f";
    public const string QueryPrompt = "query: ";
    public const int Dimensions = 768;
    public const string Tokenizer = "tokenizer.json";
    public const string Model = "model_fp16.onnx";

    public static readonly IReadOnlyList<(string Remote, string Local, long Size, string Sha256)> Files =
    [
        ("tokenizer.json", Tokenizer, 17_083_009, "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec"),
        ("onnx/model_fp16.onnx", Model, 613_266_244, "f27ab40ab6e230265ba49a202a37f1ad031556256cbbc105d0ca9c0bdc7ec42e"),
    ];

    public static string Url(string remote) => $"https://huggingface.co/{Repository}/resolve/{Revision}/{remote}";
}
