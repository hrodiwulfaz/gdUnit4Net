namespace GdUnit4.Tests.Core.Execution;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

using GdUnit4.Api;
using GdUnit4.Core.Execution;
using GdUnit4.Core.Hooks;

using Resources;

using static Assertions;

using ExecutionContext = GdUnit4.Core.Execution.ExecutionContext;

[RequireGodotRuntime]
[TestSuite]
public class TestSuiteExecutionStageTest
{
    [TestCase]
    public async Task CaptureStopFailureIsReportedAndTheAfterTestEventIsStillFired()
    {
        using var stdOutHook = new StubStdOutHook
        {
            CapturedOutput = "stdout of the executed test case",
            StopFailure = new InvalidOperationException("stop capture boom")
        };

        var events = await RunSuiteExecutionStage(stdOutHook);

        // a failing capture finalization must not remove the test from the event stream
        var afterTestEvents = events.Where(testEvent => testEvent.Type == EventType.TestAfter).ToList();
        AssertThat(afterTestEvents.Count).IsEqual(1);

        var reports = afterTestEvents[0].Reports.ToList();
        var failures = reports.Where(report => report.Type == ReportType.Failure).ToList();
        AssertThat(failures.Count).IsEqual(1);
        AssertThat(failures[0].Message).Contains("stop capture boom");

        // the collected output is still attached, an infrastructure failure must not drop existing reports
        var stdoutReports = reports.Where(report => report.Type == ReportType.Stdout).ToList();
        AssertThat(stdoutReports.Count).IsEqual(1);
        AssertThat(stdoutReports[0].Message).Contains("stdout of the executed test case");

        AssertThat(stdOutHook.StopCaptureCount).IsEqual(1);
    }

    [TestCase]
    public async Task CaptureStartFailureIsReportedAndTheTestCaseIsStillExecuted()
    {
        using var stdOutHook = new StubStdOutHook { StartFailure = new InvalidOperationException("start capture boom") };
        var executedBefore = TestSuiteStdOutCapture.ExecutedTestCases;

        var events = await RunSuiteExecutionStage(stdOutHook);

        // a failing capture start is an infrastructure failure of this test case, the test case itself still runs
        AssertThat(TestSuiteStdOutCapture.ExecutedTestCases).IsEqual(executedBefore + 1);

        var afterTestEvents = events.Where(testEvent => testEvent.Type == EventType.TestAfter).ToList();
        AssertThat(afterTestEvents.Count).IsEqual(1);

        var failures = afterTestEvents[0].Reports.Where(report => report.Type == ReportType.Failure).ToList();
        AssertThat(failures.Count).IsEqual(1);
        AssertThat(failures[0].Message).Contains("start capture boom");

        // a capture that never started must not be finalized
        AssertThat(stdOutHook.StopCaptureCount).IsEqual(0);
    }

    [TestCase]
    public async Task CapturedOutputIsAttachedOnceAndNotReplayedToTheConsole()
    {
        using var stdOutHook = new StubStdOutHook { CapturedOutput = $"multi line{Environment.NewLine}  captured payload{Environment.NewLine}" };
        using var consoleProbe = new StdOutConsoleHook();

        consoleProbe.StartCapture();
        List<ITestEvent> events;
        try
        {
            events = await RunSuiteExecutionStage(stdOutHook);
        }
        finally
        {
            consoleProbe.StopCapture();
        }

        var afterTestEvents = events.Where(testEvent => testEvent.Type == EventType.TestAfter).ToList();
        AssertThat(afterTestEvents.Count).IsEqual(1);

        var stdoutReports = afterTestEvents[0].Reports.Where(report => report.Type == ReportType.Stdout).ToList();
        AssertThat(stdoutReports.Count).IsEqual(1);
        AssertThat(stdoutReports[0].Message).Contains("captured payload");

        // VSTest owns the presentation, the captured payload must not be written back to the console
        AssertThat(consoleProbe.GetCapturedOutput()).NotContains("captured payload");
    }

    private static async Task<List<ITestEvent>> RunSuiteExecutionStage(IStdOutHook stdOutHook)
    {
        var suiteType = typeof(TestSuiteStdOutCapture);
        var testCaseNodes = suiteType
            .GetMethods()
            .Where(method => method.IsDefined(typeof(TestCaseAttribute)))
            .Select(method => new TestCaseNode
            {
                Id = Guid.NewGuid(),
                ParentId = Guid.NewGuid(),
                ManagedMethod = method.Name,
                LineNumber = 0,
                AttributeIndex = 0,
                RequireRunningGodotEngine = false
            })
            .ToList();

        var listener = new RecordingTestEventListener();

        // the execution context is held in a thread local slot, the context of the currently running test must be restored
        var contextSlot = Thread.GetNamedDataSlot("ExecutionContext");
        var currentContext = Thread.GetData(contextSlot);
        try
        {
            using var testSuite = new TestSuite(suiteType, testCaseNodes, "res://src/core/resources/testsuites/mono/TestSuiteStdOutCapture.cs");
            using var context = new ExecutionContext(testSuite, new List<ITestEventListener> { listener }, false, false) { IsCaptureStdOut = true };
            await new TestSuiteExecutionStage(testSuite, () => stdOutHook).Execute(context);
        }
        finally
        {
            Thread.SetData(contextSlot, currentContext);
        }

        return listener.Events;
    }

    private sealed class RecordingTestEventListener : ITestEventListener
    {
        public List<ITestEvent> Events { get; } = new();

        public bool IsFailed { get; set; }

        public int CompletedTests { get; set; }

        public void PublishEvent(ITestEvent testEvent) => Events.Add(testEvent);
    }

    private sealed class StubStdOutHook : IStdOutHook
    {
        public Exception? StartFailure { get; init; }

        public Exception? StopFailure { get; init; }

        public string CapturedOutput { get; init; } = string.Empty;

        public int StartCaptureCount { get; private set; }

        public int StopCaptureCount { get; private set; }

        public void StartCapture()
        {
            StartCaptureCount++;
            if (StartFailure != null)
                throw StartFailure;
        }

        public void StopCapture()
        {
            StopCaptureCount++;
            if (StopFailure != null)
                throw StopFailure;
        }

        public string GetCapturedOutput() => CapturedOutput;

        public void Dispose()
        {
        }
    }
}
