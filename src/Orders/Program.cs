using Azure.Data.Tables;
using Azure.Identity;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using BreakerDemo.Orders;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Polly;

var settings = BreakerSettings.FromEnvironment();

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddApplicationInsightsTelemetryWorkerService();
        services.ConfigureFunctionsApplicationInsights();

        services.AddSingleton(settings);
        services.AddSingleton<BreakerTelemetry>();

        // DefaultAzureCredential picks the user-assigned identity through AZURE_CLIENT_ID.
        var credential = new DefaultAzureCredential();
        services.AddSingleton(_ => new TableServiceClient(new Uri(settings.TableEndpoint), credential));
        services.AddSingleton(_ => new ServiceBusClient(settings.ServiceBusNamespace, credential));
        services.AddSingleton(_ => new ServiceBusAdministrationClient(settings.ServiceBusNamespace, credential));
        services.AddSingleton(sp => sp.GetRequiredService<ServiceBusClient>().CreateSender(settings.PendingQueueName));
        services.AddSingleton<OrderStore>();
        services.AddSingleton<PaymentClient>();

        // Path 1, "naive": no timeout budget worth the name, no breaker.
        // Every caller waits for the dependency, however slow it is.
        services.AddHttpClient(PaymentClient.Naive, client =>
        {
            client.BaseAddress = new Uri(settings.PaymentUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        });

        // Path 2, "breaker": an attempt timeout inside a circuit breaker.
        // Order matters: the breaker is the outer strategy, so a timed-out
        // attempt counts as a failure in its sampling window.
        services.AddHttpClient(PaymentClient.Breaker, client =>
        {
            client.BaseAddress = new Uri(settings.PaymentUrl);
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddResilienceHandler("payment", (pipeline, context) =>
        {
            var telemetry = context.ServiceProvider.GetRequiredService<BreakerTelemetry>();

            pipeline.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
            {
                // Open when at least FailureRatio of the calls in SamplingDuration
                // failed, but only once MinimumThroughput calls were seen.
                // The default predicate counts 5xx, 408, 429, HttpRequestException
                // and timeouts. A 400 or 404 does not open the circuit.
                FailureRatio = settings.FailureRatio,
                MinimumThroughput = settings.MinimumThroughput,
                SamplingDuration = TimeSpan.FromSeconds(settings.SamplingSeconds),
                BreakDuration = TimeSpan.FromSeconds(settings.BreakSeconds),
                StateProvider = telemetry.StateProvider,
                OnOpened = args =>
                {
                    telemetry.Opened(args.BreakDuration);
                    return default;
                },
                OnHalfOpened = _ =>
                {
                    telemetry.HalfOpened();
                    return default;
                },
                OnClosed = _ =>
                {
                    telemetry.Closed();
                    return default;
                }
            });

            pipeline.AddTimeout(TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds));
        });

        // Path 3, "gateway": no breaker in this process. API Management holds it.
        services.AddHttpClient(PaymentClient.Gateway, client =>
        {
            if (!string.IsNullOrWhiteSpace(settings.PaymentGatewayUrl))
            {
                client.BaseAddress = new Uri(settings.PaymentGatewayUrl);
            }
            client.Timeout = TimeSpan.FromSeconds(30);
        });
    })
    .Build();

host.Run();
