using FluentAssertions.Execution;
using VectorSearchApi.SystemTests.Models.Requests;
using VectorSearchApi.SystemTests.Models.Responses;
using VectorSearchApi.SystemTests.Support.Context;
using VectorSearchApi.SystemTests.Support.Infrastructure;

namespace VectorSearchApi.SystemTests.StepDefinitions;

[Binding]
public sealed class SearchStepDefinitions
{
    private readonly VectorSearchApiClient _client;
    private readonly ApiTestContext _ctx;

    public SearchStepDefinitions(VectorSearchApiClient client, ApiTestContext ctx)
    {
        _client = client;
        _ctx = ctx;
    }

    [When(@"I search for ""(.*)"" with top (.*) results")]
    public async Task WhenISearchForWithTopResults(string query, int topK)
    {
        var parameters = new SearchQueryParameters(query, topK);
        var response = await _client.SearchAsync(parameters);
        _ctx.RecordSearch(response);
    }

    [When(@"I send a search request without the query parameter")]
    public async Task WhenISendASearchRequestWithoutTheQueryParameter()
    {
        var response = await _client.SearchWithoutQueryAsync();
        _ctx.RecordSearch(response);
    }

    [Then(@"the search response should contain at least (.*) result[s]?")]
    public void ThenTheSearchResponseShouldContainAtLeastResults(int minCount)
    {
        _ctx.SearchResponse.Should().NotBeNull("a search response must exist");
        _ctx.SearchResponse!.Body.Should().NotBeNull("search response body must be deserialised");
        _ctx.SearchResponse!.Body!
            .Should().HaveCountGreaterThanOrEqualTo(minCount,
                $"expected at least {minCount} result(s) but found {_ctx.SearchResponse!.Body!.Count}");
    }

    [Then(@"the search response should contain at most (.*) results?")]
    public void ThenTheSearchResponseShouldContainAtMostResults(int maxCount)
    {
        _ctx.SearchResponse.Should().NotBeNull();
        _ctx.SearchResponse!.Body.Should().NotBeNull();
        _ctx.SearchResponse!.Body!
            .Should().HaveCountLessOrEqualTo(maxCount,
                $"expected at most {maxCount} result(s) but found {_ctx.SearchResponse!.Body!.Count}");
    }

    [Then(@"each search result should have a non-empty fileName")]
    public void ThenEachSearchResultShouldHaveANonEmptyFileName()
    {
        AssertAllResults(r => r.FileName.Should().NotBeNullOrWhiteSpace("fileName must be set"));
    }

    [Then(@"each search result should have a non-empty chunkId")]
    public void ThenEachSearchResultShouldHaveANonEmptyChunkId()
    {
        AssertAllResults(r => r.ChunkId.Should().NotBeNullOrWhiteSpace("chunkId must be set"));
    }

    [Then(@"each search result should have a non-empty docType")]
    public void ThenEachSearchResultShouldHaveANonEmptyDocType()
    {
        AssertAllResults(r => r.DocType.Should().NotBeNullOrWhiteSpace("docType must be set"));
    }

    [Then(@"each search result should have a non-empty snippet")]
    public void ThenEachSearchResultShouldHaveANonEmptySnippet()
    {
        AssertAllResults(r => r.Snippet.Should().NotBeNullOrWhiteSpace("snippet must be set"));
    }

    [Then(@"each search result should have a positive relevance score")]
    public void ThenEachSearchResultShouldHaveAPositiveRelevanceScore()
    {
        AssertAllResults(r =>
        {
            r.Score.Should().HaveValue("score must not be null");
            r.Score!.Value.Should().BeGreaterThan(0, "relevance score must be positive");
        });
    }

    private void AssertAllResults(Action<SearchResult> assertion)
    {
        _ctx.SearchResponse.Should().NotBeNull();
        _ctx.SearchResponse!.Body.Should().NotBeNull();

        using var scope = new AssertionScope();
        foreach (var result in _ctx.SearchResponse!.Body!)
        {
            assertion(result);
        }
    }
}
