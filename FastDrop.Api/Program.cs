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

using StackExchange.Redis;

using Microsoft.AspNetCore.HttpOverrides;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers and OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

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
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(sp => ConnectionMultiplexer.Connect(redisConnectionString));

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
    options.Configuration = redisConnectionString;
    options.InstanceName = "FastDrop_";
});

// Database Registration
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

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
app.UseHttpsRedirection();

// Serve static files from wwwroot
app.UseStaticFiles();

app.MapControllers(); // Maps the HTTP routes to our Controllers

// Fallback to index.html for SPA routing (if needed)
app.MapFallbackToFile("index.html");

app.Run();
