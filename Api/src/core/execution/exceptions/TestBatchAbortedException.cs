// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution.Exceptions;

using System;

#pragma warning disable CA1064
internal sealed class TestBatchAbortedException : Exception
#pragma warning restore CA1064
{
    public TestBatchAbortedException()
    {
    }

    public TestBatchAbortedException(string message)
        : base(message)
    {
    }

    public TestBatchAbortedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
