// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Server.Search;

namespace Kumbu.Server.Tests.Search;

public sealed class ModelFilesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "kumbu-models-" + Guid.NewGuid().ToString("N"));

    public ModelFilesTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private void Sparse(string name, long size)
    {
        using var f = File.Create(Path.Combine(_dir, name));
        f.SetLength(size);
    }

    [Fact]
    public void CatalogueHoldsBothArcticModels()
    {
        Assert.Equal(["arctic-l", "arctic-m"], ModelFiles.Catalogue.Keys.Order());
        Assert.Equal("arctic-m", ModelFiles.Default);

        var m = ModelFiles.Get("arctic-m");
        Assert.Equal("Snowflake/snowflake-arctic-embed-m-v2.0", m.Repository);
        Assert.Equal("95c2741480856aa9666782eb4afe11959938017f", m.Revision);
        Assert.Equal("query: ", m.QueryPrompt);
        Assert.Equal(768, m.Dimensions);
        Assert.Equal(
            [("tokenizer.json", "tokenizer.json", 17_083_009L, "f1cc44ad7faaeec47241864835473fd5403f2da94673f3f764a77ebcb0a803ec"),
             ("onnx/model_fp16.onnx", "model_fp16.onnx", 613_266_244L, "f27ab40ab6e230265ba49a202a37f1ad031556256cbbc105d0ca9c0bdc7ec42e")],
            m.Files.Select(f => (f.Remote, f.Local, f.Size, f.Sha256)));

        var l = ModelFiles.Get("arctic-l");
        Assert.Equal("Snowflake/snowflake-arctic-embed-l-v2.0", l.Repository);
        Assert.Equal("ac6544c8a46e00af67e330e85a9028c66b8cfd9a", l.Revision);
        Assert.Equal("query: ", l.QueryPrompt);
        Assert.Equal(1024, l.Dimensions);
        Assert.Equal(
            [("tokenizer.json", "tokenizer.json", 17_083_074L, "39feb9863a378165ab9c5c689047203d789422966c0c58721c5309fd039a8edc"),
             ("onnx/model_fp16.onnx", "model_fp16.onnx", 1_134_153_607L, "0b6846bcf4c0d9e53fdfc70d6dc1bbe3bb40fd205ebb7f0365da5b07064ddccc")],
            l.Files.Select(f => (f.Remote, f.Local, f.Size, f.Sha256)));

        Assert.Equal(
            "https://huggingface.co/Snowflake/snowflake-arctic-embed-l-v2.0/resolve/ac6544c8a46e00af67e330e85a9028c66b8cfd9a/onnx/model_fp16.onnx",
            l.Url(l.Files[1]));
        Assert.Equal(Path.Combine("models", "arctic-l"), ModelFiles.Directory("models", "arctic-l"));
    }

    [Fact]
    public void Step7FilesAreMovedIntoTheModelFolder()
    {
        Sparse("tokenizer.json", 17_083_009);
        Sparse("model_fp16.onnx", 613_266_244);

        ModelFiles.AdoptStep7Layout(_dir);

        foreach (var name in new[] { "tokenizer.json", "model_fp16.onnx" })
        {
            Assert.True(File.Exists(Path.Combine(_dir, "arctic-m", name)));
            Assert.False(File.Exists(Path.Combine(_dir, name)));
        }
    }

    [Fact]
    public void PartFilesAndWrongSizesStay()
    {
        Sparse("model_fp16.onnx.part", 1000);
        Sparse("model_fp16.onnx", 1000);
        Sparse("tokenizer.json", 17_083_009);

        ModelFiles.AdoptStep7Layout(_dir);

        Assert.True(File.Exists(Path.Combine(_dir, "model_fp16.onnx.part")));
        Assert.True(File.Exists(Path.Combine(_dir, "model_fp16.onnx")));
        Assert.False(File.Exists(Path.Combine(_dir, "arctic-m", "model_fp16.onnx")));
    }
}
