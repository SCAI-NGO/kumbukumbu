// SPDX-License-Identifier: AGPL-3.0-or-later
namespace Kumbu.Server.Tests.Startup;

/// <summary>MongoDB credentials (compose.yaml's optional authentication) beside the connection string.</summary>
public sealed class MongoOptionsTests
{
    [Fact]
    public void Without_a_password_the_connection_string_is_used_as_it_is()
    {
        var settings = new MongoOptions { ConnectionString = "mongodb://mongo:27017/?directConnection=true" }.ClientSettings();

        Assert.Null(settings.Credential);
        Assert.True(settings.DirectConnection);
    }

    [Fact]
    public void A_password_authenticates_the_user_against_admin_without_any_escaping()
    {
        var settings = new MongoOptions { ConnectionString = "mongodb://mongo:27017/?directConnection=true", Password = "p@ss:/word%" }.ClientSettings();

        Assert.Equal("kumbu", settings.Credential.Username);
        Assert.Equal("admin", settings.Credential.Source);
        Assert.Equal("mongo", Assert.Single(settings.Servers).Host);
    }
}
