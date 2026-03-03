using Azure;
using Azure.Data.Tables;
using Microsoft.Extensions.Logging;
using ReactorTelemetry.Shared.Models;

namespace ReactorTelemetry.AuditLogger.Services;

// EDUCATIONAL: Azure Table Storage data model
//
// Every entity has two required key fields:
//   PartitionKey — groups related entities on the same storage node (fast range queries)
//   RowKey       — unique within a partition (must be unique per PartitionKey)
//
// Our design:
//   PartitionKey = ReactorId  → all events for one reactor on one partition
//                               → fast query: "show all events for reactor X"
//   RowKey = CorrelationId    → globally unique per event (no duplicates)
//
// Alternative RowKey strategies:
//   - Reverse timestamp: (DateTimeOffset.MaxValue.Ticks - timestamp.Ticks).ToString("D20")
//     → entities naturally sort newest-first (useful for "latest N events" queries)
//   - Compound key: $"{timestamp:yyyyMMddHHmmss}_{correlationId}"
//     → sortable AND unique
//
// ITableEntity requirement: PartitionKey, RowKey, Timestamp, ETag properties.
// We implement it on a plain class (no Azure SDK coupling in the domain model).

public class AuditTableService
{
    private readonly TableServiceClient _tableServiceClient;
    private readonly ILogger<AuditTableService> _logger;

    public AuditTableService(TableServiceClient tableServiceClient, ILogger<AuditTableService> logger)
    {
        _tableServiceClient = tableServiceClient;
        _logger = logger;
    }

    public async Task WriteAuditRecordAsync(TelemetryEvent evt, CancellationToken ct)
    {
        var tableName = Environment.GetEnvironmentVariable("AuditTableName") ?? "reactorauditlog";
        var tableClient = _tableServiceClient.GetTableClient(tableName);

        var entity = new AuditEntity
        {
            PartitionKey = evt.ReactorId.ToString(),
            RowKey       = evt.CorrelationId.ToString(),

            ReactorId                = evt.ReactorId.ToString(),
            EventTimestamp           = evt.Timestamp,
            SafetyLevel              = evt.SafetyLevel.ToString(),
            CoreTemperatureCelsius   = evt.Readings.CoreTemperatureCelsius,
            CoolantPressureBar       = evt.Readings.CoolantPressureBar,
            NeutronFluxPerCm2s       = evt.Readings.NeutronFluxPerCm2s,
            AuthenticatedSubject     = evt.AuthenticatedSubject ?? "unknown",
            IngestedAt               = DateTimeOffset.UtcNow
        };

        try
        {
            // EDUCATIONAL: UpsertEntityAsync with Replace mode is idempotent.
            // If Service Bus redelivers a message (retry scenario), writing the same
            // RowKey twice just overwrites with identical data — no duplicates.
            await tableClient.UpsertEntityAsync(entity, TableUpdateMode.Replace, ct);

            _logger.LogInformation(
                "Audit record written for reactor {ReactorId}, event {CorrelationId}",
                evt.ReactorId, evt.CorrelationId);
        }
        catch (RequestFailedException ex)
        {
            _logger.LogError(ex,
                "Failed to write audit record for {ReactorId}/{CorrelationId}: {Status}",
                evt.ReactorId, evt.CorrelationId, ex.Status);
            throw; // Let the caller handle retry via Service Bus
        }
    }
}

// EDUCATIONAL: ITableEntity requires these four members for Azure.Data.Tables SDK.
// Using a concrete class (not TableEntity dictionary) gives you strong typing.
public class AuditEntity : ITableEntity
{
    public string PartitionKey { get; set; } = default!;
    public string RowKey { get; set; } = default!;
    public DateTimeOffset? Timestamp { get; set; }
    public ETag ETag { get; set; }

    public string ReactorId { get; set; } = default!;
    public DateTimeOffset EventTimestamp { get; set; }
    public string SafetyLevel { get; set; } = default!;
    public double CoreTemperatureCelsius { get; set; }
    public double CoolantPressureBar { get; set; }
    public double? NeutronFluxPerCm2s { get; set; }
    public string AuthenticatedSubject { get; set; } = default!;
    public DateTimeOffset IngestedAt { get; set; }
}
