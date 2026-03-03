using Azure.Data.Tables;
using Azure.Identity;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReactorTelemetry.AuditLogger.Services;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices((context, services) =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // EDUCATIONAL: TableServiceClient with managed identity.
        // AuditStorageConnection__accountName drives the storage account endpoint.
        // DefaultAzureCredential resolves to managed identity in Azure, az login locally.
        services.AddSingleton(_ =>
        {
            var accountName = context.Configuration["AuditStorageConnection__accountName"]
                ?? throw new InvalidOperationException("AuditStorageConnection__accountName is not configured.");
            var endpoint = new Uri($"https://{accountName}.table.core.windows.net");
            return new TableServiceClient(endpoint, new DefaultAzureCredential());
        });

        services.AddScoped<AuditTableService>();
    })
    .Build();

await host.RunAsync();
