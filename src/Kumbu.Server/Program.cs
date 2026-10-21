// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Core.Sh;
using Kumbu.Server;
using Kumbu.Server.Auth;
using Kumbu.Server.Mcp;
using Kumbu.Server.Notes;
using Microsoft.Extensions.Options;
using MongoDB.Driver;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<KumbuOptions>()
    .Bind(builder.Configuration.GetSection("Kumbu"))
    .Validate(o => !string.IsNullOrWhiteSpace(o.DevToken), "Kumbu:DevToken must be set")
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
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools([typeof(ShTool)]);

var app = builder.Build();

app.UseMiddleware<DevTokenMiddleware>();
app.MapGet("/healthz", () => "ok");
app.MapMcp("/mcp");

app.Run();

public partial class Program;
