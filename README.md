# FastDrop

FastDrop is a high-performance temporary large-file transfer platform backend, built with C# and .NET 9. It is designed as a temporary file-transfer middle layer, enabling secure, reliable, and fast chunk-based file transfers between devices without requiring user accounts or permanent storage.

## Features

- **Chunked & Resumable Uploads**: Transfer large files reliably over unstable networks without loading entire files into memory.
- **Streaming Downloads**: Stream files to receivers with support for HTTP range requests (resume support).
- **Temporary Security Model**: Cryptographically secure temporary transfer tokens are generated for each session, eliminating the need for JWT authentication, passwords, or persistent user accounts.
- **Idempotent Chunk Handling**: Safely handle duplicate chunks during network retries.
- **Data Integrity**: Cryptographic hashing (SHA-256) at both the chunk level and file level to ensure no data corruption occurs.
- **High Concurrency**: Safely process multiple simultaneous chunk uploads per transfer using proper database constraints and concurrency handling.
- **Auto-Cleanup & Expiration**: Background services automatically expire and clean up temporary transfers and files.
- **Modular Storage**: Extensible storage abstraction designed to support local filesystem initially, with readiness for MinIO, S3, or Azure Blob Storage in the future.

## Tech Stack

- **Core**: C#, .NET 9, ASP.NET Core Web API
- **Architecture**: Clean Architecture (Api, Application, Domain, Infrastructure)
- **Database**: SQL Server (via Entity Framework Core)
- **Caching/State**: Redis (for distributed locks, rate limiting, and short-lived caching)
- **Testing**: xUnit
- **Infrastructure**: Docker & Docker Compose
- **Documentation**: Swagger/OpenAPI

## Architecture

The project follows a Clean Architecture approach with a strict dependency flow:

```
FastDrop.Domain (Core business entities, value objects, interfaces)
       ↑
FastDrop.Application (Use cases, DTOs, application logic)
       ↑
FastDrop.Infrastructure (SQL Server, Redis, File Storage implementation)
       ↑
FastDrop.Api (HTTP endpoints, controllers, middleware)
```

## Transfer Flow

1. **Sender** creates a transfer session via the API.
2. The **Backend** generates secure temporary tokens (for Sender and Receiver) and returns them.
3. The **Frontend** can display the receiver join information as a QR code or link.
4. The **Receiver** joins the transfer using their secure token.
5. The **Sender** streams the file in chunks to the API. The API validates chunk numbers, sizes, and hashes, storing them via the storage abstraction.
6. The **Backend** verifies all chunks are received and complete, calculating the final file hash.
7. The **Receiver** downloads the file as a stream.
8. The **Backend** automatically expires the session and cleans up resources once completed or expired.

## Development Strategy

The development of FastDrop is structured into distinct, incremental phases focusing first on core domain logic, advancing to robust chunk handling and streaming, and concluding with performance optimizations and dockerization. Performance is measured and optimized based on real-world benchmarks.
