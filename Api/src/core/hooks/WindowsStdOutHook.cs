// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Hooks;

using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

/// <summary>
///     Captures managed and native standard output on Windows.
/// </summary>
/// <remarks>
///     Every capture lifetime owns its own pipe handles and its own reader thread. Nothing is shared between
///     two captures, so output of a previous capture can never leak into a following one.
/// </remarks>
[SuppressMessage("Style", "IDE1006", Justification = "Windows system call names follow the Win32 naming conventions")]
[SuppressMessage(
    "StyleCop.CSharp.NamingRules",
    "SA1300:Element should begin with upper-case letter",
    Justification = "Windows system call names must match the kernel32 function names exactly")]
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1201:Elements should appear in the correct order",
    Justification = "P/Invoke declarations are grouped together for clarity at the end of the class")]
[SuppressMessage(
    "StyleCop.CSharp.OrderingRules",
    "SA1204:Static members should appear before non-static members",
    Justification = "P/Invoke declarations are grouped together for clarity at the end of the class")]
[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare finalizer",
    Justification = "The only native handle field is the borrowed stdout handle of the process, it must never be closed by this hook. Every owned resource is a SafeFileHandle.")]
internal sealed class WindowsStdOutHook : IStdOutHook
{
    private const int STD_OUTPUT_HANDLE = -11;
    private const int ERROR_INVALID_HANDLE = 6;
    private const int ERROR_BROKEN_PIPE = 109;

    private static readonly TimeSpan ReaderJoinTimeout = TimeSpan.FromSeconds(5);
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private readonly object captureLock = new();
    private readonly StdOutConsoleHook stdOutHook = new();

    private bool disposed;
    private bool isCapturing;
    private int captureGeneration;
    private IntPtr originalStdOutHandle;
    private SafeFileHandle? pipeReadHandle;
    private SafeFileHandle? pipeWriteHandle;
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

    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "The pipe handles are owned by the running capture and released by StopCaptureLocked, a failing start releases them in its finally")]
    public void StartCapture()
    {
        lock (captureLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            StdOutCaptureOwnership.Acquire(this);
            if (isCapturing)
                return;

            SafeFileHandle? readHandle = null;
            SafeFileHandle? writeHandle = null;
            var managedCaptureStarted = false;
            var stdOutRedirected = false;
            try
            {
                originalStdOutHandle = GetStdHandle(STD_OUTPUT_HANDLE);
                if (originalStdOutHandle == IntPtr.Zero || originalStdOutHandle == InvalidHandleValue)
                    throw new InvalidOperationException($"Failed to get the original stdout handle. Win32 error: {Marshal.GetLastWin32Error()}");

                var isPipeCreated = CreatePipe(out var createdReadHandle, out var createdWriteHandle, IntPtr.Zero, 0);
                readHandle = createdReadHandle;
                writeHandle = createdWriteHandle;
                if (!isPipeCreated)
                    throw new InvalidOperationException($"Failed to create the stdout capture pipe. Win32 error: {Marshal.GetLastWin32Error()}");

                stdOutHook.StartCapture();
                managedCaptureStarted = true;

                if (!SetStdHandle(STD_OUTPUT_HANDLE, createdWriteHandle.DangerousGetHandle()))
                    throw new InvalidOperationException($"Failed to redirect stdout to the capture pipe. Win32 error: {Marshal.GetLastWin32Error()}");
                stdOutRedirected = true;

                pipeReadHandle = createdReadHandle;
                pipeWriteHandle = createdWriteHandle;
                readerFailure = null;
                var generation = ++captureGeneration;
                readThread = new Thread(() => ReadPipeOutput(createdReadHandle, generation))
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
                        _ = SetStdHandle(STD_OUTPUT_HANDLE, originalStdOutHandle);
                    if (managedCaptureStarted)
                        stdOutHook.StopCapture();
                    writeHandle?.Dispose();
                    readHandle?.Dispose();
                    pipeReadHandle = null;
                    pipeWriteHandle = null;
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

        // restore the native stdout first, from here on nothing new is written into the capture pipe
        var isStdOutRestored = SetStdHandle(STD_OUTPUT_HANDLE, originalStdOutHandle);
        var restoreError = isStdOutRestored ? 0 : Marshal.GetLastWin32Error();

        // closing the capture write end signals EOF so the reader drains the remaining data and ends
        pipeWriteHandle?.Dispose();
        pipeWriteHandle = null;

        var reader = readThread;
        readThread = null;
        var isReaderStopped = reader == null || reader.Join(ReaderJoinTimeout);

        // retires this capture generation, an abandoned reader stops writing and releases its own read handle
        captureGeneration++;

        stdOutHook.StopCapture();

        if (isReaderStopped)
            pipeReadHandle?.Dispose();
        pipeReadHandle = null;

        StdOutCaptureOwnership.Release(this);

        if (!isStdOutRestored)
            return new InvalidOperationException($"Failed to restore the native stdout handle. Win32 error: {restoreError}");

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
    ///     abandoned reader closes the read handle itself once its blocking read returns.
    /// </remarks>
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "The reader failure is stored and reported after stdout is restored")]
    private void ReadPipeOutput(SafeFileHandle readHandle, int generation)
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];

        try
        {
            while (Volatile.Read(ref captureGeneration) == generation)
            {
                if (!ReadFile(readHandle, buffer, (uint)buffer.Length, out var bytesRead, IntPtr.Zero))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error is ERROR_BROKEN_PIPE or ERROR_INVALID_HANDLE)
                        break;
                    throw new IOException($"Failed to read the captured stdout. Win32 error: {error}");
                }

                if (bytesRead == 0)
                    break;

                var charCount = decoder.GetChars(buffer, 0, (int)bytesRead, chars, 0);
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
                readHandle.Dispose();
        }
    }

#pragma warning disable SYSLIB1054
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.UserDirectories)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.UserDirectories)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr hHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.UserDirectories)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32 | DllImportSearchPath.UserDirectories)]
    private static extern bool ReadFile(SafeFileHandle hFile, [Out] byte[] lpBuffer, uint nNumberOfBytesToRead, out uint lpNumberOfBytesRead, IntPtr lpOverlapped);
#pragma warning restore SYSLIB1054
}
