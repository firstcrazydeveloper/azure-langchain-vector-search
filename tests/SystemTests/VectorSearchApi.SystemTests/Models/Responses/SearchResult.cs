using System.Text.Json.Serialization;

namespace VectorSearchApi.SystemTests.Models.Responses;

public sealed record SearchResult(
    [property: JsonPropertyName("fileName")] string? FileName,
    [property: JsonPropertyName("chunkId")]  string? ChunkId,
    [property: JsonPropertyName("docType")]  string? DocType,
    [property: JsonPropertyName("snippet")]  string? Snippet,
    [property: JsonPropertyName("score")]    double? Score
);
