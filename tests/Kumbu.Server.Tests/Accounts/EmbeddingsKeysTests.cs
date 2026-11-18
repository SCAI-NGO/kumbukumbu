// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Text;
using Kumbu.Server.Accounts;
using Microsoft.Extensions.Logging.Abstractions;
using MongoDB.Driver;

namespace Kumbu.Server.Tests.Accounts;

/// <summary>The account's own TensorX key, sealed on the user document (BYOK spec, "How the key is kept").</summary>
[Collection("mongo")]
public sealed class EmbeddingsKeysTests(MongoFixture fixture)
{
    private const string Key = "sk-tensorx-own-key-0001";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly IMongoDatabase _db = fixture.NewDatabase();

    private static readonly byte[] UserKey = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private async Task<EmbeddingsKeys> WithUserAsync(string id = "u1")
    {
        await _db.GetCollection<UserDocument>("users").InsertOneAsync(
            new UserDocument { Id = id, Username = id, Display = id, Role = Roles.User, Active = true, KeyId = UserCatalog.KeyId(UserKey, id) }, cancellationToken: Ct);
        return new EmbeddingsKeys(_db, NullLogger<EmbeddingsKeys>.Instance);
    }

    [Fact]
    public async Task A_saved_key_comes_back_sealed_and_goes_on_removal()
    {
        var keys = await WithUserAsync();

        await keys.SetAsync("u1", UserKey, Key, Ct);

        Assert.Equal(Key, await keys.GetAsync("u1", UserKey, Ct));
        var stored = await _db.GetCollection<UserDocument>("users").Find(u => u.Id == "u1").Project(u => u.EmbeddingsKey).FirstAsync(Ct);
        Assert.NotNull(stored);
        Assert.DoesNotContain(Key, Encoding.Latin1.GetString(stored), StringComparison.Ordinal);

        await keys.SetAsync("u1", UserKey, null, Ct);
        Assert.Null(await keys.GetAsync("u1", UserKey, Ct));
    }

    [Fact]
    public async Task Another_user_key_neither_opens_nor_writes_it()
    {
        var keys = await WithUserAsync();
        await keys.SetAsync("u1", UserKey, Key, Ct);
        var other = new byte[32];

        Assert.Null(await keys.GetAsync("u1", other, Ct));
        await Assert.ThrowsAsync<CredentialsChangedException>(() => keys.SetAsync("u1", other, "sk-someone-else-0002", Ct));
        Assert.Equal(Key, await keys.GetAsync("u1", UserKey, Ct));
    }
}
