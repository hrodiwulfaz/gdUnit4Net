namespace GdUnit4.Tests.Core.Hooks;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using GdUnit4.Core.Execution;
using GdUnit4.Core.Hooks;

using Godot;

using static Assertions;

using Environment = System.Environment;

[RequireGodotRuntime]
[TestSuite]
public class StdOutHookFactoryTest
{
    private bool savedStdOutCaptureState;

    [Before]
    public void Before()
    {
        // We disable the possible enabled stdout capture otherwise we run into unexpected behaviors
        savedStdOutCaptureState = ExecutionContext.Current!.IsCaptureStdOut;
        ExecutionContext.Current!.IsCaptureStdOut = false;
    }

    [After]
    public void After() => ExecutionContext.Current!.IsCaptureStdOut = savedStdOutCaptureState;

    [TestCase]
    public void CreateStdHook()
    {
        using var stdOutHook = StdOutHookFactory.CreateStdOutHook();
        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            AssertObject(stdOutHook).IsInstanceOf<UnixStdOutHook>();
        if (OperatingSystem.IsWindows())
            AssertObject(stdOutHook).IsInstanceOf<WindowsStdOutHook>();
    }

    [TestCase]
    public void CaptureStdOutConsole()
    {
        using var stdOutHook = StdOutHookFactory.CreateStdOutHook();
        // it should not be captured before `StartCapture`
        Console.WriteLine("Console: Short before 'StartCapture'");

        // start the capturing
        stdOutHook.StartCapture();
        Console.WriteLine("Console: Short after 'StartCapture'");
        Console.WriteLine("Console: A message");
        Console.WriteLine("Console: Short before 'StopCapture'");
        // stop it
        stdOutHook.StopCapture();
        // it should not be captured after `StopCapture`
        Console.WriteLine("Console: Short after 'StopCapture'");

        // verify
        AssertThat(stdOutHook.GetCapturedOutput())
            .IsEqual($"Console: Short after 'StartCapture'{Environment.NewLine}" +
                     $"Console: A message{Environment.NewLine}" +
                     $"Console: Short before 'StopCapture'{Environment.NewLine}");
    }

    [TestCase]
    public async Task CaptureStdOutGodot()
    {
        using var stdOutHook = StdOutHookFactory.CreateStdOutHook();
        // it should not be captured before `StartCapture`
        GD.PrintS("Godot: Short before 'StartCapture'");

        // start the capturing
        stdOutHook.StartCapture();
        GD.PrintS("Godot: Short after 'StartCapture'");
        GD.PrintS("Godot: A message");
        GD.PrintS("Godot: Short before 'StopCapture'");

        // need to await sync stdout from Godot engine is written
        await ISceneRunner.SyncProcessFrame;
        // stop it
        stdOutHook.StopCapture();
        // it should not be captured after `StopCapture`
        GD.PrintS("Godot: Short after 'StopCapture'");

        // verify
        AssertThat(stdOutHook.GetCapturedOutput())
            .IsEqual($"Godot: Short after 'StartCapture'{Environment.NewLine}" +
                     $"Godot: A message{Environment.NewLine}" +
                     $"Godot: Short before 'StopCapture'{Environment.NewLine}");
    }

    [TestCase]
    public async Task CaptureStdOutConsoleAndGodot()
    {
        using var stdOutHook = StdOutHookFactory.CreateStdOutHook();

        // it should not be captured before `StartCapture`
        Console.WriteLine("Console: Do not be captured");
        GD.PrintS("Godot: Do not be captured");

        // start the capturing
        stdOutHook.StartCapture();
        Console.WriteLine("Console: Short after 'StartCapture'");
        GD.PrintS("Godot: Short after 'StartCapture'");
        Console.WriteLine("Console: A message");
        GD.PrintS("Godot: A message");
        Console.WriteLine("Console: Short before 'StopCapture'");
        GD.PrintS("Godot: Short before 'StopCapture'");
        // need to await sync stdout from Godot engine is written
        await ISceneRunner.SyncProcessFrame;
        // stop it
        stdOutHook.StopCapture();

        // it should not be captured after `StopCapture`
        Console.WriteLine("Console: Do not be captured");
        GD.PrintS("Godot: Do not be captured");


        // verify the captured output contains all lines, but we cant guaranty the same order
        var capturedOutput = stdOutHook.GetCapturedOutput();
        AssertThat(capturedOutput)
            .Contains("Console: Short after 'StartCapture'")
            .Contains("Godot: Short after 'StartCapture'")
            .Contains("Console: A message")
            .Contains("Godot: A message")
            .Contains("Console: Short before 'StopCapture'")
            .Contains("Godot: Short before 'StopCapture'")
            // and verify before and after capture messages are not caught
            .NotContains("Console: Do not be captured")
            .NotContains("Godot: Do not be captured");
    }

    [TestCase(Timeout = 300000)]
    public async Task RepeatedCaptureLifetimesAreIsolated()
    {
        const int lifetimes = 64;
        var usedTokens = new List<string>();

        // one hook serves the whole suite and is started and stopped per test case, a hook per lifetime would
        // give every lifetime its own pipe and would not reproduce the leak at all
        using var stdOutHook = StdOutHookFactory.CreateStdOutHook();

        for (var lifetime = 0; lifetime < lifetimes; lifetime++)
        {
            var token = $"capture-{lifetime}-{Guid.NewGuid():N}";
            stdOutHook.StartCapture();

            if (lifetime % 4 == 0)
            {
                // a multiline header only payload, neither a managed nor a native writer follows it
                Console.WriteLine($"header of {token}{Environment.NewLine}  detail line 1{Environment.NewLine}  detail line 2{Environment.NewLine}");
            }
            else
            {
                Console.WriteLine($"console line of {token}");
                GD.PrintS($"godot line of {token}");
            }

            // need to await sync stdout from Godot engine is written
            await ISceneRunner.SyncProcessFrame;
            stdOutHook.StopCapture();

            // the capture must be complete, the ordering between the managed and the native writer is not a contract
            var captured = stdOutHook.GetCapturedOutput();
            AssertThat(captured).Contains(token);
            if (lifetime % 4 != 0)
                AssertThat(captured).Contains($"godot line of {token}");

            // a capture lifetime must never expose output of a previous lifetime
            usedTokens.ForEach(previousToken => AssertThat(captured).NotContains(previousToken));
            usedTokens.Add(token);
        }
    }

    [TestCase(Timeout = 300000)]
    public void UndrainedNativeOutputDoesNotLeakIntoTheNextCaptureLifetime()
    {
        const int lifetimes = 64;
        var usedTokens = new List<string>();

        // one hook serves the whole suite and is started and stopped per test case
        using var stdOutHook = StdOutHookFactory.CreateStdOutHook();

        for (var lifetime = 0; lifetime < lifetimes; lifetime++)
        {
            var token = $"undrained-{lifetime}-{Guid.NewGuid():N}";
            stdOutHook.StartCapture();

            // deliberately stops while the native output is still in flight, the teardown has to drain it into
            // this lifetime instead of leaving it in the pipe for the reader of the next one
            for (var line = 0; line < 8; line++)
                GD.PrintS($"{token} line {line}");
            stdOutHook.StopCapture();

            var captured = stdOutHook.GetCapturedOutput();
            usedTokens.ForEach(previousToken => AssertThat(captured).NotContains(previousToken));
            usedTokens.Add(token);
        }
    }

    [TestCase]
    public void ASecondConcurrentCaptureOwnerIsRejected()
    {
        using var firstHook = StdOutHookFactory.CreateStdOutHook();
        using var secondHook = StdOutHookFactory.CreateStdOutHook();

        firstHook.StartCapture();
        try
        {
            // stdout redirection is process global, a second owner must fail instead of interleaving the output
            AssertThrown(() => secondHook.StartCapture())
                .IsInstanceOf<InvalidOperationException>()
                .StartsWithMessage("Standard output capture is already owned by");
        }
        finally
        {
            firstHook.StopCapture();
        }

        // after the first owner released the capture the second owner can take it over
        secondHook.StartCapture();
        Console.WriteLine("owned by the second hook");
        secondHook.StopCapture();
        AssertThat(secondHook.GetCapturedOutput()).Contains("owned by the second hook");
    }
}
