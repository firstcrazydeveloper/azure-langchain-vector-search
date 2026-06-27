using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Polly;
using VectorSearchApi.SystemTests.Support.Configuration;

namespace VectorSearchApi.SystemTests.Support.Infrastructure;

public static class ApiClientFactory
{
    public static VectorSearchApiClient Create(TestConfiguration config)
    {
        var services = new ServiceCollection();

        services
            .AddHttpClient<VectorSearchApiClient>(client =>
            {
                client.BaseAddress = new Uri(config.ApiSettings.BaseUrl);
                client.Timeout = TimeSpan.FromSeconds(config.ApiSettings.TimeoutSeconds);
                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("User-Agent", "VectorSearchApi.SystemTests/1.0");
            })
            .AddResilienceHandler("retry", builder =>
            {
                builder.AddRetry(new HttpRetryStrategyOptions
                {
                    MaxRetryAttempts = config.ApiSettings.RetryPolicy.RetryCount,
                    Delay = TimeSpan.FromMilliseconds(config.ApiSettings.RetryPolicy.RetryDelayMilliseconds),
                    BackoffType = DelayBackoffType.Exponential,
                    UseJitter = true,
                    ShouldHandle = args => args.Outcome switch
                    {
                        { Exception: HttpRequestException } => PredicateResult.True(),
                        { Result.StatusCode: System.Net.HttpStatusCode.ServiceUnavailable } => PredicateResult.True(),
                        { Result.StatusCode: System.Net.HttpStatusCode.TooManyRequests } => PredicateResult.True(),
                        _ => PredicateResult.False()
                    }
                });
            });

        return services.BuildServiceProvider().GetRequiredService<VectorSearchApiClient>();
    }
}
