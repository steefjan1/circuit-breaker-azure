using Azure.Data.Tables;
using Azure.Identity;
using BreakerDemo.PaymentStub;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// The payment stub is the unreliable dependency. It has no resilience code on
// purpose: its only job is to fail in controlled ways and to count every call
// that reaches it, so the demo can prove how many requests a breaker stopped.
var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        var tableEndpoint = Environment.GetEnvironmentVariable("TABLE_ENDPOINT")
            ?? throw new InvalidOperationException("TABLE_ENDPOINT is not set.");

        // DefaultAzureCredential picks the user-assigned identity through AZURE_CLIENT_ID.
        services.AddSingleton(_ => new TableServiceClient(new Uri(tableEndpoint), new DefaultAzureCredential()));
        services.AddSingleton<StubStore>();
    })
    .Build();

host.Run();
