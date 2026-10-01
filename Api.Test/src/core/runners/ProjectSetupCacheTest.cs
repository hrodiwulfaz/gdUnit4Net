namespace GdUnit4.Tests.Core.Runners;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Api;

using GdUnit4.Core.Runners;

using Moq;

using static Assertions;

/// <summary>
///     Tests the project-local setup lock, success stamp, in-progress journal and content fingerprint of the
///     Godot editor preparation. The editor is the GdUnit4ApiTestHost process, a deterministic stand-in that
///     traces its lifetime, so every test can count preparations and prove that none of them overlap.
/// </summary>
[TestSuite]
public class ProjectSetupCacheTest
{
    private const string TRACE_VARIABLE = "GDUNIT4_TEST_HOST_TRACE";
    private const string DELAY_VARIABLE = "GDUNIT4_TEST_HOST_DELAY_MS";
    private const string EXIT_CODE_VARIABLE = "GDUNIT4_TEST_HOST_EXIT_CODE";
    private const string BEHAVIOR_VARIABLE = "GDUNIT4_TEST_HOST_BEHAVIOR";
    private const string PROFILING_VARIABLE = "CORECLR_ENABLE_PROFILING";
    private const string COVERAGE_SESSION_VARIABLE = "CODE_COVERAGE_SESSION_NAME";

    private const string HIT = "GdUnit4 project setup is up to date";
    private const string REQUIRED = "GdUnit4 project setup required:";
    private const string PUBLISHED = "Published GdUnit4 project setup stamp";
    private const string NOT_PUBLISHED = "GdUnit4 project setup stamp not published:";
    private const string RECOVERY_FAILURE = "GdUnit4 runtime setup failed while recovering an interrupted project setup.";
    private const string COMPILE_FAILURE = "GdUnit4 runtime setup failed while compiling the Godot project.";

    private static readonly string[] HostVariables = [TRACE_VARIABLE, DELAY_VARIABLE, EXIT_CODE_VARIABLE, BEHAVIOR_VARIABLE, PROFILING_VARIABLE, COVERAGE_SESSION_VARIABLE];

    private static readonly string HostDirectory = Path.GetFullPath(typeof(ProjectSetupCacheTest).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "GdUnit4ApiTestHostDirectory")
        .Value!);

    private string TempRoot { get; set; } = string.Empty;
    private string ProjectRoot { get; set; } = string.Empty;
    private string TracePath { get; set; } = string.Empty;
    private CapturingLogger Logger { get; set; } = new();
    private List<Process> StartedProcesses { get; } = [];

    private string StateDirectory => Path.Combine(ProjectRoot, ".godot", "gdunit4");
    private string StampPath => Path.Combine(StateDirectory, ProjectSetupCache.STAMP_FILE_NAME);
    private string JournalPath => Path.Combine(StateDirectory, ProjectSetupCache.JOURNAL_FILE_NAME);

    [BeforeTest]
    public void BeforeTest()
    {
        TempRoot = Path.Combine(Path.GetTempPath(), $"gdunit4-setup-{Guid.NewGuid():N}");
        Directory.CreateDirectory(TempRoot);
        TracePath = Path.Combine(TempRoot, "trace.log");
        Logger = new CapturingLogger();
        foreach (var variable in HostVariables)
            Environment.SetEnvironmentVariable(variable, null);
        Environment.SetEnvironmentVariable(TRACE_VARIABLE, TracePath);
        ProjectRoot = CreateProject("project");
    }

    [AfterTest]
    public void AfterTest()
    {
        foreach (var variable in HostVariables)
            Environment.SetEnvironmentVariable(variable, null);

        foreach (var process in StartedProcesses)
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    process.WaitForExit(5000);
                }
            }
        }

        StartedProcesses.Clear();
        WaitUntil(() => TryDeleteDirectory(TempRoot), 5000);
    }

    [TestCase]
    public void SettingIsDisabledByDefault()
        => AssertThat(new TestEngineSettings().ProjectSetupCache).IsFalse();

    [TestCase]
    public void FreshProjectPublishesInTheSameInvocationAndTheNextInvocationHits()
    {
        AssertThat(File.Exists(Path.Combine(ProjectRoot, ".godot", "uid_cache.bin"))).IsFalse();

        AssertThat(SetUp()).IsNull();

        AssertLogged($"{REQUIRED} no setup stamp exists");
        AssertLogged(PUBLISHED);
        AssertLogged("GdUnit4 project setup completed: lock wait");
        AssertThat(EditorRuns().Count).IsEqual(1);
        var stamp = JsonNode.Parse(File.ReadAllText(StampPath))!;
        AssertThat(stamp["schema"]!.GetValue<int>()).IsEqual(1);
        AssertThat(stamp["inputs"]!["gdunit.api"]!.GetValue<string>()).Contains("mvid=");
        AssertThat(stamp["inputs"]!["gdunit.adapter"]!.GetValue<string>()).IsEqual("adapter-1");
        AssertThat(File.Exists(JournalPath)).IsFalse();
        AssertThat(Directory.GetFiles(StateDirectory, "*.tmp").Length).IsEqual(0);

        Logger.Clear();
        AssertThat(SetUp()).IsNull();

        AssertLogged(HIT);
        AssertLogged("validation ");
        AssertNotLogged(REQUIRED);
        AssertNotLogged("Rebuild Godot Project");
        AssertThat(EditorRuns().Count).IsEqual(1);
    }

    [TestCase]
    public void TimestampOnlyChangesRemainHits()
    {
        Prepare();
        var future = DateTime.UtcNow.AddDays(3);
        foreach (var relativePath in new[]
                 {
                     "project.godot", "Main.cs", "Main.cs.uid", "icon.asset", "icon.asset.import", ".godot/uid_cache.bin", ".godot/mono/temp/bin/Debug/Fixture.dll"
                 })
            File.SetLastWriteTimeUtc(Path.Combine(ProjectRoot, relativePath), future);
        foreach (var importedFile in Directory.GetFiles(Path.Combine(ProjectRoot, ".godot", "imported")))
            File.SetLastWriteTimeUtc(importedFile, future);

        AssertHit();
    }

    [TestCase]
    public void ScannerIgnoredAndEditorOnlyStateRemainHits()
    {
        Prepare();
        File.WriteAllText(Path.Combine(ProjectRoot, "ignored", "data.txt"), "changed");
        File.WriteAllText(Path.Combine(ProjectRoot, ".hidden", "data.txt"), "changed");
        File.WriteAllText(Path.Combine(ProjectRoot, "nested", "data.txt"), "changed");
        Directory.CreateDirectory(Path.Combine(ProjectRoot, ".godot", "editor"));
        File.WriteAllText(Path.Combine(ProjectRoot, ".godot", "editor", "filesystem_cache10"), "editor only");
        File.WriteAllText(Path.Combine(StateDirectory, "unrelated.txt"), "state directory is never fingerprinted");

        AssertHit();
    }

    [TestCase]
    public void MalformedTruncatedAndUnknownSchemaStampsMiss()
    {
        Prepare();
        var validStamp = File.ReadAllText(StampPath);

        File.WriteAllText(StampPath, "not json");
        AssertMiss("the setup stamp is unreadable");

        File.WriteAllText(StampPath, validStamp[..(validStamp.Length / 2)]);
        AssertMiss("the setup stamp is unreadable");

        File.WriteAllText(StampPath, "{\"schema\":1}");
        AssertMiss("the setup stamp is incomplete");

        File.WriteAllText(StampPath, "null");
        AssertMiss("the setup stamp is incomplete");

        var unknownSchema = JsonNode.Parse(validStamp)!;
        unknownSchema["schema"] = 2;
        File.WriteAllText(StampPath, unknownSchema.ToJsonString());
        AssertMiss("the setup stamp has schema 2, expected 1");

        AssertHit();
    }

    [TestCase]
    public void AbandonedTemporaryStampIsNeverAccepted()
    {
        Prepare();
        var abandoned = Path.Combine(StateDirectory, "setup-v1.4242.0123456789abcdef0123456789abcdef.tmp");
        File.Move(StampPath, abandoned);

        AssertMiss("no setup stamp exists");

        AssertThat(File.Exists(abandoned)).OverrideFailureMessage("abandoned temporary files are discarded by the next preparation").IsFalse();
        AssertThat(File.Exists(StampPath)).IsTrue();
    }

    [TestCase]
    public void ProjectSettingsChangeInvalidates()
    {
        Prepare();
        File.AppendAllText(Path.Combine(ProjectRoot, "project.godot"), "\n[rendering]\nquality=1\n");

        AssertMiss("changed inputs: project.settings");
    }

    [TestCase]
    public void VisibleTreeMembershipContentAndRenameInvalidate()
    {
        Prepare();
        File.WriteAllText(Path.Combine(ProjectRoot, "added.txt"), "new member");
        AssertMiss("changed inputs: project.tree");

        File.WriteAllText(Path.Combine(ProjectRoot, "notes.md"), "edited content");
        AssertMiss("changed inputs: project.tree");

        File.Move(Path.Combine(ProjectRoot, "notes.md"), Path.Combine(ProjectRoot, "renamed.md"));
        AssertMiss("changed inputs: project.tree");

        File.Delete(Path.Combine(ProjectRoot, "added.txt"));
        AssertMiss("changed inputs: project.tree");
    }

    [TestCase]
    public void SameSizeEditWithPreservedTimestampInvalidates()
    {
        Prepare();
        var path = Path.Combine(ProjectRoot, "notes.md");
        var timestamp = File.GetLastWriteTimeUtc(path);
        var content = File.ReadAllText(path);
        File.WriteAllText(path, new string('z', content.Length));
        File.SetLastWriteTimeUtc(path, timestamp);
        AssertThat(new FileInfo(path).Length).IsEqual((long)content.Length);

        AssertMiss("changed inputs: project.tree");
    }

    [TestCase]
    public void GameOutputMembershipAndContentInvalidate()
    {
        Prepare();
        var outputDirectory = Path.Combine(ProjectRoot, ".godot", "mono", "temp", "bin", "Debug");
        File.WriteAllText(Path.Combine(outputDirectory, "Fixture.dll"), "rebuilt assembly");
        AssertMiss("changed inputs: game.output");

        File.WriteAllText(Path.Combine(outputDirectory, "Dependency.dll"), "new dependency");
        AssertMiss("changed inputs: game.output");
    }

    [TestCase]
    public void RunnerLocationAndAdapterIdentityInvalidate()
    {
        Prepare();
        AssertMiss("changed inputs: gdunit.adapter", adapterIdentity: "adapter-2");
        AssertHit(adapterIdentity: "adapter-2");

        const string relocatedRunner = "generated/runner";
        CreateRunner(new TestEngineSettings { RunnerSceneDirectory = relocatedRunner }, ProjectRoot).InstallTestRunnerClasses(ProjectRoot, false);
        Logger.Clear();
        AssertThat(SetUp(adapterIdentity: "adapter-2", runnerSceneDirectory: relocatedRunner)).IsNull();
        AssertLogged("runner.resource");
        AssertLogged("runner.source");
        AssertLogged(PUBLISHED);
    }

    [TestCase]
    public void EnginePathAndCompanionContentInvalidate()
    {
        var firstEngine = CopyHost("engine-a");
        var secondEngine = CopyHost("engine-b");
        AssertThat(SetUp(godotBinary: firstEngine)).IsNull();
        AssertLogged(PUBLISHED);
        AssertHit(godotBinary: firstEngine);

        AssertMiss("changed inputs: engine.executable", godotBinary: secondEngine);

        // any file next to the executable counts, a console wrapper starts the real editor from there
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(secondEngine)!, "real-editor.bin"), "added engine file");
        AssertMiss("changed inputs: engine.companions", godotBinary: secondEngine);

        File.WriteAllText(Path.Combine(Path.GetDirectoryName(secondEngine)!, "real-editor.bin"), "edited engine file");
        AssertMiss("changed inputs: engine.companions", godotBinary: secondEngine);

        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(secondEngine)!, "GodotSharp", "Api"));
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(secondEngine)!, "GodotSharp", "Api", "GodotSharp.dll"), "managed engine api");
        AssertMiss("changed inputs: engine.companions", godotBinary: secondEngine);
    }

    [TestCase]
    public void GeneratedOutputLossAndCorruptionInvalidate()
    {
        Prepare();
        File.Delete(Path.Combine(ProjectRoot, ".godot", "uid_cache.bin"));
        AssertMiss("changed outputs: godot.uid_cache");

        File.WriteAllText(Path.Combine(ProjectRoot, ".godot", "uid_cache.bin"), "corrupt");
        AssertMiss("changed outputs: godot.uid_cache");

        File.WriteAllText(Directory.GetFiles(Path.Combine(ProjectRoot, ".godot", "imported")).Single(), "corrupt");
        AssertMiss("changed outputs: godot.imported");

        File.Delete(Directory.GetFiles(Path.Combine(ProjectRoot, ".godot", "imported")).Single());
        AssertMiss("changed outputs: godot.imported");

        File.Delete(Path.Combine(ProjectRoot, "Main.cs.uid"));
        AssertMiss("changed outputs: project.sidecars");

        File.AppendAllText(Path.Combine(ProjectRoot, "icon.asset.import"), "\n[params]\nedited=true\n");
        AssertMiss("changed outputs: project.sidecars");

        File.WriteAllText(Path.Combine(ProjectRoot, "translations", "strings.translation"), "corrupt");
        AssertMiss("changed outputs: project.sidecars");

        File.WriteAllText(Path.Combine(ProjectRoot, ".godot", "global_script_class_cache.cfg"), "list=[{}]\n");
        AssertMiss("changed outputs: godot.global_script_class_cache");

        File.WriteAllText(Path.Combine(ProjectRoot, ".godot", "scene_groups_cache.cfg"), "[groups]\n");
        AssertMiss("changed outputs: godot.scene_groups_cache");
    }

    [TestCase]
    public void DisabledModeAlwaysPreparesPublishesNothingAndInvalidatesTheStamp()
    {
        Prepare();

        AssertThat(SetUp(false)).IsNull();
        AssertLogged($"{REQUIRED} ProjectSetupCache is disabled");
        AssertThat(File.Exists(StampPath)).OverrideFailureMessage("a disabled run invalidates the old stamp").IsFalse();
        AssertThat(EditorRuns().Count).IsEqual(2);

        Logger.Clear();
        AssertThat(SetUp(false)).IsNull();
        AssertNotLogged(PUBLISHED);
        AssertNotLogged(HIT);
        AssertThat(File.Exists(StampPath)).IsFalse();
        AssertThat(EditorRuns().Count).IsEqual(3);

        AssertMiss("no setup stamp exists");
    }

    [TestCase]
    public void FailingEditorPublishesNothingAndTheNextRunRecovers()
    {
        Environment.SetEnvironmentVariable(EXIT_CODE_VARIABLE, "3");

        AssertThat(SetUp()).IsEqual(COMPILE_FAILURE);
        AssertThat(File.Exists(StampPath)).IsFalse();
        AssertThat(File.Exists(JournalPath)).OverrideFailureMessage("the journal of a reaped editor is removed").IsFalse();

        Environment.SetEnvironmentVariable(EXIT_CODE_VARIABLE, null);
        Logger.Clear();
        AssertThat(SetUp()).IsNull();
        AssertLogged(PUBLISHED);
        AssertThat(EditorRuns().Count).IsEqual(2);
    }

    [TestCase]
    public void FailingEditorInvalidatesAnExistingStamp()
    {
        Prepare();
        File.WriteAllText(Path.Combine(ProjectRoot, "notes.md"), "edited content");
        Environment.SetEnvironmentVariable(EXIT_CODE_VARIABLE, "1");

        AssertThat(SetUp()).IsEqual(COMPILE_FAILURE);

        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase]
    public void TimedOutEditorIsTerminatedAndPublishesNothing()
    {
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, "60000");

        AssertThat(SetUp(compileProcessTimeout: 1500)).IsEqual(COMPILE_FAILURE);

        AssertLogged("Godot compilation TIMEOUT");
        AssertThat(File.Exists(StampPath)).IsFalse();
        AssertThat(File.Exists(JournalPath)).OverrideFailureMessage("the journal of a terminated and reaped editor is removed").IsFalse();
        var run = EditorRuns().Single();
        AssertThat(run.Ended).IsFalse();
        AssertThat(IsRunning(run.ProcessId)).OverrideFailureMessage("the owned editor is terminated").IsFalse();
    }

    [TestCase]
    public void CanceledEditorIsTerminatedAndPublishesNothing()
    {
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, "60000");
        using var cancellation = new CancellationTokenSource();
        var canceling = Task.Run(() =>
        {
            WaitUntil(() => EditorRuns().Count == 1, 20000);
            cancellation.Cancel();
        });

        AssertThat(SetUp(cancellationToken: cancellation.Token)).IsEqual(COMPILE_FAILURE);
        canceling.Wait();

        AssertLogged("Godot project compilation was canceled");
        AssertNotLogged("Godot compilation TIMEOUT");
        AssertThat(File.Exists(StampPath)).IsFalse();
        AssertThat(File.Exists(JournalPath)).IsFalse();
        AssertThat(IsRunning(EditorRuns().Single().ProcessId)).OverrideFailureMessage("the owned editor is terminated").IsFalse();
    }

    [TestCase]
    public void CancellationWhileWaitingForTheLockStartsNoEditor()
    {
        Directory.CreateDirectory(StateDirectory);
        using var heldLock = new FileStream(Path.Combine(StateDirectory, ProjectSetupCache.LOCK_FILE_NAME), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        using var cancellation = new CancellationTokenSource(500);

        AssertThat(SetUp(cancellationToken: cancellation.Token)).IsEqual("GdUnit4 runtime setup failed while waiting for the project setup lock.");

        AssertLogged("Canceled while waiting for GdUnit4 project setup lock");
        AssertThat(EditorRuns().Count).IsEqual(0);
    }

    [TestCase]
    public void InputChangedDuringPreparationPublishesNothing()
    {
        Environment.SetEnvironmentVariable(BEHAVIOR_VARIABLE, "touch-input");

        AssertThat(SetUp()).IsNull();

        AssertLogged($"{NOT_PUBLISHED} authored inputs changed during the preparation: project.tree");
        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase]
    public void MissingRequiredOutputPublishesNothing()
    {
        Environment.SetEnvironmentVariable(BEHAVIOR_VARIABLE, "skip-uid-cache");

        AssertThat(SetUp()).IsNull();

        AssertLogged($"{NOT_PUBLISHED} the UID cache '.godot/uid_cache.bin' is missing");
        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase]
    public void MissingImportResultPublishesNothing()
    {
        Prepare();
        File.WriteAllText(
            Path.Combine(ProjectRoot, "notes.md.import"),
            "[deps]\nsource_file=\"res://notes.md\"\ndest_files=[\"res://.godot/imported/notes.md-missing.res\"]\n");

        AssertThat(SetUp()).IsNull();

        AssertLogged($"{NOT_PUBLISHED} the import result '.godot/imported/notes.md-missing.res' of 'notes.md.import' is missing");
        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase]
    public void AbortedEditorScanPublishesNothing()
    {
        Environment.SetEnvironmentVariable(BEHAVIOR_VARIABLE, "abort-scan");

        AssertThat(SetUp()).IsNull();

        AssertLogged($"{NOT_PUBLISHED} the Godot editor quit before its file system scan finished");
        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase]
    public void DeniedPublicationPublishesNothing()
    {
        Environment.SetEnvironmentVariable(BEHAVIOR_VARIABLE, "block-stamp");

        AssertThat(SetUp()).IsNull();

        AssertLogged($"{NOT_PUBLISHED} the setup stamp could not be written");
        AssertThat(File.Exists(StampPath)).IsFalse();
        AssertThat(Directory.GetFiles(StateDirectory, "*.tmp").Length).OverrideFailureMessage("a failed publication leaves no temporary stamp").IsEqual(0);
    }

    [TestCase]
    public void DeniedJournalStartsNoEditor()
    {
        // a directory at the journal path can not be replaced by the journal file
        Directory.CreateDirectory(Path.Combine(JournalPath, "occupied"));

        AssertThat(SetUp()).IsEqual(COMPILE_FAILURE);

        AssertLogged("Unable to write GdUnit4 project setup state");
        AssertThat(EditorRuns().Count).OverrideFailureMessage("no editor starts without a durable journal").IsEqual(0);
        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase]
    public void UnsupportedProjectsAreAlwaysPreparedAndNeverStamped()
    {
        File.AppendAllText(Path.Combine(ProjectRoot, "project.godot"), "\n[editor_plugins]\n\nenabled=PackedStringArray(\"res://addons/tool/plugin.cfg\")\n");
        AssertIneligible("editor plugins are enabled");
        File.WriteAllText(Path.Combine(ProjectRoot, "project.godot"), ProjectSettings);

        File.WriteAllText(Path.Combine(ProjectRoot, "native.gdextension"), "[configuration]\n");
        AssertIneligible("the project declares the GDExtension 'native.gdextension'");
        File.Delete(Path.Combine(ProjectRoot, "native.gdextension"));

        File.WriteAllText(Path.Combine(ProjectRoot, "notes.md.import"), "[deps]\nsource_file=\"C:/outside/notes.md\"\ndest_files=[]\n");
        AssertIneligible("the import metadata 'notes.md.import' references 'C:/outside/notes.md' outside the project");
        File.WriteAllText(Path.Combine(ProjectRoot, "notes.md.import"), "[deps]\nsource_file=\"res://notes.md\"\ndest_files=[\"res://../outside.res\"]\n");
        AssertIneligible("the import metadata 'notes.md.import' references 'res://../outside.res' outside the project");
        File.Delete(Path.Combine(ProjectRoot, "notes.md.import"));

        Directory.CreateDirectory(Path.Combine(ProjectRoot, ".godot"));
        File.WriteAllText(Path.Combine(ProjectRoot, ".godot", "extension_list.cfg"), "res://native.gdextension\n");
        AssertIneligible("the project loads GDExtensions");
        File.Delete(Path.Combine(ProjectRoot, ".godot", "extension_list.cfg"));

        File.AppendAllText(Path.Combine(ProjectRoot, "project.godot"), "\n[application]\n\nconfig/use_hidden_project_data_directory=false\n");
        AssertIneligible("the project data directory is not hidden");
        File.WriteAllText(Path.Combine(ProjectRoot, "project.godot"), ProjectSettings);

        File.Delete(Path.Combine(ProjectRoot, ".godot", "mono", "temp", "bin", "Debug", "Fixture.dll"));
        AssertIneligible("the compiled game output");
    }

    [TestCase("res://C:/outside.res", TestName = "DriveRooted")]
    [TestCase("res://C:outside.res", TestName = "DriveRelative")]
    [TestCase("res://../outside.res", TestName = "Traversal")]
    [TestCase("res://nested/../../outside.res", TestName = "NestedTraversal")]
    [TestCase("res://nested/./outside.res", TestName = "CurrentDirectorySegment")]
    [TestCase("res:////server/share/outside.res", TestName = "UncAfterScheme")]
    [TestCase("//server/share/outside.res", TestName = "Unc")]
    [TestCase(@"\\server\share\outside.res", TestName = "UncBackslashes")]
    [TestCase(@"res://nested\..\..\outside.res", TestName = "BackslashTraversal")]
    [TestCase("C:/outside/notes.md", TestName = "AbsoluteDrivePath")]
    [TestCase("/outside/notes.md", TestName = "AbsoluteRootPath")]
    [TestCase("user://outside.res", TestName = "OtherScheme")]
    [TestCase("res://", TestName = "EmptyPath")]
    [TestCase("res://nested/", TestName = "EmptySegment")]
    [TestCase("res://notes.md:stream", TestName = "AlternateStream")]
    [TestCase("res://nested/outside.res.", TestName = "TrailingDot")]
    public void ImportPathLeavingTheProjectIsNeverStamped(string escapingPath)
        => AssertImportPathIsRejected(escapingPath);

    [TestCase]
    public void DriveRootedImportPathToAnExistingOutsideFileIsNeverStamped()
    {
        // without containment the missing-result check would look at this file outside the project and pass
        var outside = Path.Combine(TempRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "result.res"), "import result outside the project");

        AssertImportPathIsRejected("res://" + Path.Combine(outside, "result.res").Replace('\\', '/'));
    }

    [TestCase]
    public void NestedImportPathsInsideTheProjectStayEligible()
    {
        foreach (var directory in new[] { "assets/deep/v1.2", "C", "assets/name with spaces" })
            Directory.CreateDirectory(Path.Combine(ProjectRoot, directory));
        File.WriteAllText(Path.Combine(ProjectRoot, "assets", "deep", "v1.2", "tree.asset"), "tree");
        File.WriteAllText(Path.Combine(ProjectRoot, "C", "drive-like.asset"), "a directory named like a drive letter");
        File.WriteAllText(Path.Combine(ProjectRoot, "assets", "name with spaces", "words.csv"), "keys,en\nWORD,Word\n");

        Prepare();

        AssertThat(File.ReadAllText(Path.Combine(ProjectRoot, "assets", "deep", "v1.2", "tree.asset.import")))
            .Contains("source_file=\"res://assets/deep/v1.2/tree.asset\"");
        AssertThat(File.ReadAllText(Path.Combine(ProjectRoot, "C", "drive-like.asset.import")))
            .Contains("source_file=\"res://C/drive-like.asset\"");
        AssertThat(File.ReadAllText(Path.Combine(ProjectRoot, "assets", "name with spaces", "words.csv.import")))
            .Contains("dest_files=[\"res://assets/name with spaces/words.translation\"]");
        AssertHit();

        // the import result next to its nested source is tracked as a generated output
        File.WriteAllText(Path.Combine(ProjectRoot, "assets", "name with spaces", "words.translation"), "corrupt");
        AssertMiss("changed outputs: project.sidecars");
    }

    [TestCase]
    public void LinkedDirectoryInTheScannedTreeIsNeverStamped()
    {
        var outside = Path.Combine(TempRoot, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "external.txt"), "external resource graph");
        CreateDirectoryLink(Path.Combine(ProjectRoot, "linked"), outside);

        AssertIneligible("the scanned directory 'linked' is a link");
    }

    [TestCase]
    public void DifferentLogRootsShareOneLockAndStamp()
    {
        AssertThat(SetUp(logFileRoot: "logs/first")).IsNull();
        AssertLogged(PUBLISHED);
        AssertLogged("Acquired GdUnit4 project setup lock after");
        AssertLogged(Path.Combine(StateDirectory, ProjectSetupCache.LOCK_FILE_NAME));

        Logger.Clear();
        AssertThat(SetUp(logFileRoot: Path.Combine(TempRoot, "absolute-log-root"))).IsNull();

        AssertLogged(HIT);
        AssertLogged(Path.Combine(StateDirectory, ProjectSetupCache.LOCK_FILE_NAME));
        AssertThat(EditorRuns().Count).IsEqual(1);
        AssertThat(Directory.GetFiles(TempRoot, "gdunit4-setup.lock", SearchOption.AllDirectories).Length).IsEqual(0);
    }

    [TestCase]
    public void DifferentWorktreesDoNotShareState()
    {
        var otherRoot = CreateProject("other-worktree");
        Prepare();

        AssertThat(SetUp(projectRoot: otherRoot)).IsNull();
        AssertLogged($"{REQUIRED} no setup stamp exists");
        AssertThat(EditorRuns().Count).IsEqual(2);

        // a stamp copied with its worktree never validates for the copy
        File.Copy(StampPath, Path.Combine(otherRoot, ".godot", "gdunit4", ProjectSetupCache.STAMP_FILE_NAME), true);
        Logger.Clear();
        AssertThat(SetUp(projectRoot: otherRoot)).IsNull();
        AssertLogged("project.root");
        AssertLogged(PUBLISHED);
        AssertThat(EditorRuns().Count).IsEqual(3);

        AssertHit();
    }

    [TestCase]
    public void InstrumentationEnvironmentNeitherBypassesNorFragmentsTheCache()
    {
        Environment.SetEnvironmentVariable(PROFILING_VARIABLE, "1");
        Environment.SetEnvironmentVariable(COVERAGE_SESSION_VARIABLE, "gdunit4-setup-test");
        AssertThat(SetUp()).IsNull();
        AssertLogged(PUBLISHED);
        AssertThat(File.ReadAllText(TracePath)).OverrideFailureMessage("the editor process inherits the current environment").Contains("profiling=1");
        AssertHit();

        Environment.SetEnvironmentVariable(PROFILING_VARIABLE, null);
        Environment.SetEnvironmentVariable(COVERAGE_SESSION_VARIABLE, null);
        AssertHit();

        Environment.SetEnvironmentVariable(PROFILING_VARIABLE, "1");
        AssertHit();
        AssertThat(EditorRuns().Count).IsEqual(1);
    }

    [TestCase(Timeout = 180000)]
    public void EightConcurrentProcessesPrepareOnceAndSevenHit()
    {
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, "1500");

        var cold = RunOwners(8, true);

        AssertThat(cold.All(owner => owner.ExitCode == 0)).OverrideFailureMessage(Describe(cold)).IsTrue();
        AssertThat(EditorRuns().Count).OverrideFailureMessage(Describe(cold)).IsEqual(1);
        AssertThat(cold.Count(owner => owner.Output.Contains(PUBLISHED))).IsEqual(1);
        AssertThat(cold.Count(owner => owner.Output.Contains(HIT))).IsEqual(7);

        var warm = RunOwners(8, true);

        AssertThat(warm.All(owner => owner.ExitCode == 0)).OverrideFailureMessage(Describe(warm)).IsTrue();
        AssertThat(EditorRuns().Count).OverrideFailureMessage(Describe(warm)).IsEqual(1);
        AssertThat(warm.Count(owner => owner.Output.Contains(HIT))).IsEqual(8);
    }

    [TestCase(Timeout = 180000)]
    public void EightConcurrentUncachedProcessesNeverOverlapTheirEditors()
    {
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, "300");

        var owners = RunOwners(8, false);

        AssertThat(owners.All(owner => owner.ExitCode == 0)).OverrideFailureMessage(Describe(owners)).IsTrue();
        var runs = EditorRuns();
        AssertThat(runs.Count).IsEqual(8);
        AssertThat(runs.All(run => run.Ended)).IsTrue();
        AssertNoOverlap(runs);
        AssertThat(File.Exists(StampPath)).IsFalse();
    }

    [TestCase(Timeout = 120000)]
    public void KilledOwnerLeavesAnOrphanThatTheNextRunnerWaitsForBeforePreparingAgain()
    {
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, "4000");
        var owner = StartOwner("logs/owner", true);
        WaitUntil(() => File.Exists(JournalPath) && File.ReadAllText(JournalPath).Contains("editor-started"), 30000);
        AssertThat(File.ReadAllText(JournalPath)).Contains("editor-started");
        owner.Kill();
        owner.WaitForExit(10000);
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, null);

        AssertThat(SetUp()).IsNull();

        AssertLogged("of an interrupted GdUnit4 project setup to exit");
        AssertLogged("Discarding the interrupted GdUnit4 project setup");
        AssertLogged(PUBLISHED);
        var runs = EditorRuns();
        AssertThat(runs.Count).IsEqual(2);
        AssertThat(runs.All(run => run.Ended)).OverrideFailureMessage("the orphan finishes on its own, it is never killed").IsTrue();
        AssertNoOverlap(runs);
        AssertThat(File.Exists(JournalPath)).IsFalse();
        AssertHit();
    }

    [TestCase]
    public void VerifiedOrphanThatOutlivesTheTimeoutFailsClosedAndIsNotKilled()
    {
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, "60000");
        var orphan = StartProcess($"--path \"{ProjectRoot}\" -e --headless");
        WaitUntil(() => EditorRuns().Count == 1, 20000);
        WriteJournal("editor-started", orphan.Id, orphan.StartTime.ToUniversalTime().Ticks, orphan.MainModule!.FileName);
        Environment.SetEnvironmentVariable(DELAY_VARIABLE, null);

        AssertThat(SetUp(compileProcessTimeout: 1000)).IsEqual(RECOVERY_FAILURE);

        AssertLogged("it is still running after 1000ms");
        AssertLogged("ACTION REQUIRED");
        AssertThat(orphan.HasExited).OverrideFailureMessage("a recorded editor is only waited for, never killed").IsFalse();
        AssertThat(File.Exists(JournalPath)).IsTrue();
        AssertThat(EditorRuns().Count).IsEqual(1);
    }

    [TestCase]
    public void LaunchIntentWithoutEditorIdentityFailsClosed()
    {
        WriteJournal("launch-intent", 0, 0, string.Empty);

        AssertThat(SetUp()).IsEqual(RECOVERY_FAILURE);

        AssertLogged("was interrupted before its Godot editor process was identified");
        AssertLogged($"delete '{JournalPath}'");
        AssertThat(File.Exists(JournalPath)).OverrideFailureMessage("no project data is deleted automatically").IsTrue();
        AssertThat(EditorRuns().Count).IsEqual(0);

        // the documented remediation
        File.Delete(JournalPath);
        Logger.Clear();
        AssertThat(SetUp()).IsNull();
        AssertLogged(PUBLISHED);
    }

    [TestCase]
    public void MalformedJournalFailsClosed()
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(JournalPath, "{\"schema\":1,\"phase\":\"editor-st");

        AssertThat(SetUp()).IsEqual(RECOVERY_FAILURE);

        AssertThat(EditorRuns().Count).IsEqual(0);
    }

    [TestCase]
    public void JournalOfAnExitedEditorIsDiscardedAndTheProjectPreparedAgain()
    {
        Prepare();
        WriteJournal("editor-started", FindExitedProcessId(), 1, HostExecutable);

        AssertThat(SetUp()).IsNull();

        AssertLogged("Discarding the interrupted GdUnit4 project setup");
        AssertLogged($"{REQUIRED} no setup stamp exists");
        AssertLogged(PUBLISHED);
        AssertThat(EditorRuns().Count).OverrideFailureMessage("output of an interrupted setup is never trusted").IsEqual(2);
        AssertThat(File.Exists(JournalPath)).IsFalse();
    }

    [TestCase]
    public void ReusedProcessIdIsNotMistakenForTheEditor()
    {
        using var current = Process.GetCurrentProcess();
        WriteJournal("editor-started", current.Id, current.StartTime.ToUniversalTime().Ticks - TimeSpan.FromHours(1).Ticks, current.MainModule!.FileName);

        AssertThat(SetUp()).IsNull();

        AssertLogged("Discarding the interrupted GdUnit4 project setup");
        AssertLogged(PUBLISHED);
    }

    [TestCase]
    public void AmbiguousEditorIdentityFailsClosed()
    {
        using var current = Process.GetCurrentProcess();
        WriteJournal("editor-started", current.Id, current.StartTime.ToUniversalTime().Ticks, Path.Combine(TempRoot, "another-godot.exe"));

        AssertThat(SetUp()).IsEqual(RECOVERY_FAILURE);

        AssertLogged("its identity could not be verified");
        AssertThat(EditorRuns().Count).IsEqual(0);
        AssertThat(File.Exists(JournalPath)).IsTrue();
    }

    #region Helper Methods

    private const string ProjectSettings =
        """
        config_version=5

        [application]

        config/name="Fixture"

        [dotnet]

        project/assembly_name="Fixture"

        """;

    private static string HostExecutable => Path.Combine(HostDirectory, OperatingSystem.IsWindows() ? "GdUnit4ApiTestHost.exe" : "GdUnit4ApiTestHost");

    private string CreateProject(string name)
    {
        var root = Path.Combine(TempRoot, name);
        foreach (var directory in new[] { "translations", "ignored", ".hidden", "nested", ".godot/mono/temp/bin/Debug" })
            Directory.CreateDirectory(Path.Combine(root, directory));

        File.WriteAllText(Path.Combine(root, "project.godot"), ProjectSettings);
        File.WriteAllText(Path.Combine(root, "icon.asset"), "icon");
        File.WriteAllText(Path.Combine(root, "Main.cs"), "public partial class Main;");
        File.WriteAllText(Path.Combine(root, "notes.md"), "authored notes");
        File.WriteAllText(Path.Combine(root, "translations", "strings.csv"), "keys,en\nHELLO,Hello\n");
        File.WriteAllText(Path.Combine(root, "ignored", ".gdignore"), string.Empty);
        File.WriteAllText(Path.Combine(root, "ignored", "data.txt"), "ignored");
        File.WriteAllText(Path.Combine(root, ".hidden", "data.txt"), "hidden");
        File.WriteAllText(Path.Combine(root, "nested", "project.godot"), ProjectSettings);
        File.WriteAllText(Path.Combine(root, "nested", "data.txt"), "nested project");
        File.WriteAllText(Path.Combine(root, ".godot", "mono", "temp", "bin", "Debug", "Fixture.dll"), "compiled assembly");

        // the generated runner is installed up front, so the setup never needs to build the fixture
        CreateRunner(new TestEngineSettings(), root).InstallTestRunnerClasses(root, false);
        return root;
    }

    private GodotRuntimeTestRunner CreateRunner(TestEngineSettings settings, string projectRoot)
        => new(Logger, Mock.Of<IDebuggerFramework>(), settings, "Fixture.Tests.dll", projectRoot);

    private string? SetUp(
        bool projectSetupCache = true,
        string? projectRoot = null,
        string logFileRoot = "tmp/gdunit-runs",
        int compileProcessTimeout = 30000,
        string? godotBinary = null,
        string adapterIdentity = "adapter-1",
        string? runnerSceneDirectory = null,
        CancellationToken cancellationToken = default)
    {
        var settings = new TestEngineSettings
        {
            CompileProcessTimeout = compileProcessTimeout,
            LogFileRoot = logFileRoot,
            ProjectSetupCache = projectSetupCache,
            TestAdapterIdentity = adapterIdentity,
            RunnerSceneDirectory = runnerSceneDirectory ?? GodotRuntimeTestRunner.TEMP_TEST_RUNNER_DIR
        };
        var root = projectRoot ?? ProjectRoot;
        return CreateRunner(settings, root).SetUpGodotProject(root, godotBinary ?? HostExecutable, null, null, cancellationToken);
    }

    private void Prepare()
    {
        AssertThat(SetUp()).IsNull();
        AssertLogged(PUBLISHED);
        AssertThat(File.Exists(StampPath)).IsTrue();
        Logger.Clear();
    }

    private void AssertHit(string? godotBinary = null, string adapterIdentity = "adapter-1")
    {
        var runs = EditorRuns().Count;
        Logger.Clear();
        AssertThat(SetUp(godotBinary: godotBinary, adapterIdentity: adapterIdentity)).IsNull();
        AssertLogged(HIT);
        AssertThat(EditorRuns().Count).OverrideFailureMessage($"a hit starts no editor\n{Logger}").IsEqual(runs);
    }

    private void AssertMiss(string reason, string? godotBinary = null, string adapterIdentity = "adapter-1")
    {
        var runs = EditorRuns().Count;
        Logger.Clear();
        AssertThat(SetUp(godotBinary: godotBinary, adapterIdentity: adapterIdentity)).IsNull();
        AssertLogged($"{REQUIRED} {reason}");
        AssertLogged(PUBLISHED);
        AssertThat(EditorRuns().Count).OverrideFailureMessage($"a miss prepares the project once\n{Logger}").IsEqual(runs + 1);
    }

    private void AssertIneligible(string reason)
    {
        var runs = EditorRuns().Count;
        Logger.Clear();
        AssertThat(SetUp()).IsNull();
        AssertLogged($"{REQUIRED} {reason}");
        AssertLogged($"{NOT_PUBLISHED} {reason}");
        AssertThat(File.Exists(StampPath)).IsFalse();
        AssertThat(EditorRuns().Count).OverrideFailureMessage($"an unsupported project is always prepared\n{Logger}").IsEqual(runs + 1);
    }

    private void AssertLogged(string fragment)
        => AssertThat(Logger.Contains(fragment)).OverrideFailureMessage($"Expected a log message containing '{fragment}' in:\n{Logger}").IsTrue();

    private void AssertNotLogged(string fragment)
        => AssertThat(Logger.Contains(fragment)).OverrideFailureMessage($"Expected no log message containing '{fragment}' in:\n{Logger}").IsFalse();

    private static void AssertNoOverlap(IReadOnlyList<EditorRun> runs)
    {
        var ordered = runs.OrderBy(run => run.Started).ToList();
        for (var index = 1; index < ordered.Count; index++)
            AssertThat(ordered[index].Started >= ordered[index - 1].Finished)
                .OverrideFailureMessage($"editor {ordered[index].ProcessId} started before editor {ordered[index - 1].ProcessId} finished")
                .IsTrue();
    }

    private List<EditorRun> EditorRuns()
    {
        if (!File.Exists(TracePath))
            return [];

        // process ids are reused, so an end line closes the newest open run of its process id
        var runs = new List<EditorRun>();
        foreach (var line in ReadSharedLines(TracePath))
        {
            var fields = line.Split(' ');
            var processId = int.Parse(fields[1]);
            var timestamp = long.Parse(fields[2]);
            if (fields[0] == "start")
            {
                runs.Add(new EditorRun(processId, timestamp, long.MaxValue, false));
                continue;
            }

            var index = runs.FindLastIndex(run => run.ProcessId == processId && !run.Ended);
            runs[index] = runs[index] with { Finished = timestamp, Ended = true };
        }

        return runs;
    }

    private static List<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    private Process StartProcess(string arguments)
    {
        var process = Process.Start(new ProcessStartInfo(HostExecutable, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        StartedProcesses.Add(process);
        return process;
    }

    private Process StartOwner(string logFileRoot, bool projectSetupCache)
        => StartProcess($"setup \"{ProjectRoot}\" \"{logFileRoot}\" {projectSetupCache} 60000");

    private List<(int ExitCode, string Output)> RunOwners(int count, bool projectSetupCache)
    {
        var owners = Enumerable.Range(0, count)
            .Select(index => StartOwner($"logs/owner-{index}", projectSetupCache))
            .Select(owner => (Process: owner, Output: owner.StandardOutput.ReadToEndAsync(), Error: owner.StandardError.ReadToEndAsync()))
            .ToList();
        return owners.ConvertAll(owner =>
        {
            owner.Process.WaitForExit();
            return (owner.Process.ExitCode, $"{owner.Output.Result}{owner.Error.Result}");
        });
    }

    private static string Describe(IEnumerable<(int ExitCode, string Output)> owners)
        => string.Join("\n----\n", owners.Select(owner => $"exit code {owner.ExitCode}\n{owner.Output}"));

    /// <summary>
    ///     Asserts that the path makes the project ineligible both as an import source and as an import result
    /// </summary>
    private void AssertImportPathIsRejected(string escapingPath)
    {
        var reason = $"the import metadata 'notes.md.import' references '{escapingPath}' outside the project";

        WriteImportMetadata($"\"{escapingPath}\"", "[]");
        AssertIneligible(reason);

        WriteImportMetadata("\"res://notes.md\"", $"[\"{escapingPath}\"]");
        AssertIneligible(reason);
    }

    private void WriteImportMetadata(string sourceFile, string destFiles)
        => File.WriteAllText(Path.Combine(ProjectRoot, "notes.md.import"), $"[deps]\n\nsource_file={sourceFile}\ndest_files={destFiles}\n");

    private void WriteJournal(string phase, int editorProcessId, long editorStartTimeUtcTicks, string editorExecutablePath)
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(
            JournalPath,
            new JsonObject
            {
                ["schema"] = 1,
                ["phase"] = phase,
                ["ownerProcessId"] = 4242,
                ["editorProcessId"] = editorProcessId,
                ["editorStartTimeUtcTicks"] = editorStartTimeUtcTicks,
                ["editorExecutablePath"] = editorExecutablePath
            }.ToJsonString());
    }

    private string CopyHost(string name)
    {
        var target = Path.Combine(TempRoot, name);
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(HostDirectory))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        return Path.Combine(target, Path.GetFileName(HostExecutable));
    }

    private static void CreateDirectoryLink(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(linkPath, targetPath);
            return;
        }

        // junctions need no symbolic link privilege
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{linkPath}\" \"{targetPath}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        process.WaitForExit();
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static int FindExitedProcessId()
    {
        for (var processId = 999_996; processId > 0; processId -= 4)
        {
            if (!IsRunning(processId))
                return processId;
        }

        throw new InvalidOperationException("No unused process id found");
    }

    private static void WaitUntil(Func<bool> condition, int timeoutMs)
    {
        var stopwatch = Stopwatch.StartNew();
        while (!condition() && stopwatch.ElapsedMilliseconds < timeoutMs)
            Thread.Sleep(25);
    }

    private static bool TryDeleteDirectory(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private sealed record EditorRun(int ProcessId, long Started, long Finished, bool Ended);

    private sealed class CapturingLogger : ITestEngineLogger
    {
        private List<string> Messages { get; } = [];

        public void SendMessage(LogLevel logLevel, string message)
        {
            lock (Messages)
                Messages.Add($"{logLevel}: {message}");
        }

        public void Clear()
        {
            lock (Messages)
                Messages.Clear();
        }

        public bool Contains(string fragment)
        {
            lock (Messages)
                return Messages.Any(message => message.Contains(fragment, StringComparison.Ordinal));
        }

        public override string ToString()
        {
            lock (Messages)
                return string.Join('\n', Messages);
        }
    }

    #endregion
}
