// EDUCATIONAL: .NET 8 Isolated Worker model
//
// The isolated worker model runs your code in a separate process from the Azure Functions host.
// Benefits:
// - Full control over the .NET version (not pinned to what the host uses)
// - Standard .NET DI, middleware, and configuration
// - Works like a regular .NET console app with IHostBuilder
//
// ConfigureFunctionsWebApplication() adds ASP.NET Core integration so HTTP triggers
// can return IActionResult, use HttpRequest, middleware filters, etc.
//
// DefaultAzureCredential resolution order (what it tries in order):
//   1. Environment variables (AZURE_CLIENT_ID, AZURE_TENANT_ID, AZURE_CLIENT_SECRET)
//   2. Workload Identity (Kubernetes)
//   3. Managed Identity (Azure-hosted: Functions, VMs, App Service)
//   4. Azure CLI credentials (az login — great for local dev)
//   5. Azure PowerShell
//   6. Visual Studio / VS Code credentials
//   7. Interactive browser (last resort)
// In Azure: step 3 (managed identity) is used. Locally: step 4 (az login).

using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ReactorTelemetry.Ingestor.Validators;

var host = new HostBuilder()
    .ConfigureFunctionsWebApplication()   // ASP.NET Core integration for HTTP triggers
    .ConfigureServices((context, services) =>
    {
        // Application Insights: auto-configured from APPLICATIONINSIGHTS_CONNECTION_STRING
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        // Configure JSON serialization to use string enum names (not integers)
        // so TelemetryEvent.SafetyLevel serializes as "Warning" not 1
        services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(opts =>
        {
            opts.SerializerOptions.Converters.Add(
                new System.Text.Json.Serialization.JsonStringEnumConverter());
        });

        // Service Bus client using managed identity.
        // Setting ServiceBusConnection__fullyQualifiedNamespace in app settings
        // triggers this auth pattern automatically via the binding infrastructure,
        // but we also register the client manually for use in the function constructor.
        services.AddSingleton(provider =>
        {
            var fqns = context.Configuration["ServiceBusConnection__fullyQualifiedNamespace"]
                ?? throw new InvalidOperationException(
                    "ServiceBusConnection__fullyQualifiedNamespace is not configured. " +
                    "Set it in local.settings.json (local) or app settings (Azure).");

            // EDUCATIONAL: DefaultAzureCredential works in Azure (managed identity)
            // and locally (az login / VS Code / Visual Studio credentials)
            return new ServiceBusClient(fqns, new DefaultAzureCredential());
        });

        services.AddScoped<TelemetryEventValidator>();
    })
    .Build();

await host.RunAsync();
