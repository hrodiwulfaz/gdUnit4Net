// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Runners;

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;

using Api;

using Execution;

using Environment = Environment;

/// <summary>
///     Test runner implementation that executes tests in a separate Godot runtime process.
///     Handles process management, IPC, and test execution coordination with the Godot engine.
/// </summary>
[SuppressMessage(
    "Reliability",
    "CA2000:Dispose objects before losing scope",
    Justification = "GodotRuntimeExecutor ownership is transferred to base class which handles disposal")]
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1204:Static elements should appear before instance elements",
    Justification = "Static helpers are colocated with the runner operations they support.")]
internal sealed class GodotRuntimeTestRunner : BaseTestRunner
{
    /// <summary>
    ///     Directory name for temporary test runner files.
    /// </summary>
    internal const string TEMP_TEST_RUNNER_DIR = "gdunit4_testadapter_v5";

    /// <summary>
    ///     File name for the generated Godot runtime test runner scene.
    /// </summary>
    internal const string TEST_RUNNER_SCENE_FILE_NAME = "GdUnit4TestRunnerScene.cs";

    private const string DEFAULT_LOG_FILE_ROOT = "tmp/gdunit-runs";

    private readonly TestEngineSettings settings;
    private Process? process;

    /// <summary>
    ///     Initializes a new instance of the <see cref="GodotRuntimeTestRunner" /> class.
    /// </summary>
    /// <param name="logger">The test engine logger for diagnostic output.</param>
    /// <param name="debuggerFramework">Framework for debugging support.</param>
    /// <param name="settings">Test engine configuration settings.</param>
    /// <param name="assemblyPath">Path or identifier of the test assembly used as part of the runner identity.</param>
    /// <param name="godotProjectRoot">Absolute path to the Godot project root.</param>
    internal GodotRuntimeTestRunner(ITestEngineLogger logger, IDebuggerFramework debuggerFramework, TestEngineSettings settings, string assemblyPath, string godotProjectRoot)
        : this(logger, debuggerFramework, settings, CreateRunnerIdentity(assemblyPath), godotProjectRoot)
    {
    }

    private GodotRuntimeTestRunner(
        ITestEngineLogger logger,
        IDebuggerFramework debuggerFramework,
        TestEngineSettings settings,
        (string RunnerId, string PipeName) identity,
        string godotProjectRoot)
        : base(new GodotRuntimeExecutor(logger, identity.PipeName), logger, settings)
    {
        RunnerId = identity.RunnerId;
        PipeName = identity.PipeName;
        GodotProjectRoot = Path.GetFullPath(godotProjectRoot);
        RunnerSceneDirectory = NormalizeRunnerSceneDirectory(settings.RunnerSceneDirectory);
        this.settings = settings;
        DebuggerFramework = debuggerFramework;
    }

    internal string RunnerId { get; }

    internal string PipeName { get; }

    internal string GodotProjectRoot { get; }

    internal string RunnerSceneDirectory { get; }

    private object ProcessLock { get; } = new();

    private IDebuggerFramework DebuggerFramework { get; }

    /// <summary>
    ///     Gets the path to the Godot executable from environment variables.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when Godot executable path is not configured or found.</exception>
    private string GodotBin
    {
        get
        {
            var godotPath = Environment.GetEnvironmentVariable("GODOT_BIN");
            if (string.IsNullOrEmpty(godotPath))
            {
                var message = "Godot runtime is not configured. The environment variable 'GODOT_BIN' is not set or empty. Please set it to the Godot executable path.";
                Logger.LogError(message);
                throw new InvalidOperationException(message);
            }

            if (File.Exists(godotPath))
                return godotPath;

            var errorMessage = $"The Godot executable was not found at path: {godotPath}";
            Logger.LogError(errorMessage);
            throw new InvalidOperationException(errorMessage);
        }
    }

    private DataReceivedEventHandler StdErrorProcessor => (_, args) =>
    {
        var message = args.Data?.Trim();
        if (string.IsNullOrEmpty(message))
            return;

        // we do log errors to stdout otherwise running `dotnet test` from console will fail with exit code 1
        Logger.LogInfo($":: {message}");
    };

    public override void Cancel()
    {
        base.Cancel();
        lock (ProcessLock)
        {
            process?.Kill(true);
            _ = process?.WaitForExit(1000);
            CloseProcess(process);
        }
    }

    public new void RunAndWait(List<TestSuiteNode> testSuiteNodes, ITestEventListener eventListener, CancellationToken cancellationToken)
    {
        lock (ProcessLock)
        {
            var workingDirectory = GodotProjectRoot;
            var compileLogFilePath = settings.UseUniqueLogFiles
                ? ResolveRunnerLogFilePath(workingDirectory, "compile.log")
                : null;
            var runtimeLogFilePath = settings.UseUniqueLogFiles
                ? ResolveRunnerLogFilePath(workingDirectory, "runtime.log")
                : null;

            LogRunnerConfiguration(compileLogFilePath, runtimeLogFilePath);

            var godotBinary = GodotBin;
            if (!InstallTestRunnerClasses(workingDirectory))
                return;

            if (!ReCompileGodotProject(workingDirectory, godotBinary, compileLogFilePath))
                return;
            Logger.LogInfo("======== Running GdUnit4 Godot Runtime Test Runner ========");

            var processStartInfo =
                new ProcessStartInfo(godotBinary, BuildGodotArguments(workingDirectory, runtimeLogFilePath))
                {
                    StandardOutputEncoding = Encoding.Default,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = workingDirectory
                };

            if (DebuggerFramework.IsDebugProcess)
                process = DebuggerFramework.LaunchProcessWithDebuggerAttached(processStartInfo);
            else
            {
                process = new Process
                {
                    StartInfo = processStartInfo,
                    EnableRaisingEvents = true
                };
                process.ErrorDataReceived += StdErrorProcessor;
                process.Exited += ExitHandler("GdUnit4 Godot Runtime Test Runner");
                _ = process.Start();
                process.BeginErrorReadLine();
                process.BeginOutputReadLine();
                if (DebuggerFramework.IsDebugAttach)
                    _ = DebuggerFramework.AttachDebuggerToProcess(process);
            }

            base.RunAndWait(testSuiteNodes, eventListener, cancellationToken);

            _ = process.WaitForExit(2000);

            // wait until the process has finished
            var waitRetry = 0;
            while (!process.HasExited && waitRetry++ < 10)
                Thread.Sleep(100);

            // If the process not finished until 10 retries, we kill it manually
            if (!process.HasExited)
            {
                Logger.LogInfo("GdUnit4 Godot Runtime Test Runner is not terminated, force process kill.");
                process.Kill(true);
            }

            CloseProcess(process);
        }
    }

    internal bool InstallTestRunnerClasses(string workingDirectory, bool reCompile = true)
    {
        var destinationFolderPath = ResolveRunnerSceneDirectoryPath(workingDirectory);
        if (!Directory.Exists(destinationFolderPath))
            _ = Directory.CreateDirectory(destinationFolderPath);

        var sceneRunnerSource = Path.Combine(destinationFolderPath, TEST_RUNNER_SCENE_FILE_NAME);
        var content = BuildTestRunnerSceneContent();

        // check if the scene runner already installed and up to date
        if (File.Exists(sceneRunnerSource) && string.Equals(File.ReadAllText(sceneRunnerSource), content, StringComparison.Ordinal))
            return true;

        Logger.LogInfo("======== Installing GdUnit4 Godot Runtime Test Runner ========");
        Logger.LogInfo($"Installing GdUnit4TestRunnerScene at {destinationFolderPath}");
        File.WriteAllText(sceneRunnerSource, content, Encoding.UTF8);

        if (!reCompile)
            return true;
        var isSuccess = RunDotnetRestore(workingDirectory);
        if (!isSuccess)
            CleanupRunnerOnFailure(sceneRunnerSource);
        return isSuccess;
    }

    internal bool ReCompileGodotProject(string workingDirectory, string godotBinary, string? logFilePath = null)
    {
        using var compileProcess = new Process();
        try
        {
            // recompile the project
            var processStartInfo = new ProcessStartInfo($"{godotBinary}", BuildCompileGodotArguments(workingDirectory, logFilePath))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workingDirectory
            };

            Logger.LogInfo($"Working dir {workingDirectory}");
            Logger.LogInfo($"Rebuild Godot Project ... {godotBinary} {processStartInfo.Arguments}");
            compileProcess.StartInfo = processStartInfo;
            compileProcess.EnableRaisingEvents = true;
            compileProcess.OutputDataReceived += (_, args) =>
            {
                var message = args.Data?.Trim();
                if (string.IsNullOrEmpty(message))
                    return;

                Logger.LogInfo($".. {message}");
            };
            compileProcess.ErrorDataReceived += StdErrorProcessor;
            compileProcess.Exited += ExitHandler("Rebuild Godot Project");
            if (!compileProcess.Start())
            {
                Logger.LogError(@"Rebuild Godot Project fails on process start, exit ..");
                return false;
            }

            compileProcess.BeginErrorReadLine();
            compileProcess.BeginOutputReadLine();
            _ = compileProcess.WaitForExit(100);

            // The compile project can take a while, and we need to wait until it finishes
            // Calculate how many iterations we need based on the compile process timeout
            const int checkIntervalMs = 100; // Check every 100ms
            var maxRetries = settings.CompileProcessTimeout / checkIntervalMs;

            var waitRetry = 0;
            while (!compileProcess.HasExited && waitRetry++ < maxRetries)
                Thread.Sleep(checkIntervalMs);

            // If the process has not finished within the timeout period, we kill it manually
            if (!compileProcess.HasExited)
            {
                Logger.LogError(
                    $"""
                     ╔═══════════════════════ Godot compilation TIMEOUT ═════════════════════════════════════════════════════════════════════╗

                       Godot project compilation did not complete within the configured timeout of {settings.CompileProcessTimeout}ms.

                       Possible reasons:
                       - Your Godot project may be large or complex, requiring more time to compile
                       - Your system may be under heavy load or has limited resources
                       - There might be a compilation issue causing Godot to hang

                       ACTION REQUIRED:
                       To increase the compilation timeout, set the 'CompileProcessTimeout' property in your GdUnit4 settings.

                       Add or modify the following in your .runsettings file:
                       <GdUnit4>
                           <CompileProcessTimeout>60000</CompileProcessTimeout>  <!-- 60 seconds -->
                       </GdUnit4>

                       The process will now be forcefully terminated, which may result in incomplete compilation.

                     ╚══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝
                     """);

                compileProcess.Kill(true);
            }

            return compileProcess.ExitCode == 0;
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            Logger.LogError($"Install GdUnit4 `TestRunner` fails with: {e.Message}\n {e.StackTrace}");

            return false;
        }
        finally
        {
            CloseProcess(compileProcess);
        }
    }

    internal string BuildGodotArguments(string godotProjectRoot, string? logFilePath = null)
    {
        var arguments = new StringBuilder($"--path {QuoteArgument(Path.GetFullPath(godotProjectRoot))} -d -s {QuoteArgument(BuildRunnerSceneResourcePath())}");
        if (!string.IsNullOrWhiteSpace(settings.Parameters))
            _ = arguments.Append(' ').Append(settings.Parameters);

        _ = arguments.Append(" --pipe-name ").Append(QuoteArgument(PipeName));

        // Godot consumes --log-file for engine logging, while the generated gdUnit runner reads
        // --gdunit-log-file because engine arguments are not guaranteed to remain in OS.GetCmdlineArgs().
        AppendLogFileArgument(arguments, logFilePath);
        AppendGdUnitLogFileArgument(arguments, logFilePath);
        return arguments.ToString();
    }

    internal string ResolveRunnerLogFilePath(string workingDirectory, string fileName)
    {
        var runnerDirectory = Path.Combine(ResolveArtifactRootPath(workingDirectory), RunnerId);
        _ = Directory.CreateDirectory(runnerDirectory);
        return Path.GetFullPath(Path.Combine(runnerDirectory, fileName));
    }

    internal static string BuildCompileGodotArguments(string godotProjectRoot, string? logFilePath = null)
    {
        var arguments = new StringBuilder($"--path {QuoteArgument(Path.GetFullPath(godotProjectRoot))} -e --headless --quit-after 1000 --verbose");
        AppendLogFileArgument(arguments, logFilePath);
        return arguments.ToString();
    }

    internal string BuildRunnerSceneResourcePath() => $"res://{RunnerSceneDirectory}/{TEST_RUNNER_SCENE_FILE_NAME}";

    internal string ResolveRunnerSceneDirectoryPath(string godotProjectRoot)
    {
        var destinationFolderPath = Path.GetFullPath(Path.Combine(godotProjectRoot, RunnerSceneDirectory.Replace('/', Path.DirectorySeparatorChar)));
        var normalizedProjectRoot = Path.GetFullPath(godotProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedDestination = destinationFolderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!normalizedDestination.StartsWith(normalizedProjectRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"RunnerSceneDirectory '{RunnerSceneDirectory}' resolves outside the Godot project root '{godotProjectRoot}'. Use a project-relative directory without '..'.");
        }

        return destinationFolderPath;
    }

    internal static string NormalizeRunnerSceneDirectory(string? configuredDirectory)
    {
        var rawDirectory = configuredDirectory?.Trim().Trim('"') ?? string.Empty;
        if (rawDirectory.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            rawDirectory = rawDirectory["res://".Length..];

        rawDirectory = rawDirectory.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(rawDirectory))
            throw new InvalidOperationException("RunnerSceneDirectory must be a non-empty project-relative directory.");

        if (rawDirectory.StartsWith('/') || Path.IsPathRooted(rawDirectory) || rawDirectory.Contains(':', StringComparison.Ordinal))
            throw new InvalidOperationException($"RunnerSceneDirectory '{configuredDirectory}' must be project-relative and cannot be absolute.");

        var segments = rawDirectory
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => segment != ".")
            .ToArray();
        if (segments.Length == 0)
            throw new InvalidOperationException("RunnerSceneDirectory must be a non-empty project-relative directory.");

        if (segments.Any(segment => segment == ".."))
            throw new InvalidOperationException($"RunnerSceneDirectory '{configuredDirectory}' cannot contain '..' path segments.");

        return string.Join('/', segments);
    }

    internal static string CreateRunnerId(string assemblyId)
    {
        var sanitizedAssemblyId = SanitizeAssemblyId(assemblyId);
        return $"{sanitizedAssemblyId}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    }

    internal static string CreatePipeName(string runnerId) => $"gdunit4-{runnerId}";

    private static (string RunnerId, string PipeName) CreateRunnerIdentity(string assemblyId)
    {
        var runnerId = CreateRunnerId(assemblyId);
        return (runnerId, CreatePipeName(runnerId));
    }

    private static string SanitizeAssemblyId(string assemblyId)
    {
        var fileName = Path.GetFileNameWithoutExtension(assemblyId);
        var source = string.IsNullOrWhiteSpace(fileName) ? "unknown-assembly" : fileName;
        var sanitized = new StringBuilder(source.Length);
        foreach (var character in source)
            _ = sanitized.Append(char.IsLetterOrDigit(character) ? char.ToLowerInvariant(character) : '-');

        var result = sanitized.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "unknown-assembly" : result;
    }

    private static void AppendLogFileArgument(StringBuilder arguments, string? logFilePath)
    {
        if (!string.IsNullOrWhiteSpace(logFilePath))
            _ = arguments.Append(" --log-file ").Append(QuoteArgument(logFilePath));
    }

    private static void AppendGdUnitLogFileArgument(StringBuilder arguments, string? logFilePath)
    {
        if (!string.IsNullOrWhiteSpace(logFilePath))
            _ = arguments.Append(" --gdunit-log-file ").Append(QuoteArgument(logFilePath));
    }

    private static string QuoteArgument(string value)
        => $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private static string BuildTestRunnerSceneContent()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("GdUnit4.src.core.runners.GdUnit4TestRunnerSceneTemplate.cs");
        using var reader = new StreamReader(stream!);
        var content = reader.ReadToEnd();
        return content.Replace("GdUnit4TestRunnerSceneTemplate", "GdUnit4TestRunnerScene", StringComparison.Ordinal);
    }

    private string ResolveArtifactRootPath(string workingDirectory)
    {
        var logFileRoot = string.IsNullOrWhiteSpace(settings.LogFileRoot)
            ? DEFAULT_LOG_FILE_ROOT
            : settings.LogFileRoot;
        var rootPath = Path.IsPathRooted(logFileRoot)
            ? logFileRoot
            : Path.Combine(workingDirectory, logFileRoot);
        return Path.GetFullPath(rootPath);
    }

    private void LogRunnerConfiguration(string? compileLogFilePath, string? runtimeLogFilePath)
    {
        Logger.LogInfo($"GdUnit4 runtime runner id: {RunnerId}");
        Logger.LogInfo($"GdUnit4 runtime pipe name: {PipeName}");
        if (!string.IsNullOrWhiteSpace(compileLogFilePath))
            Logger.LogInfo($"GdUnit4 compile log file: {compileLogFilePath}");
        if (!string.IsNullOrWhiteSpace(runtimeLogFilePath))
            Logger.LogInfo($"GdUnit4 runtime log file: {runtimeLogFilePath}");
    }

    private bool RunDotnetRestore(string workingDirectory)
    {
        try
        {
            Logger.LogInfo("Running dotnet build to ensure dependencies are available...");
            var arguments = "build --configuration Debug " +
                            "--verbosity normal " +
                            "--no-restore " +
                            "/p:BuildProjectReferences=false " + // Don't rebuild project refs
                            "/p:_GetChildProjectCopyToOutputDirectoryItems=false " + // Don't copy child project items
                            "/p:SkipCopyingFrameworkReferences=true "; // Skip framework refs (already present)
            var processStartInfo = new ProcessStartInfo("dotnet", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = workingDirectory
            };

            using var restoreProcess = new Process();
            restoreProcess.StartInfo = processStartInfo;
            restoreProcess.EnableRaisingEvents = true;

            restoreProcess.OutputDataReceived += (_, args) =>
            {
                var message = args.Data?.Trim();
                if (!string.IsNullOrEmpty(message))
                    Logger.LogInfo($"build: {message}");
            };

            restoreProcess.ErrorDataReceived += (_, args) =>
            {
                var message = args.Data?.Trim();
                if (!string.IsNullOrEmpty(message))
                    Logger.LogInfo($"error: {message}");
            };

            if (!restoreProcess.Start())
            {
                Logger.LogError("Failed to start dotnet build process");
                return false;
            }

            restoreProcess.BeginErrorReadLine();
            restoreProcess.BeginOutputReadLine();

            // Wait for restore to complete (should be quick)
            var completed = restoreProcess.WaitForExit(30000); // 30 second timeout

            if (!completed)
            {
                Logger.LogWarning("dotnet build timed out after 30 seconds");
                restoreProcess.Kill(true);
                return false;
            }

            var success = restoreProcess.ExitCode == 0;
            if (success)
            {
                Logger.LogInfo("dotnet build completed successfully");
                return success;
            }

            Logger.LogError($"dotnet build failed with exit code: {restoreProcess.ExitCode}");
            return false;
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogError($"Error running build restore: {ex.Message}");
            return false;
        }
    }

    private EventHandler ExitHandler(string source = "") => (sender, _) =>
    {
        Console.Out.Flush();
        if (sender is Process p)
        {
            if (p.ExitCode == 0)
                Logger.LogInfo($"{source} ends with exit code: {p.ExitCode}\n");
            else
                Logger.LogError($"{source} ends with exit code: {p.ExitCode}\n");
        }
    };

    private void CloseProcess(Process? processToClose)
    {
        if (processToClose == null)
            return;
        try
        {
            processToClose.CancelErrorRead();
            processToClose.CancelOutputRead();
            processToClose.ErrorDataReceived -= StdErrorProcessor;
            processToClose.Exited -= ExitHandler();
            processToClose.Dispose();
        }
#pragma warning disable CA1031
        catch (Exception)
#pragma warning restore CA1031
        {
            // ignore
        }
        finally
        {
            lock (ProcessLock)
                process = null;
        }
    }

    /// <summary>
    ///     Cleans up the installed runner file when compilation fails to ensure
    ///     a fresh installation on the next run.
    /// </summary>
    /// <param name="runnerFilePath">Path to the runner file to remove.</param>
    private void CleanupRunnerOnFailure(string runnerFilePath)
    {
        try
        {
            if (File.Exists(runnerFilePath))
            {
                Logger.LogInfo($"Cleaning up runner file at {runnerFilePath} due to compilation failure");
                File.Delete(runnerFilePath);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Logger.LogError($"Failed to clean up runner file: {ex.Message}");

            // We don't want to throw here as this is just cleanup
        }
    }
}
