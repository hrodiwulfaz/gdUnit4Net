namespace GdUnit4.E2E.StdOutCapture;

using System.Text;

using Godot;

using static Assertions;

/// <summary>
///     Produces the managed and native output volume that made the original stdout capture defect visible.
///     Every test case is one capture lifetime.
/// </summary>
[RequireGodotRuntime]
[TestSuite]
public class CaptureHeavyTestSuite
{
    public const string ManagedTokenPrefix = "e2e-managed-capture-";
    public const string NativeTokenPrefix = "e2e-native-capture-";

    public const int CaptureLifetimesPerMethod = 16;

    private const int ReportLines = 4;
    private const int NativeLines = 8;

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(14)]
    [TestCase(15)]
    public void MultilineManagedOutputIsCaptured(int iteration)
    {
        var report = BuildTimingReport(iteration);
        Console.Write(report);

        AssertThat(report.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length).IsEqual(ReportLines);
        AssertThat(report).Contains($"{ManagedTokenPrefix}{iteration}");
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    [TestCase(7)]
    [TestCase(8)]
    [TestCase(9)]
    [TestCase(10)]
    [TestCase(11)]
    [TestCase(12)]
    [TestCase(13)]
    [TestCase(14)]
    [TestCase(15)]
    public async Task ManagedAndNativeOutputIsCaptured(int iteration)
    {
        Console.Write(BuildTimingReport(iteration));

        var nativeLines = BuildNativeLines(iteration);
        foreach (var line in nativeLines)
            GD.PrintS(line);

        // the Godot engine needs a process frame to write its native stdout
        await ISceneRunner.SyncProcessFrame;

        AssertThat(nativeLines.Count).IsEqual(NativeLines);
    }

    private static string BuildTimingReport(int iteration)
    {
        var report = new StringBuilder();
        _ = report.Append($"{ManagedTokenPrefix}{iteration} timing report\n");
        for (var line = 1; line < ReportLines; line++)
            _ = report.Append($"  stage {line}: {(line * 1000) + iteration} ms\n");
        return report.ToString();
    }

    private static List<string> BuildNativeLines(int iteration)
    {
        var lines = new List<string>(NativeLines);
        for (var line = 0; line < NativeLines; line++)
            lines.Add($"{NativeTokenPrefix}{iteration} line {line}");
        return lines;
    }
}
