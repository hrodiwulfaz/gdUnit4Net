// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Hooks;

using System;

/// <summary>
///     Serializes ownership of the process global standard output redirection.
/// </summary>
/// <remarks>
///     <para>
///         Both the managed <see cref="Console.Out" /> writer and the native stdout handle are process global.
///         Two capture owners cannot redirect them independently, so a second owner is rejected instead of
///         silently interleaving output into the wrong capture buffer.
///     </para>
///     <para>
///         Ownership is reserved by the platform hook handed out by <see cref="StdOutHookFactory" />, which owns both
///         the managed and the native redirection. Re-acquiring by the current owner is a no-op so that repeated
///         <see cref="IStdOutHook.StartCapture" /> calls stay harmless.
///     </para>
/// </remarks>
internal static class StdOutCaptureOwnership
{
    private static readonly object SyncLock = new();

    private static object? currentOwner;

    public static void Acquire(object owner)
    {
        lock (SyncLock)
        {
            if (currentOwner != null && !ReferenceEquals(currentOwner, owner))
            {
                throw new InvalidOperationException(
                    $"Standard output capture is already owned by '{currentOwner.GetType().Name}'."
                    + " Concurrent stdout capture is not supported because stdout redirection is process global.");
            }

            currentOwner = owner;
        }
    }

    public static void Release(object owner)
    {
        lock (SyncLock)
        {
            if (ReferenceEquals(currentOwner, owner))
                currentOwner = null;
        }
    }
}
