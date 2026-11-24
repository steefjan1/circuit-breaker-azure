using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;

namespace BreakerDemo.Orders;

/// <summary>
/// Records what the in-process breaker does. The state lives in this process
/// only: every Function App instance has its own breaker and its own copy of
/// this class. <see cref="InstanceId"/> makes that visible in every response.
/// </summary>
public sealed class BreakerTelemetry
{
    /// <summary>A short id per worker process, so responses show which breaker answered.</summary>
    public static readonly string InstanceId = Guid.NewGuid().ToString("N")[..8];

    private readonly ILogger<BreakerTelemetry> _logger;
    private int _openedCount;
    private long _openUntilTicks;

    public BreakerTelemetry(ILogger<BreakerTelemetry> logger) => _logger = logger;

    public CircuitBreakerStateProvider StateProvider { get; } = new();

    public string State => StateProvider.CircuitState.ToString();

    public int OpenedCount => Volatile.Read(ref _openedCount);

    /// <summary>How long callers should wait before the breaker lets a probe through.</summary>
    public TimeSpan RetryAfter
    {
        get
        {
            var remaining = new DateTime(Interlocked.Read(ref _openUntilTicks), DateTimeKind.Utc) - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public void Opened(TimeSpan breakDuration)
    {
        Interlocked.Increment(ref _openedCount);
        Interlocked.Exchange(ref _openUntilTicks, DateTime.UtcNow.Add(breakDuration).Ticks);
        // One log line per transition is the signal to alert on. See docs/queries.kql.
        _logger.LogWarning("Circuit OPENED on instance {InstanceId} for {BreakSeconds}s",
            InstanceId, breakDuration.TotalSeconds);
    }

    public void HalfOpened() =>
        _logger.LogWarning("Circuit HALF-OPEN on instance {InstanceId}, next call is the probe", InstanceId);

    public void Closed() =>
        _logger.LogWarning("Circuit CLOSED on instance {InstanceId}", InstanceId);
}
