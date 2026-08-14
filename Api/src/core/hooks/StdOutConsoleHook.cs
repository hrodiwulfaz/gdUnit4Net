// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Hooks;

using System;
using System.Globalization;
using System.IO;
using System.Text;

/// <summary>
///     Captures managed console output by redirecting <see cref="Console.Out" /> to an in memory writer.
/// </summary>
/// <remarks>
///     The hook owns only its capture writer. The writer that was active when the capture started is restored
///     exactly as it was found and is never disposed, so <see cref="Console.Out" /> stays usable for the rest
///     of the process after this hook is disposed.
/// </remarks>
internal sealed class StdOutConsoleHook : IStdOutHook
{
    private readonly object captureLock = new();
    private readonly CaptureWriter captureWriter;

    private TextWriter? restoreOutput;
    private bool disposed;

    public StdOutConsoleHook() => captureWriter = new CaptureWriter(captureLock);

    public void StartCapture()
    {
        lock (captureLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            // a repeated start continues the running capture, only a new capture resets the buffer
            if (restoreOutput != null)
                return;

            captureWriter.Clear();
            restoreOutput = Console.Out;
            Console.SetOut(captureWriter);
        }
    }

    public void StopCapture()
    {
        lock (captureLock)
        {
            if (restoreOutput == null)
                return;

            Console.SetOut(restoreOutput);
            restoreOutput = null;
        }
    }

    public string GetCapturedOutput() => captureWriter.GetCapturedOutput();

    public void Dispose()
    {
        lock (captureLock)
        {
            if (disposed)
                return;

            disposed = true;
            if (restoreOutput != null)
            {
                Console.SetOut(restoreOutput);
                restoreOutput = null;
            }
        }

        captureWriter.Dispose();
    }

    private sealed class CaptureWriter : TextWriter
    {
        private readonly StringBuilder capturedOutput = new();
        private readonly object captureLock;

        public CaptureWriter(object syncLock)
            : base(CultureInfo.InvariantCulture)
            => captureLock = syncLock;

        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value)
        {
            lock (captureLock)
                _ = capturedOutput.Append(value);
        }

        public override void Write(string? value)
        {
            if (value == null)
                return;

            lock (captureLock)
                _ = capturedOutput.Append(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            lock (captureLock)
                _ = capturedOutput.Append(buffer, index, count);
        }

        public string GetCapturedOutput()
        {
            lock (captureLock)
                return capturedOutput.ToString();
        }

        public void Clear()
        {
            lock (captureLock)
                _ = capturedOutput.Clear();
        }
    }
}
