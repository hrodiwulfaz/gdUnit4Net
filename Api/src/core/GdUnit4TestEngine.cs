// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

using Api;

using Discovery;

using Execution.Exceptions;

using Extensions;

using Runners;

[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1202:Elements should be ordered by access",
    Justification = "Project root resolution helpers are kept near their execution flow.")]
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1204:Static elements should appear before instance elements",
    Justification = "Project root resolution helpers are kept near their execution flow.")]
internal sealed class GdUnit4TestEngine : ITestEngine
{
    private readonly object activeTestRunnersLock = new();
    private readonly object taskLock = new();
    private CancellationTokenSource? cancellationSource;

    public GdUnit4TestEngine(TestEngineSettings settings, ITestEngineLogger logger)
    {
        Settings = settings;
        Logger = logger;
    }

    private TestEngineSettings Settings { get; }

    private ITestEngineLogger Logger { get; }

    private List<ITestRunner> ActiveTestRunners { get; } = [];

    public void Dispose() => cancellationSource?.Dispose();

    public void Cancel()
    {
        lock (taskLock)
            cancellationSource?.Cancel();
        foreach (var activeTestRunner in ActiveTestRunnerSnapshot())
            activeTestRunner.Cancel();
    }

    public IReadOnlyCollection<TestCaseDescriptor> Discover(string testAssembly)
        => TestCaseDiscoverer.Discover(Settings, Logger, testAssembly);

    public void Execute(
        IReadOnlyCollection<TestAssemblyNode> testAssemblyNodes,
        ITestEventListener eventListener,
        IDebuggerFramework debuggerFramework)
    {
        using var sessionTimeoutCancellationSource = new CancellationTokenSource(Settings.SessionTimeout);
        lock (taskLock)
            cancellationSource = CancellationTokenSource.CreateLinkedTokenSource(sessionTimeoutCancellationSource.Token);

        var tasks = new List<Task>();
        var semaphore = new SemaphoreSlim(Settings.MaxCpuCount);
        var stopwatch = new Stopwatch();
        TestBatchAbortedException? batchAbort = null;
        stopwatch.Start();

        try
        {
            foreach (var assemblyNode in testAssemblyNodes)
            {
                semaphore.Wait(cancellationSource.Token);

                var task = ExecuteTestsInAssembly(
                        assemblyNode,
                        eventListener,
                        debuggerFramework,
                        cancellationSource.Token)
                    .ContinueWith(
                        completedTask =>
                        {
                            var completedBatchAbort = completedTask.Exception?
                                .Flatten()
                                .InnerExceptions
                                .OfType<TestBatchAbortedException>()
                                .FirstOrDefault();
                            if (completedBatchAbort != null)
                            {
                                batchAbort = completedBatchAbort;
                                Cancel();
                            }

                            _ = semaphore.Release();
                        },
                        cancellationSource.Token,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
                tasks.Add(task);
            }

            Task.WaitAll([.. tasks], cancellationSource.Token);
        }
        catch (OperationCanceledException)
        {
            if (batchAbort != null)
            {
                _ = Task.WaitAll([.. tasks], TimeSpan.FromSeconds(2));
                return;
            }

            // is running into session timeout we need to manually cancel the current test run
            if (sessionTimeoutCancellationSource.IsCancellationRequested)
            {
                stopwatch.Stop();
                Logger.LogInfo($"Test execution is stopped because of running into session timeout of {TimeSpan.FromMilliseconds(Settings.SessionTimeout)}.!");
                Logger.LogInfo(
                    $"""

                     ╔═══════════════════════ TEST SESSION TIMEOUT ═══════════════════════════════════════╗

                       Test execution exceeded maximum allowed time:
                         • Timeout: {TimeSpan.FromMilliseconds(Settings.SessionTimeout).Humanize()}
                         • Total tests: {TotalTests(testAssemblyNodes)}
                         • Completed tests: {eventListener.CompletedTests}
                         • Time elapsed: {stopwatch.Elapsed.Humanize()}

                       ACTION REQUIRED: Please increase 'TestSessionTimeout' in your '.runsettings' file

                     ╚════════════════════════════════════════════════════════════════════════════════════╝
                     """);
                Cancel();
            }

            try
            {
                // Wait for tasks to complete cancellation
                _ = Task.WaitAll([.. tasks], TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                Logger.LogError($"Error during cancellation cleanup: {ex.Message}");
                throw;
            }
        }
        catch (AggregateException ae)
        {
            foreach (var ex in ae.InnerExceptions)
                Logger.LogError($"Error executing tests: {ex.Message}");
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error executing tests: {ex.Message}");
            throw;
        }
        finally
        {
            semaphore.Dispose();
            lock (taskLock)
            {
                cancellationSource.Dispose();
                cancellationSource = null;
            }
        }
    }

    private static (List<TestSuiteNode> DirectExecutorTestSuites, List<TestSuiteNode> GodotExecutorTestSuites) SplitTestSuitesByRequiredRuntime(List<TestSuiteNode> testSuiteNodes)
    {
        var directExecutorTestSuites = new List<TestSuiteNode>();
        var godotExecutorTestSuites = new List<TestSuiteNode>();

        foreach (var suite in testSuiteNodes)
        {
            var godotTests = suite.Tests.FindAll(test => test.RequireRunningGodotEngine);
            var directTests = suite.Tests.FindAll(test => !test.RequireRunningGodotEngine);

            if (godotTests.Count > 0)
                godotExecutorTestSuites.Add(suite with { Tests = godotTests });
            if (directTests.Count > 0)
                directExecutorTestSuites.Add(suite with { Tests = directTests });
        }

        return (directExecutorTestSuites, godotExecutorTestSuites);
    }

    private static int TotalTests(IReadOnlyCollection<TestAssemblyNode> testAssemblyNodes)
    {
        var totalTests = 0;
        foreach (var assemblyNode in testAssemblyNodes)
            totalTests += assemblyNode.Suites.Sum(ts => ts.Tests.Count);
        return totalTests;
    }

    private Task ExecuteTestsInAssembly(
        TestAssemblyNode testAssemblyNode,
        ITestEventListener eventListener,
        IDebuggerFramework debuggerFramework,
        CancellationToken cancellationToken)
        => Task.Run(
            () =>
            {
                Logger.LogInfo($"Starting tests for assembly: {testAssemblyNode.AssemblyPath}");

                var (directExecutorTestSuites, godotExecutorTestSuites) = SplitTestSuitesByRequiredRuntime(testAssemblyNode.Suites);
                string? godotProjectRoot = null;
                if (godotExecutorTestSuites.Count > 0)
                {
                    godotProjectRoot = LookupGodotProjectRoot(testAssemblyNode.AssemblyPath);
                    Logger.LogInfo($"Using Godot project root for runtime execution: {godotProjectRoot}");
                }

                ExecuteEngineTests(
                    testAssemblyNode.AssemblyPath,
                    directExecutorTestSuites,
                    godotExecutorTestSuites,
                    godotProjectRoot,
                    eventListener,
                    debuggerFramework,
                    cancellationToken);

                Logger.LogInfo($"Completed tests for assembly: {testAssemblyNode.AssemblyPath}");
            },
            cancellationToken);

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "Runners are disposed in the finally blocks after they are untracked from cancellation.")]
    private void ExecuteEngineTests(
        string assemblyPath,
        List<TestSuiteNode> directExecutorTestSuites,
        List<TestSuiteNode> godotExecutorTestSuites,
        string? godotProjectRoot,
        ITestEventListener eventListener,
        IDebuggerFramework debuggerFramework,
        CancellationToken cancellationToken)
    {
        // Run tests that require Godot runtime
        if (godotExecutorTestSuites.Count > 0)
        {
            var resolvedGodotProjectRoot = godotProjectRoot
                                           ?? throw new InvalidOperationException("Godot runtime tests require a resolved Godot project root.");
            var godotRunner = new GodotRuntimeTestRunner(Logger, debuggerFramework, Settings, assemblyPath, resolvedGodotProjectRoot);
            TrackActiveTestRunner(godotRunner);
            try
            {
                godotRunner.RunAndWait(godotExecutorTestSuites, eventListener, cancellationToken);
            }
            finally
            {
                UntrackActiveTestRunner(godotRunner);
                DisposeTestRunner(godotRunner);
            }
        }

        // Run tests that don't require Godot runtime
        if (directExecutorTestSuites.Count > 0)
        {
            var directRunner = new DefaultTestRunner(Logger, Settings);
            TrackActiveTestRunner(directRunner);
            try
            {
                directRunner.RunAndWait(directExecutorTestSuites, eventListener, cancellationToken);
            }
            finally
            {
                UntrackActiveTestRunner(directRunner);
                DisposeTestRunner(directRunner);
            }
        }
    }

    private void DisposeTestRunner(ITestRunner testRunner)
    {
        try
        {
            testRunner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogError($"Error disposing test runner: {ex.Message}");
        }
    }

    internal string LookupGodotProjectRoot(string assemblyPath)
    {
        if (!string.IsNullOrWhiteSpace(Settings.GodotProjectPath))
            return ResolveConfiguredGodotProjectRoot(assemblyPath, Settings.GodotProjectPath);

        Logger.LogInfo($"Search Godot project root at {assemblyPath}");
        var currentDir = ResolveAssemblyDirectory(assemblyPath);
        DirectoryInfo? fallbackProjectDirectory = null;
        while (currentDir != null)
        {
            if (File.Exists(Path.Combine(currentDir.FullName, "project.godot")))
                return currentDir.FullName;

            if (fallbackProjectDirectory == null && currentDir.EnumerateFiles("*.csproj").Any())
                fallbackProjectDirectory = currentDir;

            currentDir = currentDir.Parent;
        }

        if (fallbackProjectDirectory != null)
        {
            Logger.LogWarning($"Unable to locate project.godot for '{assemblyPath}'. Falling back to project directory: {fallbackProjectDirectory.FullName}");
            return fallbackProjectDirectory.FullName;
        }

        var message = $"Unable to locate a Godot project root for '{assemblyPath}'. Set <GodotProjectPath> in the GdUnit4 runsettings to a directory containing project.godot or to project.godot itself.";
        Logger.LogError(message);
        throw new FileNotFoundException(message);
    }

    internal void TrackActiveTestRunner(ITestRunner activeTestRunner)
    {
        lock (activeTestRunnersLock)
            ActiveTestRunners.Add(activeTestRunner);
    }

    internal void UntrackActiveTestRunner(ITestRunner activeTestRunner)
    {
        lock (activeTestRunnersLock)
            _ = ActiveTestRunners.Remove(activeTestRunner);
    }

    private ITestRunner[] ActiveTestRunnerSnapshot()
    {
        lock (activeTestRunnersLock)
            return [.. ActiveTestRunners];
    }

    private string ResolveConfiguredGodotProjectRoot(string assemblyPath, string configuredPath)
    {
        var trimmedPath = configuredPath.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(trimmedPath))
        {
            var message = "GdUnit4 setting <GodotProjectPath> is empty. Set it to a directory containing project.godot or to project.godot itself.";
            Logger.LogError(message);
            throw new FileNotFoundException(message);
        }

        var candidatePaths = BuildGodotProjectPathCandidates(assemblyPath, trimmedPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var candidatePath in candidatePaths)
        {
            var godotProjectRoot = TryResolveGodotProjectRoot(candidatePath);
            if (!string.IsNullOrWhiteSpace(godotProjectRoot))
            {
                Logger.LogInfo($"Resolved Godot project root from <GodotProjectPath>: {godotProjectRoot}");
                return godotProjectRoot;
            }
        }

        var failureMessage = $"Configured <GodotProjectPath> '{configuredPath}' does not resolve to a Godot project. Expected a directory containing project.godot or a direct path to project.godot. Attempted: {string.Join(", ", candidatePaths)}";
        Logger.LogError(failureMessage);
        throw new FileNotFoundException(failureMessage);
    }

    private IEnumerable<string> BuildGodotProjectPathCandidates(string assemblyPath, string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            yield return Path.GetFullPath(configuredPath);
            yield break;
        }

        var assemblyDirectory = ResolveAssemblyDirectory(assemblyPath);
        if (assemblyDirectory == null)
        {
            var message = $"Relative <GodotProjectPath> '{configuredPath}' cannot be resolved because test assembly path '{assemblyPath}' is not absolute. Use an absolute <GodotProjectPath> or ensure the test platform provides an absolute assembly path.";
            Logger.LogError(message);
            throw new FileNotFoundException(message);
        }

        Logger.LogWarning($"Relative <GodotProjectPath> '{configuredPath}' is resolved from test assembly directory '{assemblyDirectory.FullName}'. VSTest exposes runsettings XML to adapters but not the runsettings file path, so gdUnit cannot use the runsettings directory as a base here.");
        yield return Path.GetFullPath(Path.Combine(assemblyDirectory.FullName, configuredPath));
    }

    private static DirectoryInfo? ResolveAssemblyDirectory(string assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(assemblyPath) || !Path.IsPathRooted(assemblyPath))
            return null;

        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        return new FileInfo(fullAssemblyPath).Directory;
    }

    private static string? TryResolveGodotProjectRoot(string candidatePath)
    {
        var fullPath = Path.GetFullPath(candidatePath);
        if (File.Exists(fullPath) && string.Equals(Path.GetFileName(fullPath), "project.godot", StringComparison.OrdinalIgnoreCase))
            return Path.GetDirectoryName(fullPath);

        if (Directory.Exists(fullPath) && File.Exists(Path.Combine(fullPath, "project.godot")))
            return fullPath;

        return null;
    }
}
