// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Runners;

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
///     Content fingerprint of the state a Godot editor preparation consumes and produces.
/// </summary>
/// <remarks>
///     <para>
///         Inputs are the authored state the preparation depends on: project settings, the scanner-visible project
///         tree, the engine, the executing gdUnit4 assemblies, the generated runner and the compiled game output.
///         Outputs are the state the editor generates and the test runtime reads: the UID cache, the imported
///         resources, the <c>.import</c> and <c>.uid</c> sidecars with their in-tree import results and the optional
///         global class, scene group and extension caches.
///     </para>
///     <para>
///         Every component is a SHA-256 digest over ordinally sorted relative paths, file lengths and file bytes.
///         Timestamps are never read. Projects whose scan graph can not be captured safely are reported through
///         <see cref="IneligibilityReason" /> and are never reused.
///     </para>
/// </remarks>
internal sealed partial class ProjectSetupFingerprint
{
    private const string PROJECT_FILE = "project.godot";
    private const string PROJECT_DATA_DIRECTORY = ".godot";
    private const string SCAN_IGNORE_MARKER = ".gdignore";
    private const string IMPORT_SIDECAR_EXTENSION = ".import";
    private const string UID_SIDECAR_EXTENSION = ".uid";
    private const string EXTENSION_MANIFEST_EXTENSION = ".gdextension";
    private const string RESOURCE_SCHEME = "res://";
    private const string IMPORTED_RESOURCE_PREFIX = ".godot/imported/";
    private const string ABSENT = "absent";

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private ProjectSetupFingerprint(SortedDictionary<string, string> inputs, SortedDictionary<string, string> outputs, string? ineligibilityReason, string? missingOutputReason)
    {
        Inputs = inputs;
        Outputs = outputs;
        IneligibilityReason = ineligibilityReason;
        MissingOutputReason = missingOutputReason;
    }

    /// <summary>
    ///     Gets the authored input components by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Inputs { get; }

    /// <summary>
    ///     Gets the generated output components by name.
    /// </summary>
    internal IReadOnlyDictionary<string, string> Outputs { get; }

    /// <summary>
    ///     Gets the reason the project state can not be reused, or null when it is eligible.
    /// </summary>
    internal string? IneligibilityReason { get; }

    /// <summary>
    ///     Gets the generated output a completed preparation must have produced but did not, or null.
    /// </summary>
    internal string? MissingOutputReason { get; }

    /// <summary>
    ///     Captures the fingerprint of the current project state.
    /// </summary>
    /// <param name="projectRoot">The Godot project root.</param>
    /// <param name="godotBinary">The Godot executable used for the preparation.</param>
    /// <param name="runnerSourcePath">The installed generated runner source file.</param>
    /// <param name="hostIdentity">Input components supplied by the runner, such as the preparation command.</param>
    /// <returns>The captured fingerprint; unreadable project state is reported as ineligible instead of thrown.</returns>
    internal static ProjectSetupFingerprint Capture(string projectRoot, string godotBinary, string runnerSourcePath, IReadOnlyDictionary<string, string> hostIdentity)
    {
        var inputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var outputs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? missingOutputReason = null;
        try
        {
            var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var (name, value) in hostIdentity)
                inputs[name] = value;
            inputs["gdunit.api"] = DescribeAssembly(typeof(ProjectSetupFingerprint).Assembly);
            inputs["project.root"] = NormalizePathIdentity(root);

            var ineligibilityReason = CaptureProjectSettings(root, inputs)
                                      ?? CaptureEngine(godotBinary, inputs)
                                      ?? CaptureRunner(root, runnerSourcePath, inputs)
                                      ?? CaptureGameOutput(root, inputs)
                                      ?? CaptureProjectTree(root, inputs, outputs, ref missingOutputReason)
                                      ?? CaptureProjectData(root, outputs, ref missingOutputReason);
            return new ProjectSetupFingerprint(inputs, outputs, ineligibilityReason, missingOutputReason);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ProjectSetupFingerprint(inputs, outputs, $"the project state is not readable ({e.Message})", missingOutputReason);
        }
        catch (AggregateException e) when (e.InnerExceptions.All(inner => inner is IOException or UnauthorizedAccessException))
        {
            return new ProjectSetupFingerprint(inputs, outputs, $"the project state is not readable ({e.InnerExceptions[0].Message})", missingOutputReason);
        }
    }

    /// <summary>
    ///     Describes the module identity of an executing assembly.
    /// </summary>
    /// <param name="assembly">The assembly to describe.</param>
    /// <returns>The assembly name, informational version and module version id.</returns>
    internal static string DescribeAssembly(Assembly assembly)
    {
        var version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? assembly.GetName().Version?.ToString();
        return $"{assembly.GetName().Name} {version} mvid={assembly.ManifestModule.ModuleVersionId:N}";
    }

    /// <summary>
    ///     Lists the components whose values differ between two captures.
    /// </summary>
    /// <param name="expected">The recorded components.</param>
    /// <param name="actual">The current components.</param>
    /// <returns>The ordinally sorted names of added, removed and changed components.</returns>
    internal static IReadOnlyList<string> Difference(IReadOnlyDictionary<string, string> expected, IReadOnlyDictionary<string, string> actual)
        => [.. expected.Keys
            .Union(actual.Keys, StringComparer.Ordinal)
            .Where(name => !expected.TryGetValue(name, out var expectedValue)
                           || !actual.TryGetValue(name, out var actualValue)
                           || !string.Equals(expectedValue, actualValue, StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)];

    private static string? CaptureProjectSettings(string root, SortedDictionary<string, string> inputs)
    {
        var projectFilePath = Path.Combine(root, PROJECT_FILE);
        if (!File.Exists(projectFilePath))
            return $"{PROJECT_FILE} is missing";

        inputs["project.settings"] = DescribeFile(projectFilePath);

        var section = string.Empty;
        foreach (var rawLine in ReadLines(projectFilePath))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line[1..^1];
                continue;
            }

            var separatorIndex = line.IndexOf('=', StringComparison.Ordinal);
            if (separatorIndex < 0)
                continue;

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            if (section == "editor_plugins" && key == "enabled" && value != "PackedStringArray()")
                return "editor plugins are enabled";
            if (section == "application" && key == "config/use_hidden_project_data_directory" && value != "true")
                return "the project data directory is not hidden";
        }

        return null;
    }

    private static string? CaptureEngine(string godotBinary, SortedDictionary<string, string> inputs)
    {
        var executablePath = Path.GetFullPath(godotBinary);
        if (!File.Exists(executablePath))
            return $"the Godot executable '{executablePath}' is missing";

        inputs["engine.executable"] = $"{NormalizePathIdentity(executablePath)} {DescribeFile(executablePath)}";

        // the editor process loads the files next to its executable, such as native libraries or the real editor
        // behind a console wrapper, and the managed GodotSharp tree
        var engineDirectory = Path.GetDirectoryName(executablePath)!;
        var companions = Directory.EnumerateFiles(engineDirectory)
            .Where(path => !string.Equals(path, executablePath, PathComparison))
            .ToList();
        var managedDirectory = Path.Combine(engineDirectory, "GodotSharp");
        if (Directory.Exists(managedDirectory))
            companions.AddRange(Directory.EnumerateFiles(managedDirectory, "*", SearchOption.AllDirectories));
        inputs["engine.companions"] = DescribeTree(HashFiles(engineDirectory, companions).Values);
        return null;
    }

    private static string? CaptureRunner(string root, string runnerSourcePath, SortedDictionary<string, string> inputs)
    {
        var sourcePath = Path.GetFullPath(runnerSourcePath);
        if (!File.Exists(sourcePath))
            return $"the generated runner '{sourcePath}' is missing";

        inputs["runner.source"] = $"{ToRelativePath(root, sourcePath)} {DescribeFile(sourcePath)}";
        return null;
    }

    private static string? CaptureGameOutput(string root, SortedDictionary<string, string> inputs)
    {
        var outputDirectory = Path.Combine(root, PROJECT_DATA_DIRECTORY, "mono", "temp", "bin", "Debug");
        var outputFiles = Directory.Exists(outputDirectory)
            ? Directory.EnumerateFiles(outputDirectory, "*", SearchOption.AllDirectories).ToList()
            : [];
        if (outputFiles.Count == 0)
            return $"the compiled game output '{outputDirectory}' is missing";

        inputs["game.output"] = DescribeTree(HashFiles(outputDirectory, outputFiles).Values);
        return null;
    }

    private static string? CaptureProjectTree(string root, SortedDictionary<string, string> inputs, SortedDictionary<string, string> outputs, ref string? missingOutputReason)
    {
        var visibleFiles = new List<string>();
        var ineligibilityReason = CollectVisibleFiles(root, root, visibleFiles);
        if (ineligibilityReason != null)
            return ineligibilityReason;

        var extensionManifest = visibleFiles.Find(path => path.EndsWith(EXTENSION_MANIFEST_EXTENSION, StringComparison.OrdinalIgnoreCase));
        if (extensionManifest != null)
            return $"the project declares the GDExtension '{ToRelativePath(root, extensionManifest)}'";

        // import results written next to their source, such as translations, are generated outputs like the sidecars
        var importResults = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sidecarPath in visibleFiles.Where(path => path.EndsWith(IMPORT_SIDECAR_EXTENSION, StringComparison.Ordinal)))
        {
            var hasSource = File.Exists(sidecarPath[..^IMPORT_SIDECAR_EXTENSION.Length]);
            foreach (var (key, resourcePath) in ReadImportPaths(sidecarPath))
            {
                var projectPath = ToProjectPath(root, resourcePath);
                if (projectPath == null)
                    return $"the import metadata '{ToRelativePath(root, sidecarPath)}' references '{resourcePath}' outside the project";

                if (key != "dest_files")
                    continue;

                if (!projectPath.StartsWith(IMPORTED_RESOURCE_PREFIX, StringComparison.Ordinal))
                    _ = importResults.Add(projectPath);
                if (hasSource && !File.Exists(Path.Combine(root, projectPath)))
                    missingOutputReason ??= $"the import result '{projectPath}' of '{ToRelativePath(root, sidecarPath)}' is missing";
            }
        }

        var authored = new List<string>();
        var generated = new List<string>();
        foreach (var (relativePath, entry) in HashFiles(root, visibleFiles))
        {
            var isGenerated = relativePath.EndsWith(IMPORT_SIDECAR_EXTENSION, StringComparison.Ordinal)
                              || relativePath.EndsWith(UID_SIDECAR_EXTENSION, StringComparison.Ordinal)
                              || importResults.Contains(relativePath);
            (isGenerated ? generated : authored).Add(entry);
        }

        inputs["project.tree"] = DescribeTree(authored);
        outputs["project.sidecars"] = DescribeTree(generated);
        return null;
    }

    private static string? CaptureProjectData(string root, SortedDictionary<string, string> outputs, ref string? missingOutputReason)
    {
        var dataDirectory = Path.Combine(root, PROJECT_DATA_DIRECTORY);
        var uidCachePath = Path.Combine(dataDirectory, "uid_cache.bin");
        outputs["godot.uid_cache"] = DescribeOptionalFile(uidCachePath);
        if (!File.Exists(uidCachePath))
            missingOutputReason ??= "the UID cache '.godot/uid_cache.bin' is missing";

        var importedDirectory = Path.Combine(dataDirectory, "imported");
        outputs["godot.imported"] = Directory.Exists(importedDirectory)
            ? DescribeTree(HashFiles(importedDirectory, [.. Directory.EnumerateFiles(importedDirectory, "*", SearchOption.AllDirectories)]).Values)
            : ABSENT;

        outputs["godot.global_script_class_cache"] = DescribeOptionalFile(Path.Combine(dataDirectory, "global_script_class_cache.cfg"));
        outputs["godot.scene_groups_cache"] = DescribeOptionalFile(Path.Combine(dataDirectory, "scene_groups_cache.cfg"));

        var extensionListPath = Path.Combine(dataDirectory, "extension_list.cfg");
        outputs["godot.extension_list"] = DescribeOptionalFile(extensionListPath);
        return File.Exists(extensionListPath) && ReadLines(extensionListPath).Any(line => !string.IsNullOrWhiteSpace(line))
            ? "the project loads GDExtensions"
            : null;
    }

    /// <summary>
    ///     Collects the files the editor file system scan can see, using a superset of its rules: directories starting
    ///     with a dot, holding a scan-ignore marker or holding a nested project are skipped, every file is kept.
    /// </summary>
    private static string? CollectVisibleFiles(string root, string directory, List<string> visibleFiles)
    {
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo)
            {
                if (entry.Name.StartsWith('.')
                    || File.Exists(Path.Combine(entry.FullName, SCAN_IGNORE_MARKER))
                    || File.Exists(Path.Combine(entry.FullName, PROJECT_FILE)))
                    continue;

                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    return $"the scanned directory '{ToRelativePath(root, entry.FullName)}' is a link";

                var ineligibilityReason = CollectVisibleFiles(root, entry.FullName, visibleFiles);
                if (ineligibilityReason != null)
                    return ineligibilityReason;
                continue;
            }

            if (entry.LinkTarget != null)
            {
                var target = entry.ResolveLinkTarget(true);
                if (target is not { Exists: true } || !Path.GetFullPath(target.FullName).StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
                    return $"the scanned file '{ToRelativePath(root, entry.FullName)}' links outside the project";
            }

            visibleFiles.Add(entry.FullName);
        }

        return null;
    }

    private static IEnumerable<(string Key, string ResourcePath)> ReadImportPaths(string sidecarPath)
    {
        foreach (var rawLine in ReadLines(sidecarPath))
        {
            var line = rawLine.Trim();
            var key = line.StartsWith("source_file=", StringComparison.Ordinal) ? "source_file"
                : line.StartsWith("dest_files=", StringComparison.Ordinal) ? "dest_files"
                : null;
            if (key == null)
                continue;

            foreach (Match match in QuotedValuePattern().Matches(line[(key.Length + 1)..]))
                yield return (key, match.Groups[1].Value);
        }
    }

    /// <summary>
    ///     Converts a resource path to its project-relative path, or null unless it provably names a location below
    ///     the project root.
    /// </summary>
    /// <remarks>
    ///     Only plain <c>res://a/b/c</c> paths are accepted. Empty, current and parent directory segments, drive and
    ///     stream separators, backslashes, control characters and segments the file system would trim are rejected
    ///     before the path is resolved, so <c>res://C:/x</c>, <c>res://C:x</c>, <c>res://../x</c> and UNC shapes never
    ///     reach the file system. The resolved path must then still be the very same path below the root.
    /// </remarks>
    private static string? ToProjectPath(string root, string resourcePath)
    {
        if (!resourcePath.StartsWith(RESOURCE_SCHEME, StringComparison.Ordinal))
            return null;

        var projectPath = resourcePath[RESOURCE_SCHEME.Length..];
        if (projectPath.Split('/').Any(segment => segment is "" or "." or ".."
                                                  || segment[^1] is '.' or ' '
                                                  || segment.Any(character => character is ':' or '\\' || char.IsControl(character))))
            return null;

        try
        {
            var resolvedPath = Path.GetFullPath(Path.Combine(root, projectPath));
            return resolvedPath.StartsWith(root + Path.DirectorySeparatorChar, PathComparison)
                   && string.Equals(ToRelativePath(root, resolvedPath), projectPath, StringComparison.Ordinal)
                ? projectPath
                : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static SortedDictionary<string, string> HashFiles(string baseDirectory, IReadOnlyCollection<string> filePaths)
    {
        var entries = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        _ = Parallel.ForEach(
            filePaths,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            filePath =>
            {
                var relativePath = ToRelativePath(baseDirectory, filePath);
                entries[relativePath] = $"{relativePath}\0{DescribeFile(filePath)}\n";
            });
        return new SortedDictionary<string, string>(entries, StringComparer.Ordinal);
    }

    private static string DescribeTree(IReadOnlyCollection<string> entries)
    {
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var entry in entries)
            digest.AppendData(Encoding.UTF8.GetBytes(entry));
        return $"sha256:{Convert.ToHexString(digest.GetHashAndReset())} files={entries.Count}";
    }

    private static string DescribeOptionalFile(string filePath) => File.Exists(filePath) ? DescribeFile(filePath) : ABSENT;

    private static string DescribeFile(string filePath)
    {
        using var stream = OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return $"sha256:{Convert.ToHexString(hash)} bytes={stream.Position}";
    }

    private static List<string> ReadLines(string filePath)
    {
        using var reader = new StreamReader(OpenRead(filePath), Encoding.UTF8);
        var lines = new List<string>();
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    private static FileStream OpenRead(string filePath)
        => new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.SequentialScan);

    private static string ToRelativePath(string baseDirectory, string path) => Path.GetRelativePath(baseDirectory, path).Replace('\\', '/');

    private static string NormalizePathIdentity(string path) => OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    [GeneratedRegex("\"((?:[^\"\\\\]|\\\\.)*)\"", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedValuePattern();
}
