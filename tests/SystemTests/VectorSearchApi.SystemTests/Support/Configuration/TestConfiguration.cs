using Microsoft.Extensions.Configuration;

namespace VectorSearchApi.SystemTests.Support.Configuration;

public sealed class ApiSettings
{
    public string BaseUrl { get; init; } = "http://localhost:8080";
    public int TimeoutSeconds { get; init; } = 30;
    public RetryPolicySettings RetryPolicy { get; init; } = new();
}

public sealed class RetryPolicySettings
{
    public int RetryCount { get; init; } = 3;
    public int RetryDelayMilliseconds { get; init; } = 500;
}

public sealed class TestSettings
{
    public int DefaultTopK { get; init; } = 5;
    public int MaxResponseTimeMilliseconds { get; init; } = 2000;
    public int SearchMaxResponseTimeMilliseconds { get; init; } = 5000;
    public string ExpectedIndexName { get; init; } = "docs-index";
}

public sealed class TestConfiguration
{
    public ApiSettings ApiSettings { get; init; } = new();
    public TestSettings TestSettings { get; init; } = new();

    public static TestConfiguration Load()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.test.json", optional: false, reloadOnChange: false)
            .AddEnvironmentVariables(prefix: "TEST_")
            .Build();

        var result = new TestConfiguration();
        config.Bind(result);
        return result;
    }
}
