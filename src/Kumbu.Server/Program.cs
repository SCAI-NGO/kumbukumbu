// SPDX-License-Identifier: AGPL-3.0-or-later
using Kumbu.Core.Notes;
using Kumbu.Server;
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

var app = builder.Build();
app.Run();

public partial class Program;
