using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BreakerDemo.Orders;

public sealed record PlaceOrderRequest(double? Amount);

/// <summary>Body of a parked payment on the Service Bus queue.</summary>
public sealed record PendingPayment(string RunId, string OrderId, double Amount);

/// <summary>
/// The order API. One endpoint, four ways to call the payment provider:
/// <list type="bullet">
/// <item><c>naive</c>: plain HttpClient, the order fails when payment fails.</item>
/// <item><c>breaker</c>: in-process circuit breaker, the order still fails, but fast.</item>
/// <item><c>gateway</c>: API Management holds the breaker, the order still fails, but fast.</item>
/// <item><c>fallback</c>: in-process breaker plus a queue, the order is accepted and paid later.</item>
/// </list>
/// Endpoints are anonymous to keep the demo simple; run <c>azd down</c> when you are done.
/// </summary>
public sealed class OrderFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly OrderStore _store;
    private readonly PaymentClient _payments;
    private readonly BreakerTelemetry _telemetry;
    private readonly BreakerSettings _settings;
    private readonly ServiceBusSender _sender;
    private readonly ServiceBusAdministrationClient _admin;
    private readonly ILogger<OrderFunctions> _logger;

    public OrderFunctions(
        OrderStore store,
        PaymentClient payments,
        BreakerTelemetry telemetry,
        BreakerSettings settings,
        ServiceBusSender sender,
        ServiceBusAdministrationClient admin,
        ILogger<OrderFunctions> logger)
    {
        _store = store;
        _payments = payments;
        _telemetry = telemetry;
        _settings = settings;
        _sender = sender;
        _admin = admin;
        _logger = logger;
    }

    [Function("PlaceOrder")]
    public async Task<HttpResponseData> PlaceOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "orders")] HttpRequestData req,
        CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var path = (req.Query["path"] ?? "fallback").ToLowerInvariant();
        var runId = req.Query["runId"] ?? "default";
        var settleMode = (req.Query["settle"] ?? "reschedule").ToLowerInvariant();

        var body = await ReadBodyAsync(req, ct);

        // The order exists before the payment attempt. Whatever happens to the
        // payment provider, the customer's order is on record.
        var order = new OrderEntity
        {
            PartitionKey = runId,
            RowKey = Guid.NewGuid().ToString("N"),
            Amount = body?.Amount ?? 49.95,
            Path = path,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        await _store.SaveAsync(order, ct);

        var clientName = path switch
        {
            "naive" => PaymentClient.Naive,
            "gateway" => PaymentClient.Gateway,
            _ => PaymentClient.Breaker
        };

        var result = await _payments.ChargeAsync(clientName, runId, order.RowKey, order.Amount, ct);

        if (result.Outcome == ChargeOutcome.NotConfigured)
        {
            return await Json(req, HttpStatusCode.NotImplemented,
                new { error = "The gateway path needs API Management. Deploy with deployApim=true." });
        }

        HttpStatusCode status;
        if (result.Outcome == ChargeOutcome.Paid)
        {
            order.Status = OrderStatus.Paid;
            order.PaidUtc = DateTimeOffset.UtcNow;
            status = HttpStatusCode.Created;
        }
        else if (path == "fallback")
        {
            // The answer to "payment is down but the customer already ordered":
            // accept the order, park the payment, tell the customer the truth.
            order.Status = OrderStatus.PaymentPending;
            order.Reason = result.Outcome.ToString();
            status = HttpStatusCode.Accepted;
        }
        else
        {
            order.Status = OrderStatus.PaymentFailed;
            order.Reason = result.Outcome.ToString();
            status = HttpStatusCode.ServiceUnavailable;
        }

        await _store.SaveAsync(order, ct);

        if (order.Status == OrderStatus.PaymentPending)
        {
            // Known gap: the save above and this send are two operations. A crash
            // between them leaves a pending order without a message. Production
            // code closes that gap with a transactional outbox.
            await _sender.SendMessageAsync(NewPendingMessage(order, attempt: 1, settleMode, delay: TimeSpan.Zero), ct);
        }

        var response = await Json(req, status, new
        {
            orderId = order.RowKey,
            status = order.Status,
            path,
            outcome = result.Outcome.ToString(),
            paymentMs = result.ElapsedMs,
            totalMs = clock.ElapsedMilliseconds,
            instance = BreakerTelemetry.InstanceId,
            breakerState = _telemetry.State
        });

        if (result.Outcome == ChargeOutcome.CircuitOpen && status == HttpStatusCode.ServiceUnavailable)
        {
            // Tell well-behaved clients when a retry has a chance.
            response.Headers.Add("Retry-After", Math.Max(1, (int)Math.Ceiling(_telemetry.RetryAfter.TotalSeconds)).ToString());
        }

        return response;
    }

    [Function("GetOrder")]
    public async Task<HttpResponseData> GetOrder(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "orders/{runId}/{orderId}")] HttpRequestData req,
        string runId,
        string orderId,
        CancellationToken ct)
    {
        var order = await _store.GetAsync(runId, orderId, ct);
        return order is null
            ? await Json(req, HttpStatusCode.NotFound, new { error = "order not found" })
            : await Json(req, HttpStatusCode.OK, new
            {
                orderId = order.RowKey,
                order.Status,
                order.Path,
                order.Reason,
                order.Amount,
                order.CreatedUtc,
                order.PaidUtc,
                order.SettledLate,
                order.SettleAttempts
            });
    }

    [Function("GetSummary")]
    public async Task<HttpResponseData> GetSummary(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "summary")] HttpRequestData req,
        CancellationToken ct)
    {
        var runId = req.Query["runId"] ?? "default";
        var counts = await _store.SummaryAsync(runId, ct);
        return await Json(req, HttpStatusCode.OK, new { runId, counts });
    }

    [Function("GetBreaker")]
    public async Task<HttpResponseData> GetBreaker(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "breaker")] HttpRequestData req)
    {
        // This is the state of ONE instance. Call it a few times under load and
        // the instance id changes, and so may the state.
        return await Json(req, HttpStatusCode.OK, new
        {
            instance = BreakerTelemetry.InstanceId,
            state = _telemetry.State,
            openedCount = _telemetry.OpenedCount,
            retryAfterSeconds = Math.Round(_telemetry.RetryAfter.TotalSeconds, 1),
            settings = new
            {
                _settings.FailureRatio,
                _settings.MinimumThroughput,
                _settings.SamplingSeconds,
                _settings.BreakSeconds,
                _settings.AttemptTimeoutSeconds
            }
        });
    }

    [Function("GetQueue")]
    public async Task<HttpResponseData> GetQueue(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "queue")] HttpRequestData req,
        CancellationToken ct)
    {
        var props = (await _admin.GetQueueRuntimePropertiesAsync(_settings.PendingQueueName, ct)).Value;
        return await Json(req, HttpStatusCode.OK, new
        {
            queue = _settings.PendingQueueName,
            active = props.ActiveMessageCount,
            scheduled = props.ScheduledMessageCount,
            deadLetter = props.DeadLetterMessageCount
        });
    }

    /// <summary>Builds the queue message for a parked payment. Also used when a settlement is rescheduled.</summary>
    internal static ServiceBusMessage NewPendingMessage(OrderEntity order, int attempt, string settleMode, TimeSpan delay)
    {
        var message = new ServiceBusMessage(BinaryData.FromObjectAsJson(
            new PendingPayment(order.PartitionKey, order.RowKey, order.Amount), JsonOptions))
        {
            ContentType = "application/json",
            Subject = "payment-pending"
        };
        message.ApplicationProperties["attempt"] = attempt;
        message.ApplicationProperties["settleMode"] = settleMode;

        if (delay > TimeSpan.Zero)
        {
            message.ScheduledEnqueueTime = DateTimeOffset.UtcNow.Add(delay);
        }

        return message;
    }

    private static async Task<PlaceOrderRequest?> ReadBodyAsync(HttpRequestData req, CancellationToken ct)
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<PlaceOrderRequest>(req.Body, JsonOptions, ct);
        }
        catch (JsonException)
        {
            // An empty or invalid body is fine for the demo: the default amount applies.
            return null;
        }
    }

    private static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode status, object body)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json");
        await res.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return res;
    }
}
