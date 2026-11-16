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
public class OptInTest
{
    private const string NEEDS_A = "needs opt-in GdUnitOptInTestA (GDUNIT4_OPT_IN)";
    private const string NEEDS_A_B = "needs opt-in GdUnitOptInTestA, GdUnitOptInTestB (GDUNIT4_OPT_IN)";

    [TestCase]
    public void ATestWithoutTheAttributeAlwaysRuns()
    {
        var method = Method<TestSuiteOptIn>(nameof(TestSuiteOptIn.WithoutOptIn));
        AssertThat(OptInFilter.RequiredNames(method)).IsEmpty();
        AssertThat(OptInFilter.SkipReason(method, null)).IsNull();
        AssertThat(OptInFilter.SkipReason(method, "GdUnitOptInTestA")).IsNull();
    }

    [TestCase]
    public void ATestWithOneNameRunsOnlyWhenTheNameIsListed()
    {
        var method = Method<TestSuiteOptIn>(nameof(TestSuiteOptIn.OneName));
        AssertThat(OptInFilter.SkipReason(method, "GdUnitOptInTestA")).IsNull();
        AssertThat(OptInFilter.SkipReason(method, " Other , gdunitoptintesta ")).IsNull();
        AssertThat(OptInFilter.SkipReason(method, null)).IsEqual(NEEDS_A);
        AssertThat(OptInFilter.SkipReason(method, string.Empty)).IsEqual(NEEDS_A);
        AssertThat(OptInFilter.SkipReason(method, "GdUnitOptInTestAB,Other,,")).IsEqual(NEEDS_A);
    }

    [TestCase]
    public void ATestWithTwoNamesRunsOnlyWhenBothNamesAreListed()
    {
        var method = Method<TestSuiteOptIn>(nameof(TestSuiteOptIn.TwoNames));
        AssertThat(OptInFilter.RequiredNames(method)).ContainsExactly("GdUnitOptInTestA", "GdUnitOptInTestB");
        AssertThat(OptInFilter.SkipReason(method, "GdUnitOptInTestB,GdUnitOptInTestA")).IsNull();
        AssertThat(OptInFilter.SkipReason(method, "GdUnitOptInTestA")).IsEqual(NEEDS_A_B);
        AssertThat(OptInFilter.SkipReason(method, "GdUnitOptInTestB")).IsEqual(NEEDS_A_B);
        AssertThat(OptInFilter.SkipReason(method, null)).IsEqual(NEEDS_A_B);
    }

    [TestCase]
    public void TheSuiteNameIsNeededByEveryTestOfTheSuite()
    {
        var suiteName = Method<TestSuiteOptInSuite>(nameof(TestSuiteOptInSuite.SuiteName));
        var both = Method<TestSuiteOptInSuite>(nameof(TestSuiteOptInSuite.SuiteAndOwnName));
        AssertThat(OptInFilter.SkipReason(suiteName, "GdUnitOptInTestA")).IsNull();
        AssertThat(OptInFilter.SkipReason(suiteName, "GdUnitOptInTestB")).IsEqual(NEEDS_A);
        AssertThat(OptInFilter.RequiredNames(both)).ContainsExactly("GdUnitOptInTestA", "GdUnitOptInTestB");
        AssertThat(OptInFilter.SkipReason(both, "GdUnitOptInTestA")).IsEqual(NEEDS_A_B);
        AssertThat(OptInFilter.SkipReason(both, "GdUnitOptInTestA,GdUnitOptInTestB")).IsNull();
    }

    [TestCase]
    public async Task ATestTheRunDidNotOptInToIsReportedSkippedWithItsReasonAndDoesNotRun()
    {
        TestSuiteOptIn.Executed.Clear();
        var events = await RunSuite<TestSuiteOptIn>("GdUnitOptInTestB");

        // the suite still has a running test, so its suite stages run; skipped tests run no test stage at all
        AssertThat(TestSuiteOptIn.Executed).ContainsExactly("Before", "BeforeTest", "WithoutOptIn", "AfterTest", "After");
        var results = AfterTestEvents(events);
        AssertThat(results.Keys).ContainsExactly("WithoutOptIn", "OneName", "TwoNames");
        AssertThat(results["WithoutOptIn"].IsSkipped).IsFalse();
        AssertThat(results["WithoutOptIn"].IsSuccess).IsTrue();
        AssertThat(results["WithoutOptIn"].Reports.Where(report => report.Type == ReportType.Skipped)).IsEmpty();
        AssertSkipped(results["OneName"], NEEDS_A);
        AssertSkipped(results["TwoNames"], NEEDS_A_B);
    }

    [TestCase]
    public async Task ATestTheRunOptedInToRuns()
    {
        TestSuiteOptIn.Executed.Clear();
        var events = await RunSuite<TestSuiteOptIn>("GdUnitOptInTestA, GdUnitOptInTestB");

        AssertThat(TestSuiteOptIn.Executed).ContainsExactly(
            "Before",
            "BeforeTest",
            "WithoutOptIn",
            "AfterTest",
            "BeforeTest",
            "OneName",
            "AfterTest",
            "BeforeTest",
            "TwoNames",
            "AfterTest",
            "After");
        AssertThat(AfterTestEvents(events).Values.All(testEvent => testEvent is { IsSkipped: false, IsSuccess: true })).IsTrue();
    }

    [TestCase]
    public async Task ASuiteWithoutARunningTestRunsNoSuiteStage()
    {
        TestSuiteOptInSuite.Executed.Clear();
        var events = await RunSuite<TestSuiteOptInSuite>(null);

        AssertThat(TestSuiteOptInSuite.Executed).IsEmpty();
        var results = AfterTestEvents(events);
        AssertSkipped(results["SuiteName"], NEEDS_A);
        AssertSkipped(results["SuiteAndOwnName"], NEEDS_A_B);
        AssertThat(events.Count(testEvent => testEvent.Type is EventType.SuiteBefore or EventType.SuiteAfter)).IsEqual(2);

        TestSuiteOptInSuite.Executed.Clear();
        _ = await RunSuite<TestSuiteOptInSuite>("GdUnitOptInTestA");
        AssertThat(TestSuiteOptInSuite.Executed).ContainsExactly("Before", "SuiteName", "After");
    }

    [TestCase]
    public async Task ASkippedTestReadsNoDataPointAndNoFuzzerAndIsReportedOnceWithItsReason()
    {
        TestSuiteOptInData.Executed.Clear();
        var events = await RunSuite<TestSuiteOptInData>(null);

        AssertThat(TestSuiteOptInData.Executed).IsEmpty();
        var afterTests = events.Where(testEvent => testEvent.Type == EventType.TestAfter).ToList();
        AssertThat(afterTests.Count).IsEqual(4);
        var results = AfterTestEvents(events);
        AssertThat(results.Keys).ContainsExactly("WithThrowingData", "WithEmptyData", "WithData", "WithFuzzer");
        results.Values.ToList().ForEach(testEvent => AssertSkipped(testEvent, NEEDS_A));
    }

    [TestCase]
    public async Task AnOptedInTestStillReadsItsDataPoint()
    {
        TestSuiteOptInData.Executed.Clear();
        var events = await RunSuite<TestSuiteOptInData>("GdUnitOptInTestA", nameof(TestSuiteOptInData.WithData));

        AssertThat(TestSuiteOptInData.Executed).ContainsExactly("Data", "WithData 1", "WithData 2");
        AssertThat(events.Where(testEvent => testEvent.Type == EventType.TestAfter).All(testEvent => testEvent is { IsSkipped: false, IsSuccess: true })).IsTrue();
    }

    [TestCase]
    public void TheSkipIsDecidedWhenTheTestCaseIsCreated()
    {
        var method = Method<TestSuiteOptInData>(nameof(TestSuiteOptInData.WithFuzzer));
        var previousOptIn = Environment.GetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE);
        try
        {
            Environment.SetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE, null);
            var testCase = new TestCase(Guid.NewGuid(), method, 0, 0);
            Environment.SetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE, "GdUnitOptInTestA");

            AssertThat(testCase.IsSkipped).IsTrue();
            AssertThat(testCase.SkipReason).IsEqual(NEEDS_A);
            AssertThat(testCase.Arguments).IsEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE, previousOptIn);
        }
    }

    [TestCase]
    public async Task ChangingTheVariableWhileTheSuiteRunsChangesNoDecision()
    {
        TestSuiteOptInChangedWhileRunning.Executed.Clear();
        var events = await RunSuite<TestSuiteOptInChangedWhileRunning>("GdUnitOptInTestA");

        AssertThat(TestSuiteOptInChangedWhileRunning.Executed).ContainsExactly(
            "Before",
            "BeforeTest",
            "First",
            "AfterTest",
            "BeforeTest",
            "Second",
            "AfterTest",
            "After");
        AssertThat(AfterTestEvents(events).Values.All(testEvent => testEvent is { IsSkipped: false, IsSuccess: true })).IsTrue();
    }

    private static void AssertSkipped(ITestEvent testEvent, string reason)
    {
        AssertThat(testEvent.IsSkipped).IsTrue();
        AssertThat(testEvent.IsFailed || testEvent.IsError).IsFalse();
        var reports = testEvent.Reports.ToList();
        AssertThat(reports.Count).IsEqual(1);
        AssertThat(reports[0].Type).IsEqual(ReportType.Skipped);
        AssertThat(reports[0].Message).IsEqual(reason);
    }

    private static MethodInfo Method<T>(string name)
        => typeof(T).GetMethod(name) ?? throw new MissingMethodException(typeof(T).Name, name);

    private static Dictionary<string, ITestEvent> AfterTestEvents(List<ITestEvent> events)
        => events
            .Where(testEvent => testEvent.Type == EventType.TestAfter)
            .ToDictionary(testEvent => testEvent.FullyQualifiedName.Split('.').Last());

    private static async Task<List<ITestEvent>> RunSuite<T>(string? optedIn, params string[] onlyTests)
    {
        var suiteType = typeof(T);
        var testCaseNodes = suiteType
            .GetMethods()
            .Where(method => method.IsDefined(typeof(TestCaseAttribute)))
            .Where(method => onlyTests.Length == 0 || onlyTests.Contains(method.Name))
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
        var previousOptIn = Environment.GetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE);

        // the execution context is held in a thread local slot, the context of the currently running test must be restored
        var contextSlot = Thread.GetNamedDataSlot("ExecutionContext");
        var currentContext = Thread.GetData(contextSlot);
        try
        {
            Environment.SetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE, optedIn);
            using var testSuite = new TestSuite(suiteType, testCaseNodes, $"res://src/core/resources/testsuites/mono/{suiteType.Name}.cs");
            using var context = new ExecutionContext(testSuite, new List<ITestEventListener> { listener }, false, false) { IsCaptureStdOut = false };
            await new TestSuiteExecutionStage(testSuite, () => new StdOutConsoleHook()).Execute(context);
        }
        finally
        {
            Environment.SetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE, previousOptIn);
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
}
