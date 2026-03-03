using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReactorTelemetry.SafetyProcessor.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // ServiceBusClient for the DLQ drain timer function
        services.AddSingleton(_ =>
        {
            var fqns = context.Configuration["ServiceBusConnection__fullyQualifiedNamespace"]
                ?? throw new InvalidOperationException(
                    "ServiceBusConnection__fullyQualifiedNamespace is not configured.");
            return new ServiceBusClient(fqns, new DefaultAzureCredential());
        });

        services.AddScoped<SafetyEvaluationService>();
    })
    .Build();

await host.RunAsync();
