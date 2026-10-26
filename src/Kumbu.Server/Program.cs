// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Kumbu.Core.Notes;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Server;
using Kumbu.Server.Accounts;
using Kumbu.Server.Api;
using Kumbu.Server.Auth;
using Kumbu.Server.Cli;
using Kumbu.Server.Mcp;
using Kumbu.Server.Notes;
using Kumbu.Server.OAuth;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MongoDB.Driver;

// `kumbu bootstrap-admin` runs a command against the configured MongoDB instead of the server, then exits.
var bootstrapCommand = args is [BootstrapAdmin.Command, ..];
var builder = WebApplication.CreateBuilder(bootstrapCommand ? args[1..] : args);
if (bootstrapCommand)
{
    // The command prints its link alone on stdout; the log goes to stderr.
    builder.Logging.ClearProviders().AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
}

// ASP.NET logs every request line at Information ("Request starting POST /api/..."), endpoint matches and
// results: nothing an operator needs, and a URL may carry something secret. Warnings and errors stay.
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);
// HttpClientFactory logs four lines per outgoing request: for an external embedding provider, one set per search
// query, which would put when someone searched into the log. Failures are logged by the drivers themselves.
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);

builder.Services.AddOptions<KumbuOptions>()
    .Bind(builder.Configuration.GetSection("Kumbu"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KumbuOptions>, KumbuOptionsValidator>();

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Mongo.ClientSettings()));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Mongo.Database));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VaultCatalog>();
builder.Services.AddSingleton<Plans>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<OnnxEmbedder>();
builder.Services.AddSingleton<IEmbeddingDriver>(sp => sp.GetRequiredService<OnnxEmbedder>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnnxEmbedder>());
// The internal model and the external providers the admin configured; each vault uses one of them.
builder.Services.AddSingleton(sp => EmbeddingProviders.FromOptions(
    sp.GetRequiredService<IEmbeddingDriver>(), sp.GetRequiredService<IOptions<KumbuOptions>>().Value,
    sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<SearchIndex>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchIndex>());
builder.Services.AddHostedService<Seeder>();
builder.Services.AddHostedService<VaultSweeper>();
// Re-keys vaults in the background, one at a time; a hosted service so that it is there to hear of pending vaults.
builder.Services.AddSingleton<RekeyRunner>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RekeyRunner>());

builder.Services.AddSingleton(_ => new Argon2Kdf());
builder.Services.AddSingleton<UserCatalog>();
builder.Services.AddSingleton<UserKeyRotation>();
builder.Services.AddSingleton<Sessions>();
builder.Services.AddSingleton<ApiKeys>();
builder.Services.AddSingleton<Connections>();
builder.Services.AddClientMetadataFetcher();
builder.Services.AddSingleton<OAuthClients>();
builder.Services.AddSingleton<AuthorizationRequests>();
builder.Services.AddSingleton<OAuthTokens>();
builder.Services.AddSingleton<ServerSecret>();
builder.Services.AddSingleton<Invitations>();
builder.Services.AddSingleton<Admin>();
builder.Services.AddSingleton<DeletionList>();
builder.Services.AddSingleton<RestoredDeletions>();
builder.Services.AddHostedService<RestoredDeletionsCheck>();
builder.Services.AddSingleton<Deletion>();
builder.Services.AddSingleton<Kumbu.Server.Welcome.WelcomeVault>();
builder.Services.AddSingleton<Rekeying>();
builder.Services.AddSingleton<BootstrapAdmin>();
builder.Services.AddSingleton<WrongKeyLimiter>();
builder.Services.AddSingleton<OAuthLimits>();
builder.Services.AddSingleton<AccountLimits>();

// The REST API's JSON: a required field that is missing or null is a bad request, not a null in a handler. And a
// body that can't be bound throws, so ApiErrors answers it as {"error": ...} like every other refusal.
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.RespectNullableAnnotations = true;
    o.SerializerOptions.RespectRequiredConstructorParameters = true;
});
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddOpenApi();

// Per request: CallerMiddleware resolves the caller; its credential decides which vaults the store can open,
// and its writer label goes on every write. Scoped, and nothing singleton may depend on them.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped(RequestCaller.Current);
builder.Services.AddScoped<ICaller>(sp => sp.GetRequiredService<RequestCaller>());
builder.Services.AddScoped(sp => sp.GetRequiredService<RequestCaller>().Credential);
builder.Services.AddScoped<EncryptedNoteStore>();
builder.Services.AddScoped<INoteStore>(sp => sp.GetRequiredService<EncryptedNoteStore>());
builder.Services.AddScoped<ISearchIndex>(sp => sp.GetRequiredService<EncryptedNoteStore>());
builder.Services.AddScoped<NoteWriter>();
builder.Services.AddScoped<NoteMover>();
builder.Services.AddScoped<Replacer>();
builder.Services.AddScoped(sp => new Checkpoints(
    sp.GetRequiredService<INoteStore>(), sp.GetRequiredService<NoteWriter>(), sp.GetRequiredService<Plans>(),
    sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Vaults.CheckpointLimit));
builder.Services.AddScoped<Shell>();

// Stateless Streamable HTTP is the SDK default: no Mcp-Session-Id, no server state between requests.
// Note: WithTools(typeof(ShTool)) compiles but binds to WithTools<T>(T target) with T = System.Type
// and silently registers no tools; the IEnumerable<Type> overload needs the collection expression.
// Without an explicit ServerInfo the SDK reports the entry assembly's name (the test host's, under tests).
builder.Services.AddMcpServer(o =>
    {
        o.ServerInfo = new Implementation { Name = "kumbu", Version = ServerVersion() };
        o.ServerInstructions = ServerInstructions.Text;
        o.Filters.Request.CallToolFilters.Add(RequiredArguments.Filter);
    })
    .WithHttpTransport()
    .WithTools([typeof(ShTool), typeof(WriteTools)]);

var app = builder.Build();

if (bootstrapCommand)
{
    KumbuOptions options;
    try
    {
        options = app.Services.GetRequiredService<IOptions<KumbuOptions>>().Value;
    }
    catch (OptionsValidationException e)
    {
        await Console.Error.WriteLineAsync(string.Join(Environment.NewLine, e.Failures));
        return 2;
    }

    return await BootstrapAdmin.ExecuteAsync(app.Services.GetRequiredService<BootstrapAdmin>(), options.PublicUrl, Console.Out, Console.Error, CancellationToken.None);
}

// Without ICU (globalization-invariant mode, or an image without libicu) .NET compares by code point.
if (!TextOrder.IsCultureAware)
{
    app.Logger.LogWarning("ICU is not available: sort, ls and rg order text by code point (umlauts after z)");
}

if (app.Services.GetRequiredService<IOptions<KumbuOptions>>().Value.Proxy.Trusted)
{
    // Traefik reaches the container over a Docker network whose addresses Docker assigns, so no proxy address
    // can be pinned: any peer is trusted, which is safe only because the proxy is the container's only way in
    // (no published port). ForwardLimit 1 takes the rightmost X-Forwarded-For entry, the one Traefik appended;
    // whatever a client put in the header itself is ignored. X-Forwarded-Proto (which Traefik overwrites) makes
    // Request.IsHttps true for a request that reached Traefik over https, for HSTS. First, so that everything
    // after sees the client's address and scheme. Without a trusted proxy neither header is read.
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

var publicUrl = app.Services.GetRequiredService<IOptions<KumbuOptions>>().Value.PublicUrl is { Length: > 0 } url ? url : null;
app.UseSecurityHeaders(publicUrl);
app.Use(ApiErrors.BadRequestsAsync);

// The web app (web/, built into wwwroot): index.html changes with every build and must be revalidated, the
// hashed files under /assets never change. Before routing: static files need no caller.
var webApp = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        context.Context.Response.Headers.CacheControl = context.Context.Request.Path.StartsWithSegments("/assets")
            ? "public, max-age=31536000, immutable"
            : "no-cache";
    },
};
app.UseDefaultFiles();
app.UseStaticFiles(webApp);

// Routing first, so that CallerMiddleware sees the endpoint and its AllowAnonymous.
app.UseRouting();
app.UseOAuthCors();
app.UseMiddleware<CallerMiddleware>();
app.UseCallerRules(publicUrl);
app.MapGet("/healthz", () => "ok").AllowAnonymous().ExcludeFromDescription();
app.MapMcp("/mcp");
// One URL per connection (Ruling 18): CallerMiddleware answers for an unknown id and, until a token is valid, 401.
app.MapMcp("/mcp/c/{id}");
app.MapDiscovery();
app.MapOAuthEndpoints();
app.MapOpenApi("/api/openapi.json").AllowAnonymous();
app.MapAccountApi();
app.MapConnectionApi();
app.MapNoteApi();
app.MapHistoryApi();
app.MapUploadApi();
app.MapExportApi();

// An unknown /api or /mcp path is a JSON 404, never the web app. These are the more specific fallbacks, so they
// win over the web app's; and they are not anonymous: CallerMiddleware still asks for a caller first.
app.MapFallback("/api/{**path}", () => Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound));
app.MapFallback("/mcp/{**path}", () => Results.Json(new { error = "Not found." }, statusCode: StatusCodes.Status404NotFound));
// A missing build file (an old chunk after a redeploy) is a plain 404: the shell in its place would fail as a script.
app.MapFallback("/assets/{**path}", () => Results.NotFound()).AllowAnonymous();
// Every other path is the web app's router's: index.html, without signing in (the app itself asks for that).
// Not the default pattern, which skips paths with an extension: a note's address ends in .md.
app.MapFallbackToFile("{**path}", "index.html", webApp).AllowAnonymous();

app.Run();
return 0;

static string ServerVersion()
{
    var informational = typeof(KumbuOptions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    // Strip SourceLink's "+<commit sha>" suffix; the commit is not something a client needs.
    return string.IsNullOrEmpty(informational) ? "0.1.0" : informational.Split('+')[0];
}

public partial class Program;
