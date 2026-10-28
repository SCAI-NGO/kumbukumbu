// SPDX-License-Identifier: AGPL-3.0-or-later
using MongoDB.Driver;

namespace Kumbu.Server;

public sealed class KumbuOptions
{
    public const int MinDevTokenLength = 16;

    public const string DevTokenMessage =
        "Kumbu:DevToken must be at least 16 characters (generate one with: openssl rand -hex 32)";

    /// <summary>Rejects blank, short and the old placeholder token: if there is a dev token, it must be a real secret.</summary>
    public static bool IsUsableDevToken(string token) =>
        !string.IsNullOrWhiteSpace(token)
        && token.Trim().Length >= MinDevTokenLength
        && token != "change-me";

    /// <summary>Optional: without it (empty or unset) the dev token opens nothing, as on a public instance.</summary>
    public string? DevToken { get; set; }

    public bool HasDevToken => !string.IsNullOrEmpty(DevToken);

    /// <summary>
    /// Where users reach this instance (<c>KUMBU_PUBLIC_URL</c>, e.g. <c>https://kumbu.tidesson.net</c>): an origin,
    /// scheme, host and optional port, nothing more. Links (invitations) are built from it. Optional, but the
    /// <c>bootstrap-admin</c> command refuses to run without it.
    /// </summary>
    public string? PublicUrl { get; set; }

    /// <summary>True for an absolute http(s) URL that is only an origin: no path, query, fragment or user info.</summary>
    public static bool IsUsablePublicUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
        && uri.AbsolutePath == "/"
        && uri.Query == ""
        && uri.Fragment == ""
        && uri.UserInfo == "";

    public ProxyOptions Proxy { get; set; } = new();

    public MongoOptions Mongo { get; set; } = new();

    public SeedOptions Seed { get; set; } = new();

    public RecoveryOptions Recovery { get; set; } = new();

    public VaultOptions Vaults { get; set; } = new();

    public SearchOptions Search { get; set; } = new();

    public EmbeddingsOptions Embeddings { get; set; } = new();

    public OAuthOptions OAuth { get; set; } = new();

    public PrivacyOptions Privacy { get; set; } = new();

    public DeletionsOptions Deletions { get; set; } = new();
}

/// <summary>Where the ids of deleted users are listed, outside the database (<see cref="Accounts.DeletionList"/>).</summary>
public sealed class DeletionsOptions
{
    /// <summary>Optional: a directory of its own (Compose: <c>/deletions</c> on the <c>deletions</c> volume), kept out
    /// of database restores. Unset, deletions are not listed and a restored user can't be detected.</summary>
    public string? Path { get; set; }
}

/// <summary>What the deletion receipt's reply to a privacy request is signed with.</summary>
public sealed class PrivacyOptions
{
    /// <summary>Optional: the deployer's privacy contact (<c>KUMBU_PRIVACY_CONTACT</c>), e.g. <c>privacy@example.org</c>;
    /// unset, the reply leaves a placeholder.</summary>
    public string? Contact { get; set; }
}

public sealed class OAuthOptions
{
    /// <summary>How long an app connection's access token lasts (<c>KUMBU_OAUTH_ACCESS_TOKEN_MINUTES</c>).</summary>
    public int AccessTokenMinutes { get; set; } = 60;

    /// <summary>Test and development only: lets a client metadata document be fetched over http from a loopback address.</summary>
    public bool AllowLoopbackClients { get; set; }
}

/// <summary>External embedding providers (<c>Kumbu:Embeddings:Providers:&lt;name&gt;:…</c>). A vault uses one when its
/// owner opts in, or when the admin made it the <see cref="Default"/> and the owner never chose.</summary>
public sealed class EmbeddingsOptions
{
    public Dictionary<string, EmbeddingProviderOptions> Providers { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The provider of every vault whose owner never chose one (<c>KUMBU_EMBEDDINGS_DEFAULT</c>): <c>internal</c>
    /// (the model on this server, <see cref="SearchOptions.Model"/>) or the name of one of <see cref="Providers"/>.
    /// Owners can still choose another, the internal model included.</summary>
    public string Default { get; set; } = Search.EmbeddingProviders.Internal;
}

/// <summary>One external provider: a protocol (<see cref="Driver"/>), where it is, which model, and how it is described
/// to the owners who may choose it.</summary>
public sealed class EmbeddingProviderOptions
{
    /// <summary>The protocol: <c>openai-compatible</c> (<c>POST {BaseUrl}/embeddings</c>).</summary>
    public string Driver { get; set; } = "";

    /// <summary>e.g. <c>https://api.tensorx.ai/v1</c>; https, except for a provider on this machine (loopback).</summary>
    public string BaseUrl { get; set; } = "";

    public string Model { get; set; } = "";

    /// <summary>A file holding the API key, e.g. a Docker secret under <c>/run/secrets/</c>. Never the key itself in
    /// the configuration: environment variables end up in <c>docker inspect</c>, process listings and support dumps.
    /// Optional only for a loopback provider.</summary>
    public string? ApiKeyFile { get; set; }

    /// <summary>Asks the model for vectors of this length (models trained for it, as Qwen3-Embedding, keep most of
    /// their quality at a quarter); unset, the model's own length.</summary>
    public int? Dimensions { get; set; }

    /// <summary>Put before every search query, for models that embed queries with an instruction.</summary>
    public string? QueryPrompt { get; set; }

    /// <summary>What owners see; the name if unset.</summary>
    public string? Label { get; set; }

    /// <summary>What owners should know before their notes go there, e.g. "EU, zero data retention".</summary>
    public string? Description { get; set; }

    /// <summary>Texts per request.</summary>
    public int MaxBatch { get; set; } = 32;

    /// <summary>Per request; a slower answer counts as a failure and is retried.</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>The key in <paramref name="path"/>, trimmed; null without a path or for an empty file.</summary>
    public static string? ReadKey(string? path) => path is null ? null : File.ReadAllText(path).Trim() is { Length: > 0 } key ? key : null;
}

public sealed class SearchOptions
{
    /// <summary>Search by meaning with the internal model (semsearch); without it, the vaults that use it rank by
    /// words only. Vaults that use an external provider still search by meaning.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Where the embedding model is kept; downloaded there on the first start.</summary>
    public string ModelPath { get; set; } = "models";

    /// <summary>Key of the embedding model: <c>arctic-m</c> or <c>arctic-l</c> (see <c>ModelFiles.Catalogue</c>).</summary>
    public string Model { get; set; } = "arctic-m";

    /// <summary>Threads for the model; 0 means half of the cores.</summary>
    public int Threads { get; set; }
}

public sealed class RecoveryOptions
{
    /// <summary>Directory new vaults' recovery keys are written to, once. Without it, vaults get no recovery key.</summary>
    public string? Path { get; set; }

    /// <summary>Recovery keys (separated by commas or whitespace) to give the dev token a slot in their
    /// vaults at startup, e.g. after changing the token.</summary>
    public string? Keys { get; set; }
}

public sealed class VaultOptions
{
    /// <summary>An unlocked vault is locked again after this many idle minutes (concept §7).</summary>
    public int IdleMinutes { get; set; } = 15;

    /// <summary>Unlocked vaults are locked, least recently used first, above this much memory.</summary>
    public int MemoryLimitMegabytes { get; set; } = 512;

    /// <summary>Named checkpoints a vault keeps at most (concept §4); mkcp beyond it fails.</summary>
    public int CheckpointLimit { get; set; } = Kumbu.Core.Notes.Checkpoints.DefaultLimit;

    /// <summary>Attachments' bytes held in memory at once, server-wide, while they are served (each up to 15 MB, and
    /// about twice that while it is decrypted); a further read waits for one to finish.</summary>
    public int ConcurrentFileReads { get; set; } = 4;
}

public sealed class ProxyOptions
{
    /// <summary>Behind a reverse proxy (Traefik): take the client IP from <c>X-Forwarded-For</c> and the scheme from
    /// <c>X-Forwarded-Proto</c> (which decides HSTS). Only set this
    /// when the proxy is the only way in, or clients could pick the IP that rate limits count against.</summary>
    public bool Trusted { get; set; }
}

public sealed class MongoOptions
{
    public string ConnectionString { get; set; } = "";

    public string Database { get; set; } = "kumbu";

    /// <summary>With <see cref="Password"/>: the user to authenticate as, against the <c>admin</c> database
    /// (compose.yaml's root user). Kept apart from the connection string, so a password needs no URL escaping.</summary>
    public string User { get; set; } = "kumbu";

    /// <summary>Empty: no credentials beyond what the connection string carries.</summary>
    public string? Password { get; set; }

    /// <summary>In place of <see cref="Password"/>: a file holding it, e.g. a Docker secret under <c>/run/secrets/</c>,
    /// so the password isn't in the container's environment. Its line break at the end is not part of it.</summary>
    public string? PasswordFile { get; set; }

    /// <summary>The password set, or the one in <see cref="PasswordFile"/> (read now); null for neither.</summary>
    public string? EffectivePassword() =>
        !string.IsNullOrEmpty(Password) ? Password
        : string.IsNullOrEmpty(PasswordFile) ? null
        : File.ReadAllText(PasswordFile).TrimEnd('\r', '\n') is { Length: > 0 } password ? password : null;

    public MongoClientSettings ClientSettings()
    {
        var settings = MongoClientSettings.FromConnectionString(ConnectionString);
        if (EffectivePassword() is { } password) settings.Credential = MongoCredential.CreateCredential("admin", User, password);
        return settings;
    }
}

public sealed class SeedOptions
{
    public string? Path { get; set; }
}
