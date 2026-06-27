using VectorSearchApi.SystemTests.Models;
using VectorSearchApi.SystemTests.Models.Responses;

namespace VectorSearchApi.SystemTests.Support.Context;

/// <summary>
/// Scenario-scoped bag of state shared across step definitions via SpecFlow DI.
/// </summary>
public sealed class ApiTestContext
{
    public ApiResponse<HealthCheckResponse>? HealthResponse { get; set; }
    public ApiResponse<List<SearchResult>>? SearchResponse { get; set; }
    public long LastResponseTimeMs { get; set; }

    public int LastStatusCode =>
        HealthResponse?.StatusCode
        ?? SearchResponse?.StatusCode
        ?? throw new InvalidOperationException("No API call has been made yet in this scenario.");

    public void RecordHealth(ApiResponse<HealthCheckResponse> response)
    {
        HealthResponse = response;
        LastResponseTimeMs = response.ResponseTimeMs;
    }

    public void RecordSearch(ApiResponse<List<SearchResult>> response)
    {
        SearchResponse = response;
        LastResponseTimeMs = response.ResponseTimeMs;
    }
}
