// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Search;

namespace Kumbu.Server.Search;

/// <summary>Turns text into vectors whose dot product measures closeness of meaning.</summary>
public interface IEmbedder
{
    SemanticState State { get; }

    /// <summary>Key of the configured model in <see cref="ModelFiles.Catalogue"/>.</summary>
    string Model { get; }

    /// <summary>Why <see cref="State"/> isn't Ready, as a sentence for the user.</summary>
    string? Reason { get; }

    /// <summary>Completes when the model is loaded; never, if search by meaning is off.</summary>
    Task WhenReadyAsync(CancellationToken ct);

    /// <summary>Unit vectors, one per text. <paramref name="query"/> marks a search query, which the model
    /// embeds with its query prompt; notes are embedded as they are.</summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, bool query, CancellationToken ct);
}

/// <summary>One model of the <see cref="ModelFiles.Catalogue"/>, pinned to a revision.</summary>
public sealed record EmbeddingModel(string Key, string Repository, string Revision, string QueryPrompt, int Dimensions, IReadOnlyList<ModelFile> Files)
{
    public string Url(ModelFile file) => $"https://huggingface.co/{Repository}/resolve/{Revision}/{file.Remote}";
}

/// <summary>A file of a model; it is checked against its size and SHA-256.</summary>
public sealed record ModelFile(string Remote, string Local, long Size, string Sha256);

/// <summary>The models the server can run (concept §6); the configured one is kept in <c>ModelPath/&lt;key&gt;/</c>.</summary>
public static class ModelFiles
{
    public const string Default = "arctic-m";
    public const string Tokenizer = "tokenizer.json";
    public const string Model = "model_fp16.onnx";

    public static readonly IReadOnlyDictionary<string, EmbeddingModel> Catalogue = new[]
    {
        new EmbeddingModel("arctic-m", "Snowflake/snowflake-arctic-embed-m-v2.0", "95c2741480856aa9666782eb4afe11959938017f", "query: ", 768,
        [
            new("tokenizer.json", Tokenizer, 17_083_009, "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec"),
            new("onnx/model_fp16.onnx", Model, 613_266_244, "f27ab40ab6e230265ba49a202a37f1ad031556256cbbc105d0ca9c0bdc7ec42e"),
        ]),
        new EmbeddingModel("arctic-l", "Snowflake/snowflake-arctic-embed-l-v2.0", "ac6544c8a46e00af67e330e85a9028c66b8cfd9a", "query: ", 1024,
        [
            new("tokenizer.json", Tokenizer, 17_083_074, "39feb9863a378165ab9c5c689047203d789422966c0c58721c5309fd039a8edc"),
            new("onnx/model_fp16.onnx", Model, 1_134_153_607, "0b6846bcf4c0d9e53fdfc70d6dc1bbe3bb40fd205ebb7f0365da5b07064ddccc"),
        ]),
    }.ToDictionary(m => m.Key);

    /// <summary>Follows a symlink to the file, which <see cref="FileInfo.Length"/> does not.</summary>
    public static bool HasSize(string path, long size)
    {
        if (!File.Exists(path)) return false;
        using var handle = File.OpenHandle(path);
        return RandomAccess.GetLength(handle) == size;
    }

    public static EmbeddingModel Get(string key) => Catalogue[key];

    public static string Directory(string modelPath, string key) => Path.Combine(modelPath, key);

    /// <summary>Step 7 kept arctic-m's files directly in <paramref name="modelPath"/>; files of the right size
    /// are moved into <c>arctic-m/</c>, so that a download of 600 MB isn't repeated.</summary>
    public static void AdoptStep7Layout(string modelPath)
    {
        var target = Directory(modelPath, Default);
        foreach (var file in Get(Default).Files)
        {
            var old = Path.Combine(modelPath, file.Local);
            if (!HasSize(old, file.Size)) continue;
            System.IO.Directory.CreateDirectory(target);
            File.Move(old, Path.Combine(target, file.Local), overwrite: true);
        }
    }
}
