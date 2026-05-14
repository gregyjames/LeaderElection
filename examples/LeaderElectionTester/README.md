# LeaderElectionTester Example

This example app demonstrates distributed leader election using the `LeaderElection` libraries in
this repository.

When you run multiple instances of the app:

- exactly one instance becomes leader and periodically runs "leader work"
- leadership automatically transfers to another instance when the current leader stops

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
- A compatible container runtime, such as Docker.

## Running With Aspire (Recommended)

The `LeaderElectionTester.AppHost` project orchestrates the infrastructure automatically using .NET
Aspire. This is the recommended way to run the example, as it provides a unified experience and
dashboard for viewing logs of all the running instances.

From the repository root:

```bash
./build.ps1 runExample
```

This builds the example project then creates a Redis container and two example application
instances. To use a different LeaderElection type, pass it as a command-line argument. For example:

```bash
./build.ps1 runExample -- redis # same as above
./build.ps1 runExample -- blob # or 'BlobStorage'
./build.ps1 runExample -- s3
./build.ps1 runExample -- postgres
./build.ps1 runExample -- dc # or 'DistributedCache'
./build.ps1 runExample -- fc # or 'FusionCache'
```

You can also adjust the number of example application instances using the `-count` parameter. For
example, to start 5 instances using Postgres:

```bash
  ./build.ps1 runExample -- postgres -count 5
```

The Aspire dashboard (click the "dashboard" link in the console output) lets you view logs from all
example application instances side-by-side. You can stop and start individual instances to see
leadership transfer in action.

## Running Standalone (Without Aspire)

You can also run the tester directly against manually configured infrastructure.

### Start A Backing Store

#### Redis (for Redis, DistributedCache, FusionCache)

```bash
docker run --name leader-election-redis -p 6379:6379 -d redis:7
```

#### Azurite (for BlobStorage)

```bash
docker run --name leader-election-azurite -p 10000:10000 -d mcr.microsoft.com/azure-storage/azurite:latest azurite --skipApiVersionCheck --loose --blobPort 10000 --blobHost 0.0.0.0
```

#### MinIO (for S3)

```bash
docker run --name leader-election-minio -p 9000:9000 -p 9001:9001 -e MINIO_ROOT_USER=accessKey -e MINIO_ROOT_PASSWORD=secretKey -d minio/minio server /data --console-address ":9001"
```

**Important:** Log into the MinIO console (<http://localhost:9001>) and create the `my-app-locks`
bucket (or modify the example to use a existing bucket name).

#### PostgreSQL (for Postgres)

```bash
docker run --name leader-election-postgres -p 5432:5432 -e POSTGRES_DB=mydb -e POSTGRES_USER=myuser -e POSTGRES_PASSWORD=mypassword -d postgres:17
```

### Run The Example (Standalone)

```bash
./build.ps1 runExample -- -NoAspire
```

This starts a single example application instance running Redis leader election. To use a different
leader election type, pass it as a command-line argument. For example:

```bash
./build.ps1 runExample -- -NoAspire redis
./build.ps1 runExample -- -NoAspire blob # or 'BlobStorage'
./build.ps1 runExample -- -NoAspire s3
./build.ps1 runExample -- -NoAspire postgres
./build.ps1 runExample -- -NoAspire dc # or 'DistributedCache'
./build.ps1 runExample -- -NoAspire fc # or 'FusionCache'
```

Run the command in multiple terminals to start multiple instances. Each instance will log whether it
is the leader or not. Stop the leader instance (Ctrl+C) to see leadership transfer in the remaining
instances.

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
