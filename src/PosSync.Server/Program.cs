using System.Diagnostics;
using System.IO.Compression;
using Microsoft.AspNetCore.ResponseCompression;
using PosSync.Server.Admin;
using PosSync.Server.Infrastructure;
using PosSync.Server.Persistence;
using PosSync.Server.Sync;
using Scalar.AspNetCore;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.AddObservability();

// Pushes are bounded twice: by change count (SyncOptions.MaxPushBatch) and by body size. The request decompression
// middleware enforces this limit on the decompressed stream, so a small gzip bomb cannot expand past it.
builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 2 * 1024 * 1024);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddPersistence(builder.Configuration);
builder.Services.Configure<SyncOptions>(builder.Configuration.GetSection(SyncOptions.Section));
builder.Services.AddSingleton<SyncStore>();
builder.Services.AddSingleton<DeviceRegistry>();
builder.Services.AddSingleton<DemoMenuSeeder>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database", tags: [HealthEndpoints.ReadyTag]);

builder.Services.AddRequestDecompression();
builder.Services.AddResponseCompression(options =>
{
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ["application/json"];
});
builder.Services.Configure<GzipCompressionProviderOptions>(o => o.Level = CompressionLevel.Fastest);

// RFC 9457 problem details for every error, with the trace id so a client-reported error can be found in traces.
builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
    context.ProblemDetails.Extensions.TryAdd("traceId", Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier));
builder.Services.AddExceptionHandler<DatabaseExceptionHandler>();

builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "offline-pos-sync server";
    document.Info.Version = "v1";
    document.Info.Description = "Sync server for offline-first POS terminals: idempotent push, cursor-based partial pull, HLC-ordered merges.";
    return Task.CompletedTask;
}));

var app = builder.Build();

// One image, three roles: `--migrate-only` applies migrations as the schema owner and exits; `--seed-demo` writes the
// synthetic demo menus as the application role and exits; with no flag the server serves requests.
if (args.Contains("--migrate-only"))
{
    await app.Services.MigrateDatabaseAsync();
    return;
}

if (args.Contains("--seed-demo"))
{
    await app.Services.GetRequiredService<DemoMenuSeeder>().RunAsync(CancellationToken.None);
    return;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging(options =>
    options.GetLevel = (context, _, exception) =>
        exception is null && context.Response.StatusCode < 400 &&
        (context.Request.Path.StartsWithSegments("/health") || context.Request.Path.StartsWithSegments("/api/v1/sync"))
            ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);
app.UseRequestDecompression();
app.UseResponseCompression();

if (app.Configuration.GetValue<bool>("OpenApi:Enabled"))
{
    app.MapOpenApi();
    app.MapScalarApiReference(options => options.WithTitle("offline-pos-sync"));
}

app.MapHealthEndpoints();
app.MapGroup("/api/v1")
    .WithGroupName("v1")
    .MapSyncEndpoints()
    .MapAdminEndpoints();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await app.Services.MigrateDatabaseAsync();
}

await app.RunAsync();

/// <summary>Entry point; public so WebApplicationFactory can boot the app in integration tests.</summary>
public partial class Program;
