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

var builder = WebApplication.CreateBuilder(args);

// Add Controllers and OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

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

app.UseHttpsRedirection();
app.MapControllers(); // Maps the HTTP routes to our Controllers

app.Run();
