// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Tests.Host;

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

using GdUnit4.Api;
using GdUnit4.Core.Runners;

/// <summary>
///     Process-level test double of the project setup tests.
/// </summary>
/// <remarks>
///     <para>
///         Started with the Godot editor arguments it stands in for the headless editor preparation: it writes
///         deterministic UID, import and sidecar state into the project and traces its lifetime, so tests can count
///         preparations and prove that no two of them overlap.
///     </para>
///     <para>
///         Started with <c>setup</c> it owns one project setup in its own process, with itself as the editor.
///     </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    ///     Names the file every editor stand-in appends its <c>start</c> and <c>end</c> lines to.
    /// </summary>
    internal const string TRACE_VARIABLE = "GDUNIT4_TEST_HOST_TRACE";

    /// <summary>
    ///     Milliseconds the editor stand-in waits before it writes the project state.
    /// </summary>
    internal const string DELAY_VARIABLE = "GDUNIT4_TEST_HOST_DELAY_MS";

    /// <summary>
    ///     Exit code of the editor stand-in.
    /// </summary>
    internal const string EXIT_CODE_VARIABLE = "GDUNIT4_TEST_HOST_EXIT_CODE";

    /// <summary>
    ///     Comma separated deviations of the editor stand-in: <c>abort-scan</c>, <c>touch-input</c>,
    ///     <c>skip-uid-cache</c> or <c>block-stamp</c>.
    /// </summary>
    internal const string BEHAVIOR_VARIABLE = "GDUNIT4_TEST_HOST_BEHAVIOR";

    private const string PROJECT_DATA_DIRECTORY = ".godot";

    private static int Main(string[] args)
        => args is ["setup", var projectRoot, var logFileRoot, var projectSetupCache, var compileProcessTimeout]
            ? RunSetup(projectRoot, logFileRoot, bool.Parse(projectSetupCache), int.Parse(compileProcessTimeout))
            : RunEditor(args);

    private static int RunSetup(string projectRoot, string logFileRoot, bool projectSetupCache, int compileProcessTimeout)
    {
        var settings = new TestEngineSettings
        {
            CompileProcessTimeout = compileProcessTimeout,
            LogFileRoot = logFileRoot,
            ProjectSetupCache = projectSetupCache
        };
        var runner = new GodotRuntimeTestRunner(new ConsoleLogger(), new NoDebuggerFramework(), settings, "GdUnit4ApiTestHost.dll", projectRoot);
        try
        {
            var failure = runner.SetUpGodotProject(projectRoot, Environment.ProcessPath!, null, null, CancellationToken.None);
            if (failure != null)
                Console.WriteLine($"Error: {failure}");
            return failure == null ? 0 : 1;
        }
        finally
        {
            runner.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static int RunEditor(string[] args)
    {
        var pathIndex = Array.IndexOf(args, "--path");
        if (pathIndex < 0 || pathIndex + 1 >= args.Length || !args.Contains("-e"))
            return 2;

        var projectRoot = Path.GetFullPath(args[pathIndex + 1]);
        var behavior = (Environment.GetEnvironmentVariable(BEHAVIOR_VARIABLE) ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Trace("start");
        Thread.Sleep(ReadNumber(DELAY_VARIABLE));
        WriteProjectState(projectRoot, behavior);
        if (behavior.Contains("abort-scan"))
            Console.Error.WriteLine("WARNING: Scan thread aborted...");
        Trace("end");
        return ReadNumber(EXIT_CODE_VARIABLE);
    }

    private static void WriteProjectState(string projectRoot, string[] behavior)
    {
        var dataDirectory = Path.Combine(projectRoot, PROJECT_DATA_DIRECTORY);
        _ = Directory.CreateDirectory(Path.Combine(dataDirectory, "imported"));

        var uniqueIds = new List<string>();
        foreach (var filePath in EnumerateVisibleFiles(projectRoot).Order(StringComparer.Ordinal))
        {
            var resourcePath = Path.GetRelativePath(projectRoot, filePath).Replace('\\', '/');
            switch (Path.GetExtension(filePath))
            {
                case ".asset":
                    var importedPath = $"{PROJECT_DATA_DIRECTORY}/imported/{Path.GetFileName(filePath)}-{Hash(resourcePath)}.res";
                    File.WriteAllBytes(Path.Combine(projectRoot, importedPath), File.ReadAllBytes(filePath));
                    File.WriteAllText($"{filePath}.import", BuildImportSidecar(resourcePath, importedPath));
                    uniqueIds.Add(resourcePath);
                    break;
                case ".csv":
                    var translationPath = Path.ChangeExtension(resourcePath, ".translation");
                    File.WriteAllBytes(Path.Combine(projectRoot, translationPath), File.ReadAllBytes(filePath));
                    File.WriteAllText($"{filePath}.import", BuildImportSidecar(resourcePath, translationPath));
                    break;
                case ".cs":
                    if (!File.Exists($"{filePath}.uid"))
                        File.WriteAllText($"{filePath}.uid", $"uid://{Hash(resourcePath)}\n");
                    uniqueIds.Add(resourcePath);
                    break;
                default:
                    break;
            }
        }

        if (!behavior.Contains("skip-uid-cache"))
            File.WriteAllText(Path.Combine(dataDirectory, "uid_cache.bin"), string.Join('\n', uniqueIds));
        File.WriteAllText(Path.Combine(dataDirectory, "global_script_class_cache.cfg"), "list=[]\n");

        if (behavior.Contains("touch-input"))
            File.AppendAllText(Path.Combine(projectRoot, "touched-by-editor.txt"), "x");
        if (behavior.Contains("block-stamp"))
            _ = Directory.CreateDirectory(Path.Combine(projectRoot, ProjectSetupCache.STATE_DIRECTORY, ProjectSetupCache.STAMP_FILE_NAME));
    }

    private static IEnumerable<string> EnumerateVisibleFiles(string directory)
    {
        foreach (var filePath in Directory.EnumerateFiles(directory))
            yield return filePath;

        foreach (var subDirectory in Directory.EnumerateDirectories(directory))
        {
            if (Path.GetFileName(subDirectory).StartsWith('.')
                || File.Exists(Path.Combine(subDirectory, ".gdignore"))
                || File.Exists(Path.Combine(subDirectory, "project.godot")))
                continue;

            foreach (var filePath in EnumerateVisibleFiles(subDirectory))
                yield return filePath;
        }
    }

    private static string BuildImportSidecar(string sourcePath, string resultPath)
        => $"""
            [remap]

            importer="test_host"
            path="res://{resultPath}"

            [deps]

            source_file="res://{sourcePath}"
            dest_files=["res://{resultPath}"]

            """.ReplaceLineEndings("\n");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..16];

    private static int ReadNumber(string variable) => int.TryParse(Environment.GetEnvironmentVariable(variable), out var value) ? value : 0;

    private static void Trace(string phase)
    {
        var tracePath = Environment.GetEnvironmentVariable(TRACE_VARIABLE);
        if (string.IsNullOrEmpty(tracePath))
            return;

        var line = $"{phase} {Environment.ProcessId} {Stopwatch.GetTimestamp()} profiling={Environment.GetEnvironmentVariable("CORECLR_ENABLE_PROFILING")}{Environment.NewLine}";
        while (true)
        {
            try
            {
                using var stream = new FileStream(tracePath, FileMode.Append, FileAccess.Write, FileShare.Read);
                stream.Write(Encoding.UTF8.GetBytes(line));
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(5);
            }
        }
    }

    private sealed class ConsoleLogger : ITestEngineLogger
    {
        public void SendMessage(LogLevel logLevel, string message) => Console.WriteLine($"{logLevel}: {message}");
    }

    private sealed class NoDebuggerFramework : IDebuggerFramework
    {
        public bool IsDebugProcess => false;

        public bool IsDebugAttach => false;

        public Process LaunchProcessWithDebuggerAttached(ProcessStartInfo processStartInfo) => throw new NotSupportedException("The test host never debugs a Godot process.");

        public bool AttachDebuggerToProcess(Process process) => false;
    }
}
