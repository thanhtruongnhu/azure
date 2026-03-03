using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.DataContracts;
using Microsoft.Extensions.Logging;
using ReactorTelemetry.Shared.Models;

namespace ReactorTelemetry.SafetyProcessor.Services;

// EDUCATIONAL: Custom Application Insights metrics
//
// TelemetryClient.TrackMetric()  → numeric measurement (gauges, readings)
// TelemetryClient.TrackEvent()   → discrete named event with properties
// TelemetryClient.TrackException() → exceptions with stack traces
//
// These appear in Application Insights under:
//   customMetrics  → TrackMetric results
//   customEvents   → TrackEvent results
// Queryable with KQL:
//   customEvents | where name == "EmergencyEvent" | project timestamp, customDimensions

public class SafetyEvaluationService
{
    private readonly ILogger<SafetyEvaluationService> _logger;
    private readonly TelemetryClient _telemetryClient;

    // Operating thresholds — in production, load from Azure App Configuration
    private const double CriticalTempThreshold  = 320.0;
    private const double EmergencyTempThreshold = 350.0;
    private const double CriticalPressureBar    = 160.0;
    private const double EmergencyPressureBar   = 175.0;

    public SafetyEvaluationService(
        ILogger<SafetyEvaluationService> logger,
        TelemetryClient telemetryClient)
    {
        _logger = logger;
        _telemetryClient = telemetryClient;
    }

    public Task EvaluateSafetyEventAsync(TelemetryEvent evt, CancellationToken ct)
    {
        // Always record the raw readings as metrics for trending/alerting in App Insights
        TrackReadingMetrics(evt);

        return evt.SafetyLevel switch
        {
            SafetyLevel.Warning   => HandleWarningAsync(evt, ct),
            SafetyLevel.Critical  => HandleCriticalAsync(evt, ct),
            SafetyLevel.Emergency => HandleEmergencyAsync(evt, ct),
            // Normal events are filtered by the Service Bus SQL filter and never arrive here.
            // If they somehow do, log and ignore.
            _ => Task.CompletedTask
        };
    }

    private Task HandleWarningAsync(TelemetryEvent evt, CancellationToken ct)
    {
        _logger.LogWarning(
            "WARNING: Reactor {ReactorId} at {Temp}°C / {Pressure} bar. Subject: {Subject}",
            evt.ReactorId, evt.Readings.CoreTemperatureCelsius,
            evt.Readings.CoolantPressureBar, evt.AuthenticatedSubject);

        _telemetryClient.TrackEvent("WarningEvent", new Dictionary<string, string>
        {
            ["ReactorId"]    = evt.ReactorId.ToString(),
            ["SafetyLevel"]  = evt.SafetyLevel.ToString(),
            ["CorrelationId"] = evt.CorrelationId.ToString()
        });

        return Task.CompletedTask;
    }

    private Task HandleCriticalAsync(TelemetryEvent evt, CancellationToken ct)
    {
        _logger.LogError(
            "CRITICAL: Reactor {ReactorId} at {Temp}°C / {Pressure} bar. Operator attention required.",
            evt.ReactorId, evt.Readings.CoreTemperatureCelsius, evt.Readings.CoolantPressureBar);

        _telemetryClient.TrackEvent("CriticalEvent", new Dictionary<string, string>
        {
            ["ReactorId"]    = evt.ReactorId.ToString(),
            ["SafetyLevel"]  = evt.SafetyLevel.ToString(),
            ["CorrelationId"] = evt.CorrelationId.ToString()
        });

        // In a real system: trigger PagerDuty alert, post to Teams channel, etc.
        return Task.CompletedTask;
    }

    private Task HandleEmergencyAsync(TelemetryEvent evt, CancellationToken ct)
    {
        _logger.LogCritical(
            "EMERGENCY: Reactor {ReactorId} at {Temp}°C / {Pressure} bar. Automated response may activate.",
            evt.ReactorId, evt.Readings.CoreTemperatureCelsius, evt.Readings.CoolantPressureBar);

        // EDUCATIONAL: TrackEvent with rich properties + a severity metric
        // Create a custom EventTelemetry for maximum control over the App Insights payload
        var telemetry = new EventTelemetry("EmergencyEvent");
        telemetry.Properties["ReactorId"]             = evt.ReactorId.ToString();
        telemetry.Properties["CorrelationId"]         = evt.CorrelationId.ToString();
        telemetry.Properties["CoreTempCelsius"]       = evt.Readings.CoreTemperatureCelsius.ToString("F1");
        telemetry.Properties["CoolantPressureBar"]    = evt.Readings.CoolantPressureBar.ToString("F1");
        telemetry.Properties["AuthenticatedSubject"]  = evt.AuthenticatedSubject ?? "unknown";
        telemetry.Metrics["CoreTempCelsius"]          = evt.Readings.CoreTemperatureCelsius;
        telemetry.Metrics["CoolantPressureBar"]       = evt.Readings.CoolantPressureBar;
        _telemetryClient.TrackEvent(telemetry);

        // Flush immediately for emergency events — don't wait for the 30s buffer
        _telemetryClient.Flush();

        return Task.CompletedTask;
    }

    private void TrackReadingMetrics(TelemetryEvent evt)
    {
        var reactorId = evt.ReactorId.ToString();

        // These metrics power App Insights dashboards and metric-based alerts
        _telemetryClient.TrackMetric(
            new MetricTelemetry("CoreTemperatureCelsius", evt.Readings.CoreTemperatureCelsius)
            {
                Properties = { ["ReactorId"] = reactorId }
            });

        _telemetryClient.TrackMetric(
            new MetricTelemetry("CoolantPressureBar", evt.Readings.CoolantPressureBar)
            {
                Properties = { ["ReactorId"] = reactorId }
            });

        if (evt.Readings.NeutronFluxPerCm2s.HasValue)
            _telemetryClient.TrackMetric(
                new MetricTelemetry("NeutronFluxPerCm2s", evt.Readings.NeutronFluxPerCm2s.Value)
                {
                    Properties = { ["ReactorId"] = reactorId }
                });
    }
}
