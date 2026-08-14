namespace GdUnit4.E2E.Regression;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

using Microsoft.VisualStudio.TestTools.UnitTesting;

/// <summary>
///     Drives the capture heavy child test run in its own VSTest process and verifies that a real assertion failure
///     survives the Godot runtime, the named pipe protocol, the test adapter and the VSTest result.
/// </summary>
/// <remarks>
///     The original defect crossed the process and adapter boundary, it cannot be closed by hook only coverage.
///     Set <c>GDUNIT4_E2E_REPEAT</c> to run the failing and the clean child pair repeatedly, every pair covers
///     <see cref="CaptureLifetimesPerPair" /> capture lifetimes.
/// </remarks>
[TestClass]
public class StdOutCaptureRegressionTest
{
    /// <summary>The generic transport failure the fork used to report instead of the real assertion.</summary>
    private const string MaskingStatusCodeMessage = "The server returned an unexpected status code";

    /// <summary>The generic discovery failure the fork used to report after the masked transport failure.</summary>
    private const string MaskingNoTestMatchesMessage = "No test matches";

    private const string AssertionToken = "intentional-e2e-assertion-failure";
    private const string IntentionalFailureFilter = "FullyQualifiedName!~IntentionalFailureTestSuite";

    private const string ManagedTokenPrefix = "e2e-managed-capture-";
    private const string NativeTokenPrefix = "e2e-native-capture-";

    /// <summary>Must match the trx LogFileName configured in the child .runsettings.</summary>
    private const string ChildResultsFileName = "test-result.trx";

    private const int ExpectedTotalWithFailure = 33;
    private const int ExpectedTotalWithoutFailure = 32;
    private const int CaptureLifetimesPerPair = ExpectedTotalWithFailure + ExpectedTotalWithoutFailure;

    private const int ChildRunTimeoutMs = 900_000;

    [TestMethod]
    [Timeout(7_200_000)]
    public void TheIntentionalAssertionSurvivesAndDoesNotContaminateTheNextRun()
    {
        var repeats = ReadRepeatCount();
        Console.WriteLine($"Running {repeats} child pair(s), {repeats * CaptureLifetimesPerPair} aggregate capture lifetimes.");

        for (var repeat = 0; repeat < repeats; repeat++)
        {
            var failingRun = RunChild(null);
            AssertNoMaskingMessage(failingRun.Output, repeat, "failing");

            Assert.AreNotEqual(0, failingRun.ExitCode, $"pair {repeat}: the child run must fail on the intentional assertion");
            Assert.AreEqual(ExpectedTotalWithFailure, ParseReportedTotal(failingRun.Output, repeat, "failing"), $"pair {repeat}: not every test was reported");
            StringAssert.Contains(failingRun.Output, AssertionToken, StringComparison.Ordinal);

            // The captured output of passing tests must be attached to their results. It is asserted on the result
            // file, not on the console: the adapter only echoes captured stdout to the console when it detects an
            // IDE host, so a command line run would never show it.
            StringAssert.Contains(failingRun.Results, $"{ManagedTokenPrefix}0", StringComparison.Ordinal);
            StringAssert.Contains(failingRun.Results, $"{ManagedTokenPrefix}15", StringComparison.Ordinal);
            StringAssert.Contains(failingRun.Results, $"{NativeTokenPrefix}0", StringComparison.Ordinal);
            StringAssert.Contains(failingRun.Results, $"{NativeTokenPrefix}15", StringComparison.Ordinal);

            var cleanRun = RunChild(IntentionalFailureFilter);
            AssertNoMaskingMessage(cleanRun.Output, repeat, "clean");

            Assert.AreEqual(0, cleanRun.ExitCode, $"pair {repeat}: the clean child run must pass\n{cleanRun.Output}");
            Assert.AreEqual(ExpectedTotalWithoutFailure, ParseReportedTotal(cleanRun.Output, repeat, "clean"), $"pair {repeat}: not every test was reported");

            // the captured output must still arrive when no test fails
            StringAssert.Contains(cleanRun.Results, $"{ManagedTokenPrefix}0", StringComparison.Ordinal);
            StringAssert.Contains(cleanRun.Results, $"{NativeTokenPrefix}0", StringComparison.Ordinal);

            // a previous run must never leak into the following one
            Assert.IsFalse(
                cleanRun.Output.Contains(AssertionToken, StringComparison.Ordinal),
                $"pair {repeat}: the previous failing run contaminated the clean run console output");
            Assert.IsFalse(
                cleanRun.Results.Contains(AssertionToken, StringComparison.Ordinal),
                $"pair {repeat}: the previous failing run contaminated the clean run results");
        }
    }

    private static void AssertNoMaskingMessage(string output, int repeat, string runName)
    {
        Assert.IsFalse(
            output.Contains(MaskingStatusCodeMessage, StringComparison.Ordinal),
            $"pair {repeat}: the {runName} run masked the real failure with '{MaskingStatusCodeMessage}'\n{output}");
        Assert.IsFalse(
            output.Contains(MaskingNoTestMatchesMessage, StringComparison.Ordinal),
            $"pair {repeat}: the {runName} run masked the real failure with '{MaskingNoTestMatchesMessage}'\n{output}");
    }

    private static int ReadRepeatCount()
    {
        var configured = Environment.GetEnvironmentVariable("GDUNIT4_E2E_REPEAT");
        return int.TryParse(configured, NumberStyles.Integer, CultureInfo.InvariantCulture, out var repeats) && repeats > 0 ? repeats : 1;
    }

    private static int ParseReportedTotal(string output, int repeat, string runName)
    {
        var match = Regex.Match(output, @"Total:\s*(\d+)", RegexOptions.IgnoreCase);
        Assert.IsTrue(match.Success, $"pair {repeat}: the {runName} run did not report a test total\n{output}");
        return int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }

    private static string LocateChildProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "StdOutCaptureChild")))
            directory = directory.Parent;

        Assert.IsNotNull(directory, "cannot locate the 'StdOutCaptureChild' project relative to the regression assembly");
        return Path.Combine(directory.FullName, "StdOutCaptureChild", "StdOutCaptureChild.csproj");
    }

    private static (int ExitCode, string Output, string Results) RunChild(string? filter)
    {
        var childProject = LocateChildProject();
        var childDirectory = Path.GetDirectoryName(childProject);
        Assert.IsNotNull(childDirectory);

        // a stale result file from an earlier run would satisfy the capture assertions of this one
        var resultsFile = Path.Combine(childDirectory, "TestResults", ChildResultsFileName);
        if (File.Exists(resultsFile))
            File.Delete(resultsFile);

        var arguments = new StringBuilder()
            .Append("test \"").Append(childProject).Append('"')
            .Append(" --settings \"").Append(Path.Combine(childDirectory, ".runsettings")).Append('"');
        if (filter != null)
            _ = arguments.Append(" --filter \"").Append(filter).Append('"');

        var startInfo = new ProcessStartInfo("dotnet", arguments.ToString())
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = childDirectory
        };

        using var process = Process.Start(startInfo);
        Assert.IsNotNull(process, "cannot start the child test run");

        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) => AppendLine(output, e.Data);
        process.ErrorDataReceived += (_, e) => AppendLine(output, e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(ChildRunTimeoutMs))
        {
            process.Kill(true);
            Assert.Fail($"the child test run did not finish within {ChildRunTimeoutMs} ms");
        }

        // flushes the remaining redirected output
        process.WaitForExit();

        lock (output)
        {
            var consoleOutput = output.ToString();
            Assert.IsTrue(File.Exists(resultsFile), $"the child test run did not write '{resultsFile}'\n{consoleOutput}");
            return (process.ExitCode, consoleOutput, File.ReadAllText(resultsFile));
        }
    }

    private static void AppendLine(StringBuilder output, string? line)
    {
        if (line == null)
            return;

        lock (output)
            _ = output.AppendLine(line);
    }
}
