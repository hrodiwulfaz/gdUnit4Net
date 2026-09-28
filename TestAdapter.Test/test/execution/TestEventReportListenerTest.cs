namespace GdUnit4.TestAdapter.Test.Execution;

using Api;

using GdUnit4.TestAdapter.Execution;
using GdUnit4.TestAdapter.Extensions;

using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using TestCase = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestCase;
using TestResult = Microsoft.VisualStudio.TestPlatform.ObjectModel.TestResult;

[TestClass]
public class TestEventReportListenerTest
{
    private const string STDOUT_PAYLOAD = "capture-heavy-stdout-payload";
    private const string ASSERTION_MESSAGE = "Expecting to be equal but is not";
    private const string SUITE_NAME = "Example.Tests.CaptureSuite";

    [TestMethod]
    public void AFailingStdoutPresentationStillRecordsOneTerminalResult()
    {
        var testCase = CreateTestCase();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);
        _ = framework
            .Setup(handle => handle.SendMessage(It.IsAny<TestMessageLevel>(), It.Is<string>(message => message.Contains(STDOUT_PAYLOAD, StringComparison.Ordinal))))
            .Throws(new InvalidOperationException("stdout report presentation failed"));

        var listener = new TestEventReportListener(framework.Object, new[] { testCase });
        listener.PublishEvent(new TestEventStub(EventType.TestBefore, testCase.Id));
        listener.PublishEvent(new TestEventStub(EventType.TestAfter, testCase.Id)
        {
            IsFailed = true,
            Reports = new List<ITestReport>
            {
                new TestReportStub(ReportType.Stdout, STDOUT_PAYLOAD),
                new TestReportStub(ReportType.Failure, ASSERTION_MESSAGE)
            }
        });

        framework.Verify(handle => handle.RecordStart(testCase), Times.Once);

        // the started test must end with exactly one terminal result, an abandoned result leaves VSTest without an outcome
        Assert.AreEqual(1, recordedResults.Count);
        Assert.AreEqual(TestOutcome.Failed, recordedResults[0].Outcome);
        var errorMessage = recordedResults[0].ErrorMessage;
        Assert.IsNotNull(errorMessage);
        StringAssert.Contains(errorMessage, "stdout report presentation failed", StringComparison.Ordinal);

        // a failing stdout presentation must not hide the real assertion of the same test
        StringAssert.Contains(errorMessage, ASSERTION_MESSAGE, StringComparison.Ordinal);

        Assert.AreEqual(1, recordedEnds.Count);
        Assert.AreEqual(TestOutcome.Failed, recordedEnds[0]);
        Assert.AreEqual(1, listener.CompletedTests);
    }

    [TestMethod]
    public void TheRuntimeOutcomeAndReportsArePreserved()
    {
        var testCase = CreateTestCase();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);

        var listener = new TestEventReportListener(framework.Object, new[] { testCase });
        listener.PublishEvent(new TestEventStub(EventType.TestBefore, testCase.Id));
        listener.PublishEvent(new TestEventStub(EventType.TestAfter, testCase.Id)
        {
            IsFailed = true,
            Reports = new List<ITestReport>
            {
                new TestReportStub(ReportType.Stdout, STDOUT_PAYLOAD),
                new TestReportStub(ReportType.Failure, ASSERTION_MESSAGE)
            }
        });

        Assert.AreEqual(1, recordedResults.Count);
        Assert.AreEqual(TestOutcome.Failed, recordedResults[0].Outcome);
        var errorMessage = recordedResults[0].ErrorMessage;
        Assert.IsNotNull(errorMessage);

        // the original assertion must reach VSTest, it must not be replaced by an infrastructure message
        StringAssert.Contains(errorMessage, ASSERTION_MESSAGE, StringComparison.Ordinal);
        Assert.AreEqual(1, recordedResults[0].Messages.Count(message => message.Text != null && message.Text.Contains(STDOUT_PAYLOAD, StringComparison.Ordinal)));
        Assert.AreEqual(1, recordedEnds.Count);
        Assert.AreEqual(TestOutcome.Failed, recordedEnds[0]);
    }

    [TestMethod]
    public void AReportWithoutAMessageDoesNotFailTheTest()
    {
        var testCase = CreateTestCase();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);

        // the real framework rejects a null, empty or whitespace only message with an ArgumentException
        _ = framework
            .Setup(handle => handle.SendMessage(It.IsAny<TestMessageLevel>(), It.Is<string>(message => string.IsNullOrWhiteSpace(message))))
            .Throws(new ArgumentException("The parameter cannot be null or empty.", "message"));

        var listener = new TestEventReportListener(framework.Object, new[] { testCase });
        listener.PublishEvent(new TestEventStub(EventType.TestBefore, testCase.Id));
        listener.PublishEvent(new TestEventStub(EventType.TestAfter, testCase.Id)
        {
            Reports = new List<ITestReport> { new TestReportStub(ReportType.Warning, string.Empty) }
        });

        // a report that carries no message must not be forwarded to the framework and must not fail its test
        Assert.AreEqual(1, recordedResults.Count);
        Assert.AreEqual(TestOutcome.Passed, recordedResults[0].Outcome);
        Assert.AreEqual(1, recordedEnds.Count);
        Assert.AreEqual(TestOutcome.Passed, recordedEnds[0]);
    }

    [TestMethod]
    public void AFrameworkCallbackFailurePropagatesUnchanged()
    {
        var testCase = CreateTestCase();
        var framework = CreateFrameworkHandle(out _, out _);
        _ = framework
            .Setup(handle => handle.RecordResult(It.IsAny<TestResult>()))
            .Throws(new InvalidOperationException("host callback failed"));

        var listener = new TestEventReportListener(framework.Object, new[] { testCase });
        var afterEvent = new TestEventStub(EventType.TestAfter, testCase.Id);

        // a failing host callback is not a server response and not a presentation failure, it must stay visible
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => listener.PublishEvent(afterEvent));
        Assert.AreEqual("host callback failed", exception.Message);
        Assert.AreEqual(0, listener.CompletedTests);
    }

    [TestMethod]
    public void ASuiteAfterWarningNextToAFailedChildReportsPassedRows()
    {
        var suiteTests = CreateSuiteTestCases();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);

        var listener = new TestEventReportListener(framework.Object, suiteTests);

        // the suite statistics are recursive and fail for one failed child, the own orphan report is only a warning
        listener.PublishEvent(new TestEventStub(EventType.SuiteAfter, Guid.NewGuid())
        {
            FullyQualifiedName = SUITE_NAME,
            IsFailed = true,
            Reports = new List<ITestReport>
            {
                new TestReportStub(ReportType.Orphan, "Found 1 possible orphan nodes"),
                new TestReportStub(ReportType.Warning, "suite warning")
            }
        });

        Assert.AreEqual(suiteTests.Length, recordedResults.Count);
        Assert.IsTrue(recordedResults.All(result => result.DisplayName!.StartsWith("[After].", StringComparison.Ordinal)));
        Assert.IsTrue(recordedResults.All(result => result.Outcome == TestOutcome.Passed));
        Assert.IsTrue(recordedEnds.All(outcome => outcome == TestOutcome.Passed));
    }

    [TestMethod]
    public void ASuiteAfterFailureReportsFailedRows()
    {
        var suiteTests = CreateSuiteTestCases();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);

        var listener = new TestEventReportListener(framework.Object, suiteTests);
        listener.PublishEvent(new TestEventStub(EventType.SuiteAfter, Guid.NewGuid())
        {
            FullyQualifiedName = SUITE_NAME,
            IsFailed = true,
            Reports = new List<ITestReport> { new TestReportStub(ReportType.Failure, ASSERTION_MESSAGE) }
        });

        Assert.AreEqual(suiteTests.Length, recordedResults.Count);
        Assert.IsTrue(recordedResults.All(result => result.Outcome == TestOutcome.Failed));
        Assert.AreEqual(suiteTests.Length, recordedEnds.Count(outcome => outcome == TestOutcome.Failed));
    }

    [TestMethod]
    public void ASuiteAfterAbortReportsFailedRows()
    {
        var suiteTests = CreateSuiteTestCases();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);

        var listener = new TestEventReportListener(framework.Object, suiteTests);

        // a stage timeout arrives as an abort report on the suite after event
        listener.PublishEvent(new TestEventStub(EventType.SuiteAfter, Guid.NewGuid())
        {
            FullyQualifiedName = SUITE_NAME,
            IsError = true,
            Reports = new List<ITestReport> { new TestReportStub(ReportType.Abort, "stage timed out") }
        });

        Assert.AreEqual(suiteTests.Length, recordedResults.Count);
        Assert.IsTrue(recordedResults.All(result => result.Outcome == TestOutcome.Failed));
        Assert.AreEqual(suiteTests.Length, recordedEnds.Count(outcome => outcome == TestOutcome.Failed));
    }

    [TestMethod]
    public void ASuiteAfterWithoutReportsRecordsNoRows()
    {
        var suiteTests = CreateSuiteTestCases();
        var framework = CreateFrameworkHandle(out var recordedResults, out var recordedEnds);

        var listener = new TestEventReportListener(framework.Object, suiteTests);
        listener.PublishEvent(new TestEventStub(EventType.SuiteAfter, Guid.NewGuid())
        {
            FullyQualifiedName = SUITE_NAME,
            IsFailed = true
        });

        Assert.AreEqual(0, recordedResults.Count);
        Assert.AreEqual(0, recordedEnds.Count);
        framework.Verify(handle => handle.RecordStart(It.IsAny<TestCase>()), Times.Never);
    }

    private static Mock<IFrameworkHandle> CreateFrameworkHandle(out List<TestResult> recordedResults, out List<TestOutcome> recordedEnds)
    {
        var results = new List<TestResult>();
        var ends = new List<TestOutcome>();
        var framework = new Mock<IFrameworkHandle>();
        _ = framework
            .Setup(handle => handle.RecordResult(It.IsAny<TestResult>()))
            .Callback<TestResult>(results.Add);
        _ = framework
            .Setup(handle => handle.RecordEnd(It.IsAny<TestCase>(), It.IsAny<TestOutcome>()))
            .Callback<TestCase, TestOutcome>((_, outcome) => ends.Add(outcome));

        recordedResults = results;
        recordedEnds = ends;
        return framework;
    }

    private static TestCase CreateTestCase()
        => CreateTestCase("TestA");

    private static TestCase[] CreateSuiteTestCases()
        => [CreateTestCase("TestA"), CreateTestCase("TestB"), CreateTestCase("TestC")];

    private static TestCase CreateTestCase(string methodName)
    {
        var testCase = new TestCase($"{SUITE_NAME}.{methodName}", new Uri(GdUnit4TestExecutor.EXECUTOR_URI), "ExampleProject.dll")
        {
            CodeFilePath = "Tests/CaptureSuite.cs",
            DisplayName = methodName,
            LineNumber = 10
        };
        testCase.SetPropertyValue(TestCaseExtensions.ManagedTypeProperty, SUITE_NAME);
        testCase.SetPropertyValue(TestCaseExtensions.ManagedMethodProperty, methodName);
        testCase.SetPropertyValue(TestCaseExtensions.ManagedMethodAttributeIndexProperty, 0);
        testCase.SetPropertyValue(TestCaseExtensions.RequireRunningGodotEngineProperty, true);
        return testCase;
    }

    private sealed class TestEventStub : ITestEvent
    {
        public TestEventStub(EventType type, Guid id)
        {
            Type = type;
            Id = id;
        }

        public EventType Type { get; }

        public Guid Id { get; }

        public string FullyQualifiedName { get; init; } = "Example.Tests.CaptureSuite.TestA";

        public string? DisplayName { get; init; }

        public bool IsFailed { get; init; }

        public bool IsError { get; init; }

        public bool IsWarning { get; init; }

        public bool IsSkipped { get; init; }

        public bool IsSuccess => !IsFailed && !IsError && !IsWarning && !IsSkipped;

        public TimeSpan ElapsedInMs { get; init; } = TimeSpan.FromMilliseconds(42);

        public ICollection<ITestReport> Reports { get; init; } = new List<ITestReport>();
    }

    private sealed class TestReportStub : ITestReport
    {
        public TestReportStub(ReportType type, string message)
        {
            Type = type;
            Message = message;
        }

        public ReportType Type { get; }

        public int LineNumber => 10;

        public string Message { get; }

        public string? StackTrace => null;

        public bool IsError => Type is ReportType.Terminated or ReportType.Interrupted or ReportType.Abort;

        public bool IsFailure => Type == ReportType.Failure;

        public bool IsWarning => Type == ReportType.Warning;

        public IDictionary<string, object> Serialize() => new Dictionary<string, object>
        {
            { "type", (int)Type },
            { "line_number", LineNumber },
            { "message", Message }
        };
    }
}
