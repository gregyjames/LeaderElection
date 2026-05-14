using Microsoft.Extensions.Configuration;

var builder = DistributedApplication.CreateBuilder(args);

var leaderElectionType = (
    builder.Configuration["LeaderElectionType"] ?? "Redis"
).ToUpperInvariant() switch
{
    "REDIS" => "Redis",
    "DISTRIBUTEDCACHE" or "DC" => "DistributedCache",
    "FUSIONCACHE" or "FC" => "FusionCache",
    "BLOBSTORAGE" or "BLOB" => "BlobStorage",
    "S3" => "S3",
    "POSTGRES" => "Postgres",
#pragma warning disable CA2208
    var v => throw new ArgumentException(
        $"Invalid LeaderElectionType '{v}'. Supported values: Redis, DistributedCache (dc), FusionCache (fc), BlobStorage (blob), S3, Postgres.",
        "LeaderElectionType"
    ),
#pragma warning restore CA2208
};

// Create multiple instances of the tester application. Each instance will
// attempt to acquire leadership using the selected leader election
// mechanism...
var testers = Enumerable
    .Range(1, builder.Configuration.GetValue("TesterCount", 2))
    .Select(i =>
    {
        var testerId = $"tester-{i}";
        return builder
            .AddProject<Projects.LeaderElectionTester>(testerId)
            .WithEnvironment("LeaderElectionType", leaderElectionType)
            .WithEnvironment("InstanceId", testerId);
    })
    .ToList();

// Setup the required infrastructure for the selected leader election mechanism
// and configure the testers to reference it and wait for it to be ready...
if (leaderElectionType is "Redis" or "DistributedCache" or "FusionCache")
{
    var redis = builder.AddRedis("redis");
    testers.ForEach(tester => tester.WithReference(redis).WaitFor(redis));
}
else if (leaderElectionType is "BlobStorage")
{
    var blobs = builder.AddAzureStorage("storage").RunAsEmulator().AddBlobs("blobs");
    testers.ForEach(tester => tester.WithReference(blobs).WaitFor(blobs));
}
else if (leaderElectionType is "Postgres")
{
    var db = builder.AddPostgres("postgres").AddDatabase("mydb");
    testers.ForEach(tester => tester.WithReference(db).WaitFor(db));
}
else if (leaderElectionType is "S3")
{
    var minioAccessKey = "accessKey";
    var minioSecretKey = "secretKey";
    var minioBucketName = "my-app-locks";

    var minio = builder
        .AddContainer("minio", "minio/minio")
        .WithHttpEndpoint(name: "api", targetPort: 9000)
        .WithHttpEndpoint(name: "console", targetPort: 9001)
        .WithEnvironment("MINIO_ROOT_USER", minioAccessKey)
        .WithEnvironment("MINIO_ROOT_PASSWORD", minioSecretKey)
        .WithArgs("server", "/data", "--console-address", ":9001");

    var minioApi = minio.GetEndpoint("api");

    var minioEndpointExpr = ReferenceExpression.Create(
        $"http://{minioApi.Property(EndpointProperty.Host)}:{minioApi.Property(EndpointProperty.Port)}"
    );

    // Setup MinIO with the required bucket for leader election locks before starting the testers.
    var minioSetup = builder
        .AddContainer("minio-setup", "minio/mc")
        .WithEntrypoint("/bin/sh")
        .WithArgs(
            "-c",
            $"until mc alias set myminio \"$MINIO_ENDPOINT\" \"{minioAccessKey}\" \"{minioSecretKey}\" --insecure; do echo 'Waiting for MinIO...'; sleep 1; done; mc mb --ignore-existing myminio/{minioBucketName} --insecure"
        )
        .WithEnvironment("MINIO_ENDPOINT", minioEndpointExpr)
        .WaitFor(minio);

    testers.ForEach(tester =>
        tester
            .WithEnvironment("Minio__Endpoint", minioEndpointExpr)
            .WithEnvironment("Minio__AccessKey", minioAccessKey)
            .WithEnvironment("Minio__SecretKey", minioSecretKey)
            .WithEnvironment("Minio__BucketName", minioBucketName)
            .WaitForCompletion(minioSetup)
    );
}

builder.Build().Run();
