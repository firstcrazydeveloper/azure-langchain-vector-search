namespace VectorSearchApi.SystemTests.Models;

public sealed class ApiResponse<T>
{
    public int StatusCode { get; init; }
    public T? Body { get; init; }
    public string? RawContent { get; init; }
    public long ResponseTimeMs { get; init; }
    public bool IsSuccess { get; init; }
    public Exception? Exception { get; init; }

    public bool HasBody => Body is not null;

    public override string ToString() =>
        $"HTTP {StatusCode} | {ResponseTimeMs}ms | Success={IsSuccess}";
}
