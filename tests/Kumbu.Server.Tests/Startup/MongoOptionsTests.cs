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

    private static string PasswordFile(string content)
    {
        var path = Path.Combine(Directory.CreateTempSubdirectory("kumbu-mongo-").FullName, "password");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void A_password_file_authenticates_as_the_password_does_without_its_line_break()
    {
        var options = new MongoOptions { ConnectionString = "mongodb://mongo:27017/?directConnection=true", PasswordFile = PasswordFile("p@ss:/word% \r\n") };
        var settings = options.ClientSettings();

        Assert.Equal("kumbu", settings.Credential.Username);
        Assert.Equal("admin", settings.Credential.Source);
        Assert.Equal("p@ss:/word% ", options.EffectivePassword());
    }

    [Fact]
    public void The_startup_check_refuses_both_a_password_and_a_file_and_a_file_that_is_empty_or_missing()
    {
        static IEnumerable<string> Failures(MongoOptions mongo) =>
            new KumbuOptionsValidator().Validate(null, new KumbuOptions { Mongo = mongo }).Failures ?? [];
        const string Connection = "mongodb://mongo:27017";

        Assert.Empty(Failures(new MongoOptions { ConnectionString = Connection, PasswordFile = PasswordFile("secret") }));
        // An empty value is no setting, as compose.yaml passes an unset variable.
        Assert.Empty(Failures(new MongoOptions { ConnectionString = Connection, Password = "", PasswordFile = "" }));
        Assert.Contains("only one of", Assert.Single(Failures(new MongoOptions { ConnectionString = Connection, Password = "secret", PasswordFile = PasswordFile("secret") })));
        Assert.Contains("is empty", Assert.Single(Failures(new MongoOptions { ConnectionString = Connection, PasswordFile = PasswordFile("\n") })));
        var missing = Assert.Single(Failures(new MongoOptions { ConnectionString = Connection, PasswordFile = "/nonexistent/password" }));
        Assert.Equal("Kumbu:Mongo:PasswordFile /nonexistent/password can't be read", missing);
    }
}
