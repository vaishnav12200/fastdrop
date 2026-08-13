using FastDrop.Application.Common.Interfaces;
using Scalar.AspNetCore;
using FastDrop.Application.Security;
using FastDrop.Application.Services;
using FastDrop.Infrastructure.Data;
using FastDrop.Infrastructure.Repositories;
using FastDrop.Infrastructure.Security;
using FastDrop.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers and OpenAPI
builder.Services.AddControllers();
builder.Services.AddOpenApi();

// Dependency Injection Registrations
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<IFileStorage, LocalFileStorage>();
builder.Services.AddScoped<ITransferRepository, TransferRepository>();
builder.Services.AddScoped<ITransferService, TransferService>();

// Database Registration
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") 
    ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

builder.Services.AddDbContext<FastDropDbContext>(options =>
{
    options.UseSqlServer(connectionString);
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
