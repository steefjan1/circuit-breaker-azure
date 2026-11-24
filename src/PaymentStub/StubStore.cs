using Azure;
using Azure.Data.Tables;

namespace BreakerDemo.PaymentStub;

/// <summary>Failure mode the stub is currently in. Stored in Table Storage so every instance agrees.</summary>
public sealed class ModeEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "mode";
    public string RowKey { get; set; } = "current";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    /// <summary>healthy | down | slow | slow-success | flaky</summary>
    public string Mode { get; set; } = "healthy";
    public int DelayMs { get; set; }
    public double FailureRate { get; set; }
}

/// <summary>One row per call that reached the stub. The count is the proof of what a breaker stopped.</summary>
public sealed class CallEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public string Outcome { get; set; } = "";
}

/// <summary>One row per idempotency key. Inserting it is the charge; a 409 means "already charged".</summary>
public sealed class ChargeEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }
    public double Amount { get; set; }
}

public sealed class StubStore
{
    private readonly TableClient _mode;
    private readonly TableClient _calls;
    private readonly TableClient _charges;
    private readonly Lazy<Task> _ensureTables;

    public StubStore(TableServiceClient service)
    {
        _mode = service.GetTableClient("stubmode");
        _calls = service.GetTableClient("stubcalls");
        _charges = service.GetTableClient("stubcharges");
        _ensureTables = new Lazy<Task>(() => Task.WhenAll(
            _mode.CreateIfNotExistsAsync(),
            _calls.CreateIfNotExistsAsync(),
            _charges.CreateIfNotExistsAsync()));
    }

    public async Task<ModeEntity> GetModeAsync(CancellationToken ct)
    {
        await _ensureTables.Value;
        var found = await _mode.GetEntityIfExistsAsync<ModeEntity>("mode", "current", cancellationToken: ct);
        return found.HasValue && found.Value is not null ? found.Value : new ModeEntity();
    }

    public async Task SetModeAsync(ModeEntity mode, CancellationToken ct)
    {
        await _ensureTables.Value;
        mode.PartitionKey = "mode";
        mode.RowKey = "current";
        await _mode.UpsertEntityAsync(mode, TableUpdateMode.Replace, ct);
    }

    public async Task RecordCallAsync(string runId, string outcome, CancellationToken ct)
    {
        await _ensureTables.Value;
        var entity = new CallEntity
        {
            PartitionKey = runId,
            RowKey = $"{DateTime.UtcNow.Ticks:D19}-{Guid.NewGuid():N}",
            Outcome = outcome
        };
        await _calls.AddEntityAsync(entity, ct);
    }

    /// <summary>Returns true when this call created the charge, false when the key was charged before.</summary>
    public async Task<bool> TryChargeAsync(string runId, string idempotencyKey, double amount, CancellationToken ct)
    {
        await _ensureTables.Value;
        try
        {
            await _charges.AddEntityAsync(
                new ChargeEntity { PartitionKey = runId, RowKey = idempotencyKey, Amount = amount }, ct);
            return true;
        }
        catch (RequestFailedException ex) when (ex.Status == 409)
        {
            return false;
        }
    }

    public async Task<Dictionary<string, int>> GetStatsAsync(string runId, CancellationToken ct)
    {
        await _ensureTables.Value;
        var stats = new Dictionary<string, int>
        {
            ["received"] = 0, ["charged"] = 0, ["duplicate"] = 0, ["rejected"] = 0
        };

        var query = _calls.QueryAsync<CallEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {runId}"),
            select: new[] { "Outcome" },
            cancellationToken: ct);

        await foreach (var call in query)
        {
            stats["received"]++;
            stats[call.Outcome] = stats.GetValueOrDefault(call.Outcome) + 1;
        }

        return stats;
    }
}
