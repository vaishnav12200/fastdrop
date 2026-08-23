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

## Deploying file transfers on Render

The `free` Render plan cannot reliably host this application: its filesystem is
ephemeral and Render can restart the instance, deleting every uploaded chunk.
For a working hosted transfer service, use a paid web-service plan and attach a
persistent disk in the Render Dashboard:

1. Change the `fastdrop-api` service to `starter` or above.
2. Add a persistent disk mounted at `/app/storage`. Choose a size larger than
   the largest supported transfer plus headroom (for example, 10 GB for files
   up to about 8 GB).
3. Set `Storage__BasePath=/app/storage/transfers` in the service environment.
4. Keep the service at one instance. A Render disk can only be attached to one
   instance; use object storage such as S3/R2 for a horizontally scalable
   deployment.

The download endpoint supports HTTP Range requests and the browser app hands
downloads to the native browser download manager. This streams directly to the
receiver's disk, avoids holding a whole multi-gigabyte file in tab memory, and
allows a browser to resume after a dropped connection.

## Malware scanning and quarantined uploads

FastDrop does not create a receiver link until the complete upload has passed a
malware scan. Files follow `Uploading → Scanning → Clean → link generated`.
Threats and files that cannot be safely scanned are `Blocked`; their stored
chunks are deleted. Scanner errors, timeouts, unknown results, and unavailable
services fail closed: the transfer stays quarantined and never receives a link.

If transfer metadata survives but its required local chunks do not (for example,
after a Render restart without a persistent disk), FastDrop marks the transfer
`Failed`, deletes any remaining directory, and never retries or shares it. The
sender must upload the file again. Render can probe the lightweight `GET /health`
endpoint; it does not contact Postgres, Redis, storage, ClamAV, or MetaDefender.

### Local development

`docker compose up --build` uses local PostgreSQL, Redis, and ClamAV. The
Compose database is intentionally isolated from Neon: local and Render use
different chunk storage, so they must never run scan workers against the same
transfer records. Compose does not load a local `.env` file, so a Neon
connection string cannot accidentally cross that boundary. It streams the composite chunk file to ClamAV without
creating `final.dat` or loading the full file into memory. The Compose
configuration keeps ClamAV's scan limit at 4 GB.

### Render production: MetaDefender Cloud

Production uses [OPSWAT MetaDefender Cloud](https://opswat.com/products/metadefender/cloud), not a Render private ClamAV service. FastDrop streams the quarantined composite file once to `POST /v4/file`, stores MetaDefender's `data_id` in Postgres, and then polls only that ID. This means no `final.dat`, no whole-file RAM allocation, and no repeated GB-sized upload while waiting for a verdict.

MetaDefender Cloud is asynchronous, so scanning may take seconds to minutes;
archives and free-tier requests can take longer. Its free API is lower priority.
The paid Cloud tiers advertise limits of 140 MB, 256 MB, and **1 GB+** depending
on the contract. Set `MaximumFileSizeBytes` to the actual value shown by your
MetaDefender API key/contract. FastDrop rejects files above that configured
limit rather than falsely treating them as clean. A provider that only supports
smaller files is not suitable for FastDrop's GB-sized production use.

Private scanning requires a paid MetaDefender Cloud license. With
`PrivateScanning=true`, FastDrop sends `samplesharing: 0`; MetaDefender states
that it removes the uploaded file after processing while retaining the scan
result. Even with this mode, the file's content leaves Render for scanning, so
do not present the service as end-to-end encrypted.

Add these variables in the Render service's **Environment** tab:

```text
MalwareScanner__MetaDefenderCloud__ApiKey=<your MetaDefender Cloud API key>
MalwareScanner__MetaDefenderCloud__MaximumFileSizeBytes=1073741824
MalwareScanner__MetaDefenderCloud__PrivateScanning=true
MalwareScanner__MetaDefenderCloud__UploadTimeoutMinutes=45
MalwareScanner__MetaDefenderCloud__PollTimeoutSeconds=20
```

`ApiKey` is required. The remaining values show the supplied 1 GiB default;
increase `MaximumFileSizeBytes` only when your paid MetaDefender contract
supports it. `BaseUrl` defaults to `https://api.metadefender.com/v4/`; set
`MalwareScanner__MetaDefenderCloud__BaseUrl` only for an approved alternate
endpoint.

FastDrop retries result polling three times for transient API failures, then
uses persisted exponential backoff (5 seconds through 5 minutes). It does not
automatically retry a timed-out submission, because doing so could send a
multi-GB file to the provider twice. A failed submission remains quarantined
and is retried later; a submission that returned a `data_id` is only polled.
