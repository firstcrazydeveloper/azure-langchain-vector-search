using System.Text.Json.Serialization;

namespace VectorSearchApi.SystemTests.Models.Responses;

public sealed record HealthCheckResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("index")] string Index
);
