namespace GdUnit4.Tests.Core.Runners;

using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;

using Api;

using GdUnit4.Core.Runners;

using Moq;

using static Assertions;

/// <summary>
///     Tests for GodotRuntimeTestRunner's InstallTestRunnerClasses method
/// </summary>
[TestSuite]
public class GodotRuntimeTestRunnerTest
{
    // TODO implement the TempDirectory annotation
    // [TempDirectory]
    private string? TestTempDirectory { get; set; }

    public required string MockGodotBinPath { get; set; }
    public required Mock<IDebuggerFramework> DebuggerFrameworkMock { get; set; }
    public required Mock<ITestEngineLogger> LoggerMock { get; set; }


    /// <summary>
    ///     Set up test environment and create mocks
    /// </summary>
    [Before]
    public void Before()
    {
        // TODO remove if [TempDirectory] implemented
        TestTempDirectory = Path.Combine(Path.GetTempPath(), "test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestTempDirectory);

        // Create mock Godot executable - we'll use a batch script for Windows or shell script for Unix
        // Windows batch script
        MockGodotBinPath = Path.Combine(TestTempDirectory, Environment.OSVersion.Platform == PlatformID.Win32NT
            ? "mock_godot.bat"
            // Unix shell script
            : "mock_godot.sh");

        // Setup mocks
        LoggerMock = new Mock<ITestEngineLogger>();
        DebuggerFrameworkMock = new Mock<IDebuggerFramework>();
    }

    private GodotRuntimeTestRunner CreateTestRunner(int timeout, TestEngineSettings? settings = null, string assemblyPath = "Outpostia.Tests.dll") => new(
        LoggerMock.Object,
        DebuggerFrameworkMock.Object,
        settings ?? new TestEngineSettings { CompileProcessTimeout = timeout },
        assemblyPath,
        TestTempDirectory!);

    /// <summary>
    ///     Clean up after tests
    /// </summary>
    [After]
    public void After()
    {
        try
        {
            // Clean up temp directory if it wasn't handled by the annotation
            // TODO remove if [TempDirectory] implemented
            if (TestTempDirectory == null || !Directory.Exists(TestTempDirectory))
                return;

            Directory.Delete(TestTempDirectory, true);
        }
        catch
        {
            // Ignore cleanup failures
        }
    }

    [AfterTest]
    public void AfterTest()
    {
        // Reset mocks to clear any recorded invocations
        LoggerMock.Reset();
        DebuggerFrameworkMock.Reset();
    }

    [TestCase]
    public void CreateRunnerIdCreatesUniqueSanitizedIds()
    {
        var firstRunnerId = GodotRuntimeTestRunner.CreateRunnerId("Outpostia.Tests.dll");
        var secondRunnerId = GodotRuntimeTestRunner.CreateRunnerId("Outpostia.Tests.dll");

        AssertThat(firstRunnerId).StartsWith($"outpostia-tests-{Environment.ProcessId}-");
        AssertThat(secondRunnerId).StartsWith($"outpostia-tests-{Environment.ProcessId}-");
        AssertThat(firstRunnerId).IsNotEqual(secondRunnerId);
        AssertThat(GodotRuntimeTestRunner.CreatePipeName(firstRunnerId)).IsEqual($"gdunit4-{firstRunnerId}");
    }

    [TestCase]
    public void BuildGodotArgumentsIncludesPipeNameAndLogFile()
    {
        var settings = new TestEngineSettings
        {
            CompileProcessTimeout = 1000,
            Parameters = "\"--minimized\""
        };
        var runner = CreateTestRunner(1000, settings);
        var runtimeLogFile = Path.Combine(TestTempDirectory!, "runtime.log");
        var userDataDir = Path.Combine(TestTempDirectory!, "user-data");

        var arguments = runner.BuildGodotArguments(runtimeLogFile, userDataDir);

        AssertThat(arguments).Contains("--path");
        AssertThat(arguments).Contains(Path.GetFullPath(TestTempDirectory!));
        AssertThat(arguments).Contains("-s");
        AssertThat(arguments).Contains("res://gdunit4_testadapter_v5/GdUnit4TestRunnerScene.cs");
        AssertThat(arguments).Contains("--pipe-name");
        AssertThat(arguments).Contains(runner.PipeName);
        AssertThat(arguments).Contains("--log-file");
        AssertThat(arguments).Contains(runtimeLogFile);
        AssertThat(arguments).Contains("--gdunit-log-file");
        AssertThat(arguments).Contains("--user-data-dir");
        AssertThat(arguments).Contains(userDataDir);
        AssertThat(arguments).Contains("\"--minimized\"");
    }

    [TestCase]
    public void BuildCompileGodotArgumentsIncludesProjectRootAndLogFile()
    {
        var compileLogFile = Path.Combine(TestTempDirectory!, "compile.log");
        var userDataDir = Path.Combine(TestTempDirectory!, "user-data");

        var arguments = GodotRuntimeTestRunner.BuildCompileGodotArguments(TestTempDirectory!, compileLogFile, userDataDir);

        AssertThat(arguments).Contains("--path");
        AssertThat(arguments).Contains(Path.GetFullPath(TestTempDirectory!));
        AssertThat(arguments).Contains("--log-file");
        AssertThat(arguments).Contains(compileLogFile);
        AssertThat(arguments).Contains("--user-data-dir");
        AssertThat(arguments).Contains(userDataDir);
    }

    [TestCase]
    public void RunnerSceneDirectoryNormalizesToProjectRelativeResourcePath()
    {
        var settings = new TestEngineSettings
        {
            CompileProcessTimeout = 1000,
            RunnerSceneDirectory = @"res://Data\Testing//Generated/./GdUnit4"
        };
        var runner = CreateTestRunner(1000, settings);

        var destinationPath = runner.ResolveRunnerSceneDirectoryPath(TestTempDirectory!);

        AssertThat(runner.RunnerSceneDirectory).IsEqual("Data/Testing/Generated/GdUnit4");
        AssertThat(runner.BuildRunnerSceneResourcePath()).IsEqual("res://Data/Testing/Generated/GdUnit4/GdUnit4TestRunnerScene.cs");
        AssertThat(destinationPath).IsEqual(Path.GetFullPath(Path.Combine(TestTempDirectory!, "Data", "Testing", "Generated", "GdUnit4")));
    }

    [TestCase]
    public void RunnerSceneDirectoryRejectsAbsoluteAndEscapingPaths()
    {
        AssertThrown(() => GodotRuntimeTestRunner.NormalizeRunnerSceneDirectory("/tmp/generated"))
            .StartsWithMessage("RunnerSceneDirectory '/tmp/generated' must be project-relative");
        AssertThrown(() => GodotRuntimeTestRunner.NormalizeRunnerSceneDirectory("Data/../Generated"))
            .StartsWithMessage("RunnerSceneDirectory 'Data/../Generated' cannot contain '..'");
    }

    [TestCase]
    public void DotnetBuildArgumentsUseProjectGodotAssemblyProject()
    {
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_project_godot");
        Directory.CreateDirectory(workingDirectory);
        File.WriteAllText(
            Path.Combine(workingDirectory, "project.godot"),
            """
            [dotnet]
            project/assembly_name="ExampleGame"
            """);
        var projectPath = Path.Combine(workingDirectory, "ExampleGame.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var arguments = CreateTestRunner(1000).BuildDotnetBuildArguments(workingDirectory);

        AssertThat(arguments).StartsWith($"build \"{Path.GetFullPath(projectPath)}\" --configuration Debug");
        AssertThat(arguments).Contains("--no-restore");
    }

    [TestCase]
    public void DotnetBuildArgumentsFallBackToSingleProjectFile()
    {
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_single_project");
        Directory.CreateDirectory(workingDirectory);
        var projectPath = Path.Combine(workingDirectory, "SingleGame.csproj");
        File.WriteAllText(projectPath, "<Project />");

        var arguments = CreateTestRunner(1000).BuildDotnetBuildArguments(workingDirectory);

        AssertThat(arguments).StartsWith($"build \"{Path.GetFullPath(projectPath)}\" --configuration Debug");
    }

    [TestCase]
    public void DotnetBuildArgumentsFallBackToWorkingDirectoryWhenProjectCannotBeResolved()
    {
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_no_project");
        Directory.CreateDirectory(workingDirectory);

        var arguments = CreateTestRunner(1000).BuildDotnetBuildArguments(workingDirectory);

        AssertThat(arguments).StartsWith("build --configuration Debug");
    }

    /// <summary>
    ///     Test successful execution of InstallTestRunnerClasses
    /// </summary>
    [TestCase]
    public void ReCompileGodotProject()
    {
        // Arrange
        CreateSuccessScript();

        // Create a separate temp working directory
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir");
        Directory.CreateDirectory(workingDirectory);

        // Act
        var result = CreateTestRunner(1000).ReCompileGodotProject(workingDirectory, MockGodotBinPath);

        // Assert
        AssertThat(result).OverrideFailureMessage("InstallTestRunnerClasses should return true for successful compilation").IsTrue();

        // Verify logger was called with success messages
        VerifyLoggerInfo("Rebuild Godot Project ...");
        VerifyLoggerInfo("Rebuild Godot Project ends with exit code: 0");
    }

    /// <summary>
    ///     Test a process that takes nearly the full timeout but still completes successfully
    /// </summary>
    [TestCase]
    public void ReCompileGodotProjectNearTimeout()
    {
        // Create a script that runs for 4 seconds (near the timeout but should complete)
        CreateNearTimeoutScript();

        // Create a separate temp working directory
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_near_timeout");
        Directory.CreateDirectory(workingDirectory);

        // Act, Set a longer timeout for this test
        var result = CreateTestRunner(5000).ReCompileGodotProject(workingDirectory, MockGodotBinPath);

        // Assert
        AssertThat(result).OverrideFailureMessage("InstallTestRunnerClasses should return true for a process that completes just before timeout").IsTrue();

        // Verify success messages were logged
        VerifyLoggerInfo("Rebuild Godot Project ...");
        VerifyLoggerInfo("Rebuild Godot Project ends with exit code: 0");

        // Verify that no timeout error was logged
        LoggerMock.Verify(l => l.LogError(It.Is<string>(s =>
            s.Contains("Godot compilation TIMEOUT"))), Times.Never());
    }

    /// <summary>
    ///     Test timeout scenario in InstallTestRunnerClasses
    /// </summary>
    [TestCase]
    public void ReCompileGodotProjectTimeout()
    {
        // Arrange
        CreateTimeoutScript();

        // Create a separate temp working directory
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_timeout");
        Directory.CreateDirectory(workingDirectory);

        // Act
        var result = CreateTestRunner(1000).ReCompileGodotProject(workingDirectory, MockGodotBinPath);

        // Assert
        AssertThat(result).OverrideFailureMessage("InstallTestRunnerClasses should return false on timeout").IsFalse();

        // Verify timeout error was logged
        VerifyLoggerError("Godot compilation TIMEOUT");
        var errorCode = Environment.OSVersion.Platform == PlatformID.Win32NT ? -1 : 137;
        VerifyLoggerError($"Rebuild Godot Project ends with exit code: {errorCode}");

        // Verify recompiling the Godot project does not create a runner file
        var runnerPath = Path.Combine(workingDirectory, GodotRuntimeTestRunner.TEMP_TEST_RUNNER_DIR, GodotRuntimeTestRunner.TEST_RUNNER_SCENE_FILE_NAME);
        AssertThat(File.Exists(runnerPath)).OverrideFailureMessage("Runner file should not be created by Godot project recompilation").IsFalse();
    }

    /// <summary>
    ///     Test compilation failure scenario
    /// </summary>
    [TestCase]
    public void TestInstallTestRunnerCompilationFailure()
    {
        // Create a separate temp working directory
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_failure");
        Directory.CreateDirectory(workingDirectory);

        // Act
        var result = CreateTestRunner(1000).InstallTestRunnerClasses(workingDirectory);

        // Assert
        AssertThat(result).OverrideFailureMessage("InstallTestRunnerClasses should return false on compilation failure").IsFalse();

        // Verify error message was logged
        VerifyLoggerError("dotnet build failed with exit code: 1");

        // Verify the deterministic runner file remains so the next run can skip reinstalling it
        var runnerPath = Path.Combine(workingDirectory, GodotRuntimeTestRunner.TEMP_TEST_RUNNER_DIR, GodotRuntimeTestRunner.TEST_RUNNER_SCENE_FILE_NAME);
        AssertThat(File.Exists(runnerPath)).OverrideFailureMessage("Runner file should remain after dotnet build failure").IsTrue();
    }

    [TestCase]
    public void TestInstallTestRunnerSuccess()
    {
        // Create a separate temp working directory
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_success");
        Directory.CreateDirectory(workingDirectory);

        // Act
        var result = CreateTestRunner(1000).InstallTestRunnerClasses(workingDirectory, false);

        // Assert
        AssertThat(result).OverrideFailureMessage("InstallTestRunnerClasses should return true").IsTrue();

        // Verify error message was logged
        VerifyLoggerInfo("======== Installing GdUnit4 Godot Runtime Test Runner ========");

        // Verify the runner file was created in the correct location
        var runnerPath = Path.Combine(workingDirectory, GodotRuntimeTestRunner.TEMP_TEST_RUNNER_DIR, GodotRuntimeTestRunner.TEST_RUNNER_SCENE_FILE_NAME);
        AssertThat(File.Exists(runnerPath)).OverrideFailureMessage($"Runner file should exist at {runnerPath}").IsTrue();
    }

    [TestCase]
    public void InstallTestRunnerUsesConfiguredSceneDirectory()
    {
        var workingDirectory = Path.Combine(TestTempDirectory!, "working_dir_configured");
        Directory.CreateDirectory(workingDirectory);
        var settings = new TestEngineSettings
        {
            CompileProcessTimeout = 1000,
            RunnerSceneDirectory = "Data/Testing/Generated/GdUnit4"
        };

        var result = CreateTestRunner(1000, settings).InstallTestRunnerClasses(workingDirectory, false);

        AssertThat(result).OverrideFailureMessage("InstallTestRunnerClasses should return true").IsTrue();
        var runnerPath = Path.Combine(workingDirectory, "Data", "Testing", "Generated", "GdUnit4", GodotRuntimeTestRunner.TEST_RUNNER_SCENE_FILE_NAME);
        AssertThat(File.Exists(runnerPath)).OverrideFailureMessage($"Runner file should exist at {runnerPath}").IsTrue();
        var defaultRunnerPath = Path.Combine(workingDirectory, GodotRuntimeTestRunner.TEMP_TEST_RUNNER_DIR, GodotRuntimeTestRunner.TEST_RUNNER_SCENE_FILE_NAME);
        AssertThat(File.Exists(defaultRunnerPath)).OverrideFailureMessage($"Runner file should not exist at {defaultRunnerPath}").IsFalse();
    }


    #region Helper Methods

    /// <summary>
    ///     Create a script that succeeds quickly
    /// </summary>
    private void CreateSuccessScript()
    {
        var content = Environment.OSVersion.Platform == PlatformID.Win32NT
            ?
            // Windows batch file that succeeds immediately
            """
            @echo off

            echo Godot Engine v4.1.stable.mono - https://godotengine.org
            echo Godot Engine v4.1.stable.mono
            echo Running...
            echo Compilation successful!
            exit 0
            """
            :
            // Unix shell script that succeeds immediately
            """
            #!/bin/bash

            echo 'Godot Engine v4.1.stable.mono - https://godotengine.org'
            echo 'Godot Engine v4.1.stable.mono'
            echo 'Running...'
            echo 'Compilation successful!'
            exit 0
            """;

        File.WriteAllText(MockGodotBinPath, content);
        SetAsExecutable(MockGodotBinPath);
    }

    /// <summary>
    ///     Create a script that takes nearly the full timeout (4 seconds) but still completes
    /// </summary>
    private void CreateNearTimeoutScript()
    {
        var content = Environment.OSVersion.Platform == PlatformID.Win32NT
            ?
            // Windows batch file that sleeps for 4 seconds
            """
            @echo off

            echo Godot Engine v4.1.stable.mono - https://godotengine.org
            echo Godot Engine v4.1.stable.mono
            echo Running...
            echo Starting compilation (will take 4 seconds)...
            ping 127.0.0.1 -n 5 > nul
            echo Compilation complete!
            exit 0
            """
            :
            // Unix shell script that sleeps for 4 seconds
            """
            #!/bin/bash

            echo 'Godot Engine v4.1.stable.mono - https://godotengine.org'
            echo 'Godot Engine v4.1.stable.mono'
            echo 'Running...'
            echo 'Starting compilation (will take 4 seconds)...'
            sleep 5
            echo 'Compilation complete!'
            exit 0
            """;

        File.WriteAllText(MockGodotBinPath, content);
        SetAsExecutable(MockGodotBinPath);
    }

    /// <summary>
    ///     Create a script that times out
    /// </summary>
    private void CreateTimeoutScript()
    {
        var content = Environment.OSVersion.Platform == PlatformID.Win32NT
            ?
            // Windows batch file that sleeps longer than our timeout
            """
            @echo off

            echo Godot Engine v4.1.stable.mono - https://godotengine.org
            echo Godot Engine v4.1.stable.mono
            echo Running...
            ping 127.0.0.1 -n 10 > nul
            exit 0
            """
            :
            // Unix shell script that sleeps longer than our timeout
            """
            #!/bin/bash

            echo 'Godot Engine v4.1.stable.mono - https://godotengine.org'
            echo 'Godot Engine v4.1.stable.mono'
            echo 'Running...'
            sleep 10
            exit 0
            """;

        File.WriteAllText(MockGodotBinPath, content);
        SetAsExecutable(MockGodotBinPath);
    }

    /// <summary>
    ///     Create a script that fails with an error code
    /// </summary>
    private void CreateFailureScript()
    {
        var content = Environment.OSVersion.Platform == PlatformID.Win32NT
            ?
            // Windows batch file that exits with error code 1
            """
            echo off
            echo Godot Engine v4 .1.stable.mono - https: //godotengine.org
            echo Godot Engine v4 .1.stable.mono
            echo Running...
            echo
            echo ERROR: Compilation failed
            exit 1
            """
            :
            // Unix shell script that exits with error code 1
            """
            #!/bin/bash
            echo 'Godot Engine v4.1.stable.mono - https://godotengine.org'
            echo 'Godot Engine v4.1.stable.mono'
            echo 'Running...'
            echo 'ERROR: Compilation failed'
            exit 1
            """;

        File.WriteAllText(MockGodotBinPath, content);
        SetAsExecutable(MockGodotBinPath);
    }

    [SuppressMessage("Interoperability", "CA1416")]
    private static void SetAsExecutable(string filePath)
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT)
            File.SetUnixFileMode(filePath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    /// <summary>
    ///     Verify that logger.LogInfo was called with a message containing the specified text
    /// </summary>
    private void VerifyLoggerInfo(string expectedText)
    {
        try
        {
            LoggerMock.Verify(l => l.LogInfo(It.Is<string>(s => s.Contains(expectedText))), Times.AtLeastOnce());
        }
        catch (MockException)
        {
            // Capture all invocations to provide context
            var invocations = LoggerMock.Invocations
                .Where(i => i.Method.Name == "LogInfo")
                .Select(i => i.Arguments[0]?.ToString() ?? "null")
                .ToList();

            var message = $"Expected log message containing '{expectedText}' was not found.\n\n" +
                          $"Actual LogInfo calls ({invocations.Count}):\n" +
                          string.Join("\n", invocations.Select((msg, i) => $"  {i + 1}. {msg}"));

            AssertBool(true).OverrideFailureMessage(message).IsFalse();
        }
    }

    /// <summary>
    ///     Verify that logger.LogError was called with a message containing the specified text
    /// </summary>
    private void VerifyLoggerError(string expectedText)
    {
        try
        {
            LoggerMock.Verify(l => l.LogError(It.Is<string>(s => s.Contains(expectedText))), Times.AtLeastOnce());
        }
        catch (MockException)
        {
            // Capture all invocations to provide context
            var invocations = LoggerMock.Invocations
                .Where(i => i.Method.Name == "LogError")
                .Select(i => i.Arguments[0]?.ToString() ?? "null")
                .ToList();

            var message = $"Expected error message containing '{expectedText}' was not found.\n\n" +
                          $"Actual LogError calls ({invocations.Count}):\n" +
                          string.Join("\n", invocations.Select((msg, i) => $"  {i + 1}. {msg}"));

            AssertBool(true).OverrideFailureMessage(message).IsFalse();
        }
    }

    #endregion
}
