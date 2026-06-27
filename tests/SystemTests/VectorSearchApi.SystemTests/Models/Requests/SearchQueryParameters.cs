namespace VectorSearchApi.SystemTests.Models.Requests;

public sealed record SearchQueryParameters(string Query, int TopK = 5)
{
    public string ToQueryString() =>
        $"q={Uri.EscapeDataString(Query)}&k={TopK}";
}
