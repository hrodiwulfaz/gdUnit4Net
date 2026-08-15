namespace GdUnit4.E2E.TestCaseTimeout;

internal static class TimeoutFixtureMarkers
{
    private const string MarkerDirectoryEnvironmentVariable = "GDUNIT4_TIMEOUT_MARKER_DIR";

    internal static void Write(string markerName)
    {
        var markerDirectory = Environment.GetEnvironmentVariable(MarkerDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(markerDirectory))
            return;

        _ = Directory.CreateDirectory(markerDirectory);
        File.WriteAllText(Path.Combine(markerDirectory, markerName), markerName);
    }
}

[RequireGodotRuntime]
[TestSuite]
public class AsyncHangBatch
{
    [TestCase]
    public void PassBeforeTimeout()
        => TimeoutFixtureMarkers.Write("async-pass-before.txt");

    [TestCase]
    public async Task AsyncNeverCompletes()
        => await Task.Delay(Timeout.InfiniteTimeSpan);

    [TestCase]
    public void WouldPassAfterTimeout()
        => TimeoutFixtureMarkers.Write("async-would-pass-after.txt");
}

[RequireGodotRuntime]
[TestSuite]
public class SynchronousHangBatch
{
    [TestCase]
    public void PassBeforeTimeout()
        => TimeoutFixtureMarkers.Write("sync-pass-before.txt");

    [TestCase]
    public void SynchronouslyBlocks()
        => Thread.Sleep(Timeout.Infinite);

    [TestCase]
    public void WouldPassAfterTimeout()
        => TimeoutFixtureMarkers.Write("sync-would-pass-after.txt");
}

[RequireGodotRuntime]
[TestSuite]
public class LifecycleHangBatch
{
    [BeforeTest]
    public async Task BeforeTestNeverCompletes()
        => await Task.Delay(Timeout.InfiniteTimeSpan);

    [TestCase]
    public void TestBodyMustNotExecute()
        => TimeoutFixtureMarkers.Write("lifecycle-test-body.txt");
}

[RequireGodotRuntime]
[TestSuite]
public class PassingWatchdogBatch
{
    [TestCase]
    public void FirstPass()
        => TimeoutFixtureMarkers.Write("normal-first.txt");

    [TestCase]
    public async Task AsyncPass()
    {
        await Task.Delay(25);
        TimeoutFixtureMarkers.Write("normal-async.txt");
    }

    [TestCase]
    public void LastPass()
        => TimeoutFixtureMarkers.Write("normal-last.txt");
}
