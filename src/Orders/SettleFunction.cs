using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace BreakerDemo.Orders;

/// <summary>
/// Settles parked payments. It calls the payment provider through the same
/// circuit breaker as the order API.
///
/// The interesting part is what happens when the breaker is open. A queue
/// consumer that simply throws gets the message back at once, fails again in
/// a millisecond (the breaker fails fast) and burns through the queue's
/// MaxDeliveryCount within seconds. Fast failure plus automatic redelivery
/// turns an outage into a dead-letter queue full of perfectly good payments.
///
/// So the default mode does not throw. It sends a copy of the message that
/// becomes visible after the break duration and completes the original.
/// The <c>settleMode</c> property on the message selects the behavior, so the
/// test script can show both without a redeploy.
/// </summary>
public sealed class SettleFunction
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrderStore _store;
    private readonly PaymentClient _payments;
    private readonly BreakerTelemetry _telemetry;
    private readonly BreakerSettings _settings;
    private readonly ServiceBusSender _sender;
    private readonly ILogger<SettleFunction> _logger;

    public SettleFunction(
        OrderStore store,
        PaymentClient payments,
        BreakerTelemetry telemetry,
        BreakerSettings settings,
        ServiceBusSender sender,
        ILogger<SettleFunction> logger)
    {
        _store = store;
        _payments = payments;
        _telemetry = telemetry;
        _settings = settings;
        _sender = sender;
        _logger = logger;
    }

    [Function("SettlePayment")]
    public async Task Run(
        [ServiceBusTrigger("%PendingQueueName%", Connection = "ServiceBusConnection", AutoCompleteMessages = false)]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
    {
        var pending = message.Body.ToObjectFromJson<PendingPayment>(JsonOptions)
            ?? throw new InvalidOperationException("Empty payment message.");
        var attempt = message.ApplicationProperties.TryGetValue("attempt", out var a) ? Convert.ToInt32(a) : 1;
        var settleMode = message.ApplicationProperties.TryGetValue("settleMode", out var m) ? m?.ToString() : "reschedule";

        var order = await _store.GetAsync(pending.RunId, pending.OrderId, ct);
        if (order is null || order.Status == OrderStatus.Paid)
        {
            // Nothing to do: unknown order, or a duplicate message for a paid one.
            await actions.CompleteMessageAsync(message, ct);
            return;
        }

        // Same idempotency key as the first attempt. If that attempt timed out
        // but the provider did charge, this call finds the existing charge.
        var result = await _payments.ChargeAsync(PaymentClient.Breaker, pending.RunId, pending.OrderId, pending.Amount, ct);

        if (result.Outcome == ChargeOutcome.Paid)
        {
            order.Status = OrderStatus.Paid;
            order.PaidUtc = DateTimeOffset.UtcNow;
            order.SettledLate = true;
            order.SettleAttempts = attempt;
            await _store.SaveAsync(order, ct);
            await actions.CompleteMessageAsync(message, ct);
            _logger.LogInformation("Order {OrderId} settled late on attempt {Attempt}", pending.OrderId, attempt);
            return;
        }

        if (settleMode == "throw")
        {
            // The anti-pattern, kept for the demo: the host abandons the message,
            // Service Bus redelivers it immediately, the open breaker rejects it again.
            throw new InvalidOperationException(
                $"Payment for order {pending.OrderId} failed with {result.Outcome} (delivery {message.DeliveryCount}).");
        }

        if (attempt >= _settings.MaxSettleAttempts)
        {
            // Give up on purpose, with a reason a human can read in the dead-letter queue.
            order.Status = OrderStatus.PaymentAbandoned;
            order.SettleAttempts = attempt;
            await _store.SaveAsync(order, ct);
            await actions.DeadLetterMessageAsync(
                message,
                deadLetterReason: "SettleAttemptsExhausted",
                deadLetterErrorDescription: $"Last outcome {result.Outcome} after {attempt} attempts.",
                cancellationToken: ct);
            return;
        }

        // Wait at least as long as the breaker stays open, plus jitter so the
        // parked payments do not all return in the same second.
        var wait = result.Outcome == ChargeOutcome.CircuitOpen && _telemetry.RetryAfter > TimeSpan.Zero
            ? _telemetry.RetryAfter
            : TimeSpan.FromSeconds(_settings.BreakSeconds);
        var delay = wait + TimeSpan.FromMilliseconds(Random.Shared.Next(500, 3000));

        // Send first, complete second. A crash in between produces a duplicate
        // message, never a lost one. The idempotency key makes the duplicate harmless.
        await _sender.SendMessageAsync(OrderFunctions.NewPendingMessage(order, attempt + 1, "reschedule", delay), ct);
        await actions.CompleteMessageAsync(message, ct);

        _logger.LogInformation(
            "Order {OrderId} not settled ({Outcome}); attempt {Attempt} rescheduled in {DelaySeconds:F1}s",
            pending.OrderId, result.Outcome, attempt + 1, delay.TotalSeconds);
    }
}
