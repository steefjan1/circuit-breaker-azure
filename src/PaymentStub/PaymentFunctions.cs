using System.Net;
using System.Text.Json;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Http;
using Microsoft.Extensions.Logging;

namespace BreakerDemo.PaymentStub;

public sealed record ChargeRequest(string? OrderId, double Amount);
public sealed record ModeRequest(string? Mode, int? DelayMs, double? FailureRate);

/// <summary>
/// The dependency that fails. Endpoints are anonymous to keep the demo simple;
/// run <c>azd down</c> when you are done.
/// </summary>
public sealed class PaymentFunctions
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] KnownModes = { "healthy", "down", "slow", "slow-success", "flaky" };

    private readonly StubStore _store;
    private readonly ILogger<PaymentFunctions> _logger;

    public PaymentFunctions(StubStore store, ILogger<PaymentFunctions> logger)
    {
        _store = store;
        _logger = logger;
    }

    [Function("Charge")]
    public async Task<HttpResponseData> Charge(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "charge")] HttpRequestData req,
        CancellationToken ct)
    {
        var body = await JsonSerializer.DeserializeAsync<ChargeRequest>(req.Body, JsonOptions, ct);
        var runId = Header(req, "X-Run-Id") ?? "default";
        var key = Header(req, "Idempotency-Key") ?? body?.OrderId;

        if (body is null || string.IsNullOrWhiteSpace(key))
        {
            return await Json(req, HttpStatusCode.BadRequest, new { error = "orderId or Idempotency-Key is required" });
        }

        var mode = await _store.GetModeAsync(ct);

        switch (mode.Mode)
        {
            case "down":
                return await Reject(req, runId, ct);

            case "slow":
                // The dangerous failure: the caller waits, then still gets an error.
                // The delay ignores the caller's cancellation, as a hung dependency would.
                await Task.Delay(mode.DelayMs, CancellationToken.None);
                return await Reject(req, runId, CancellationToken.None);

            case "slow-success":
                // The ambiguous failure: the caller gives up, but the charge still happens.
                await Task.Delay(mode.DelayMs, CancellationToken.None);
                return await ChargeOnce(req, runId, key, body.Amount, CancellationToken.None);

            case "flaky":
                return Random.Shared.NextDouble() < mode.FailureRate
                    ? await Reject(req, runId, ct)
                    : await ChargeOnce(req, runId, key, body.Amount, ct);

            default:
                return await ChargeOnce(req, runId, key, body.Amount, ct);
        }
    }

    [Function("SetMode")]
    public async Task<HttpResponseData> SetMode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "control/mode")] HttpRequestData req,
        CancellationToken ct)
    {
        var body = await JsonSerializer.DeserializeAsync<ModeRequest>(req.Body, JsonOptions, ct);
        var name = body?.Mode?.ToLowerInvariant() ?? "";

        if (!KnownModes.Contains(name))
        {
            return await Json(req, HttpStatusCode.BadRequest, new { error = "unknown mode", allowed = KnownModes });
        }

        var mode = new ModeEntity
        {
            Mode = name,
            DelayMs = Math.Clamp(body!.DelayMs ?? 8000, 0, 60000),
            FailureRate = Math.Clamp(body.FailureRate ?? 0.5, 0, 1)
        };
        await _store.SetModeAsync(mode, ct);
        _logger.LogWarning("Payment stub mode set to {Mode} (delay {DelayMs} ms, failure rate {FailureRate})",
            mode.Mode, mode.DelayMs, mode.FailureRate);

        return await Json(req, HttpStatusCode.OK, new { mode = mode.Mode, mode.DelayMs, mode.FailureRate });
    }

    [Function("GetMode")]
    public async Task<HttpResponseData> GetMode(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "control/mode")] HttpRequestData req,
        CancellationToken ct)
    {
        var mode = await _store.GetModeAsync(ct);
        return await Json(req, HttpStatusCode.OK, new { mode = mode.Mode, mode.DelayMs, mode.FailureRate });
    }

    [Function("GetStats")]
    public async Task<HttpResponseData> GetStats(
        [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "control/stats")] HttpRequestData req,
        CancellationToken ct)
    {
        var runId = req.Query["runId"] ?? "default";
        var stats = await _store.GetStatsAsync(runId, ct);
        return await Json(req, HttpStatusCode.OK, new { runId, stats });
    }

    private async Task<HttpResponseData> Reject(HttpRequestData req, string runId, CancellationToken ct)
    {
        await _store.RecordCallAsync(runId, "rejected", ct);
        return await Json(req, HttpStatusCode.ServiceUnavailable, new { error = "payment provider unavailable" });
    }

    private async Task<HttpResponseData> ChargeOnce(
        HttpRequestData req, string runId, string key, double amount, CancellationToken ct)
    {
        var created = await _store.TryChargeAsync(runId, key, amount, ct);
        await _store.RecordCallAsync(runId, created ? "charged" : "duplicate", ct);
        return await Json(req, HttpStatusCode.OK, new { charged = true, duplicate = !created, idempotencyKey = key });
    }

    private static string? Header(HttpRequestData req, string name) =>
        req.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static async Task<HttpResponseData> Json(HttpRequestData req, HttpStatusCode status, object body)
    {
        var res = req.CreateResponse(status);
        res.Headers.Add("Content-Type", "application/json");
        // Lets a caller behind API Management tell "the stub said 503" from "the gateway said 503".
        res.Headers.Add("X-Payment-Stub", "1");
        await res.WriteStringAsync(JsonSerializer.Serialize(body, JsonOptions));
        return res;
    }
}
