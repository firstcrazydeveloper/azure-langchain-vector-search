using VectorSearchApi.SystemTests.Support.Configuration;
using VectorSearchApi.SystemTests.Support.Context;
using VectorSearchApi.SystemTests.Support.Infrastructure;

namespace VectorSearchApi.SystemTests.Support.Hooks;

[Binding]
public sealed class TestLifecycleHooks
{
    private readonly IObjectContainer _container;
    private static ILogger _log = Serilog.Log.Logger;

    public TestLifecycleHooks(IObjectContainer container)
    {
        _container = container;
    }

    [BeforeTestRun]
    public static void BeforeTestRun()
    {
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console(outputTemplate:
                "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
            .WriteTo.File(
                "logs/system-tests-.log",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7)
            .CreateLogger();

        _log = Log.Logger;
        _log.Information("========== System Test Run Starting ==========");
    }

    [AfterTestRun]
    public static void AfterTestRun()
    {
        _log.Information("========== System Test Run Completed ==========");
        Log.CloseAndFlush();
    }

    [BeforeScenario(Order = 0)]
    public void BeforeScenario(ScenarioContext scenarioContext)
    {
        _log.Information(
            "▶ Scenario: [{Tags}] {Title}",
            string.Join(", ", scenarioContext.ScenarioInfo.Tags),
            scenarioContext.ScenarioInfo.Title);

        var config = TestConfiguration.Load();
        _container.RegisterInstanceAs(config);

        var apiClient = ApiClientFactory.Create(config);
        _container.RegisterInstanceAs(apiClient);

        _container.RegisterInstanceAs(new ApiTestContext());
    }

    [AfterScenario]
    public void AfterScenario(ScenarioContext scenarioContext)
    {
        if (scenarioContext.TestError is not null)
        {
            _log.Error(
                scenarioContext.TestError,
                "✗ FAILED: {Title}",
                scenarioContext.ScenarioInfo.Title);
        }
        else
        {
            _log.Information("✓ PASSED: {Title}", scenarioContext.ScenarioInfo.Title);
        }
    }
}
