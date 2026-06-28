using System;
using System.IO;
using IoTEdgeGateway.Application.Filters;
using IoTEdgeGateway.Application.Pipeline;
using IoTEdgeGateway.Application.Services;
using IoTEdgeGateway.Application.Translators;
using IoTEdgeGateway.Application.Validators;
using IoTEdgeGateway.Domain.Interfaces;
using IoTEdgeGateway.Infrastructure.Adapters;
using IoTEdgeGateway.Infrastructure.Alert;
using IoTEdgeGateway.Infrastructure.Configuration;
using IoTEdgeGateway.Infrastructure.IoTHub;
using IoTEdgeGateway.Module;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((context, config) =>
    {
        config
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile($"appsettings.{context.HostingEnvironment.EnvironmentName}.json", optional: true)
            .AddEnvironmentVariables("GATEWAY_");
    })
    .ConfigureLogging(logging =>
    {
        logging.ClearProviders();
        logging.AddConsole(opts => opts.FormatterName = "json");
        logging.AddDebug();
    })
    .ConfigureServices((context, services) =>
    {
        // Configuration
        services.Configure<GatewayConfiguration>(context.Configuration.GetSection("Gateway"));
        services.Configure<FilterOptions>(context.Configuration.GetSection("Gateway:Filter"));
        services.Configure<ValidationOptions>(context.Configuration.GetSection("Gateway:Validation"));

        // Infrastructure — IoT Hub
        services.AddSingleton<IIoTHubClient, IoTHubClient>();

        // Infrastructure — Protocol Adapters
        services.AddSingleton<IProtocolAdapter, MqttProtocolAdapter>();
        services.AddSingleton<IProtocolAdapter, OpcUaProtocolAdapter>();
        services.AddSingleton<IProtocolAdapter, ModbusTcpProtocolAdapter>();

        // Infrastructure — Alert Manager
        services.AddSingleton<IAlertService, LocalAlertManager>();

        // Application — Translators (strategy per protocol)
        services.AddSingleton<IDataTranslator, MqttDataTranslator>();
        services.AddSingleton<IDataTranslator, OpcUaDataTranslator>();
        services.AddSingleton<IDataTranslator, ModbusDataTranslator>();

        // Application — Filter & Validator
        services.AddSingleton<IDataFilter, DataPointFilter>();
        services.AddSingleton<IDataValidator, CriticalDataValidator>();

        // Application — Pipeline
        services.AddSingleton<DataProcessingPipeline>();

        // Application — Command handlers
        services.AddSingleton<ICommandHandler, ConfigurationCommandHandler>();
        services.AddSingleton<CommandDispatchService>();

        // Background service (the module)
        services.AddHostedService<EdgeModule>();
    })
    .Build();

await host.RunAsync();
