using Azure;
using Azure.Data.Tables;

namespace BreakerDemo.Orders;

public static class OrderStatus
{
    public const string Created = "Created";
    public const string Paid = "Paid";
    public const string PaymentFailed = "PaymentFailed";
    public const string PaymentPending = "PaymentPending";
    public const string PaymentAbandoned = "PaymentAbandoned";
}

/// <summary>An order. PartitionKey is the run id of the test, RowKey the order id.</summary>
public sealed class OrderEntity : ITableEntity
{
    public string PartitionKey { get; set; } = "";
    public string RowKey { get; set; } = "";
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public double Amount { get; set; }
    public string Status { get; set; } = OrderStatus.Created;
    public string Path { get; set; } = "";
    public string? Reason { get; set; }
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset? PaidUtc { get; set; }
    public bool SettledLate { get; set; }
    public int SettleAttempts { get; set; }
}

public sealed class OrderStore
{
    private readonly TableClient _orders;
    private readonly Lazy<Task> _ensureTable;

    public OrderStore(TableServiceClient service)
    {
        _orders = service.GetTableClient("orders");
        _ensureTable = new Lazy<Task>(() => _orders.CreateIfNotExistsAsync());
    }

    public async Task SaveAsync(OrderEntity order, CancellationToken ct)
    {
        await _ensureTable.Value;
        await _orders.UpsertEntityAsync(order, TableUpdateMode.Replace, ct);
    }

    public async Task<OrderEntity?> GetAsync(string runId, string orderId, CancellationToken ct)
    {
        await _ensureTable.Value;
        var found = await _orders.GetEntityIfExistsAsync<OrderEntity>(runId, orderId, cancellationToken: ct);
        return found.HasValue ? found.Value : null;
    }

    /// <summary>Order counts per status for one run. The test script polls this.</summary>
    public async Task<Dictionary<string, int>> SummaryAsync(string runId, CancellationToken ct)
    {
        await _ensureTable.Value;
        var counts = new Dictionary<string, int>();

        var query = _orders.QueryAsync<OrderEntity>(
            filter: TableClient.CreateQueryFilter($"PartitionKey eq {runId}"),
            select: new[] { "Status" },
            cancellationToken: ct);

        await foreach (var order in query)
        {
            counts[order.Status] = counts.GetValueOrDefault(order.Status) + 1;
        }

        return counts;
    }
}
