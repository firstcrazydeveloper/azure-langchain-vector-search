using VectorSearchApi.SystemTests.Support.Context;
using VectorSearchApi.SystemTests.Support.Infrastructure;

namespace VectorSearchApi.SystemTests.StepDefinitions;

[Binding]
public sealed class HealthCheckStepDefinitions
{
    private readonly VectorSearchApiClient _client;
    private readonly ApiTestContext _ctx;

    public HealthCheckStepDefinitions(VectorSearchApiClient client, ApiTestContext ctx)
    {
        _client = client;
        _ctx = ctx;
    }

    [When(@"I send a GET request to the health check endpoint")]
    public async Task WhenISendAGetRequestToTheHealthCheckEndpoint()
    {
        var response = await _client.GetHealthAsync();
        _ctx.RecordHealth(response);
    }

    [Then(@"the health response status field should be ""(.*)""")]
    public void ThenTheHealthResponseStatusFieldShouldBe(string expectedStatus)
    {
        _ctx.HealthResponse.Should().NotBeNull("a health response must exist");
        _ctx.HealthResponse!.Body.Should().NotBeNull("health response body must be deserialised");
        _ctx.HealthResponse!.Body!.Status
            .Should().Be(expectedStatus, $"health status field should equal '{expectedStatus}'");
    }

    [Then(@"the health response should contain a non-empty index name")]
    public void ThenTheHealthResponseShouldContainANonEmptyIndexName()
    {
        _ctx.HealthResponse.Should().NotBeNull();
        _ctx.HealthResponse!.Body.Should().NotBeNull();
        _ctx.HealthResponse!.Body!.Index
            .Should().NotBeNullOrWhiteSpace("the index name must be present in the health response");
    }

    [Then(@"the health response index field should be ""(.*)""")]
    public void ThenTheHealthResponseIndexFieldShouldBe(string expectedIndex)
    {
        _ctx.HealthResponse.Should().NotBeNull();
        _ctx.HealthResponse!.Body.Should().NotBeNull();
        _ctx.HealthResponse!.Body!.Index
            .Should().Be(expectedIndex, $"expected index name '{expectedIndex}'");
    }
}
