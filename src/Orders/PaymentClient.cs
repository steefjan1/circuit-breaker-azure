using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace BreakerDemo.Orders;

public enum ChargeOutcome
{
    /// <summary>The provider confirmed the charge.</summary>
    Paid,
    /// <summary>The provider answered with an error.</summary>
    Failed,
    /// <summary>No answer in time. The charge may or may not have happened.</summary>
    TimedOut,
    /// <summary>The in-process breaker rejected the call. Nothing left this process.</summary>
    CircuitOpen,
    /// <summary>API Management rejected the call. Nothing reached the provider.</summary>
    GatewayCircuitOpen,
    /// <summary>The gateway path was requested but API Management is not deployed.</summary>
    NotConfigured
}

public sealed record ChargeResult(ChargeOutcome Outcome, int? StatusCode, long ElapsedMs);

/// <summary>
/// One method, three HTTP clients. The caller picks the client by name and
/// always gets a <see cref="ChargeResult"/> back, never an exception, so the
/// order logic can decide what a failure means for the order.
/// </summary>
public sealed class PaymentClient
{
    public const string Naive = "payment-naive";
    public const string Breaker = "payment-breaker";
    public const string Gateway = "payment-gateway";

    private readonly IHttpClientFactory _factory;
    private readonly BreakerSettings _settings;

    public PaymentClient(IHttpClientFactory factory, BreakerSettings settings)
    {
        _factory = factory;
        _settings = settings;
    }

    public async Task<ChargeResult> ChargeAsync(
        string clientName, string runId, string orderId, double amount, CancellationToken ct)
    {
        if (clientName == Gateway && string.IsNullOrWhiteSpace(_settings.PaymentGatewayUrl))
        {
            return new ChargeResult(ChargeOutcome.NotConfigured, null, 0);
        }

        var clock = Stopwatch.StartNew();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "charge")
            {
                Content = JsonContent.Create(new { orderId, amount })
            };
            // The order id is the idempotency key. A retry or a late settlement
            // of the same order can never charge twice.
            request.Headers.Add("Idempotency-Key", orderId);
            request.Headers.Add("X-Run-Id", runId);

            using var response = await _factory.CreateClient(clientName).SendAsync(request, ct);

            if (response.IsSuccessStatusCode)
            {
                return new ChargeResult(ChargeOutcome.Paid, (int)response.StatusCode, clock.ElapsedMilliseconds);
            }

            // A 503 without the stub's marker header was produced by the gateway
            // itself: its circuit is open and the provider never saw the call.
            var fromGateway = clientName == Gateway
                && response.StatusCode == HttpStatusCode.ServiceUnavailable
                && !response.Headers.Contains("X-Payment-Stub");

            return new ChargeResult(
                fromGateway ? ChargeOutcome.GatewayCircuitOpen : ChargeOutcome.Failed,
                (int)response.StatusCode,
                clock.ElapsedMilliseconds);
        }
        catch (BrokenCircuitException)
        {
            return new ChargeResult(ChargeOutcome.CircuitOpen, null, clock.ElapsedMilliseconds);
        }
        catch (TimeoutRejectedException)
        {
            return new ChargeResult(ChargeOutcome.TimedOut, null, clock.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            // HttpClient.Timeout fired (the naive and gateway paths).
            return new ChargeResult(ChargeOutcome.TimedOut, null, clock.ElapsedMilliseconds);
        }
        catch (HttpRequestException)
        {
            return new ChargeResult(ChargeOutcome.Failed, null, clock.ElapsedMilliseconds);
        }
    }
}
