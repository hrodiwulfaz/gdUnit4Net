// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Net;
using System.Security.Principal;

using Api;

using Commands;

using Exceptions;

using Reporting;

using Runners;

using static Api.ReportType;

/// <summary>
///     Implements a command executor that communicates with the Godot runtime through named pipes.
///     Handles test command execution, event processing, and interprocess communication with the Godot engine.
/// </summary>
/// <remarks>
///     This executor uses a named pipe for bidirectional communication with the Godot process.
///     It manages the connection lifecycle and handles command execution with event propagation.
/// </remarks>
internal sealed class GodotRuntimeExecutor : InOutPipeProxy<NamedPipeClientStream>, ICommandExecutor
{
    public GodotRuntimeExecutor(ITestEngineLogger logger, string pipeName, int shutdownTimeout)
        : base(new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation), logger)
    {
        ShutdownTimeout = shutdownTimeout;
        Logger.LogInfo($"Starting GodotGdUnit4RestClient on pipe '{pipeName}'.");
    }

    private int ShutdownTimeout { get; }

    public async Task StartAsync()
    {
        try
        {
            await Proxy
                .ConnectAsync(10000)
                .ConfigureAwait(false);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            throw;
        }
    }

    /// <summary>
    ///     Shuts the Godot runtime down and releases the pipe.
    /// </summary>
    /// <returns>A task representing the asynchronous shutdown.</returns>
    /// <remarks>
    ///     Delivering the terminate command to an already exiting Godot process is best effort. A broken shutdown
    ///     pipe is neither a test result nor a transport failure of a test run, and it must not prevent the pipe
    ///     from being disposed.
    ///     The handshake is bound by <see cref="TestEngineSettings.ShutdownTimeout" />. A runtime that never answers
    ///     would otherwise block here forever, which leaves the caller unable to terminate the Godot process.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failing shutdown handshake must not fail the test run")]
    public async Task StopAsync()
    {
        try
        {
            using var shutdownToken = new CancellationTokenSource(TimeSpan.FromMilliseconds(ShutdownTimeout));
            _ = await ExecuteCommand(new TerminateGodotInstanceCommand(), new NoInteractTestEventListener(), shutdownToken.Token)
                .ConfigureAwait(true);

            // Give server time to process shutdown
            await Task
                .Delay(100)
                .ConfigureAwait(true);
        }
        catch (Exception e)
        {
            Logger.LogInfo($"The Godot runtime did not acknowledge the shutdown command. {e.Message}");
        }
        finally
        {
            await DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task<Response> ExecuteCommand<T>(T command, ITestEventListener testEventListener, CancellationToken cancellationToken)
        where T : BaseCommand
    {
        if (!IsConnected)
            throw new InvalidOperationException("Client is not connected");

        // do not run the command if cancellation requested
        cancellationToken.ThrowIfCancellationRequested();

        // commit command
        await WriteCommand(command)
            .ConfigureAwait(false);

        // read incoming data until is command response or canceled
        TestEvent? lastTestEvent = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            object? data;
            try
            {
                data = await ReadInData(cancellationToken)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                // a transport or deserialization failure is a local failure, it must never look like a server response
                throw new IOException($"Failed to read the response of command '{typeof(T).Name}' from the Godot runtime pipe.", ex);
            }

            switch (data)
            {
                case TestEvent testEvent:
                    // save last event to be used for test cancellation report
                    lastTestEvent = testEvent;

                    // a failure raised by the listener is a host callback failure and must propagate unchanged
                    testEventListener.PublishEvent(testEvent);
                    if (testEvent.IsBatchAborted)
                    {
                        var message = testEvent.Reports.First(report => report.Type == Abort).Message;
                        throw new TestBatchAbortedException(message);
                    }

                    break;
                case Response response:
                    if (response.StatusCode != HttpStatusCode.Gone || lastTestEvent == null)
                        return response;

                    // if connection gone we report at interrupted to the actual test
                    var testCanceledEvent = TestEvent
                        .AfterTest(lastTestEvent.Id, lastTestEvent.ResourcePath, lastTestEvent.SuiteName, lastTestEvent.TestName)
                        .WithStatistic(TestEvent.StatisticKey.Errors, 1)
                        .WithReport(new TestReport(Interrupted, 0, response.Payload));
                    testEventListener.PublishEvent(testCanceledEvent);
                    return response;
                default:
                    // an unreadable frame on a closed pipe repeats forever, the peer is gone and no response can arrive
                    if (!IsConnected)
                        throw new IOException($"The Godot runtime pipe closed before the response of command '{typeof(T).Name}' arrived.");
                    continue;
            }
        }
    }
}

#pragma warning disable SA1402
internal class NoInteractTestEventListener : ITestEventListener
#pragma warning restore SA1402
{
    public bool IsFailed { get; set; }

    public int CompletedTests { get; set; }

    public void PublishEvent(ITestEvent testEvent)
    {
    }
}
