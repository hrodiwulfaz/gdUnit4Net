// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.TestAdapter.Settings;

using System.Xml;
using System.Xml.Serialization;

using Microsoft.VisualStudio.TestPlatform.ObjectModel;

/// <summary>
///     Specifies how test case names are displayed in test results and Test Explorer.
/// </summary>
public enum DisplayNameOptions
{
    /// <summary>
    ///     Display only the simple method name (e.g., "TestMethod").
    /// </summary>
    SimpleName,

    /// <summary>
    ///     Display the fully qualified name including namespace, class, and method (e.g., "MyNamespace.MyClass.TestMethod").
    /// </summary>
    FullyQualifiedName
}

/// <summary>
///     Configuration settings for GdUnit4 test adapter that can be specified in .runsettings files.
///     This class extends VSTest's TestRunSettings to provide GdUnit4-specific configuration options
///     for test execution, display formatting, and Godot engine integration.
/// </summary>
/// <remarks>
///     These settings can be configured in a .runsettings file using the following format:
///     <code>
/// &lt;RunSettings&gt;
///   &lt;GdUnit4&gt;
///     &lt;DisplayName&gt;SimpleName&lt;/DisplayName&gt;
///     &lt;CaptureStdOut&gt;true&lt;/CaptureStdOut&gt;
///     &lt;Parameters&gt;--verbose --headless&lt;/Parameters&gt;
///     &lt;CompileProcessTimeout&gt;30000&lt;/CompileProcessTimeout&gt;
///     &lt;TestCaseTimeout&gt;300000&lt;/TestCaseTimeout&gt;
///     &lt;UseUniqueLogFiles&gt;true&lt;/UseUniqueLogFiles&gt;
///     &lt;LogFileRoot&gt;tmp/gdunit-runs&lt;/LogFileRoot&gt;
///     &lt;RunnerRetentionCount&gt;150&lt;/RunnerRetentionCount&gt;
///     &lt;UseUniqueUserDataDir&gt;false&lt;/UseUniqueUserDataDir&gt;
///     &lt;RunnerSceneDirectory&gt;Data/Testing/Generated/GdUnit4&lt;/RunnerSceneDirectory&gt;
///     &lt;GodotProjectPath&gt;D:\Projects\MyGame&lt;/GodotProjectPath&gt;
///     &lt;ProjectSetupCache&gt;true&lt;/ProjectSetupCache&gt;
///   &lt;/GdUnit4&gt;
/// &lt;/RunSettings&gt;
/// </code>
///     The settings control:
///     - Test case display formatting in Test Explorer and results
///     - Standard output capture for debugging purposes
///     - Additional parameters passed to the Godot engine during test execution
///     - Compilation timeout for projects that require extended build times
///     These settings are loaded by <see cref="GdUnit4SettingsProvider" /> during test discovery and execution.
/// </remarks>
[XmlRoot(RUN_SETTINGS_XML_NODE)]
public class GdUnit4Settings : TestRunSettings
{
    /// <summary>
    ///     The XML node name used to identify GdUnit4 settings in .runsettings files.
    /// </summary>
    public const string RUN_SETTINGS_XML_NODE = "GdUnit4";

    private static readonly XmlSerializer Serializer = new(typeof(GdUnit4Settings));

    /// <summary>
    ///     Initializes a new instance of the <see cref="GdUnit4Settings" /> class.
    /// </summary>
    public GdUnit4Settings()
        : base(RUN_SETTINGS_XML_NODE)
    {
    }

    /// <summary>
    ///     Gets or sets additional Godot runtime parameters. These are passed to the Godot executable when running tests.
    /// </summary>
    /// <value>
    ///     A string containing command-line parameters for the Godot engine, such as "--verbose", "--headless", or custom flags.
    ///     Can be null or empty if no additional parameters are needed.
    /// </value>
    /// <example>
    ///     <code>
    /// Parameters = "--verbose --headless"
    /// </code>
    /// </example>
    public string? Parameters { get; set; }

    /// <summary>
    ///     Gets or sets the display name format for test cases in test results and Test Explorer.
    /// </summary>
    /// <value>
    ///     A <see cref="DisplayNameOptions" /> value that controls how test names appear.
    ///     Default is <see cref="DisplayNameOptions.SimpleName" />.
    /// </value>
    /// <remarks>
    ///     <para><see cref="DisplayNameOptions.SimpleName" />: Shows only the method name (e.g., "TestMethod").</para>
    ///     <para><see cref="DisplayNameOptions.FullyQualifiedName" />: Shows the complete path (e.g., "MyNamespace.MyClass.TestMethod").</para>
    /// </remarks>
    public DisplayNameOptions DisplayName { get; set; } = DisplayNameOptions.SimpleName;

    /// <summary>
    ///     Gets or sets a value indicating whether standard output (stdout) from test cases is captured and included in test results.
    /// </summary>
    /// <value>
    ///     <c>true</c> to capture standard output for debugging purposes; <c>false</c> to ignore stdout.
    ///     Default is <c>false</c>.
    /// </value>
    /// <remarks>
    ///     When enabled, any console output from tests (Console.WriteLine, Godot print statements, etc.)
    ///     will be captured and displayed in test results. This can be useful for debugging test failures
    ///     but may impact performance for tests with extensive output.
    /// </remarks>
    public bool CaptureStdOut { get; set; }

    /// <summary>
    ///     Gets the maximum duration allowed for a compilation process in milliseconds.
    /// </summary>
    /// <value>
    ///     The timeout value in milliseconds. Default is 20000 milliseconds (20 seconds).
    /// </value>
    /// <remarks>
    ///     <para>
    ///         This timeout applies to the compilation phase before test execution. If compilation takes longer
    ///         than this duration, the process will be forcefully terminated.
    ///     </para>
    ///     <para>
    ///         Increase this value for larger projects or slower build environments that require more time
    ///         to compile. Very large Godot projects with many dependencies may need timeouts of 60+ seconds.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     For a large project that requires more compilation time:
    ///     <code>
    /// CompileProcessTimeout = 60000; // 60 seconds
    /// </code>
    /// </example>
    public int CompileProcessTimeout { get; init; } = 20000;

    /// <summary>
    ///     Gets the maximum duration allowed for a single test stage in milliseconds.
    /// </summary>
    /// <value>
    ///     The timeout value in milliseconds. Default is -1, which disables the timeout.
    /// </value>
    /// <remarks>
    ///     <para>
    ///         This timeout is applied to each test case and to its <c>Before</c>, <c>After</c>, <c>BeforeTest</c>
    ///         and <c>AfterTest</c> stages individually. It never bounds the run as a whole; use the standard
    ///         <c>RunConfiguration.TestSessionTimeout</c> for that.
    ///     </para>
    ///     <para>
    ///         A positive explicit <c>Timeout</c> on the test attribute always takes precedence, so individual
    ///         long-running tests can request a larger budget. When this setting is left at -1 and no positive
    ///         attribute timeout is present, a stage runs until it completes, which is the historical behavior.
    ///     </para>
    /// </remarks>
    /// <example>
    ///     To interrupt any single test or setup stage that hangs for more than five minutes:
    ///     <code>
    /// TestCaseTimeout = 300000; // 5 minutes
    /// </code>
    /// </example>
    public int TestCaseTimeout { get; init; } = -1;

    /// <summary>
    ///     Gets the maximum duration allowed for the Godot runtime to acknowledge the shutdown command in milliseconds.
    /// </summary>
    /// <remarks>
    ///     Applies only to the shutdown handshake after a test run has finished, never to the run itself, so a long
    ///     running test suite can not be affected by it. Without a bound, a runtime that never answers leaves the test
    ///     host waiting forever and the Godot process orphaned, holding file locks on the build output.
    ///     After this timeout the runner falls back to terminating the process, which still grants the regular grace
    ///     period before it is killed.
    /// </remarks>
    /// <example>
    ///     For a heavily loaded machine that needs longer to schedule the answer:
    ///     <code>
    /// ShutdownTimeout = 60000; // 60 seconds
    /// </code>
    /// </example>
    public int ShutdownTimeout { get; init; } = 30000;

    /// <summary>
    ///     Gets a value indicating whether Godot compile and runtime processes write to per-runner log files.
    /// </summary>
    public bool UseUniqueLogFiles { get; init; } = true;

    /// <summary>
    ///     Gets the root directory used for per-runner runtime artifacts when unique log files are enabled.
    /// </summary>
    public string LogFileRoot { get; init; } = "tmp/gdunit-runs";

    /// <summary>
    ///     Gets the number of newest per-runner artifact folders kept under <see cref="LogFileRoot" />.
    /// </summary>
    /// <remarks>
    ///     Before a runner sets up the Godot project it deletes older runner folders under <see cref="LogFileRoot" />, keeping the
    ///     newest N. Folders of the current runner and of still running test hosts are never deleted. 0 disables cleanup, and
    ///     negative values are treated as 0. For long investigations, copy out the folders you need, or disable cleanup for one
    ///     run with <c>dotnet test ... -- GdUnit4.RunnerRetentionCount=0</c>.
    /// </remarks>
    public int RunnerRetentionCount { get; init; } = 150;

    /// <summary>
    ///     Gets a value indicating whether each runner should use its own Godot user data directory.
    /// </summary>
    public bool UseUniqueUserDataDir { get; init; } = true;

    /// <summary>
    ///     Gets the project-relative directory where the generated Godot runtime runner scene is written.
    /// </summary>
    public string RunnerSceneDirectory { get; init; } = "gdunit4_testadapter_v5";

    /// <summary>
    ///     Gets the optional Godot project path. The value can point to a directory containing project.godot or to project.godot itself.
    ///     Relative values are resolved from the test assembly directory because VSTest exposes runsettings XML but not the runsettings file path to adapters.
    /// </summary>
    public string? GodotProjectPath { get; init; }

    /// <summary>
    ///     Gets a value indicating whether a validated Godot editor preparation of the project is reused between runners.
    /// </summary>
    /// <value>
    ///     <c>true</c> to skip the editor pass when the project-local setup stamp below <c>.godot/gdunit4/</c> still matches
    ///     every recorded input and generated output; <c>false</c> to prepare the project on every run. Default is <c>false</c>.
    /// </value>
    /// <remarks>
    ///     Every runner still starts its own Godot runtime. Disable the cache for one run with
    ///     <c>dotnet test ... -- GdUnit4.ProjectSetupCache=false</c>.
    /// </remarks>
    public bool ProjectSetupCache { get; init; }

    /// <summary>
    ///     Converts the current settings instance to an XML element for inclusion in .runsettings files.
    /// </summary>
    /// <returns>
    ///     An <see cref="XmlElement" /> representing the serialized settings that can be embedded
    ///     in VSTest run settings configuration.
    /// </returns>
    /// <remarks>
    ///     This method is called by the VSTest framework when processing .runsettings files.
    ///     The returned XML element will be used to reconstruct the settings during test execution.
    /// </remarks>
    public override XmlElement ToXml()
    {
        using var stringWriter = new StringWriter();
        Serializer.Serialize(stringWriter, this);

        var document = new XmlDocument();
        document.LoadXml(stringWriter.ToString());

        return document.DocumentElement!;
    }
}
