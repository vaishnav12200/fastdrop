using FastDrop.Application.Common.Interfaces;
using FastDrop.Api.BackgroundServices;
using Scalar.AspNetCore;
using FastDrop.Application.Security;
using FastDrop.Application.Services;
using FastDrop.Infrastructure.Data;
using FastDrop.Infrastructure.Repositories;
using FastDrop.Infrastructure.Security;
using FastDrop.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using StackExchange.Redis;
using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers, OpenAPI, and Response Compression
builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddResponseCompression(opts => opts.EnableForHttps = true);

// Configure Forwarded Headers for reverse proxy environments (e.g. Docker + Nginx)
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Clear known networks/proxies to trust all for local development/docker compose
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Configure Global Rate Limiter
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        // Extract IP address or fallback to unknown
        var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        
        return RateLimitPartition.GetSlidingWindowLimiter(
            partitionKey: ipAddress,
            factory: partition => new SlidingWindowRateLimiterOptions
            {
                PermitLimit = 500,
                Window = TimeSpan.FromSeconds(10),
                SegmentsPerWindow = 2,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 100
            });
    });
});

// Redis Registration (Connection Multiplexer for Locks)
var rawRedisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
var redisConnectionString = NormalizeRedisConnectionString(rawRedisConnectionString);
var redisOptions = StackExchange.Redis.ConfigurationOptions.Parse(redisConnectionString);
redisOptions.AbortOnConnectFail = false; // Guaranteed to not crash on startup
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisOptions));

// Dependency Injection Registrations
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddSingleton<IDistributedLockProvider, RedisLockProvider>();
builder.Services.AddScoped<ITransferRepository, TransferRepository>();
builder.Services.AddScoped<ITransferService, TransferService>();

// Register the background cleanup worker.
// AddHostedService registers it as a singleton that is started and stopped with the application.
builder.Services.AddHostedService<TransferCleanupWorker>();

// Redis Registration
builder.Services.AddStackExchangeRedisCache(options =>
{
    options.ConfigurationOptions = redisOptions;
    options.InstanceName = "FastDrop_";
});

// Database Registration
var rawConnectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

// Normalize the connection string: Render (and Neon) may provide a postgres:// URI,
// but Npgsql only accepts the standard ADO.NET Key=Value format.
// This helper converts either format transparently.
var connectionString = NormalizePostgresConnectionString(rawConnectionString);

builder.Services.AddDbContext<FastDropDbContext>(options =>
{
    options.UseNpgsql(connectionString);
    // Log all SQL statements to the console in Development so we can debug EF Core issues
    if (builder.Environment.IsDevelopment())
    {
        options.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
               .EnableSensitiveDataLogging();
    }
});

var app = builder.Build();

// Auto-apply EF Core migrations on startup.
// This is safe for containerized deployments where migrations must run before first request.
// Includes retry logic to handle SQL Server container startup lag.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FastDropDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<FastDropDbContext>>();
    
    var retries = 10;
    while (retries > 0)
    {
        try
        {
            logger.LogInformation("Attempting to apply database migrations...");
            db.Database.Migrate();
            logger.LogInformation("Database migrations applied successfully.");
            break;
        }
        catch (Exception ex)
        {
            retries--;
            logger.LogWarning("Migration failed: {Message}. Retries left: {Retries}", ex.Message, retries);
            if (retries == 0) throw;
            Thread.Sleep(5000); // Wait 5s before retrying
        }
    }
}

// Enable Scalar UI in Development
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseForwardedHeaders(); // Must be before RateLimiter
app.UseRateLimiter(); // Apply Rate Limiting
app.UseResponseCompression(); // Compress responses (gzip/brotli)
app.UseHttpsRedirection();

// Serve static files from wwwroot with cache headers
app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = ctx =>
    {
        // Cache static assets (JS, CSS) for 1 hour. HTML is short-lived.
        var path = ctx.File.Name;
        if (path.EndsWith(".js") || path.EndsWith(".css"))
        {
            ctx.Context.Response.Headers.CacheControl = "public, max-age=3600";
        }
        else
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    }
});

app.MapControllers(); // Maps the HTTP routes to our Controllers

// Explicitly serve about.html so the SPA fallback doesn't swallow it
app.MapGet("/about", async context =>
{
    context.Response.ContentType = "text/html";
    await context.Response.SendFileAsync(
        Path.Combine(app.Environment.WebRootPath, "about.html")
    );
});

// Fallback to index.html for SPA routing (all other non-API routes)
app.MapFallbackToFile("index.html");

app.Run();

// ---------------------------------------------------------------------------
// Helper: converts a postgres:// URI to an Npgsql ADO.NET connection string.
// Npgsql does NOT accept the URI format that Neon/Render use natively.
// ---------------------------------------------------------------------------
static string NormalizePostgresConnectionString(string cs)
{
    cs = cs?.Trim('"', '\'', ' ', '\n', '\r') ?? "";
    if (!cs.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase) && 
        !cs.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        return cs; // Already in ADO.NET format — nothing to do.

    var uri = new Uri(cs);
    var userInfo = uri.UserInfo.Split(':', 2);
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password  = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : string.Empty;
    var host      = uri.Host;
    var port      = uri.IsDefaultPort ? 5432 : uri.Port;
    var database  = uri.AbsolutePath.TrimStart('/');

    // Parse query params (e.g. sslmode=require)
    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    var sslMode = query["sslmode"] ?? "Require";

    return $"Host={host};Port={port};Database={database};Username={username};Password={password};Ssl Mode={sslMode};";
}

// ---------------------------------------------------------------------------
// Helper: converts a redis:// URI to a StackExchange.Redis connection string.
// ---------------------------------------------------------------------------
static string NormalizeRedisConnectionString(string cs)
{
    cs = cs?.Trim('"', '\'', ' ', '\n', '\r') ?? "";
    if (!cs.StartsWith("redis://", StringComparison.OrdinalIgnoreCase) && 
        !cs.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
        return cs; // Already in hostname:port format

    var uri = new Uri(cs);
    var host = uri.Host;
    var port = uri.IsDefaultPort ? 6379 : uri.Port;
    var password = "";
    
    if (!string.IsNullOrEmpty(uri.UserInfo))
    {
        var userInfo = uri.UserInfo.Split(':', 2);
        password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : Uri.UnescapeDataString(userInfo[0]);
    }

    var result = $"{host}:{port}";
    if (!string.IsNullOrEmpty(password))
    {
        result += $",password={password}";
    }
    
    if (cs.StartsWith("rediss://", StringComparison.OrdinalIgnoreCase))
    {
        result += ",ssl=True";
    }

    return result;
}
