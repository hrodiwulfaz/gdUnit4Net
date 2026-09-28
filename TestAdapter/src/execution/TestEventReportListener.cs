// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.TestAdapter.Execution;

using System.Diagnostics.CodeAnalysis;
using System.Text.Encodings.Web;

using Api;

using Core.Extensions;

using Extensions;

using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;

using Utilities;

using static Api.EventType;
using static Api.ReportType;

using TestCase = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase;

internal sealed class TestEventReportListener : ITestEventListener
{
    public TestEventReportListener(IFrameworkHandle framework, IReadOnlyList<TestCase> testCases)
    {
        Framework = framework;
        TestCases = testCases;
        DetailedOutput = new[] { Ide.VisualStudio, Ide.VisualStudioCode, Ide.JetBrainsRider }.Contains(IdeType);
        framework.SendMessage(TestMessageLevel.Informational, $"Detected IDE {IdeType}");
    }

    public IFrameworkHandle Framework { get; }

    public IReadOnlyList<TestCase> TestCases { get; }

    public bool DetailedOutput { get; }

    public Ide IdeType => IdeDetector.Detect(Framework);

    public int CompletedTests { get; set; }

    public bool IsFailed { get; set; }

    public void PublishEvent(ITestEvent testEvent)
    {
        switch (testEvent.Type)
        {
            case SuiteBefore:
                if (DetailedOutput)
                    SendMessage(TestMessageLevel.Informational, $"TestSuite: {testEvent.FullyQualifiedName} Processing...");
                ReportSuiteFailure(testEvent, "[Before]");
                break;

            case TestBefore:
            {
                var testCase = FindTestCase(testEvent);
                if (testCase == null)
                {
                    // check is the event just the parent of parameterized tests we do ignore it because all children will be executed
                    if (FindParameterizedTestCase(testEvent))
                        return;
                    SendMessage(TestMessageLevel.Error, $"TESTCASE_BEFORE: cant find test case Id: {testEvent.Id}");
                    return;
                }

                Framework.RecordStart(testCase);
                if (DetailedOutput)
                    SendMessage(TestMessageLevel.Informational, $"TestCase: {testEvent.FullyQualifiedName} Processing...");
                break;
            }

            case TestAfter:
            {
                var testCase = FindTestCase(testEvent);
                if (testCase == null)
                {
                    // check is the event just the parent of parameterized tests we do ignore it because all children will be executed
                    if (FindParameterizedTestCase(testEvent))
                        return;
                    SendMessage(TestMessageLevel.Error, $"TESTCASE_AFTER: cant find test case {testEvent.FullyQualifiedName}");
                    return;
                }

                RecordTestResult(testEvent, testCase);
                break;
            }

            case SuiteAfter:
                if (DetailedOutput)
                    SendMessage(TestMessageLevel.Informational, $"TestSuite: {testEvent.FullyQualifiedName}: {testEvent.AsTestOutcome()}\n");
                ReportSuiteFailure(testEvent, "[After]");
                break;

            case Init:
            case Stop:
            case DiscoverStart:
            case DiscoverEnd:
            default:
                break;
        }
    }

    public void Dispose()
    {
    }

    private static bool IsEventFailed(ITestEvent e)
        => e.Reports.Count > 0;

    /// <summary>
    ///     Resolves the outcome of the synthetic rows reported for a suite stage from the stage's own reports.
    /// </summary>
    /// <param name="testEvent">The suite before or after event carrying the stage reports.</param>
    /// <returns>Failed when any own report is an error or failure, otherwise Passed.</returns>
    /// <remarks>
    ///     The event statistics are recursive and already fail when any child test failed. Using them would mark every
    ///     test of the suite as failed for a harmless stage warning, such as orphan nodes, next to one real failure.
    /// </remarks>
    private static TestOutcome AsSuiteReportsOutcome(ITestEvent testEvent)
        => testEvent.Reports.Any(report => report.IsError || report.IsFailure) ? TestOutcome.Failed : TestOutcome.Passed;

    /// <summary>
    ///     Creates and finalizes the single terminal result of a completed test case.
    /// </summary>
    /// <param name="testEvent">The after test event supplying the outcome and the runtime reports.</param>
    /// <param name="testCase">The VSTest test case the event belongs to.</param>
    /// <remarks>
    ///     The outcome and the reports supplied by the runtime are preserved. If adapter owned report normalization,
    ///     IDE specific formatting or informational messaging fails, the same result is marked as failed and carries the
    ///     adapter exception. Abandoning it would leave VSTest with a started test and no terminal result.
    ///     Failures raised by <see cref="IFrameworkHandle" /> itself are host callback failures and propagate unchanged.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "A presentation failure must not remove the terminal test result")]
    private void RecordTestResult(ITestEvent testEvent, TestCase testCase)
    {
        var testResult = new TestResult(testCase)
        {
            DisplayName = IdeType is Ide.DotNet or Ide.Unknown ? testCase.FullyQualifiedName : testCase.DisplayName,
            Outcome = testEvent.AsTestOutcome(),
            EndTime = DateTimeOffset.Now,
            Duration = testEvent.ElapsedInMs
        };

        // Set dynamic driven test name (DataPointAttribute)
        if (testEvent.DisplayName != null)
            testResult.DisplayName = testEvent.DisplayName;

        // each report is presented on its own, a failing report must not hide the remaining ones
        var presentationFailures = new List<Exception>();
        foreach (var report in testEvent.Reports.ToList())
        {
            try
            {
                _ = AddTestReport(report, testResult);
            }
            catch (Exception e)
            {
                presentationFailures.Add(e);
            }
        }

        try
        {
            if (DetailedOutput)
                SendMessage(TestMessageLevel.Informational, $"TestCase: {testEvent.FullyQualifiedName} {testResult.Outcome}");
        }
        catch (Exception e)
        {
            presentationFailures.Add(e);
        }

        if (presentationFailures.Count > 0)
        {
            testResult.Outcome = TestOutcome.Failed;
            testResult.ErrorMessage = $"{testResult.ErrorMessage}\n{string.Join("\n", presentationFailures)}".TrimStart('\n');
            testResult.ErrorStackTrace ??= presentationFailures[0].StackTrace;
        }

        Framework.RecordResult(testResult);
        Framework.RecordEnd(testCase, testResult.Outcome);
        CompletedTests += 1;
    }

    private void ReportSuiteFailure(ITestEvent testEvent, string displayName)
    {
        try
        {
            if (!IsEventFailed(testEvent))
                return;
            FindChildTestCases(testEvent)
                .ForEach(testCase =>
                {
                    var testResult = new TestResult(testCase)
                    {
                        DisplayName = $"{displayName}.{testCase.DisplayName}",
                        Outcome = AsSuiteReportsOutcome(testEvent),
                        EndTime = DateTimeOffset.Now
                    };

                    testEvent.Reports
                        .ToList()
                        .ForEach(report => AddTestReport(report, testResult));

                    Framework.RecordStart(testCase);
                    Framework.RecordResult(testResult);
                    Framework.RecordEnd(testCase, testResult.Outcome);
                });
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            SendMessage(TestMessageLevel.Error, $"{e.Message}\n{e.StackTrace}");
        }
    }

#pragma warning disable IDE0072
    private TestResult AddTestReport(ITestReport report, TestResult testResult)
        => IdeDetector.Detect(Framework) switch
#pragma warning restore IDE0072
        {
            Ide.JetBrainsRider => AddRiderTestReport(report, testResult),
            Ide.VisualStudio => AddVisualStudio2022TestReport(report, testResult),
            Ide.VisualStudioCode => AddVisualStudioCodeTestReport(report, testResult),
            _ => AddDefaultTestReport(report, testResult)
        };

    /// <summary>
    ///     Forwards a message to the test framework.
    /// </summary>
    /// <param name="level">The level to report the message at.</param>
    /// <param name="message">The message to forward.</param>
    /// <remarks>
    ///     The framework rejects a null, empty or whitespace only message with an ArgumentException. A report can
    ///     carry no message, and a blank line of captured stdout normalizes to whitespace only. Neither is a reason
    ///     to fail the test the report belongs to, so such a message is dropped instead of forwarded.
    /// </remarks>
    private void SendMessage(TestMessageLevel level, string? message)
    {
        if (!string.IsNullOrWhiteSpace(message))
            Framework.SendMessage(level, message);
    }

    private TestResult AddRiderTestReport(ITestReport report, TestResult testResult)
    {
        var normalizedMessage = report.Message.RichTextNormalize().TrimEnd();

        switch (report.Type)
        {
            case Stdout:
                SendMessage(TestMessageLevel.Informational, $"Standard Output:\n{normalizedMessage.Indent()}");
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, normalizedMessage.FormatMessageColored(report.Type)));
                break;

            case Warning:
            case Orphan:
                normalizedMessage = normalizedMessage.Replace("WARNING:\n", string.Empty, StringComparison.Ordinal);
                testResult.ErrorMessage = "Warning Detected!";
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.AdditionalInfoCategory, normalizedMessage.FormatMessageColored(report.Type)));
                SendMessage(TestMessageLevel.Warning, $"Warning:\n{normalizedMessage.Indent()}");
                break;
            case Success:
            case Skipped:
                break;
            case Failure:
            case Terminated:
            case Interrupted:
            case Abort:
            default:
                testResult.ErrorMessage = normalizedMessage;
                testResult.ErrorStackTrace = report.StackTrace;
                SendMessage(TestMessageLevel.Error, $"Error:\n{normalizedMessage.Indent()}");
                break;
        }

        return testResult;
    }

    private TestResult AddVisualStudio2022TestReport(ITestReport report, TestResult testResult)
    {
        var normalizedMessage = report.Message.RichTextNormalize().TrimEnd();

        switch (report.Type)
        {
            case Stdout:
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, normalizedMessage));
                SendMessage(TestMessageLevel.Informational, $"Standard Output:\n{normalizedMessage.Indent()}");
                break;

            case Warning:
            case Orphan:
                // for now, we report in category error
                // see https://developercommunity.visualstudio.com/t/Test-Explorer-not-show-additional-report/10768871?port=1025&fsid=1427bd7b-5ee3-4b74-9bc6-3f3f4663546c
                normalizedMessage = normalizedMessage.Replace("WARNING:", "Warning:", StringComparison.Ordinal);
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, normalizedMessage));
                SendMessage(TestMessageLevel.Warning, normalizedMessage);
                break;

            case Success:
            case Skipped:
                break;
            case Failure:
            case Terminated:
            case Interrupted:
            case Abort:
            default:
                testResult.ErrorMessage = normalizedMessage;
                testResult.ErrorStackTrace = report.StackTrace;
                SendMessage(TestMessageLevel.Error, $"Error:\n{normalizedMessage.Indent()}");
                break;
        }

        return testResult;
    }

    private TestResult AddVisualStudioCodeTestReport(ITestReport report, TestResult testResult)
    {
        var normalizedMessage = report.Message.RichTextNormalize().TrimEnd();

        switch (report.Type)
        {
            case Stdout:
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, normalizedMessage));
                SendMessage(TestMessageLevel.Informational, $"Standard Output:\n{normalizedMessage.Indent()}");
                break;

            case Warning:
            case Orphan:
                // for now, we report in category error
                // see https://developercommunity.visualstudio.com/t/Test-Explorer-not-show-additional-report/10768871?port=1025&fsid=1427bd7b-5ee3-4b74-9bc6-3f3f4663546c
                testResult.ErrorMessage = normalizedMessage;
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, normalizedMessage));
                SendMessage(TestMessageLevel.Warning, $"{normalizedMessage.Replace("WARNING:", "Warning:", StringComparison.Ordinal)}");
                break;
            case Success:
            case Skipped:
                break;
            case Failure:
            case Terminated:
            case Interrupted:
            case Abort:
            default:
                testResult.ErrorMessage = normalizedMessage;
                testResult.ErrorStackTrace = report.StackTrace;
                SendMessage(TestMessageLevel.Error, $"Error:\n{normalizedMessage.Indent()}");
                break;
        }

        return testResult;
    }

    private TestResult AddDefaultTestReport(ITestReport report, TestResult testResult)
    {
        var normalizedMessage = report.Message
            .RichTextNormalize()
            .TrimEnd()
            .Replace("WARNING:", "Warning:", StringComparison.Ordinal)
            .Replace("ERROR:", "Error:", StringComparison.Ordinal);

        switch (report.Type)
        {
            case Stdout:
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardOutCategory, normalizedMessage));
                foreach (var message in normalizedMessage.Split("\n"))
                    SendMessage(TestMessageLevel.Informational, HtmlEncoder.Default.Encode($"    {message}"));
                break;

            case Warning:
            case Orphan:
                // for now, we report in category error
                // see https://developercommunity.visualstudio.com/t/Test-Explorer-not-show-additional-report/10768871?port=1025&fsid=1427bd7b-5ee3-4b74-9bc6-3f3f4663546c
                testResult.ErrorMessage = normalizedMessage;
                testResult.Messages.Add(new TestResultMessage(TestResultMessage.StandardErrorCategory, normalizedMessage));
                SendMessage(TestMessageLevel.Warning, normalizedMessage);
                break;
            case Success:
            case Skipped:
                break;
            case Failure:
            case Terminated:
            case Interrupted:
            case Abort:
            default:
                testResult.ErrorMessage = normalizedMessage;
                testResult.ErrorStackTrace = report.StackTrace;
                SendMessage(TestMessageLevel.Error, normalizedMessage);
                break;
        }

        return testResult;
    }

    private TestCase? FindTestCase(ITestEvent e)
        => TestCases.FirstOrDefault(t => e.Id.Equals(t.Id));

    private bool FindParameterizedTestCase(ITestEvent e)
        => TestCases.Any(t => t.FullyQualifiedName.StartsWith(e.FullyQualifiedName, StringComparison.Ordinal));

    private List<TestCase> FindChildTestCases(ITestEvent e)
        => [.. TestCases.Where(t => t.FullyQualifiedName.StartsWith(e.FullyQualifiedName, StringComparison.Ordinal))];
}
