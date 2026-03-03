// EDUCATIONAL: Integration testing Azure Functions
//
// The isolated worker model doesn't have a built-in WebApplicationFactory like ASP.NET Core,
// but we can unit-test the function class directly by constructing it with mock dependencies.
//
// For true end-to-end integration tests (calling the actual HTTP endpoint locally),
// you'd use Azure Functions Core Tools (func start) + HttpClient. That requires a running
// local.settings.json with real Service Bus connection and is better suited for CI with
// a deployed environment.
//
// This file shows the "function unit test as integration test" pattern:
// - Real validator (no mock)
// - Real JSON deserialization logic
// - Mocked ServiceBusClient (NSubstitute)
// - Mocked ILogger
// Tests the full function flow without actually connecting to Azure.

using Azure.Messaging.ServiceBus;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using ReactorTelemetry.Ingestor.Functions;
using ReactorTelemetry.Ingestor.Validators;
using ReactorTelemetry.Shared.Models;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace ReactorTelemetry.Tests.Integration;

public class IngestorFunctionIntegrationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IngestTelemetryFunction _sut;
    private readonly ServiceBusSender _mockSender;

    public IngestorFunctionIntegrationTests()
    {
        var mockClient = Substitute.For<ServiceBusClient>();
        _mockSender = Substitute.For<ServiceBusSender>();
        mockClient.CreateSender(Arg.Any<string>()).Returns(_mockSender);

        _sut = new IngestTelemetryFunction(
            Substitute.For<ILogger<IngestTelemetryFunction>>(),
            mockClient,
            new TelemetryEventValidator());
    }

    [Fact]
    public async Task Valid_event_returns_202_Accepted()
    {
        var req = BuildRequest(BuildValidEvent());
        var result = await _sut.Run(req, CancellationToken.None);

        result.Should().BeOfType<AcceptedResult>();
        var accepted = (AcceptedResult)result;
        accepted.Value.Should().NotBeNull();
    }

    [Fact]
    public async Task Valid_event_publishes_to_service_bus()
    {
        var req = BuildRequest(BuildValidEvent());
        await _sut.Run(req, CancellationToken.None);

        await _mockSender.Received(1)
            .SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Invalid_json_returns_400()
    {
        var req = new DefaultHttpContext().Request;
        req.Body = new MemoryStream(Encoding.UTF8.GetBytes("{ not valid json }"));
        req.ContentType = "application/json";

        var result = await _sut.Run(req, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Missing_required_fields_returns_400_with_validation_errors()
    {
        // Missing required 'readings' field
        var evt = new { reactorId = Guid.NewGuid(), timestamp = DateTimeOffset.UtcNow, safetyLevel = "Normal" };
        var req = BuildRequest(evt);

        var result = await _sut.Run(req, CancellationToken.None);

        result.Should().BeOfType<BadRequestObjectResult>();
    }

    [Fact]
    public async Task Service_bus_disabled_returns_503()
    {
        var mockClient = Substitute.For<ServiceBusClient>();
        var failingSender = Substitute.For<ServiceBusSender>();
        mockClient.CreateSender(Arg.Any<string>()).Returns(failingSender);
        failingSender
            .SendMessageAsync(Arg.Any<ServiceBusMessage>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new ServiceBusException("disabled",
                ServiceBusFailureReason.MessagingEntityDisabled));

        var sut = new IngestTelemetryFunction(
            Substitute.For<ILogger<IngestTelemetryFunction>>(),
            mockClient,
            new TelemetryEventValidator());

        var req = BuildRequest(BuildValidEvent());
        var result = await sut.Run(req, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>()
            .Which.StatusCode.Should().Be(503);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static HttpRequest BuildRequest(object body)
    {
        var ctx = new DefaultHttpContext();
        var json = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);
        ctx.Request.Body = new MemoryStream(json);
        ctx.Request.ContentType = "application/json";
        ctx.Request.Headers["x-correlation-id"] = Guid.NewGuid().ToString();
        ctx.Request.Headers["x-authenticated-subject"] = "test-regulator-sub";
        return ctx.Request;
    }

    private static TelemetryEvent BuildValidEvent() => new()
    {
        ReactorId   = Guid.NewGuid(),
        Timestamp   = DateTimeOffset.UtcNow,
        SafetyLevel = SafetyLevel.Warning,
        Readings    = new ReactorReadings
        {
            CoreTemperatureCelsius = 312.0,
            CoolantPressureBar     = 158.5
        }
    };
}
