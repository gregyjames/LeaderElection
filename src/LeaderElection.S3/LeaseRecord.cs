using System.Text.Json.Serialization;

namespace LeaderElection.S3;

internal sealed class LeaseRecord
{
    [JsonPropertyName("holderId")]
    public string HolderId { get; init; } = "";

    [JsonPropertyName("leaseUntilUtc")]
    public DateTime LeaseUntilUtc { get; init; }
}
