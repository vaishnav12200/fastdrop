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
    options.UseSqlServer(connectionString);
    // Log all SQL statements to the console in Development so we can debug EF Core issues
    if (builder.Environment.IsDevelopment())
    {
        options.LogTo(Console.WriteLine, Microsoft.Extensions.Logging.LogLevel.Information)
               .EnableSensitiveDataLogging();
    }
});

var app = builder.Build();

// Enable Scalar UI in Development
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.UseForwardedHeaders(); // Must be before RateLimiter
app.UseRateLimiter(); // Apply Rate Limiting
app.UseHttpsRedirection();
app.MapControllers(); // Maps the HTTP routes to our Controllers

app.Run();
