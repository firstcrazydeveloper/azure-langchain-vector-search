using VectorSearchApi.SystemTests.Support.Configuration;
using VectorSearchApi.SystemTests.Support.Context;
using VectorSearchApi.SystemTests.Support.Infrastructure;

namespace VectorSearchApi.SystemTests.StepDefinitions;

[Binding]
public sealed class CommonStepDefinitions
{
    private readonly VectorSearchApiClient _client;
    private readonly ApiTestContext _ctx;
    private readonly TestConfiguration _config;

    public CommonStepDefinitions(
        VectorSearchApiClient client,
        ApiTestContext ctx,
        TestConfiguration config)
    {
        _client = client;
        _ctx = ctx;
        _config = config;
    }

    [Given(@"the Vector Search API is reachable")]
    public async Task GivenTheVectorSearchApiIsReachable()
    {
        var response = await _client.GetHealthAsync();

        response.IsSuccess.Should().BeTrue(
            $"the Vector Search API at '{_config.ApiSettings.BaseUrl}' must be reachable " +
            $"before running scenarios. Got HTTP {response.StatusCode}.");
    }

    [Then(@"the response status code should be (.*)")]
    public void ThenTheResponseStatusCodeShouldBe(int expected)
    {
        _ctx.LastStatusCode.Should().Be(expected,
            $"expected HTTP {expected} but received HTTP {_ctx.LastStatusCode}.");
    }

    [Then(@"the response status code should not be (.*)")]
    public void ThenTheResponseStatusCodeShouldNotBe(int notExpected)
    {
        _ctx.LastStatusCode.Should().NotBe(notExpected,
            $"response status code should not be {notExpected}.");
    }

    [Then(@"the response time should be less than (.*) milliseconds")]
    public void ThenTheResponseTimeShouldBeLessThanMilliseconds(long maxMs)
    {
        _ctx.LastResponseTimeMs.Should().BeLessOrEqualTo(maxMs,
            $"response completed in {_ctx.LastResponseTimeMs}ms which exceeds the {maxMs}ms threshold.");
    }
}
