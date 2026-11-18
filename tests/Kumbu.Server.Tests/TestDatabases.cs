// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections.Concurrent;
using System.Reflection;
using MongoDB.Driver;
using Xunit.v3;

[assembly: Kumbu.Server.Tests.DropTestDatabases]

namespace Kumbu.Server.Tests;

/// <summary>
/// Databases a test made, dropped when that test is done: each collection and index is a WiredTiger file, and
/// a shared mongod that keeps every test's databases runs out of open files long before the suite ends.
/// </summary>
internal static class TestDatabases
{
    private static readonly ConcurrentDictionary<string, ConcurrentBag<IMongoDatabase>> ByTest = new();

    /// <summary>A fresh randomly named database, dropped after the test that is running now.</summary>
    public static IMongoDatabase Create(MongoClientSettings settings)
    {
        var db = new MongoClient(settings).GetDatabase("t" + Guid.NewGuid().ToString("N"));
        if (TestContext.Current.Test is { } test)
        {
            ByTest.GetOrAdd(test.UniqueID, _ => []).Add(db);
        }

        return db;
    }

    public static IMongoDatabase Create(string connectionString) => Create(MongoClientSettings.FromConnectionString(connectionString));

    /// <summary>Best effort: a database that cannot be dropped is left to the container, never a failed test.</summary>
    public static async Task DropAsync(IMongoDatabase db)
    {
        try
        {
            await db.Client.DropDatabaseAsync(db.DatabaseNamespace.DatabaseName);
        }
        catch (Exception)
        {
        }
    }

    internal static async ValueTask DropAsync(string testId)
    {
        if (ByTest.TryRemove(testId, out var dbs))
        {
            await Task.WhenAll(dbs.Select(DropAsync));
        }
    }
}

[AttributeUsage(AttributeTargets.Assembly)]
internal sealed class DropTestDatabasesAttribute : BeforeAfterTestAttribute
{
    public override void After(MethodInfo methodUnderTest, IXunitTest test) =>
        TestDatabases.DropAsync(test.UniqueID).AsTask().GetAwaiter().GetResult();
}
