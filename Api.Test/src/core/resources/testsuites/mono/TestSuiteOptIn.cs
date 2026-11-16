namespace GdUnit4.Tests.Resources;

using System;
using System.Collections.Generic;

// will be ignored because of missing `[TestSuite]` annotation
// used by the opt-in test, records which stages and test cases ran
public class TestSuiteOptIn
{
    public static List<string> Executed { get; } = [];

    [Before]
    public void Before() => Executed.Add(nameof(Before));

    [BeforeTest]
    public void BeforeTest() => Executed.Add(nameof(BeforeTest));

    [TestCase]
    public void WithoutOptIn() => Executed.Add(nameof(WithoutOptIn));

    [TestCase]
    [OptIn("GdUnitOptInTestA")]
    public void OneName() => Executed.Add(nameof(OneName));

    [TestCase]
    [OptIn("GdUnitOptInTestA")]
    [OptIn("GdUnitOptInTestB")]
    public void TwoNames() => Executed.Add(nameof(TwoNames));

    [AfterTest]
    public void AfterTest() => Executed.Add(nameof(AfterTest));

    [After]
    public void After() => Executed.Add(nameof(After));
}

// will be ignored because of missing `[TestSuite]` annotation
// used by the opt-in test, a suite that needs an opt-in for every test case
[OptIn("GdUnitOptInTestA")]
public class TestSuiteOptInSuite
{
    public static List<string> Executed { get; } = [];

    [Before]
    public void Before() => Executed.Add(nameof(Before));

    [TestCase]
    public void SuiteName() => Executed.Add(nameof(SuiteName));

    [TestCase]
    [OptIn("GdUnitOptInTestB")]
    public void SuiteAndOwnName() => Executed.Add(nameof(SuiteAndOwnName));

    [After]
    public void After() => Executed.Add(nameof(After));
}

// will be ignored because of missing `[TestSuite]` annotation
// used by the opt-in test, tests that take their cases from data points and fuzzers; a skipped test must read none
[OptIn("GdUnitOptInTestA")]
public class TestSuiteOptInData
{
    public static List<string> Executed { get; } = [];

    public static IEnumerable<object[]> ThrowingData
    {
        get
        {
            Executed.Add(nameof(ThrowingData));
            throw new InvalidOperationException("the data of a skipped test was read");
        }
    }

    public static IEnumerable<object[]> EmptyData
    {
        get
        {
            Executed.Add(nameof(EmptyData));
            return [];
        }
    }

    public static IEnumerable<object[]> Data
    {
        get
        {
            Executed.Add(nameof(Data));
            return [[1], [2]];
        }
    }

    [TestCase]
    [DataPoint(nameof(ThrowingData))]
    public void WithThrowingData(int value) => Executed.Add($"{nameof(WithThrowingData)} {value}");

    [TestCase]
    [DataPoint(nameof(EmptyData))]
    public void WithEmptyData(int value) => Executed.Add($"{nameof(WithEmptyData)} {value}");

    [TestCase]
    [DataPoint(nameof(Data))]
    public void WithData(int value) => Executed.Add($"{nameof(WithData)} {value}");

    [TestCase]
    public void WithFuzzer([Fuzzer(1)] int value) => Executed.Add($"{nameof(WithFuzzer)} {value}");
}

// will be ignored because of missing `[TestSuite]` annotation
// used by the opt-in test, a suite whose stages clear the opt-in variable while it runs
[OptIn("GdUnitOptInTestA")]
public class TestSuiteOptInChangedWhileRunning
{
    public static List<string> Executed { get; } = [];

    [Before]
    public void Before()
    {
        Executed.Add(nameof(Before));
        Environment.SetEnvironmentVariable(OptInAttribute.ENVIRONMENT_VARIABLE, null);
    }

    [BeforeTest]
    public void BeforeTest() => Executed.Add(nameof(BeforeTest));

    [TestCase]
    public void First() => Executed.Add(nameof(First));

    [TestCase]
    public void Second() => Executed.Add(nameof(Second));

    [AfterTest]
    public void AfterTest() => Executed.Add(nameof(AfterTest));

    [After]
    public void After() => Executed.Add(nameof(After));
}
