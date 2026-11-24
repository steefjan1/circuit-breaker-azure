using System.Globalization;

namespace BreakerDemo.Orders;

/// <summary>
/// All tunables in one place, read from app settings. The values are small on
/// purpose so the demo trips and recovers within seconds. They are not
/// production defaults.
/// </summary>
public sealed record BreakerSettings
{
    public required string PaymentUrl { get; init; }
    public required string PaymentGatewayUrl { get; init; }
    public required string TableEndpoint { get; init; }
    public required string ServiceBusNamespace { get; init; }
    public required string PendingQueueName { get; init; }

    public double FailureRatio { get; init; } = 0.5;
    public int MinimumThroughput { get; init; } = 10;
    public int SamplingSeconds { get; init; } = 10;
    public int BreakSeconds { get; init; } = 15;
    public int AttemptTimeoutSeconds { get; init; } = 2;

    /// <summary>How often a parked payment is rescheduled before it is dead-lettered.</summary>
    public int MaxSettleAttempts { get; init; } = 20;

    public static BreakerSettings FromEnvironment() => new()
    {
        PaymentUrl = WithSlash(Required("PAYMENT_URL")),
        PaymentGatewayUrl = WithSlash(Environment.GetEnvironmentVariable("PAYMENT_GATEWAY_URL") ?? ""),
        TableEndpoint = Required("TABLE_ENDPOINT"),
        ServiceBusNamespace = Required("ServiceBusConnection__fullyQualifiedNamespace"),
        PendingQueueName = Environment.GetEnvironmentVariable("PendingQueueName") ?? "payments-pending",
        FailureRatio = Number("BREAKER_FAILURE_RATIO", 0.5),
        MinimumThroughput = (int)Number("BREAKER_MINIMUM_THROUGHPUT", 10),
        SamplingSeconds = (int)Number("BREAKER_SAMPLING_SECONDS", 10),
        BreakSeconds = (int)Number("BREAKER_BREAK_SECONDS", 15),
        AttemptTimeoutSeconds = (int)Number("PAYMENT_ATTEMPT_TIMEOUT_SECONDS", 2),
        MaxSettleAttempts = (int)Number("SETTLE_MAX_ATTEMPTS", 20)
    };

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"App setting {name} is not set.");

    private static double Number(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : fallback;

    // HttpClient only appends a relative path correctly when the base ends with a slash.
    private static string WithSlash(string url) =>
        url.Length == 0 || url.EndsWith('/') ? url : url + "/";
}
