// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Hooks;

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
///     Captures managed and native standard output on Linux and macOS.
/// </summary>
/// <remarks>
///     Every capture lifetime owns its own pipe file descriptors and its own reader thread. Nothing is shared
///     between two captures, so output of a previous capture can never leak into a following one.
/// </remarks>
[SuppressMessage("Style", "IDE1006", Justification = "Unix system call names follow C naming conventions")]
[SuppressMessage(
    "StyleCop.CSharp.NamingRules",
    "SA1300:Element should begin with upper-case letter",
    Justification = "Unix system call names must match libc function names exactly")]
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1201:Elements should appear in the correct order",
    Justification = "P/Invoke declarations are grouped together for clarity at the end of the class")]
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1204:Static members should appear before non-static members",
    Justification = "P/Invoke declarations are grouped together for clarity at the end of the class")]
internal sealed class UnixStdOutHook : IStdOutHook
{
    private const int STD_OUTPUT_HANDLE = 1; // STDOUT_FILENO in Unix
    private const int PIPE_READ = 0;
    private const int PIPE_WRITE = 1;
    private const int INVALID_HANDLE = -1;
    private const int EINTR = 4;

    private static readonly TimeSpan ReaderJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly object captureLock = new();
    private readonly StdOutConsoleHook stdOutHook = new();

    private bool disposed;
    private bool isCapturing;
    private int captureGeneration;
    private int originalStdOutHandle = INVALID_HANDLE;
    private int pipeReadHandle = INVALID_HANDLE;
    private int pipeWriteHandle = INVALID_HANDLE;
    private Thread? readThread;
    private Exception? readerFailure;

    public void Dispose()
    {
        lock (captureLock)
        {
            if (disposed)
                return;

            disposed = true;
            _ = StopCaptureLocked();
        }

        stdOutHook.Dispose();
    }

    [SuppressMessage("Style", "IDE0058:Expression value is never used", Justification = "Method called for side effects only, return value intentionally ignored")]
    public void StartCapture()
    {
        lock (captureLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            StdOutCaptureOwnership.Acquire(this);
            if (isCapturing)
                return;

            var pipeHandles = new[] { INVALID_HANDLE, INVALID_HANDLE };
            var managedCaptureStarted = false;
            var stdOutRedirected = false;
            try
            {
                originalStdOutHandle = dup(STD_OUTPUT_HANDLE);
                if (originalStdOutHandle == INVALID_HANDLE)
                    throw new InvalidOperationException($"Failed to duplicate the original stdout handle. Error: {Marshal.GetLastPInvokeError()}");

                if (pipe(pipeHandles) != 0)
                    throw new InvalidOperationException($"Failed to create the stdout capture pipe. Error: {Marshal.GetLastPInvokeError()}");

                stdOutHook.StartCapture();
                managedCaptureStarted = true;

                if (dup2(pipeHandles[PIPE_WRITE], STD_OUTPUT_HANDLE) == INVALID_HANDLE)
                    throw new InvalidOperationException($"Failed to redirect stdout to the capture pipe. Error: {Marshal.GetLastPInvokeError()}");
                stdOutRedirected = true;

                var readHandle = pipeHandles[PIPE_READ];
                pipeReadHandle = readHandle;
                pipeWriteHandle = pipeHandles[PIPE_WRITE];
                readerFailure = null;
                var generation = ++captureGeneration;
                readThread = new Thread(() => ReadPipeOutput(readHandle, generation))
                {
                    IsBackground = true,
                    Name = "GdUnit4.StdOutCapture"
                };
                readThread.Start();
                isCapturing = true;
            }
            finally
            {
                if (!isCapturing)
                {
                    if (stdOutRedirected)
                        _ = dup2(originalStdOutHandle, STD_OUTPUT_HANDLE);
                    if (managedCaptureStarted)
                        stdOutHook.StopCapture();
                    CloseHandle(ref pipeHandles[PIPE_WRITE]);
                    CloseHandle(ref pipeHandles[PIPE_READ]);
                    CloseHandle(ref originalStdOutHandle);
                    pipeReadHandle = INVALID_HANDLE;
                    pipeWriteHandle = INVALID_HANDLE;
                    readThread = null;
                    StdOutCaptureOwnership.Release(this);
                }
            }
        }
    }

    public void StopCapture()
    {
        Exception? failure;
        lock (captureLock)
            failure = StopCaptureLocked();

        if (failure != null)
            throw failure;
    }

    public string GetCapturedOutput() => stdOutHook.GetCapturedOutput();

    private static void CloseHandle(ref int handle)
    {
        if (handle == INVALID_HANDLE)
            return;

        _ = close(handle);
        handle = INVALID_HANDLE;
    }

    /// <summary>
    ///     Tears the current capture down in the reverse order it was established and returns a pending
    ///     infrastructure failure instead of throwing it.
    /// </summary>
    /// <returns>The reader failure or timeout to report, or <see langword="null" /> if the teardown was clean.</returns>
    /// <remarks>
    ///     All process global state is restored before a failure is returned, so a failing teardown never leaves
    ///     stdout redirected. Must be called while holding the capture lock.
    /// </remarks>
    private Exception? StopCaptureLocked()
    {
        if (!isCapturing)
            return null;

        isCapturing = false;

        // restore the native stdout first, the write end is only unreferenced once stdout no longer points to it
        var isStdOutRestored = dup2(originalStdOutHandle, STD_OUTPUT_HANDLE) != INVALID_HANDLE;
        var restoreError = isStdOutRestored ? 0 : Marshal.GetLastPInvokeError();

        // closing the capture write end signals EOF so the reader drains the remaining data and ends
        CloseHandle(ref pipeWriteHandle);

        var reader = readThread;
        readThread = null;
        var isReaderStopped = reader == null || reader.Join(ReaderJoinTimeout);

        // retires this capture generation, an abandoned reader stops writing and closes its own read end
        captureGeneration++;

        stdOutHook.StopCapture();

        if (isReaderStopped)
            CloseHandle(ref pipeReadHandle);

        // a still blocking reader owns the read end, closing it here would recycle the descriptor under the reader
        pipeReadHandle = INVALID_HANDLE;
        CloseHandle(ref originalStdOutHandle);
        StdOutCaptureOwnership.Release(this);

        if (!isStdOutRestored)
            return new InvalidOperationException($"Failed to restore the native stdout handle. Error: {restoreError}");

        if (!isReaderStopped)
            return new TimeoutException($"The stdout capture reader did not stop within {ReaderJoinTimeout}, the captured output may be incomplete.");

        var failure = readerFailure;
        readerFailure = null;
        return failure == null ? null : new InvalidOperationException("The stdout capture reader failed.", failure);
    }

    /// <summary>
    ///     Drains the capture pipe until it reports EOF.
    /// </summary>
    /// <param name="readHandle">The read end owned by this capture lifetime.</param>
    /// <param name="generation">The capture lifetime this reader belongs to.</param>
    /// <remarks>
    ///     A reader that could not be joined outlives its capture. It must neither write into the capture of a
    ///     following lifetime nor overwrite its failure state, so every write is gated on its own generation and the
    ///     abandoned reader closes the read end itself once its blocking read returns.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The reader failure is stored and reported after stdout is restored")]
    private void ReadPipeOutput(int readHandle, int generation)
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        try
        {
            while (Volatile.Read(ref captureGeneration) == generation)
            {
                var bytesRead = read(readHandle, buffer, buffer.Length);
                if (bytesRead == 0)
                    break;

                if (bytesRead < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == EINTR)
                        continue;
                    throw new IOException($"Failed to read the captured stdout. Error: {error}");
                }

                var charCount = decoder.GetChars(buffer, 0, bytesRead, chars, 0);
                if (charCount > 0 && Volatile.Read(ref captureGeneration) == generation)
                    Console.Write(chars, 0, charCount);
            }
        }
        catch (Exception e)
        {
            // the failure is picked up by the teardown after it joined this thread
            if (Volatile.Read(ref captureGeneration) == generation)
                readerFailure = e;
        }
        finally
        {
            if (Volatile.Read(ref captureGeneration) != generation)
                CloseHandle(ref readHandle);
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int pipe(int[] pipefd);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int dup(int oldfd);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int dup2(int oldfd, int newfd);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int read(int fd, byte[] buf, int count);

    [DllImport("libc", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern int close(int fd);
#pragma warning restore SYSLIB1054
}
