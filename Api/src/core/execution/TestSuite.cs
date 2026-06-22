// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Reflection;

using Api;

internal sealed class TestSuite : IDisposable
{
    private readonly Lazy<IEnumerable<TestCase>> testCases;

    public TestSuite(TestSuiteNode suite)
        : this(
            FindTypeOnAssembly(suite.AssemblyPath, suite.ManagedType),
            suite.Tests,
            suite.SourceFile)
    {
    }

    internal TestSuite(Type type, List<TestCaseNode> tests, string sourceFile)
    {
        Instance = Activator.CreateInstance(type)
                   ?? throw new InvalidOperationException($"Cannot create an instance of '{type.FullName}' because it does not have a public parameterless constructor.");

        Name = type.Name;
        ResourcePath = sourceFile;

        // we do lazy loading to only load test case one times
        testCases = new Lazy<IEnumerable<TestCase>>(() => LoadTestCases(type, tests));
    }

    public int TestCaseCount => TestCases.Count();

    public IEnumerable<TestCase> TestCases => testCases.Value;

    public string ResourcePath { get; set; }

    public string Name { get; set; }

    public string? FullName => GetType().FullName;

    public object Instance { get; set; }

    public bool FilterDisabled { get; set; }

    public void Dispose()
    {
        if (Instance is IDisposable disposable)
            disposable.Dispose();
    }

    private static List<TestCase> LoadTestCases(Type type, List<TestCaseNode> includedTests)
        =>
        [
            .. type.GetMethods()
                .Where(m => m.IsDefined(typeof(TestCaseAttribute)))
                .Join(
                    includedTests,
                    m => m.Name,
                    test => test.ManagedMethod,
                    (mi, test) => new TestCase(test.Id, mi, test.LineNumber, test.AttributeIndex))
        ];

    private static Type FindTypeOnAssembly(string assemblyPath, string clazz)
    {
        try
        {
            return TestAssemblyLoader.ResolveType(assemblyPath, clazz);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to resolve type '{clazz}' from assembly '{assemblyPath}': {ex.Message}");
            throw new InvalidOperationException($"Could not find type '{clazz}' on assembly '{assemblyPath}'. {ex.Message}", ex);
        }
    }
}
