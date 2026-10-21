// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Reflection;
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Server;
using Kumbu.Server.Auth;
using Kumbu.Server.Mcp;
using Kumbu.Server.Notes;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Protocol;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<KumbuOptions>()
    .Bind(builder.Configuration.GetSection("Kumbu"))
    .Validate(KumbuOptions.IsUsableDevToken, KumbuOptions.DevTokenMessage)
    .Validate(o => !string.IsNullOrWhiteSpace(o.Mongo.ConnectionString), "Kumbu:Mongo:ConnectionString must be set")
    .ValidateOnStart();

builder.Services.AddSingleton<IMongoClient>(sp =>
    new MongoClient(sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Mongo.ConnectionString));
builder.Services.AddSingleton(sp =>
    sp.GetRequiredService<IMongoClient>().GetDatabase(sp.GetRequiredService<IOptions<KumbuOptions>>().Value.Mongo.Database));
builder.Services.AddSingleton<MongoNoteStore>();
builder.Services.AddSingleton<INoteReader>(sp => sp.GetRequiredService<MongoNoteStore>());
builder.Services.AddHostedService<Seeder>();
builder.Services.AddSingleton<Shell>();

// Stateless Streamable HTTP is the SDK default: no Mcp-Session-Id, no server state between requests.
// Note: WithTools(typeof(ShTool)) compiles but binds to WithTools<T>(T target) with T = System.Type
// and silently registers no tools; the IEnumerable<Type> overload needs the collection expression.
// Without an explicit ServerInfo the SDK reports the entry assembly's name (the test host's, under tests).
builder.Services.AddMcpServer(o => o.ServerInfo = new Implementation { Name = "kumbu", Version = ServerVersion() })
    .WithHttpTransport()
    .WithTools([typeof(ShTool)]);

var app = builder.Build();

app.UseMiddleware<DevTokenMiddleware>();
app.MapGet("/healthz", () => "ok");
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
