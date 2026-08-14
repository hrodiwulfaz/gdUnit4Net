namespace GdUnit4.Tests.Core.Execution;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using GdUnit4.Api;
using GdUnit4.Core.Commands;
using GdUnit4.Core.Execution;

using Newtonsoft.Json;

using static Assertions;

[TestSuite]
public class GodotRuntimeExecutorTest
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);

#pragma warning disable CA2326, CA2327
    private static readonly JsonSerializerSettings PipeJsonSettings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting = Formatting.Indented
    };
#pragma warning restore CA2326, CA2327

    [TestCase(Timeout = 30000)]
    public async Task AListenerFailureEscapesAsTheOriginalLocalException()
    {
        await using var pipe = await PipeTestHarness.Connect();
        var listener = new ThrowingTestEventListener(new InvalidOperationException("listener boom"));

        var execution = pipe.Executor.ExecuteCommand(new TestPingCommand(), listener, CancellationToken.None);
        _ = await pipe.ReadFrame();
        await pipe.WriteFrame(TestEvent.AfterTest(Guid.NewGuid(), "res://a_suite.cs", "SuiteA", "TestA"));

        // a failing listener is a local host callback failure, it must not be converted into a server response
        var exception = await AssertThrown(execution);
        AssertObject(exception).IsNotNull();
        _ = exception!
            .IsInstanceOf<InvalidOperationException>()
            .HasMessage("listener boom");
    }

    [TestCase(Timeout = 30000)]
    public async Task AMalformedPipeMessageIsReportedAsTransportFailure()
    {
        await using var pipe = await PipeTestHarness.Connect();

        var execution = pipe.Executor.ExecuteCommand(new TestPingCommand(), new RecordingTestEventListener(), CancellationToken.None);
        _ = await pipe.ReadFrame();
        await pipe.WriteRawFrame("{ this is not a protocol message ]");

        var exception = await AssertThrown(execution);
        AssertObject(exception).IsNotNull();
        _ = exception!
            .IsInstanceOf<IOException>()
            .StartsWithMessage("Failed to read the response of command 'TestPingCommand'");
    }

    [TestCase(Timeout = 30000)]
    public async Task ASerializationFailureFailsTheWriteWithoutSubstitutingAResponse()
    {
        await using var pipe = await PipeTestHarness.Connect();

        var execution = pipe.Executor.ExecuteCommand(new UnserializableCommand(), new RecordingTestEventListener(), CancellationToken.None);

        var exception = await AssertThrown(execution);
        AssertObject(exception).IsNotNull();
        _ = exception!.IsInstanceOf<JsonException>();

        // a different protocol type must never be written in place of the requested command
        var received = await pipe.ReadFrame(TimeSpan.FromMilliseconds(500));
        AssertObject(received).IsNull();
    }

    [TestCase(Timeout = 30000)]
    public async Task ASuccessResponseIsReturnedUnchanged()
    {
        await using var pipe = await PipeTestHarness.Connect();

        var execution = pipe.Executor.ExecuteCommand(new TestPingCommand(), new RecordingTestEventListener(), CancellationToken.None);
        _ = await pipe.ReadFrame();
        await pipe.WriteFrame(new Response { StatusCode = HttpStatusCode.OK, Payload = "suite executed successfully" });

        var response = await execution;
        AssertObject(response.StatusCode).IsEqual(HttpStatusCode.OK);
        AssertThat(response.Payload).IsEqual("suite executed successfully");
    }

    [TestCase(Timeout = 30000)]
    public async Task ANonSuccessResponseIsReturnedUnchanged()
    {
        await using var pipe = await PipeTestHarness.Connect();

        var execution = pipe.Executor.ExecuteCommand(new TestPingCommand(), new RecordingTestEventListener(), CancellationToken.None);
        _ = await pipe.ReadFrame();
        await pipe.WriteFrame(new Response { StatusCode = HttpStatusCode.InternalServerError, Payload = "System.InvalidOperationException: the original server failure" });

        // the server payload identifies the failing boundary and must survive the transport unchanged
        var response = await execution;
        AssertObject(response.StatusCode).IsEqual(HttpStatusCode.InternalServerError);
        AssertThat(response.Payload).Contains("the original server failure");
    }

    [TestCase(Timeout = 30000)]
    public async Task AGoneResponseReportsTheRunningTestAsInterrupted()
    {
        await using var pipe = await PipeTestHarness.Connect();
        var listener = new RecordingTestEventListener();

        var execution = pipe.Executor.ExecuteCommand(new TestPingCommand(), listener, CancellationToken.None);
        _ = await pipe.ReadFrame();
        await pipe.WriteFrame(TestEvent.AfterTest(Guid.NewGuid(), "res://a_suite.cs", "SuiteA", "TestA"));
        await pipe.WriteFrame(new Response { StatusCode = HttpStatusCode.Gone, Payload = "connection interrupted" });

        var response = await execution;
        AssertObject(response.StatusCode).IsEqual(HttpStatusCode.Gone);

        // the interrupted test must be reported before the cancellation response is returned
        AssertThat(listener.Events.Count).IsEqual(2);
        var interruptedReports = new List<ITestReport>(listener.Events[1].Reports);
        AssertThat(interruptedReports.Count).IsEqual(1);
        AssertObject(interruptedReports[0].Type).IsEqual(ReportType.Interrupted);
        AssertThat(interruptedReports[0].Message).Contains("connection interrupted");
    }

    private sealed class PipeTestHarness : IAsyncDisposable
    {
        private readonly NamedPipeServerStream server;

        private PipeTestHarness(NamedPipeServerStream server, GodotRuntimeExecutor executor)
        {
            this.server = server;
            Executor = executor;
        }

        public GodotRuntimeExecutor Executor { get; }

        public static async Task<PipeTestHarness> Connect()
        {
            var pipeName = $"gdunit4-transport-{Guid.NewGuid():N}";
            var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            var connection = server.WaitForConnectionAsync();
            var executor = new GodotRuntimeExecutor(new NoOpTestEngineLogger(), pipeName);
            await executor.StartAsync();
            await connection;
            return new PipeTestHarness(server, executor);
        }

        public async ValueTask DisposeAsync()
        {
            await Executor.DisposeAsync();
            await server.DisposeAsync();
        }

        public Task<string?> ReadFrame() => ReadFrame(FrameTimeout);

        public async Task<string?> ReadFrame(TimeSpan timeout)
        {
            using var tokenSource = new CancellationTokenSource(timeout);
            var lengthBytes = new byte[4];
            if (!await ReadExact(lengthBytes, tokenSource.Token))
                return null;

            var payload = new byte[BinaryPrimitives.ReadInt32LittleEndian(lengthBytes)];
            return await ReadExact(payload, tokenSource.Token) ? Encoding.UTF8.GetString(payload) : null;
        }

        public Task WriteFrame<TData>(TData data) => WriteRawFrame(JsonConvert.SerializeObject(data, PipeJsonSettings));

        public async Task WriteRawFrame(string json)
        {
            var payload = Encoding.UTF8.GetBytes(json);
            var lengthBytes = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, payload.Length);
            await server.WriteAsync(lengthBytes);
            await server.WriteAsync(payload);
            await server.FlushAsync();
        }

        private async Task<bool> ReadExact(byte[] buffer, CancellationToken cancellationToken)
        {
            var totalBytesRead = 0;
            while (totalBytesRead < buffer.Length)
            {
                try
                {
                    var bytesRead = await server.ReadAsync(buffer.AsMemory(totalBytesRead, buffer.Length - totalBytesRead), cancellationToken);
                    if (bytesRead == 0)
                        return false;
                    totalBytesRead += bytesRead;
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }

            return true;
        }
    }

    private sealed class NoOpTestEngineLogger : ITestEngineLogger
    {
        public void SendMessage(LogLevel logLevel, string message)
        {
        }
    }

    private sealed class RecordingTestEventListener : ITestEventListener
    {
        public List<ITestEvent> Events { get; } = new();

        public bool IsFailed { get; set; }

        public int CompletedTests { get; set; }

        public void PublishEvent(ITestEvent testEvent) => Events.Add(testEvent);
    }

    private sealed class ThrowingTestEventListener : ITestEventListener
    {
        private readonly Exception failure;

        public ThrowingTestEventListener(Exception failure) => this.failure = failure;

        public bool IsFailed { get; set; }

        public int CompletedTests { get; set; }

        public void PublishEvent(ITestEvent testEvent) => throw failure;
    }

    private sealed class TestPingCommand : BaseCommand
    {
        public override Task<Response> Execute(ITestEventListener testEventListener)
            => Task.FromResult(new Response { StatusCode = HttpStatusCode.OK, Payload = "ping" });
    }

    private sealed class UnserializableCommand : BaseCommand
    {
        [JsonProperty]
        public string Broken => throw new InvalidOperationException("this command cannot be serialized");

        public override Task<Response> Execute(ITestEventListener testEventListener)
            => Task.FromResult(new Response { StatusCode = HttpStatusCode.OK, Payload = "unreachable" });
    }
}
