namespace GdUnit4.TestAdapter.Test.Execution;

using Extensions;

using Microsoft.VisualStudio.TestPlatform.ObjectModel;
using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

[TestClass]
public class GdUnit4TestExecutorTest
{
    private const string XML_SETTINGS =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <RunSettings>
            <RunConfiguration>
                <MaxCpuCount>1</MaxCpuCount>
                <TestAdaptersPaths>.</TestAdaptersPaths>
                <ResultsDirectory>./TestResults</ResultsDirectory>
                <TargetFrameworks>net7.0;net8.0</TargetFrameworks>
                <!-- set default session timeout to 5m -->
                <TestSessionTimeout>500000</TestSessionTimeout>
                <TreatNoTestsAsError>true</TreatNoTestsAsError>
                <EnvironmentVariables>
                    <TestEnvironmentA>23</TestEnvironmentA>
                    <TestEnvironmentB>42</TestEnvironmentB>
                </EnvironmentVariables>
            </RunConfiguration>
        </RunSettings>
        """;

    [TestCleanup]
    public void Cleanup()
    {
        Environment.SetEnvironmentVariable("TestEnvironmentA", null);
        Environment.SetEnvironmentVariable("TestEnvironmentB", null);
    }

    [TestMethod]
    public void ToGdUnitTestNodesGroupsPartialSuiteByManagedType()
    {
        const string assemblyPath = "ExampleProject.dll";
        const string managedType = "Example.Tests.PartialSuite";
        var firstTest = CreateTestCase(
            "Example.Tests.PartialSuite.TestA",
            assemblyPath,
            managedType,
            "TestA",
            "Tests/PartialSuite.A.cs",
            10);
        var secondTest = CreateTestCase(
            "Example.Tests.PartialSuite.TestB",
            assemblyPath,
            managedType,
            "TestB",
            "Tests/PartialSuite.B.cs",
            25);

        var assemblies = GdUnit4TestExecutor.ToGdUnitTestNodes([secondTest, firstTest]);

        Assert.AreEqual(1, assemblies.Count);
        Assert.AreEqual(assemblyPath, assemblies[0].AssemblyPath);
        Assert.AreEqual(1, assemblies[0].Suites.Count);
        var suite = assemblies[0].Suites[0];
        Assert.AreEqual(managedType, suite.ManagedType);
        Assert.AreEqual("Tests/PartialSuite.A.cs", suite.SourceFile);
        Assert.AreEqual(2, suite.Tests.Count);
        Assert.IsTrue(suite.Tests.Any(test => test.ManagedMethod == "TestA" && test.LineNumber == 10));
        Assert.IsTrue(suite.Tests.Any(test => test.ManagedMethod == "TestB" && test.LineNumber == 25));
        Assert.AreEqual("Tests/PartialSuite.A.cs", firstTest.CodeFilePath);
        Assert.AreEqual("Tests/PartialSuite.B.cs", secondTest.CodeFilePath);
    }

    [TestMethod]
    public void SetupRunnerEnvironment()
    {
        // pretest the environment is not set
        Assert.AreEqual(Environment.GetEnvironmentVariable("TestEnvironmentA"), null);
        Environment.SetEnvironmentVariable("TestEnvironmentB", "666");

        var frameworkHandle = new Mock<IFrameworkHandle>().Object;

        // Setup mock RunContext with RunSettings
        var mockRunContext = new Mock<IRunContext>();
        _ = mockRunContext.SetupGet(rc => rc.RunSettings)
            .Returns(Mock.Of<IRunSettings>(rs => rs.SettingsXml == XML_SETTINGS));

        // run
        GdUnit4TestExecutor.SetupRunnerEnvironment(mockRunContext.Object, frameworkHandle);

        // verify the TestEnvironmentA=23 and TestEnvironmentB is overwritten by 42
        Assert.AreEqual(Environment.GetEnvironmentVariable("TestEnvironmentA"), "23");
        Assert.AreEqual(Environment.GetEnvironmentVariable("TestEnvironmentB"), "42");
    }

    private static TestCase CreateTestCase(string fullyQualifiedName, string source, string managedType, string managedMethod, string codeFilePath, int lineNumber)
    {
        var testCase = new TestCase(fullyQualifiedName, new Uri(GdUnit4TestExecutor.EXECUTOR_URI), source)
        {
            CodeFilePath = codeFilePath,
            DisplayName = managedMethod,
            LineNumber = lineNumber
        };
        testCase.SetPropertyValue(TestCaseExtensions.ManagedTypeProperty, managedType);
        testCase.SetPropertyValue(TestCaseExtensions.ManagedMethodProperty, managedMethod);
        testCase.SetPropertyValue(TestCaseExtensions.ManagedMethodAttributeIndexProperty, 0);
        testCase.SetPropertyValue(TestCaseExtensions.RequireRunningGodotEngineProperty, true);
        return testCase;
    }
}
