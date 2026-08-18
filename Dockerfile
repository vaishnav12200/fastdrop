# See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

# Stage 1: Base runtime image
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS base
WORKDIR /app
EXPOSE 8080

# Stage 2: Build SDK image
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

# Copy project files and restore dependencies
COPY ["FastDrop.Api/FastDrop.Api.csproj", "FastDrop.Api/"]
COPY ["FastDrop.Application/FastDrop.Application.csproj", "FastDrop.Application/"]
COPY ["FastDrop.Domain/FastDrop.Domain.csproj", "FastDrop.Domain/"]
COPY ["FastDrop.Infrastructure/FastDrop.Infrastructure.csproj", "FastDrop.Infrastructure/"]

RUN dotnet restore "FastDrop.Api/FastDrop.Api.csproj"

# Copy the remaining source code and build
COPY . .
WORKDIR "/src/FastDrop.Api"
RUN dotnet build "FastDrop.Api.csproj" -c Release -o /app/build

# Stage 3: Publish
FROM build AS publish
RUN dotnet publish "FastDrop.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# Stage 4: Final runtime image
FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Define the entry point for the container
ENTRYPOINT ["dotnet", "FastDrop.Api.dll"]
