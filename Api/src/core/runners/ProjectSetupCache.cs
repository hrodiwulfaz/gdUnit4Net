// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Runners;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

using Api;

/// <summary>
///     Project-local coordination state of the Godot editor preparation below <c>.godot/gdunit4/</c>.
/// </summary>
/// <remarks>
///     Every runner of one Godot project shares the same setup lock, success stamp and in-progress journal, whatever
///     its log root or test assembly. The lock is owned through an exclusive file handle, never through the age of the
///     lock file. The success stamp is the only proof of a reusable preparation and is replaced atomically. The journal
///     is recovery state for an interrupted preparation and is never treated as success.
/// </remarks>
internal sealed class ProjectSetupCache
{
    /// <summary>
    ///     Project-relative directory holding the setup coordination state.
    /// </summary>
    internal const string STATE_DIRECTORY = ".godot/gdunit4";

    /// <summary>
    ///     File name of the project-local setup lock.
    /// </summary>
    internal const string LOCK_FILE_NAME = "setup.lock";

    /// <summary>
    ///     File name of the schema-versioned success stamp.
    /// </summary>
    internal const string STAMP_FILE_NAME = "setup-v1.json";

    /// <summary>
    ///     File name of the in-progress journal.
    /// </summary>
    internal const string JOURNAL_FILE_NAME = "setup-in-progress.json";

    private const int SCHEMA_VERSION = 1;
    private const string PHASE_LAUNCH_INTENT = "launch-intent";
    private const string PHASE_EDITOR_STARTED = "editor-started";
    private const int LOCK_RETRY_INTERVAL_MS = 50;
    private const int PROCESS_POLL_INTERVAL_MS = 100;
    private const int FILE_RETRY_COUNT = 5;
    private const int FILE_RETRY_INTERVAL_MS = 50;

    // the state files are read by developers and never embedded in markup, so paths and versions stay unescaped
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly TimeSpan IdentityProbeTimeout = TimeSpan.FromSeconds(3);

    // Windows reports an exact creation time, other platforms derive it from the boot time with jitter.
    private static readonly TimeSpan StartTimeTolerance = OperatingSystem.IsWindows() ? TimeSpan.Zero : TimeSpan.FromSeconds(1);

    private readonly ITestEngineLogger logger;

    /// <summary>
    ///     Initializes a new instance of the <see cref="ProjectSetupCache" /> class.
    /// </summary>
    /// <param name="logger">The test engine logger for diagnostic output.</param>
    /// <param name="projectRoot">The Godot project root owning the coordination state.</param>
    internal ProjectSetupCache(ITestEngineLogger logger, string projectRoot)
    {
        this.logger = logger;
        ProjectRoot = Path.GetFullPath(projectRoot);
        StateDirectory = Path.GetFullPath(Path.Combine(ProjectRoot, STATE_DIRECTORY));
        LockPath = Path.Combine(StateDirectory, LOCK_FILE_NAME);
        StampPath = Path.Combine(StateDirectory, STAMP_FILE_NAME);
        JournalPath = Path.Combine(StateDirectory, JOURNAL_FILE_NAME);
    }

    private enum EditorState
    {
        Gone,
        Running,
        Unknown
    }

    internal string StateDirectory { get; }

    internal string LockPath { get; }

    internal string StampPath { get; }

    internal string JournalPath { get; }

    private string ProjectRoot { get; }

    /// <summary>
    ///     Acquires the exclusive project setup lock.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for the lock.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The owning lock handle, or null when the wait timed out or was canceled.</returns>
    internal FileStream? AcquireLock(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        logger.LogInfo($"Waiting for GdUnit4 project setup lock: {LockPath}");

        while (!cancellationToken.IsCancellationRequested && stopwatch.Elapsed < timeout)
        {
            try
            {
                _ = Directory.CreateDirectory(StateDirectory);
                var lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                logger.LogInfo($"Acquired GdUnit4 project setup lock after {stopwatch.ElapsedMilliseconds}ms: {LockPath}");
                return lockStream;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _ = cancellationToken.WaitHandle.WaitOne(LOCK_RETRY_INTERVAL_MS);
            }
        }

        if (cancellationToken.IsCancellationRequested)
            logger.LogWarning($"Canceled while waiting for GdUnit4 project setup lock: {LockPath}");
        else
            logger.LogError($"Timed out waiting for GdUnit4 project setup lock: {LockPath}");
        return null;
    }

    /// <summary>
    ///     Resolves the journal of a preparation whose owner did not finish it. Must be called while holding the lock.
    /// </summary>
    /// <param name="timeout">The maximum time to wait for a still running editor of the interrupted preparation.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>
    ///     True when no interrupted preparation remains and a fresh preparation may start. False when the recorded
    ///     editor could not be verified gone; the journal is kept and no editor may be started.
    /// </returns>
    /// <remarks>
    ///     A recorded editor is only waited for, never killed. Output of an interrupted preparation is never trusted:
    ///     the success stamp is removed so the next decision is a miss.
    /// </remarks>
    internal bool RecoverInterruptedSetup(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!File.Exists(JournalPath))
            return true;

        var journal = ReadJournal();
        if (journal == null || journal.Phase != PHASE_EDITOR_STARTED)
        {
            logger.LogError(
                $"""
                 GdUnit4 project setup was interrupted before its Godot editor process was identified, so an editor may still be modifying the project.
                 No new editor is started and no project data is deleted automatically.
                 ACTION REQUIRED: stop every Godot process that uses '{ProjectRoot}', delete '{JournalPath}' and run the tests again.
                 """);
            return false;
        }

        var stopwatch = Stopwatch.StartNew();
        var state = InspectEditor(journal);
        if (state == EditorState.Running)
            logger.LogWarning($"Waiting for the Godot editor (pid {journal.EditorProcessId}) of an interrupted GdUnit4 project setup to exit.");

        while (state == EditorState.Running && stopwatch.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
        {
            _ = cancellationToken.WaitHandle.WaitOne(PROCESS_POLL_INTERVAL_MS);
            state = InspectEditor(journal);
        }

        if (state != EditorState.Gone)
        {
            var cause = state == EditorState.Running
                ? cancellationToken.IsCancellationRequested ? "the wait for it was canceled" : $"it is still running after {timeout.TotalMilliseconds:0}ms"
                : "its identity could not be verified";
            logger.LogError(
                $"""
                 GdUnit4 project setup was interrupted and its Godot editor (pid {journal.EditorProcessId}, '{journal.EditorExecutablePath}') can not be treated as gone: {cause}.
                 No new editor is started and the process is not terminated automatically.
                 ACTION REQUIRED: stop that Godot process, delete '{JournalPath}' and run the tests again.
                 """);
            return false;
        }

        logger.LogWarning($"Discarding the interrupted GdUnit4 project setup of process {journal.OwnerProcessId}; the project is prepared again.");
        if (!Invalidate())
            return false;
        DiscardTemporaryFiles();
        return DeleteFile(JournalPath);
    }

    /// <summary>
    ///     Compares the success stamp with the current project state. Must be called while holding the lock.
    /// </summary>
    /// <param name="fingerprint">The fingerprint captured from the current project state.</param>
    /// <param name="missReason">The reason the stamp can not be reused.</param>
    /// <returns>True when the stamp records exactly the captured inputs and outputs.</returns>
    internal bool IsCurrent(ProjectSetupFingerprint fingerprint, out string missReason)
    {
        if (!File.Exists(StampPath))
        {
            missReason = "no setup stamp exists";
            return false;
        }

        SetupStamp? stamp;
        try
        {
            stamp = JsonSerializer.Deserialize<SetupStamp>(File.ReadAllText(StampPath, Encoding.UTF8), SerializerOptions);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            missReason = $"the setup stamp is unreadable ({e.Message})";
            return false;
        }

        if (stamp?.Inputs == null || stamp.Outputs == null)
        {
            missReason = "the setup stamp is incomplete";
            return false;
        }

        if (stamp.Schema != SCHEMA_VERSION)
        {
            missReason = $"the setup stamp has schema {stamp.Schema}, expected {SCHEMA_VERSION}";
            return false;
        }

        var changedInputs = ProjectSetupFingerprint.Difference(stamp.Inputs, fingerprint.Inputs);
        var changedOutputs = ProjectSetupFingerprint.Difference(stamp.Outputs, fingerprint.Outputs);
        if (changedInputs.Count == 0 && changedOutputs.Count == 0)
        {
            missReason = string.Empty;
            return true;
        }

        var reasons = new List<string>();
        if (changedInputs.Count > 0)
            reasons.Add($"changed inputs: {string.Join(", ", changedInputs)}");
        if (changedOutputs.Count > 0)
            reasons.Add($"changed outputs: {string.Join(", ", changedOutputs)}");
        missReason = string.Join("; ", reasons);
        return false;
    }

    /// <summary>
    ///     Removes the success stamp. Must be called while holding the lock.
    /// </summary>
    /// <returns>True when no success stamp remains.</returns>
    internal bool Invalidate() => DeleteFile(StampPath);

    /// <summary>
    ///     Starts a preparation: removes the success stamp and records the intent to launch an editor.
    ///     Must be called while holding the lock.
    /// </summary>
    /// <returns>True when the stamp is gone and the journal is durable; otherwise no editor may be started.</returns>
    internal bool BeginPreparation()
    {
        if (!Invalidate())
            return false;
        DiscardTemporaryFiles();
        return WriteAtomically(JournalPath, new SetupJournal(SCHEMA_VERSION, PHASE_LAUNCH_INTENT, Environment.ProcessId, 0, 0, string.Empty));
    }

    /// <summary>
    ///     Records the identity of the started editor process in the journal.
    /// </summary>
    /// <param name="editor">The editor process started by this runner.</param>
    /// <returns>
    ///     True when the identity is durable or the editor has already exited. False when a running editor could not be
    ///     identified; the caller must terminate its own child.
    /// </returns>
    internal bool RecordEditorProcess(Process editor)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                if (editor.HasExited)
                    return true;

                var executablePath = editor.MainModule?.FileName;
                if (!string.IsNullOrEmpty(executablePath))
                {
                    return WriteAtomically(
                        JournalPath,
                        new SetupJournal(
                            SCHEMA_VERSION,
                            PHASE_EDITOR_STARTED,
                            Environment.ProcessId,
                            editor.Id,
                            editor.StartTime.ToUniversalTime().Ticks,
                            Path.GetFullPath(executablePath)));
                }
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // the module list of a starting process is not readable yet, retry until it is or the process exits
            }

            if (stopwatch.Elapsed >= IdentityProbeTimeout)
            {
                logger.LogError($"Unable to identify the started Godot editor process (pid {editor.Id}) for the GdUnit4 project setup journal.");
                return false;
            }

            Thread.Sleep(PROCESS_POLL_INTERVAL_MS / 10);
        }
    }

    /// <summary>
    ///     Ends a preparation whose editor is verified gone by removing the journal.
    /// </summary>
    internal void EndPreparation() => _ = DeleteFile(JournalPath);

    /// <summary>
    ///     Atomically publishes the success stamp for a validated preparation. Must be called while holding the lock.
    /// </summary>
    /// <param name="fingerprint">The fingerprint of the stable prepared state.</param>
    /// <returns>True when the stamp was published.</returns>
    internal bool Publish(ProjectSetupFingerprint fingerprint)
        => WriteAtomically(
            StampPath,
            new SetupStamp(
                SCHEMA_VERSION,
                new Dictionary<string, string>(fingerprint.Inputs, StringComparer.Ordinal),
                new Dictionary<string, string>(fingerprint.Outputs, StringComparer.Ordinal)));

    private static bool IsSamePath(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static EditorState InspectEditor(SetupJournal journal)
    {
        Process editor;
        try
        {
            editor = Process.GetProcessById(journal.EditorProcessId);
        }
        catch (ArgumentException)
        {
            return EditorState.Gone;
        }

        using (editor)
        {
            try
            {
                if (editor.HasExited)
                    return EditorState.Gone;

                // a different start time means the process id was reused by an unrelated process
                var startTimeDelta = editor.StartTime.ToUniversalTime().Ticks - journal.EditorStartTimeUtcTicks;
                if (Math.Abs(startTimeDelta) > StartTimeTolerance.Ticks)
                    return EditorState.Gone;

                return editor.MainModule?.FileName is { Length: > 0 } executablePath
                       && journal.EditorExecutablePath is { Length: > 0 } recordedPath
                       && IsSamePath(executablePath, recordedPath)
                    ? EditorState.Running
                    : EditorState.Unknown;
            }
            catch (InvalidOperationException)
            {
                return IsGone(editor) ? EditorState.Gone : EditorState.Unknown;
            }
            catch (Exception e) when (e is Win32Exception or NotSupportedException)
            {
                return EditorState.Unknown;
            }
        }
    }

    private static bool IsGone(Process editor)
    {
        try
        {
            return editor.HasExited;
        }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            return false;
        }
    }

    private SetupJournal? ReadJournal()
    {
        try
        {
            var journal = JsonSerializer.Deserialize<SetupJournal>(File.ReadAllText(JournalPath, Encoding.UTF8), SerializerOptions);
            return journal is { Schema: SCHEMA_VERSION, Phase: not null, EditorExecutablePath: not null } ? journal : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            logger.LogWarning($"Unable to read the GdUnit4 project setup journal {JournalPath}: {e.Message}");
            return null;
        }
    }

    private bool WriteAtomically<TRecord>(string targetPath, TRecord record)
    {
        var temporaryPath = Path.Combine(StateDirectory, $"{Path.GetFileNameWithoutExtension(targetPath)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            _ = Directory.CreateDirectory(StateDirectory);
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(record, SerializerOptions));
                stream.Flush(true);
            }

            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporaryPath, targetPath, true);
                    return true;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < FILE_RETRY_COUNT)
                {
                    Thread.Sleep(FILE_RETRY_INTERVAL_MS);
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogError($"Unable to write GdUnit4 project setup state {targetPath}: {e.Message}");
            _ = DeleteFile(temporaryPath);
            return false;
        }
    }

    private bool DeleteFile(string path)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                if (attempt >= FILE_RETRY_COUNT)
                {
                    logger.LogError($"Unable to delete GdUnit4 project setup state {path}: {e.Message}");
                    return false;
                }

                Thread.Sleep(FILE_RETRY_INTERVAL_MS);
            }
        }
    }

    private void DiscardTemporaryFiles()
    {
        if (!Directory.Exists(StateDirectory))
            return;

        try
        {
            foreach (var temporaryPath in Directory.EnumerateFiles(StateDirectory, "*.tmp"))
                _ = DeleteFile(temporaryPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning($"Unable to list abandoned GdUnit4 project setup files under {StateDirectory}: {e.Message}");
        }
    }

    private sealed record SetupStamp(int Schema, Dictionary<string, string>? Inputs, Dictionary<string, string>? Outputs);

    private sealed record SetupJournal(int Schema, string? Phase, int OwnerProcessId, int EditorProcessId, long EditorStartTimeUtcTicks, string? EditorExecutablePath);
}
