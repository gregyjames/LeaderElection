# LeaderElectionTester Example

This example app demonstrates distributed leader election using the `LeaderElection` libraries in
this repository.

When you run multiple instances of the app:

- only one instance becomes leader
- only the leader runs the leader-only work
- leadership transfers automatically when the current leader stops

## What This Example Uses

The app is a .NET Generic Host with a background service that:

1. Starts leader election.
2. Subscribes to leadership events.
3. Runs `RunTaskIfLeaderAsync(...)` every 5 seconds.

LeaderElection type is controlled by the `LeaderElectionType` command line argument.

Supported values:

- `Redis`
- `DistributedCache` or `dc`
- `FusionCache` or `fc`
- `BlobStorage` or `blob`
- `S3`
- `Postgres`

If not set, it defaults to `Redis`.

See below for examples.

## Prerequisites

- .NET SDK that supports `net10.0`, e.g. .NET 10 SDK
- One backing store running locally (Redis, Azurite, MinIO, or Postgres)

## Start A Backing Store

### Redis (for Redis, DistributedCache, FusionCache)

```bash
docker run --name leader-election-redis -p 6379:6379 -d redis:7
```

### Azurite (for BlobStorage)

```bash
docker run --name leader-election-azurite -p 10000:10000 -d mcr.microsoft.com/azure-storage/azurite:latest azurite --skipApiVersionCheck --loose --blobPort 10000 --blobHost 0.0.0.0
```

### MinIO (for S3)

```bash
docker run --name leader-election-minio -p 9000:9000 -p 9001:9001 -e MINIO_ROOT_USER=accessKey -e MINIO_ROOT_PASSWORD=secretKey -d minio/minio server /data --console-address ":9001"
```

**Important:** Log into the MinIO console (<http://localhost:9001>) and create the `my-app-locks`
bucket (or modify the example to use a existing bucket name).

### PostgreSQL (for Postgres)

```bash
docker run --name leader-election-postgres -p 5432:5432 -e POSTGRES_DB=mydb -e POSTGRES_USER=myuser -e POSTGRES_PASSWORD=mypassword -d postgres:17
```

## Build

From the `./examples/LeaderElectionTester` directory, build the app:

```bash
cd ./examples/LeaderElectionTester
dotnet build
```

## Run The Example

From the `./examples/LeaderElectionTester` directory, run one instance:

```bash
cd ./examples/LeaderElectionTester
dotnet run --no-build
```

This defaults to `LeaderElectionType=Redis`.

Run a specific LeaderElection type:

```bash
dotnet run --no-build -- LeaderElectionType=Redis
dotnet run --no-build -- LeaderElectionType=DistributedCache
dotnet run --no-build -- LeaderElectionType=FusionCache
dotnet run --no-build -- LeaderElectionType=BlobStorage
dotnet run --no-build -- LeaderElectionType=S3
dotnet run --no-build -- LeaderElectionType=Postgres
```

You can also set it via environment variable:

```powershell
$env:LeaderElectionType = "Redis"
dotnet run
```

## Observe Leadership Transfer

1. Open two terminals.
2. Start the app in both terminals (same LeaderElection type).
3. Watch logs: one instance should report it is leader and run leader work.
4. Stop the leader instance.
5. The other instance should become leader shortly after.

## Notes

- Logging is configured in `appsettings.json` and can be adjusted to show more or less details.
- Each running instance is required to have a unique `InstanceId`, which is based on the app domain,
  machine name, and process ID. You can override this with the `InstanceId` argument, e.g.,
  `dotnet run -- InstanceId=Joe`.
- `Redis`, `DistributedCache`, and `FusionCache` sample modes all use Redis at `localhost:6379`.
- `BlobStorage` mode uses `UseDevelopmentStorage=true` (Azurite/storage emulator style connection).
- `S3` mode uses MinIO at `localhost:9000` with `accessKey` / `secretKey` and `my-app-locks` bucket.
- `Postgres` mode uses `Host=localhost;Database=mydb;Username=myuser;Password=mypassword` with
  advisory lock id `1`.
