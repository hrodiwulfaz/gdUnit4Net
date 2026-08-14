// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

using Data;

using Hooks;

using Reporting;

using static Api.ReportType;

internal sealed class TestSuiteExecutionStage : IExecutionStage
{
    public TestSuiteExecutionStage(TestSuite testSuite, Func<IStdOutHook> stdOutHookFactory)
    {
        BeforeStage = new BeforeExecutionStage(testSuite);
        AfterStage = new AfterExecutionStage(testSuite);
        BeforeTestStage = new BeforeTestExecutionStage(testSuite);
        AfterTestStage = new AfterTestExecutionStage(testSuite);
        CreateStdOutHook = stdOutHookFactory;
    }

    private BeforeExecutionStage BeforeStage { get; }

    private AfterExecutionStage AfterStage { get; }

    private BeforeTestExecutionStage BeforeTestStage { get; }

    private AfterTestExecutionStage AfterTestStage { get; }

    private Func<IStdOutHook> CreateStdOutHook { get; }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "testSuiteContext ownership is transferred to ExecutionContext which handles disposal")]
    public async Task Execute(ExecutionContext testSuiteContext)
    {
        await BeforeStage
            .Execute(testSuiteContext)
            .ConfigureAwait(true);
        using (var stdoutHook = testSuiteContext.IsCaptureStdOut ? CreateStdOutHook() : null)
        {
            foreach (var testCase in testSuiteContext.TestSuite.TestCases)
            {
                using var testCaseContext = new ExecutionContext(testSuiteContext, testCase);
                if (testCase.HasDataPoint)
                {
                    await RunTestCaseWithDataPoint(stdoutHook, testCaseContext, testCase)
                        .ConfigureAwait(true);
                }
                else
                {
                    await RunTestCase(stdoutHook, testCaseContext, testCase, testCase.TestCaseAttribute, testCase.Arguments)
                        .ConfigureAwait(true);
                }
            }
        }

        await AfterStage
            .Execute(testSuiteContext)
            .ConfigureAwait(true);
    }

    /// <summary>
    ///     Starts the stdout capture for a single test case.
    /// </summary>
    /// <param name="stdoutHook">The suite owned capture hook or <see langword="null" /> if capturing is disabled.</param>
    /// <param name="executionContext">The context of the test case to report a capture failure to.</param>
    /// <returns>The started hook that must be finalized after the test case, or <see langword="null" /> if no capture is active.</returns>
    /// <remarks>
    ///     A failing capture start is an infrastructure failure of this test case only, it must not abort the suite
    ///     and it must not prevent the test case from being executed and reported.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A capture start failure is converted into an infrastructure failure report")]
    private static IStdOutHook? StartCapture(IStdOutHook? stdoutHook, ExecutionContext executionContext)
    {
        if (stdoutHook == null)
            return null;

        try
        {
            stdoutHook.StartCapture();
            return stdoutHook;
        }
        catch (Exception e)
        {
            executionContext.ReportCollector.Consume(new TestReport(Failure, executionContext.CurrentTestCase?.Line ?? 0, e.ToString()));
            return null;
        }
    }

    /// <summary>
    ///     Stops the stdout capture of a single test case and attaches the captured output to the test report.
    /// </summary>
    /// <param name="stdoutHook">The suite owned capture hook that was started for this test case.</param>
    /// <param name="executionContext">The context of the test case to attach the output and any failure to.</param>
    /// <remarks>
    ///     A failing stop, drain or output retrieval is reported as an additional infrastructure failure. Reports that
    ///     were already collected, especially assertion failures, are preserved.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A capture finalization failure is converted into an infrastructure failure report")]
    private static void FinalizeCapture(IStdOutHook stdoutHook, ExecutionContext executionContext)
    {
        Exception? captureFailure = null;
        try
        {
            stdoutHook.StopCapture();
        }
        catch (Exception e)
        {
            captureFailure = e;
        }

        string? stdoutMessage = null;
        try
        {
            stdoutMessage = stdoutHook.GetCapturedOutput();
        }
        catch (Exception e)
        {
            captureFailure ??= e;
        }

        // VSTest owns the presentation of the captured output, it is attached once and never replayed to the console
        if (!string.IsNullOrEmpty(stdoutMessage))
            executionContext.ReportCollector.PushFront(new TestReport(Stdout, executionContext.CurrentTestCase?.Line ?? 0, stdoutMessage));

        if (captureFailure != null)
            executionContext.ReportCollector.Consume(new TestReport(Failure, executionContext.CurrentTestCase?.Line ?? 0, captureFailure.ToString()));
    }

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "testSuiteContext ownership is transferred to ExecutionContext which handles disposal")]
    private async Task RunTestCaseWithDataPoint(IStdOutHook? stdoutHook, ExecutionContext executionContext, TestCase testCase)
    {
        executionContext.FireBeforeTestEvent();

        try
        {
            var testAttribute = testCase.TestCaseAttributes.First();
            if (DataPointValueProvider.IsAsyncDataPoint(testCase))
            {
                try
                {
                    var timeout = executionContext.GetExecutionTimeout(testAttribute);
                    await foreach (var dataPointValues in DataPointValueProvider.GetDataAsync(testCase, timeout).ConfigureAwait(false))
                    {
                        var displayName = TestCase.BuildDisplayName(testCase.Name, new TestCaseAttribute(dataPointValues));
                        using ExecutionContext testCaseContext = new(executionContext, displayName);
                        await RunTestCase(stdoutHook, testCaseContext, testCase, testAttribute, dataPointValues)
                            .ConfigureAwait(true);
                    }
                }
                catch (AsyncDataPointCanceledException e)
                {
                    if (!executionContext.IsExpectingToFailWithException(e, testCase.MethodInfo))
                    {
                        executionContext.ReportCollector.Consume(
                            new TestReport(
                                Interrupted,
                                executionContext.CurrentTestCase?.Line ?? -1,
                                e.Message,
                                e.StackTrace));
                    }
                }
            }
            else
            {
                foreach (var dataPointValues in DataPointValueProvider.GetData(testCase))
                {
                    var displayName = TestCase.BuildDisplayName(testCase.Name, new TestCaseAttribute(dataPointValues));
                    using ExecutionContext testCaseContext = new(executionContext, displayName);
                    await RunTestCase(stdoutHook, testCaseContext, testCase, testAttribute, dataPointValues)
                        .ConfigureAwait(true);
                }
            }
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            executionContext.ReportCollector.Consume(new TestReport(Failure, executionContext.CurrentTestCase?.Line ?? -1, e.Message, e.StackTrace));
        }

        executionContext.FireAfterTestEvent();
    }

    private async Task RunTestCase(
        IStdOutHook? stdoutHook,
        ExecutionContext executionContext,
        TestCase testCase,
        TestCaseAttribute stageAttribute,
        params object?[] methodArguments)
    {
        var activeCapture = StartCapture(stdoutHook, executionContext);
        try
        {
            await BeforeTestStage
                .Execute(executionContext)
                .ConfigureAwait(true);

            using ExecutionContext context = new(executionContext, methodArguments);
            await new TestCaseExecutionStage(context.TestCaseName, testCase, stageAttribute)
                .Execute(context)
                .ConfigureAwait(true);
        }
        finally
        {
            if (activeCapture != null)
                FinalizeCapture(activeCapture, executionContext);

            // the after test event must be fired exactly once, even when the capture finalization failed,
            // otherwise a started test disappears from the event stream
            await AfterTestStage
                .Execute(executionContext)
                .ConfigureAwait(true);
        }
    }
}
