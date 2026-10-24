// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Kumbu.Core.Notes;
using Kumbu.Core.Search;
using Kumbu.Core.Sh;
using Kumbu.Server;
using Kumbu.Server.Accounts;
using Kumbu.Server.Auth;
using Kumbu.Server.Mcp;
using Kumbu.Server.Notes;
using Kumbu.Server.Search;
using Kumbu.Server.Vaults;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<KumbuOptions>()
    .Bind(builder.Configuration.GetSection("Kumbu"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<KumbuOptions>, KumbuOptionsValidator>();

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Mongo.ConnectionString));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Mongo.Database));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VaultCatalog>();
builder.Services.AddSingleton<Plans>();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<OnnxEmbedder>();
builder.Services.AddSingleton<IEmbedder>(sp => sp.GetRequiredService<OnnxEmbedder>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<OnnxEmbedder>());
builder.Services.AddSingleton<SearchIndex>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SearchIndex>());
builder.Services.AddHostedService<Seeder>();
builder.Services.AddHostedService<VaultSweeper>();

builder.Services.AddSingleton<Sessions>();
builder.Services.AddSingleton<WrongKeyLimiter>();

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
    // whatever a client put in the header itself is ignored.
    var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor, ForwardLimit = 1 };
    forwarded.KnownIPNetworks.Clear();
    forwarded.KnownProxies.Clear();
    app.UseForwardedHeaders(forwarded);
}

// Routing first, so that CallerMiddleware sees the endpoint and its AllowAnonymous.
app.UseRouting();
app.UseMiddleware<CallerMiddleware>();
app.MapGet("/healthz", () => "ok").AllowAnonymous();
app.MapMcp("/mcp");

app.Run();

static string ServerVersion()
{
    var informational = typeof(KumbuOptions).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
    // Strip SourceLink's "+<commit sha>" suffix; the commit is not something a client needs.
    return string.IsNullOrEmpty(informational) ? "0.1.0" : informational.Split('+')[0];
}

public partial class Program;
