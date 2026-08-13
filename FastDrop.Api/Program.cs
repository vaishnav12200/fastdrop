using FastDrop.Application.Common.Interfaces;
using FastDrop.Application.Security;
using FastDrop.Application.Services;
using FastDrop.Infrastructure.Data;
using FastDrop.Infrastructure.Repositories;
using FastDrop.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add Controllers and Swagger
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Dependency Injection Registrations
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
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

// Enable Swagger UI in Development
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.MapControllers(); // Maps the HTTP routes to our Controllers

app.Run();
