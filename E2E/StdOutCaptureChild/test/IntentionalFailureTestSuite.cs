namespace GdUnit4.E2E.StdOutCapture;

using Godot;

using static Assertions;

/// <summary>
///     Holds the deliberate assertion failure the end to end regression expects to survive the runtime, the named
///     pipe protocol, the test adapter and the VSTest result.
/// </summary>
/// <remarks>
///     The clean follow up run excludes this suite by test filter, it must not be made conditional in code.
/// </remarks>
[RequireGodotRuntime]
[TestSuite]
public class IntentionalFailureTestSuite
{
    public const string AssertionToken = "intentional-e2e-assertion-failure";

    private const int NoiseLines = 200;

    [TestCase]
    public async Task FailsWithAnAssertionAfterCaptureHeavyOutput()
    {
        for (var line = 0; line < NoiseLines; line++)
            Console.WriteLine($"noise line {line} written before the intentional failure");
        GD.PrintS("native noise written before the intentional failure");

        // the Godot engine needs a process frame to write its native stdout
        await ISceneRunner.SyncProcessFrame;

        AssertThat("the actual value").IsEqual(AssertionToken);
    }
}
