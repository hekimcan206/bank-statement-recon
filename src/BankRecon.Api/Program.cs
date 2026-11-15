using System.Text.Json;
using System.Text.Json.Serialization;
using BankRecon.Api.Demo;
using BankRecon.Api.Features;
using BankRecon.Api.Infrastructure;
using BankRecon.Api.Persistence;
using BankRecon.Api.Reconciliation;
using BankRecon.Api.WorkQueue;
using BankRecon.Core.Matching;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);
builder.AddObservability();
builder.Services.AddPersistence(builder.Configuration);
builder.Services.AddSingleton(builder.Configuration.GetSection("Matching").Get<MatchingOptions>() ?? new MatchingOptions());
builder.Services.AddSingleton(builder.Configuration.GetSection("WorkQueue").Get<WorkQueueOptions>() ?? new WorkQueueOptions());
builder.Services.AddSingleton(new ApiOptions
{
    Keys = (builder.Configuration["Api:Keys"] ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
});
builder.Services.AddSingleton<ApiKeyFilter>();
builder.Services.AddSingleton<StatementImporter>();
builder.Services.AddSingleton<LedgerItemStore>();
builder.Services.AddSingleton<ReconciliationService>();
builder.Services.AddSingleton<WorkQueueService>();
builder.Services.AddSingleton<DemoSeeder>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower)));
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

var app = builder.Build();

if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

if (args.Contains("--seed-demo"))
{
    var summary = await app.Services.GetRequiredService<DemoSeeder>().SeedAsync(CancellationToken.None);
    Console.WriteLine(DemoSeeder.ToJson(summary));
    return;
}

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.MigrateDatabaseAsync();
}

var apiKeys = app.Services.GetRequiredService<ApiKeyFilter>();
if (!apiKeys.Enabled)
{
    app.Logger.LogWarning("No API key configured (Api:Keys): the API is open. Acceptable for local development only.");
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.MapHealthEndpoints();
if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapGroup("/api/v1").AddEndpointFilter<ApiKeyFilter>().MapReconApi();

await app.RunAsync();

/// <summary>Entry point; public so integration tests can host it with WebApplicationFactory.</summary>
public partial class Program;
