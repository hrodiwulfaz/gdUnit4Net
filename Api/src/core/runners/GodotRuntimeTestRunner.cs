// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Runners;

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

using Api;

using Execution;

using Reporting;

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
internal sealed partial class GodotRuntimeTestRunner : BaseTestRunner
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
    private const int EDITOR_REAP_TIMEOUT_MS = 5000;

    private readonly TestEngineSettings settings;
    private Process? process;
    private volatile string? editorIncompleteReason;

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
        : base(new GodotRuntimeExecutor(logger, identity.PipeName, settings.ShutdownTimeout), logger, settings)
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
        TerminateRunningProcess();
    }

    public override void AbortCurrentRun()
    {
        var runtimeProcess = process;
        if (runtimeProcess == null || runtimeProcess.HasExited)
            return;

        Logger.LogError("Terminating the Godot runtime process because the current test batch was aborted.");
        TerminateRunningProcess();
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
            var userDataDir = settings.UseUniqueUserDataDir
                ? ResolveRunnerUserDataDir(workingDirectory)
                : null;

            LogRunnerConfiguration(compileLogFilePath, runtimeLogFilePath, userDataDir);

            var godotBinary = GodotBin;
            var setupFailure = SetUpGodotProject(workingDirectory, godotBinary, compileLogFilePath, userDataDir, cancellationToken);
            if (setupFailure != null)
            {
                ReportRuntimeSetupFailure(testSuiteNodes, eventListener, setupFailure);
                return;
            }

            Logger.LogInfo("======== Running GdUnit4 Godot Runtime Test Runner ========");

            var processStartInfo =
                new ProcessStartInfo(godotBinary, BuildGodotArguments(runtimeLogFilePath, userDataDir))
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

            try
            {
                base.RunAndWait(testSuiteNodes, eventListener, cancellationToken);
            }
            finally
            {
                // the started process must be terminated on every path, an abandoned runtime keeps holding
                // file locks on the build output and blocks the next build
                TerminateRuntime(process);
            }
        }
    }

    /// <summary>
    ///     Prepares the Godot project for a runtime under the project-local setup lock.
    /// </summary>
    /// <param name="workingDirectory">The Godot project root.</param>
    /// <param name="godotBinary">The Godot executable.</param>
    /// <param name="compileLogFilePath">The per-runner editor log file, or null.</param>
    /// <param name="userDataDir">The per-runner user data directory, or null.</param>
    /// <param name="cancellationToken">Cancels the lock wait and a running editor preparation.</param>
    /// <returns>Null when the project is prepared; otherwise the setup failure to report.</returns>
    /// <remarks>
    ///     The generated runner is always installed first. With <see cref="TestEngineSettings.ProjectSetupCache" /> the
    ///     editor preparation is skipped when the project-local success stamp still matches the captured project state;
    ///     a fresh stamp is published only after one successful preparation left the authored inputs untouched and the
    ///     generated outputs stable. Nothing but that editor preparation is ever reused.
    /// </remarks>
    internal string? SetUpGodotProject(string workingDirectory, string godotBinary, string? compileLogFilePath, string? userDataDir, CancellationToken cancellationToken)
    {
        var setupCache = new ProjectSetupCache(Logger, workingDirectory);
        var setupStopwatch = Stopwatch.StartNew();
        var lockTimeout = TimeSpan.FromMilliseconds(Math.Max(settings.SessionTimeout, 600000));
        using var setupLock = setupCache.AcquireLock(lockTimeout, cancellationToken);
        if (setupLock == null)
            return "GdUnit4 runtime setup failed while waiting for the project setup lock.";

        var lockWait = setupStopwatch.Elapsed;
        if (!setupCache.RecoverInterruptedSetup(TimeSpan.FromMilliseconds(settings.CompileProcessTimeout), cancellationToken))
            return "GdUnit4 runtime setup failed while recovering an interrupted project setup.";

        var artifactRootPath = ResolveArtifactRootPath(workingDirectory);
        if (settings.RunnerRetentionCount > 0 && (settings.UseUniqueLogFiles || settings.UseUniqueUserDataDir) && !IsSameOrNestedPath(setupCache.StateDirectory, artifactRootPath))
            PruneRunnerFolders(artifactRootPath, settings.RunnerRetentionCount);

        if (!InstallTestRunnerClasses(workingDirectory))
            return "GdUnit4 runtime setup failed while installing the generated test runner classes.";

        var validationStopwatch = Stopwatch.StartNew();
        ProjectSetupFingerprint? fingerprint = null;
        if (!settings.ProjectSetupCache)
            Logger.LogInfo("GdUnit4 project setup required: ProjectSetupCache is disabled");
        else
        {
            fingerprint = CaptureProjectSetupFingerprint(workingDirectory, godotBinary);
            var missReason = fingerprint.IneligibilityReason;
            if (missReason == null && setupCache.IsCurrent(fingerprint, out missReason))
            {
                Logger.LogInfo(
                    $"GdUnit4 project setup is up to date, reusing the prepared project: lock wait {lockWait.TotalMilliseconds:0}ms, validation {validationStopwatch.ElapsedMilliseconds}ms, total {setupStopwatch.ElapsedMilliseconds}ms");
                return null;
            }

            Logger.LogInfo($"GdUnit4 project setup required: {missReason}");
        }

        var validation = validationStopwatch.Elapsed;
        var preparationStopwatch = Stopwatch.StartNew();
        if (!ReCompileGodotProject(workingDirectory, godotBinary, compileLogFilePath, userDataDir, cancellationToken))
            return "GdUnit4 runtime setup failed while compiling the Godot project.";

        var preparation = preparationStopwatch.Elapsed;
        if (fingerprint != null)
            PublishProjectSetupStamp(setupCache, fingerprint, workingDirectory, godotBinary);

        Logger.LogInfo(
            $"GdUnit4 project setup completed: lock wait {lockWait.TotalMilliseconds:0}ms, validation {validation.TotalMilliseconds:0}ms, preparation {preparation.TotalMilliseconds:0}ms, total {setupStopwatch.ElapsedMilliseconds}ms");
        return null;
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
        var isSuccess = RunDotnetBuild(workingDirectory);
        if (!isSuccess)
            Logger.LogWarning($"Keeping generated runner file at {sceneRunnerSource} after dotnet build failure.");
        return isSuccess;
    }

    /// <summary>
    ///     Runs the headless Godot editor preparation of the project. Must be called while holding the setup lock.
    /// </summary>
    /// <param name="workingDirectory">The Godot project root.</param>
    /// <param name="godotBinary">The Godot executable.</param>
    /// <param name="logFilePath">The editor log file, or null.</param>
    /// <param name="userDataDir">The editor user data directory, or null.</param>
    /// <param name="cancellationToken">Cancels the running editor.</param>
    /// <returns>True when the editor exited successfully within the compile timeout.</returns>
    /// <remarks>
    ///     Every preparation first removes the project setup stamp and journals the launch, then journals the identity
    ///     of the started editor. The journal is removed only once the editor is verified gone, so a later runner never
    ///     starts a second editor next to an orphan of this one.
    /// </remarks>
    internal bool ReCompileGodotProject(string workingDirectory, string godotBinary, string? logFilePath = null, string? userDataDir = null, CancellationToken cancellationToken = default)
    {
        var setupCache = new ProjectSetupCache(Logger, workingDirectory);
        editorIncompleteReason = null;
        if (!setupCache.BeginPreparation())
            return false;

        using var compileProcess = new Process();
        var isEditorStarted = false;
        try
        {
            // recompile the project
            var processStartInfo = new ProcessStartInfo($"{godotBinary}", BuildCompileGodotArguments(workingDirectory, logFilePath, userDataDir))
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
            compileProcess.OutputDataReceived += ObserveEditorOutput;
            compileProcess.ErrorDataReceived += ObserveEditorOutput;
            compileProcess.Exited += ExitHandler("Rebuild Godot Project");
            if (!compileProcess.Start())
            {
                Logger.LogError(@"Rebuild Godot Project fails on process start, exit ..");
                return false;
            }

            isEditorStarted = true;
            compileProcess.BeginErrorReadLine();
            compileProcess.BeginOutputReadLine();
            if (!setupCache.RecordEditorProcess(compileProcess))
            {
                compileProcess.Kill(true);
                return false;
            }

            // The compile project can take a while, and we need to wait until it finishes
            const int checkIntervalMs = 100; // Check every 100ms
            var compileStopwatch = Stopwatch.StartNew();
            while (!compileProcess.HasExited
                   && compileStopwatch.ElapsedMilliseconds < settings.CompileProcessTimeout
                   && !cancellationToken.IsCancellationRequested)
                _ = compileProcess.WaitForExit(checkIntervalMs);

            if (!compileProcess.HasExited && cancellationToken.IsCancellationRequested)
            {
                Logger.LogWarning("Godot project compilation was canceled, terminating the Godot editor process.");
                compileProcess.Kill(true);
                return false;
            }

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
                return false;
            }

            if (!DrainEditorOutput(compileProcess))
                editorIncompleteReason ??= "the Godot editor output could not be read completely";
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
            // the journal keeps naming an editor that could not be verified gone, the next runner then waits for it
            if (!isEditorStarted || HasEditorExited(compileProcess))
                setupCache.EndPreparation();
            compileProcess.OutputDataReceived -= ObserveEditorOutput;
            compileProcess.ErrorDataReceived -= ObserveEditorOutput;
            CloseProcess(compileProcess);
        }
    }

    internal string BuildGodotArguments(string? logFilePath = null, string? userDataDir = null)
    {
        var arguments = new StringBuilder($"--path {QuoteArgument(GodotProjectRoot)} -d -s {QuoteArgument(BuildRunnerSceneResourcePath())}");
        if (!string.IsNullOrWhiteSpace(settings.Parameters))
            _ = arguments.Append(' ').Append(settings.Parameters);

        _ = arguments.Append(" --pipe-name ").Append(QuoteArgument(PipeName));

        // Godot consumes --log-file for engine logging, while the generated gdUnit runner reads
        // --gdunit-log-file because engine arguments are not guaranteed to remain in OS.GetCmdlineArgs().
        AppendLogFileArgument(arguments, logFilePath);
        AppendGdUnitLogFileArgument(arguments, logFilePath);
        AppendUserDataDirArgument(arguments, userDataDir);
        return arguments.ToString();
    }

    internal string ResolveRunnerLogFilePath(string workingDirectory, string fileName)
    {
        var runnerDirectory = Path.Combine(ResolveArtifactRootPath(workingDirectory), RunnerId);
        _ = Directory.CreateDirectory(runnerDirectory);
        return Path.GetFullPath(Path.Combine(runnerDirectory, fileName));
    }

    internal string ResolveRunnerUserDataDir(string workingDirectory)
    {
        var userDataDirectory = Path.Combine(ResolveArtifactRootPath(workingDirectory), RunnerId, "user-data");
        _ = Directory.CreateDirectory(userDataDirectory);
        return Path.GetFullPath(userDataDirectory);
    }

    /// <summary>
    ///     Deletes old per-runner artifact folders under the artifact root, keeping the newest ones.
    /// </summary>
    /// <param name="artifactRootPath">The artifact root holding the per-runner folders.</param>
    /// <param name="retentionCount">The number of newest runner folders to keep; 0 or less disables pruning.</param>
    /// <remarks>
    ///     Only direct subdirectories named like a runner id are considered. Folders are ordered by creation time, newest first.
    ///     The folder of this runner and folders whose test host process is still running are never deleted. A folder that
    ///     cannot be deleted is logged and skipped, pruning never fails the test run.
    /// </remarks>
    internal void PruneRunnerFolders(string artifactRootPath, int retentionCount)
    {
        if (retentionCount <= 0 || !Directory.Exists(artifactRootPath))
            return;

        List<DirectoryInfo> runnerFolders;
        try
        {
            runnerFolders = [.. new DirectoryInfo(artifactRootPath)
                .EnumerateDirectories()
                .Where(folder => RunnerFolderNamePattern().IsMatch(folder.Name))
                .OrderByDescending(folder => folder.CreationTimeUtc)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning($"Unable to list GdUnit4 runner folders under {artifactRootPath}: {e.Message}");
            return;
        }

        var deleted = 0;
        var failed = 0;
        foreach (var folder in runnerFolders.Skip(retentionCount))
        {
            if (folder.Name == RunnerId || IsRunnerProcessAlive(folder.Name))
                continue;

            try
            {
                Directory.Delete(folder.FullName, true);
                deleted++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                failed++;
                Logger.LogWarning($"Unable to delete GdUnit4 runner folder {folder.FullName}: {e.Message}");
            }
        }

        if (deleted > 0 || failed > 0)
            Logger.LogInfo($"Pruned GdUnit4 runner folders under {artifactRootPath}: deleted {deleted}, failed {failed}, kept {runnerFolders.Count - deleted}.");
    }

    internal static string BuildCompileGodotArguments(string godotProjectRoot, string? logFilePath = null, string? userDataDir = null)
    {
        var arguments = new StringBuilder($"--path {QuoteArgument(Path.GetFullPath(godotProjectRoot))} -e --headless --quit-after 1000 --verbose");
        AppendLogFileArgument(arguments, logFilePath);
        AppendUserDataDirArgument(arguments, userDataDir);
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

    internal string BuildDotnetBuildArguments(string workingDirectory)
    {
        var targetPath = ResolveGodotCSharpProjectPath(workingDirectory);
        var targetArgument = string.IsNullOrWhiteSpace(targetPath)
            ? string.Empty
            : QuoteArgument(targetPath) + " ";
        return "build " +
               targetArgument +
               "--configuration Debug " +
               "--verbosity normal " +
               "--no-restore " +
               "/p:BuildProjectReferences=false " +
               "/p:_GetChildProjectCopyToOutputDirectoryItems=false " +
               "/p:SkipCopyingFrameworkReferences=true ";
    }

    internal string? ResolveGodotCSharpProjectPath(string workingDirectory)
    {
        var projectRoot = Path.GetFullPath(workingDirectory);
        var projectGodotPath = Path.Combine(projectRoot, "project.godot");
        if (File.Exists(projectGodotPath))
        {
            var assemblyName = ReadProjectGodotSetting(projectGodotPath, "dotnet", "project/assembly_name");
            if (!string.IsNullOrWhiteSpace(assemblyName))
            {
                var namedProjectPath = Path.Combine(projectRoot, $"{assemblyName}.csproj");
                if (File.Exists(namedProjectPath))
                    return Path.GetFullPath(namedProjectPath);
            }
        }

        if (!Directory.Exists(projectRoot))
            return null;

        var projectPaths = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly);
        return projectPaths.Length == 1
            ? Path.GetFullPath(projectPaths[0])
            : null;
    }

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

    private static void AppendUserDataDirArgument(StringBuilder arguments, string? userDataDir)
    {
        if (!string.IsNullOrWhiteSpace(userDataDir))
            _ = arguments.Append(" --user-data-dir ").Append(QuoteArgument(userDataDir));
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

    /// <summary>
    ///     Terminates the Godot runtime process of a finished run.
    /// </summary>
    /// <param name="runtimeProcess">The Godot process started for the run.</param>
    /// <remarks>
    ///     The runtime is granted a grace period to exit on its own before it is killed, so a shutdown that is merely
    ///     slow still ends cleanly.
    /// </remarks>
    private void TerminateRuntime(Process runtimeProcess)
    {
        _ = runtimeProcess.WaitForExit(2000);

        // wait until the process has finished
        var waitRetry = 0;
        while (!runtimeProcess.HasExited && waitRetry++ < 10)
            Thread.Sleep(100);

        // If the process not finished until 10 retries, we kill it manually
        if (!runtimeProcess.HasExited)
        {
            Logger.LogInfo("GdUnit4 Godot Runtime Test Runner is not terminated, force process kill.");
            runtimeProcess.Kill(true);
        }

        CloseProcess(runtimeProcess);
    }

    private void TerminateRunningProcess()
    {
        var runtimeProcess = process;
        if (runtimeProcess == null || runtimeProcess.HasExited)
            return;

        runtimeProcess.Kill(true);
        _ = runtimeProcess.WaitForExit(1000);
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

    private static bool IsRunnerProcessAlive(string runnerFolderName)
    {
        var match = RunnerFolderNamePattern().Match(runnerFolderName);
        if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var processId))
            return false;

        try
        {
            using var runnerProcess = Process.GetProcessById(processId);
            return !runnerProcess.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private void LogRunnerConfiguration(string? compileLogFilePath, string? runtimeLogFilePath, string? userDataDir)
    {
        Logger.LogInfo($"GdUnit4 runtime runner id: {RunnerId}");
        Logger.LogInfo($"GdUnit4 runtime pipe name: {PipeName}");
        if (!string.IsNullOrWhiteSpace(compileLogFilePath))
            Logger.LogInfo($"GdUnit4 compile log file: {compileLogFilePath}");
        if (!string.IsNullOrWhiteSpace(runtimeLogFilePath))
            Logger.LogInfo($"GdUnit4 runtime log file: {runtimeLogFilePath}");
        if (!string.IsNullOrWhiteSpace(userDataDir))
            Logger.LogInfo($"GdUnit4 user data directory: {userDataDir}");
    }

    private ProjectSetupFingerprint CaptureProjectSetupFingerprint(string workingDirectory, string godotBinary)
        => ProjectSetupFingerprint.Capture(
            workingDirectory,
            godotBinary,
            Path.Combine(ResolveRunnerSceneDirectoryPath(workingDirectory), TEST_RUNNER_SCENE_FILE_NAME),
            new Dictionary<string, string>
            {
                ["gdunit.adapter"] = settings.TestAdapterIdentity,
                ["preparation.command"] = BuildCompileGodotArguments(workingDirectory),
                ["runner.resource"] = BuildRunnerSceneResourcePath(),
                ["runner.template"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(BuildTestRunnerSceneContent())))
            });

    /// <summary>
    ///     Publishes the project setup stamp when the finished preparation is provably reusable.
    /// </summary>
    /// <param name="setupCache">The project-local setup state.</param>
    /// <param name="before">The fingerprint captured before the preparation.</param>
    /// <param name="workingDirectory">The Godot project root.</param>
    /// <param name="godotBinary">The Godot executable.</param>
    /// <remarks>
    ///     The authored inputs must equal the pre-setup capture in two captures taken after the editor is gone, and the
    ///     generated outputs must be equal in both. Anything else leaves the project prepared but unstamped.
    /// </remarks>
    private void PublishProjectSetupStamp(ProjectSetupCache setupCache, ProjectSetupFingerprint before, string workingDirectory, string godotBinary)
    {
        var reason = before.IneligibilityReason ?? editorIncompleteReason;
        if (reason == null)
        {
            var prepared = CaptureProjectSetupFingerprint(workingDirectory, godotBinary);
            var stable = CaptureProjectSetupFingerprint(workingDirectory, godotBinary);
            var changedInputs = ProjectSetupFingerprint.Difference(before.Inputs, prepared.Inputs)
                .Union(ProjectSetupFingerprint.Difference(before.Inputs, stable.Inputs), StringComparer.Ordinal)
                .ToList();
            var unstableOutputs = ProjectSetupFingerprint.Difference(prepared.Outputs, stable.Outputs);
            reason = prepared.IneligibilityReason ?? stable.IneligibilityReason;
            if (reason == null && changedInputs.Count > 0)
                reason = $"authored inputs changed during the preparation: {string.Join(", ", changedInputs)}";
            if (reason == null && unstableOutputs.Count > 0)
                reason = $"generated outputs are not stable: {string.Join(", ", unstableOutputs)}";
            reason ??= stable.MissingOutputReason;
            if (reason == null && !setupCache.Publish(stable))
                reason = "the setup stamp could not be written";
        }

        if (reason == null)
            Logger.LogInfo($"Published GdUnit4 project setup stamp: {setupCache.StampPath}");
        else
            Logger.LogInfo($"GdUnit4 project setup stamp not published: {reason}");
    }

    private void ObserveEditorOutput(object sender, DataReceivedEventArgs args)
    {
        // the editor still exits with code 0 when its --quit-after budget ends before the file system scan and import
        if (args.Data?.Contains("Scan thread aborted", StringComparison.Ordinal) == true)
            editorIncompleteReason = "the Godot editor quit before its file system scan finished";
    }

    private static bool DrainEditorOutput(Process editor)
    {
        // the timed WaitForExit overloads do not wait for the redirected streams of an exited process
        using var timeout = new CancellationTokenSource(EDITOR_REAP_TIMEOUT_MS);
        try
        {
            editor.WaitForExitAsync(timeout.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static bool HasEditorExited(Process editor)
    {
        try
        {
            return editor.WaitForExit(EDITOR_REAP_TIMEOUT_MS);
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static bool IsSameOrNestedPath(string parentDirectory, string path)
    {
        var parent = Path.GetFullPath(parentDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private bool RunDotnetBuild(string workingDirectory)
    {
        try
        {
            Logger.LogInfo("Running dotnet build to ensure dependencies are available...");
            var targetPath = ResolveGodotCSharpProjectPath(workingDirectory);
            if (string.IsNullOrWhiteSpace(targetPath))
                Logger.LogWarning($"Unable to resolve a Godot C# project under {workingDirectory}; falling back to bare dotnet build in the working directory.");
            else
                Logger.LogInfo($"Resolved Godot C# project for dotnet build: {targetPath}");
            Logger.LogInfo($"dotnet build timeout: {settings.CompileProcessTimeout}ms");
            var arguments = BuildDotnetBuildArguments(workingDirectory);
            Logger.LogInfo($"dotnet build command: dotnet {arguments}");
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

            var completed = restoreProcess.WaitForExit(settings.CompileProcessTimeout);

            if (!completed)
            {
                Logger.LogWarning($"dotnet build timed out after {settings.CompileProcessTimeout}ms");
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

    private void ReportRuntimeSetupFailure(List<TestSuiteNode> testSuiteNodes, ITestEventListener eventListener, string message)
    {
        Logger.LogError(message);
        foreach (var testSuiteNode in testSuiteNodes)
        {
            var statistics = TestEvent.BuildStatistics(
                0,
                true,
                testSuiteNode.Tests.Count,
                false,
                0,
                false,
                false,
                0,
                0);
            var testEvent = TestEvent
                .After(
                    testSuiteNode.SourceFile,
                    testSuiteNode.ManagedType,
                    statistics,
                    [new TestReport(ReportType.Abort, -1, message)])
                .WithFullyQualifiedName(testSuiteNode.ManagedType);
            eventListener.PublishEvent(testEvent);
        }
    }

    private static string? ReadProjectGodotSetting(string projectGodotPath, string sectionName, string settingName)
    {
        var activeSection = string.Empty;
        foreach (var rawLine in File.ReadLines(projectGodotPath))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                activeSection = line[1..^1];
                continue;
            }

            if (!string.Equals(activeSection, sectionName, StringComparison.OrdinalIgnoreCase))
                continue;

            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex < 0)
                continue;

            var key = line[..separatorIndex].Trim();
            if (!string.Equals(key, settingName, StringComparison.OrdinalIgnoreCase))
                continue;

            return line[(separatorIndex + 1)..].Trim().Trim('"');
        }

        return null;
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

    [GeneratedRegex(@"^.+-(\d+)-[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RunnerFolderNamePattern();
}
