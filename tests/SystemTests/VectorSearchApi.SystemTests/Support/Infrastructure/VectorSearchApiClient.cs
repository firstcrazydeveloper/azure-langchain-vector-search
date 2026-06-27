using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using VectorSearchApi.SystemTests.Models;
using VectorSearchApi.SystemTests.Models.Requests;
using VectorSearchApi.SystemTests.Models.Responses;

namespace VectorSearchApi.SystemTests.Support.Infrastructure;

public sealed class VectorSearchApiClient
{
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public VectorSearchApiClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<ApiResponse<HealthCheckResponse>> GetHealthAsync(
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync<HealthCheckResponse>(
            () => _http.GetAsync("/healthz", cancellationToken),
            cancellationToken);
    }

    public async Task<ApiResponse<List<SearchResult>>> SearchAsync(
        SearchQueryParameters parameters,
        CancellationToken cancellationToken = default)
    {
        var url = $"/search?{parameters.ToQueryString()}";
        return await ExecuteAsync<List<SearchResult>>(
            () => _http.GetAsync(url, cancellationToken),
            cancellationToken);
    }

    public async Task<ApiResponse<List<SearchResult>>> SearchWithoutQueryAsync(
        CancellationToken cancellationToken = default)
    {
        return await ExecuteAsync<List<SearchResult>>(
            () => _http.GetAsync("/search", cancellationToken),
            cancellationToken);
    }

    private static async Task<ApiResponse<T>> ExecuteAsync<T>(
        Func<Task<HttpResponseMessage>> send,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var response = await send();
            sw.Stop();

            var raw = await response.Content.ReadAsStringAsync(cancellationToken);
            T? body = default;

            if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(raw))
            {
                body = JsonSerializer.Deserialize<T>(raw, JsonOptions);
            }

            return new ApiResponse<T>
            {
                StatusCode = (int)response.StatusCode,
                Body = body,
                RawContent = raw,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                IsSuccess = response.IsSuccessStatusCode
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new ApiResponse<T>
            {
                StatusCode = 0,
                Exception = ex,
                ResponseTimeMs = sw.ElapsedMilliseconds,
                IsSuccess = false
            };
        }
    }
}
