namespace GdUnit4.Tests.Resources;

// will be ignored because of missing `[TestSuite]` annotation
// used by the test suite execution stage test
public class TestSuiteStdOutCapture
{
    public static int ExecutedTestCases { get; set; }

    [TestCase]
    public void TestCaseIsExecuted() => ExecutedTestCases++;
}
