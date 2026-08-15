// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Runners;

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

using Api;

using Commands;

using Execution.Exceptions;

/// <summary>
///     Base implementation of a test runner that manages test execution lifecycle and command processing.
/// </summary>
internal class BaseTestRunner : ITestRunner
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="BaseTestRunner" /> class.
    ///     Initializes a new instance of the BaseTestRunner.
    /// </summary>
    /// <param name="executor">The command executor for test operations.</param>
    /// <param name="logger">The test engine logger for diagnostic output.</param>
    /// <param name="settings">Test engine configuration settings.</param>
    protected BaseTestRunner(ICommandExecutor executor, ITestEngineLogger logger, TestEngineSettings settings)
    {
        Executor = executor;
        Logger = logger;
        Settings = settings;
    }

    protected ITestEngineLogger Logger { get; }

    private object SyncLock { get; } = new();

    private ICommandExecutor Executor { get; }

    private TestEngineSettings Settings { get; }

    private CancellationTokenSource? RunnerCancellationToken { get; set; }

    public async ValueTask DisposeAsync()
    {
        await Executor
            .DisposeAsync()
            .ConfigureAwait(true);
        RunnerCancellationToken?.Dispose();
        GC.SuppressFinalize(this);
    }

    public virtual void Cancel()
    {
        Logger.LogInfo("Try cancelling the test run...");
        lock (SyncLock)
            RunnerCancellationToken?.Cancel();
    }

    public void RunAndWait(List<TestSuiteNode> testSuiteNodes, ITestEventListener eventListener, CancellationToken cancellationToken)
    {
        TestBatchAbortedException? batchAbort = null;
        lock (SyncLock)
            RunnerCancellationToken = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = RunnerCancellationToken.Token;
        Task.Run(
                async () =>
                {
                    var isExecutorStarted = false;
                    try
                    {
                        await Executor
                            .StartAsync()
                            .ConfigureAwait(true);
                        isExecutorStarted = true;
                        foreach (var testSuite in testSuiteNodes)
                        {
                            var response = await Executor
                                .ExecuteCommand(new ExecuteTestSuiteCommand(testSuite, Settings.CaptureStdOut, true, Settings.TestCaseTimeout), eventListener, token)
                                .ConfigureAwait(true);
                            ValidateResponse(response);
                        }
                    }
                    catch (TimeoutException)
                    {
                        Logger.LogError("Failed to connect: Connection timeout");
                    }
                    catch (OperationCanceledException)
                    {
                        Logger.LogInfo("Running tests are cancelled.");
                    }
                    catch (TestBatchAbortedException ex)
                    {
                        batchAbort = ex;
                        AbortCurrentRun();
                    }
#pragma warning disable CA1031
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        // log the full exception, the inner exceptions and their stacks identify the failing boundary
                        Logger.LogError(ex.ToString());
                    }
                    finally
                    {
                        if (isExecutorStarted)
                            await StopExecutor().ConfigureAwait(true);
                    }
                },
                token)
            .ContinueWith(
                _ =>
                {
                    lock (SyncLock)
                    {
                        RunnerCancellationToken?.Dispose();
                        RunnerCancellationToken = null;
                    }
                },
                TaskScheduler.Default)
            .Wait(token);

        if (batchAbort != null)
            throw batchAbort;
    }

    public virtual void AbortCurrentRun() => Cancel();

    /// <summary>
    ///     Validates the response of an executed command.
    /// </summary>
    /// <param name="response">The response returned by the command executor.</param>
    /// <remarks>
    ///     Only <see cref="HttpStatusCode.OK" /> is a success. <see cref="HttpStatusCode.Gone" /> is an interruption
    ///     of the running test run, every other status is a failure that carries the full server payload.
    /// </remarks>
    private static void ValidateResponse(Response response)
    {
        if (response.StatusCode == HttpStatusCode.OK)
            return;

        if (response.StatusCode == HttpStatusCode.Gone)
            throw new OperationCanceledException($"The test run was interrupted.\n{response.Payload}");

        throw new InvalidOperationException($"The server returned status code {(int)response.StatusCode} '{response.StatusCode}'.\n{response.Payload}");
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A failing shutdown must not replace the primary test or transport failure")]
    private async Task StopExecutor()
    {
        try
        {
            await Executor
                .StopAsync()
                .ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            Logger.LogError($"Failed to stop the test executor.\n{ex}");
        }
    }
}
