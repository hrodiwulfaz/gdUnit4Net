namespace GdUnit4.Tests.Core;

using System;
using GdUnit4.Core.Execution.Exceptions;

using static Assertions;

[TestSuite]
public class TestSuiteWithExpectedExceptions
{
    [TestCase]
    [ThrowsException(typeof(ExecutionTimeoutException), "The execution has timed out after 100ms.")]
    public void ExpectExecutionTimeoutException()
        => throw new ExecutionTimeoutException("The execution has timed out after 100ms.");

    [TestCase]
    [ThrowsException(typeof(TestFailedException), "Expecting: 'False' but is 'True'")]
    public void ExpectTestFailedException() =>
        AssertBool(true).IsFalse();


    [TestCase]
    [ThrowsException(typeof(TestFailedException), "Expecting: 'False' but is 'True'", 25)]
    public void ExpectTestFailedExceptionWithLineNumber() =>
        AssertBool(true).IsFalse();

    [TestCase]
    [ThrowsException(typeof(ArgumentException), "The argument 'message' is invalid")]
    public void ExpectArgumentException() =>
        throw new ArgumentException("The argument 'message' is invalid");

    [TestCase]
    [ThrowsException(typeof(NullReferenceException))]
    public void ExpectNullException()
    {
        string? value = null;
        // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
        value!.Contains(""); // This will throw NullReferenceException
    }
}
