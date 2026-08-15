// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Commands;

using System.Net;

using Api;

using Execution;

using Extensions;

using Hooks;

using Newtonsoft.Json;

/// <summary>
///     Command to execute a test suite with configurable execution options.
/// </summary>
internal class ExecuteTestSuiteCommand : BaseCommand
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="ExecuteTestSuiteCommand" /> class.
    ///     Initializes a new instance of the ExecuteTestSuiteCommand.
    /// </summary>
    /// <param name="testSuite">The test suite to execute.</param>
    /// <param name="isCaptureStdOut">Whether to capture standard output during test execution.</param>
    /// <param name="isReportOrphanNodesEnabled">Whether to report orphaned nodes after test execution.</param>
    /// <param name="testCaseTimeout">
    ///     The per-stage timeout in milliseconds, or -1 to let each stage run until it completes.
    /// </param>
    public ExecuteTestSuiteCommand(TestSuiteNode testSuite, bool isCaptureStdOut, bool isReportOrphanNodesEnabled, int testCaseTimeout = -1)
    {
        Suite = testSuite;
        IsCaptureStdOut = isCaptureStdOut;
        IsReportOrphanNodesEnabled = isReportOrphanNodesEnabled;
        TestCaseTimeout = testCaseTimeout;
        IsEngineMode = Suite.Tests.First().RequireRunningGodotEngine;
    }

    [JsonConstructor]
    private ExecuteTestSuiteCommand()
    {
    }

    [JsonProperty]
    private TestSuiteNode Suite { get; set; } = null!;

    [JsonProperty]
    private bool IsCaptureStdOut { get; set; }

    [JsonProperty]
    private bool IsEngineMode { get; set; }

    [JsonProperty]
    private bool IsReportOrphanNodesEnabled { get; set; }

    [JsonProperty]
    private int TestCaseTimeout { get; set; } = -1;

    public override async Task<Response> Execute(ITestEventListener testEventListener)
    {
        try
        {
            var testSuite = new TestSuite(Suite);

            try
            {
                if (!IsReportOrphanNodesEnabled)
                    Console.WriteLine("Warning!!! Reporting orphan nodes is disabled. Please check GdUnit settings.");

                using ExecutionContext context = new(
                    testSuite,
                    [testEventListener],
                    IsReportOrphanNodesEnabled,
                    IsEngineMode);
                context.IsCaptureStdOut = IsCaptureStdOut;
                context.TestCaseTimeout = TestCaseTimeout > 0
                    ? TimeSpan.FromMilliseconds(TestCaseTimeout)
                    : Timeout.InfiniteTimeSpan;
                if (context.IsEngineMode)
                    _ = await GodotObjectExtensions.SyncProcessFrame;
                await new TestSuiteExecutionStage(testSuite, StdOutHookFactory.CreateStdOutHook)
                    .Execute(context)
                    .ConfigureAwait(true);
            }
            finally
            {
                testSuite.Dispose();
            }

            return new Response
            {
                StatusCode = HttpStatusCode.OK,
                Payload = $"Test suite {Suite.ManagedType} executed successfully."
            };
        }

        // an unexpected execution failure must reach the command boundary, it must never be reported as a successful run
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new Response
            {
                StatusCode = HttpStatusCode.InternalServerError,
                Payload = ex.ToString()
            };
        }
    }
}
