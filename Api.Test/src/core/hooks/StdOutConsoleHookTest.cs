namespace GdUnit4.Tests.Core.Hooks;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using GdUnit4.Core.Hooks;

using static Assertions;

[TestSuite]
public class StdOutConsoleHookTest
{
    [TestCase]
    public void CaptureStdOut()
    {
        using var hook = new StdOutConsoleHook();
        Console.WriteLine("Before capture.");

        hook.StartCapture();

        Console.WriteLine("Hello World A!");
        Console.WriteLine("Hello World B!");

        hook.StopCapture();

        Console.WriteLine("After capture.");

        var capturedMessages = hook.GetCapturedOutput();
        AssertThat(capturedMessages)
            .IsEqual($"Hello World A!{Environment.NewLine}" +
                     $"Hello World B!{Environment.NewLine}");
    }

    [TestCase]
    public void RepeatedCaptureLifetimesAreIsolated()
    {
        const int lifetimes = 16;
        using var hook = new StdOutConsoleHook();
        var usedTokens = new List<string>();

        for (var lifetime = 0; lifetime < lifetimes; lifetime++)
        {
            var token = $"managed-{lifetime}-{Guid.NewGuid():N}";

            hook.StartCapture();
            Console.WriteLine($"first line of {token}");
            Console.WriteLine($"second line of {token}");
            hook.StopCapture();

            var captured = hook.GetCapturedOutput();
            AssertThat(captured)
                .Contains($"first line of {token}")
                .Contains($"second line of {token}");

            // a capture lifetime must never expose output of a previous lifetime
            usedTokens.ForEach(previousToken => AssertThat(captured).NotContains(previousToken));
            usedTokens.Add(token);
        }
    }

    [TestCase]
    public void StopAndDisposeAreIdempotent()
    {
        var hook = new StdOutConsoleHook();

        // stopping a capture that was never started is a no-op
        hook.StopCapture();

        hook.StartCapture();
        Console.WriteLine("captured before the repeated start");

        // a repeated start by the same owner is harmless and continues the running capture
        hook.StartCapture();
        Console.WriteLine("captured after the repeated start");
        hook.StopCapture();
        hook.StopCapture();
        AssertThat(hook.GetCapturedOutput())
            .Contains("captured before the repeated start")
            .Contains("captured after the repeated start");

        hook.Dispose();
        hook.Dispose();
        AssertThrown(() => hook.StartCapture()).IsInstanceOf<ObjectDisposedException>();
    }

    [TestCase]
    public void DisposeKeepsTheReplacedWriterUsable()
    {
        var probe = new ProbeWriter();
        var restoreOutput = Console.Out;
        Console.SetOut(probe);
        try
        {
            using (var hook = new StdOutConsoleHook())
            {
                hook.StartCapture();
                Console.WriteLine("captured by the hook");
                hook.StopCapture();
            }

            Console.WriteLine("written after the hook is disposed");
        }
        finally
        {
            Console.SetOut(restoreOutput);
        }

        // the hook does not own the writer it replaced, disposing it must not close the console
        AssertBool(probe.IsDisposed).IsFalse();
        AssertThat(probe.Written)
            .Contains("written after the hook is disposed")
            .NotContains("captured by the hook");
    }

    [TestCase(Timeout = 60000)]
    public async Task ConcurrentWritesAndSnapshotsAreSafe()
    {
        const int writers = 4;
        const int linesPerWriter = 250;

        using var hook = new StdOutConsoleHook();
        hook.StartCapture();

        var writerTasks = Enumerable
            .Range(0, writers)
            .Select(writer => Task.Run(() =>
            {
                for (var line = 0; line < linesPerWriter; line++)
                    Console.WriteLine($"writer-{writer}-line-{line}");
            }))
            .ToList();

        // snapshots the buffer while it is written to, an unsynchronized buffer corrupts or throws here
        var snapshotTask = Task.Run(() =>
        {
            while (!writerTasks.TrueForAll(task => task.IsCompleted))
                _ = hook.GetCapturedOutput().Length;
        });

        await Task.WhenAll(writerTasks.Append(snapshotTask));
        hook.StopCapture();

        var captured = hook.GetCapturedOutput();
        for (var writer = 0; writer < writers; writer++)
        {
            AssertThat(captured)
                .Contains($"writer-{writer}-line-0")
                .Contains($"writer-{writer}-line-{linesPerWriter - 1}");
        }
    }

    private sealed class ProbeWriter : TextWriter
    {
        private readonly StringBuilder written = new();

        public override Encoding Encoding => Encoding.UTF8;

        public bool IsDisposed { get; private set; }

        public string Written
        {
            get
            {
                lock (written)
                    return written.ToString();
            }
        }

        public override void Write(char value)
        {
            lock (written)
                written.Append(value);
        }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }
}
