namespace GdUnit4.Tests.Core;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using Api;

using GdUnit4.Core;

using static Assertions;

[TestSuite]
public sealed class GdUnit4TestEngineTest
{
    private string? TestTempDirectory { get; set; }

    private TestLogger Logger { get; } = new();

    [Before]
    public void Before()
    {
        TestTempDirectory = Path.Combine(Path.GetTempPath(), "gdunit_test_engine_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestTempDirectory);
    }

    [After]
    public void After()
    {
        try
        {
            if (TestTempDirectory != null && Directory.Exists(TestTempDirectory))
                Directory.Delete(TestTempDirectory, true);
        }
        catch
        {
            // Ignore cleanup failures from test process handles.
        }
    }

    [TestCase]
    public void LookupGodotProjectRootPrefersProjectGodotOverNearestCsproj()
    {
        var godotRoot = CreateGodotProject("Game");
        var testProjectDirectory = Path.Combine(godotRoot, "Tests");
        Directory.CreateDirectory(testProjectDirectory);
        File.WriteAllText(Path.Combine(testProjectDirectory, "Tests.csproj"), "<Project />");
        var assemblyPath = CreateAssemblyFile(testProjectDirectory, "bin", "Debug", "net9.0", "Tests.dll");

        var projectRoot = CreateEngine().LookupGodotProjectRoot(assemblyPath);

        AssertThat(projectRoot).IsEqual(Path.GetFullPath(godotRoot));
    }

    [TestCase]
    public void LookupGodotProjectRootDoesNotMutateCurrentDirectory()
    {
        var godotRoot = CreateGodotProject("NoCurrentDirectoryMutationGame");
        var assemblyPath = CreateAssemblyFile(godotRoot, "Tests", "bin", "Debug", "net9.0", "Tests.dll");
        var unrelatedCurrentDirectory = Path.Combine(TestTempDirectory!, "UnrelatedCurrentDirectory");
        Directory.CreateDirectory(unrelatedCurrentDirectory);
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.SetCurrentDirectory(unrelatedCurrentDirectory);

            var projectRoot = CreateEngine().LookupGodotProjectRoot(assemblyPath);

            AssertThat(projectRoot).IsEqual(Path.GetFullPath(godotRoot));
            AssertThat(Environment.CurrentDirectory).IsEqual(unrelatedCurrentDirectory);
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [TestCase]
    public void LookupGodotProjectRootUsesConfiguredDirectory()
    {
        var godotRoot = CreateGodotProject("ConfiguredGame");
        var assemblyPath = CreateAssemblyFile(TestTempDirectory!, "ExternalTests", "bin", "Debug", "net9.0", "ExternalTests.dll");
        var engine = CreateEngine(new TestEngineSettings { GodotProjectPath = godotRoot });

        var projectRoot = engine.LookupGodotProjectRoot(assemblyPath);

        AssertThat(projectRoot).IsEqual(Path.GetFullPath(godotRoot));
    }

    [TestCase]
    public void LookupGodotProjectRootUsesConfiguredProjectFile()
    {
        var godotRoot = CreateGodotProject("ConfiguredProjectFileGame");
        var assemblyPath = CreateAssemblyFile(TestTempDirectory!, "ExternalTests", "bin", "Debug", "net9.0", "ExternalTests.dll");
        var engine = CreateEngine(new TestEngineSettings { GodotProjectPath = Path.Combine(godotRoot, "project.godot") });

        var projectRoot = engine.LookupGodotProjectRoot(assemblyPath);

        AssertThat(projectRoot).IsEqual(Path.GetFullPath(godotRoot));
    }

    [TestCase]
    public void LookupGodotProjectRootUsesConfiguredRelativePathFromAssemblyDirectory()
    {
        var assemblyDirectory = Path.Combine(TestTempDirectory!, "ExternalTests", "bin", "Debug", "net9.0");
        var godotRoot = CreateGodotProject(assemblyDirectory, "RelativeGame");
        var assemblyPath = CreateAssemblyFile(assemblyDirectory, "ExternalTests.dll");
        var unrelatedCurrentDirectory = Path.Combine(TestTempDirectory!, "UnrelatedCurrentDirectory");
        Directory.CreateDirectory(unrelatedCurrentDirectory);
        var originalDirectory = Environment.CurrentDirectory;
        try
        {
            Directory.SetCurrentDirectory(unrelatedCurrentDirectory);
            var engine = CreateEngine(new TestEngineSettings { GodotProjectPath = Path.Combine("RelativeGame", "project.godot") });

            var projectRoot = engine.LookupGodotProjectRoot(assemblyPath);

            AssertThat(projectRoot).IsEqual(Path.GetFullPath(godotRoot));
            AssertThat(Logger.Messages.Any(message => message.LogLevel == LogLevel.Warning && message.Message.Contains("VSTest exposes runsettings XML"))).IsTrue();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [TestCase]
    public void LookupGodotProjectRootRejectsRelativeConfiguredPathWhenAssemblyPathIsRelative()
    {
        var engine = CreateEngine(new TestEngineSettings { GodotProjectPath = Path.Combine("RelativeGame", "project.godot") });

        AssertThrown(() => engine.LookupGodotProjectRoot("ExternalTests.dll"))
            .StartsWithMessage("Relative <GodotProjectPath> 'RelativeGame");
    }

    [TestCase]
    public void LookupGodotProjectRootFallsBackToCsprojWhenProjectGodotIsMissing()
    {
        var projectDirectory = Path.Combine(TestTempDirectory!, "LegacyProject");
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "LegacyProject.csproj"), "<Project />");
        var assemblyPath = CreateAssemblyFile(projectDirectory, "bin", "Debug", "net9.0", "LegacyProject.dll");

        var projectRoot = CreateEngine().LookupGodotProjectRoot(assemblyPath);

        AssertThat(projectRoot).IsEqual(Path.GetFullPath(projectDirectory));
    }

    [TestCase]
    public void LookupGodotProjectRootRejectsInvalidConfiguredPath()
    {
        var assemblyPath = CreateAssemblyFile(TestTempDirectory!, "ExternalTests", "bin", "Debug", "net9.0", "ExternalTests.dll");
        var missingProjectPath = Path.Combine(TestTempDirectory!, "MissingGame");
        var engine = CreateEngine(new TestEngineSettings { GodotProjectPath = missingProjectPath });

        AssertThrown(() => engine.LookupGodotProjectRoot(assemblyPath))
            .StartsWithMessage($"Configured <GodotProjectPath> '{missingProjectPath}' does not resolve to a Godot project.");
    }

    private GdUnit4TestEngine CreateEngine(TestEngineSettings? settings = null)
        => new(settings ?? new TestEngineSettings(), Logger);

    private string CreateGodotProject(string directoryName)
        => CreateGodotProject(TestTempDirectory!, directoryName);

    private static string CreateGodotProject(string rootDirectory, string directoryName)
    {
        var projectDirectory = Path.Combine(rootDirectory, directoryName);
        Directory.CreateDirectory(projectDirectory);
        File.WriteAllText(Path.Combine(projectDirectory, "project.godot"), "; Engine configuration file.");
        return projectDirectory;
    }

    private static string CreateAssemblyFile(string root, params string[] relativePathSegments)
    {
        var assemblyPath = Path.Combine([root, .. relativePathSegments]);
        Directory.CreateDirectory(Path.GetDirectoryName(assemblyPath)!);
        File.WriteAllText(assemblyPath, string.Empty);
        return assemblyPath;
    }

    private sealed class TestLogger : ITestEngineLogger
    {
        public List<(LogLevel LogLevel, string Message)> Messages { get; } = [];

        public void SendMessage(LogLevel logLevel, string message)
            => Messages.Add((logLevel, message));
    }
}
