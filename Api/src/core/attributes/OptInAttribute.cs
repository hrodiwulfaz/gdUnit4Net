// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

// ReSharper disable once CheckNamespace
// Need to be placed in the root namespace to be accessible by the test runner.
namespace GdUnit4;

using System;

/// <summary>
///     Runs a test, or every test of a suite, only in a run that opts in to <see cref="Name" />.
/// </summary>
/// <remarks>
///     A run opts in by listing names, separated by commas, in the <see cref="ENVIRONMENT_VARIABLE" /> environment
///     variable; names are compared ignoring case and surrounding white space. The attribute may be given more than
///     once. A test runs only when every name of its own attributes and of its suite's attributes is listed.
///     Otherwise it is reported as skipped with the reason <c>needs opt-in &lt;names&gt;</c>, and neither the test
///     nor its <see cref="BeforeTestAttribute" /> and <see cref="AfterTestAttribute" /> stages run. When no test of a
///     suite runs, its <see cref="BeforeAttribute" /> and <see cref="AfterAttribute" /> stages do not run either.
/// </remarks>
/// <example>
///     <code>
/// [TestSuite]
/// [OptIn("Network")]
/// public class ServerTests
/// {
///     [TestCase]
///     [OptIn("Accounts")]
///     public void SignInRuns() { } // runs only with GDUNIT4_OPT_IN=Network,Accounts
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = true, Inherited = true)]
public sealed class OptInAttribute : Attribute
{
    /// <summary>
    ///     The environment variable that lists the names a run opts in to.
    /// </summary>
    public const string ENVIRONMENT_VARIABLE = "GDUNIT4_OPT_IN";

    /// <summary>
    ///     Initializes a new instance of the <see cref="OptInAttribute" /> class.
    /// </summary>
    /// <param name="name">The name a run must list in <see cref="ENVIRONMENT_VARIABLE" /> to run the test.</param>
    public OptInAttribute(string name) => Name = name;

    /// <summary>
    ///     Gets the name a run must list in <see cref="ENVIRONMENT_VARIABLE" /> to run the test.
    /// </summary>
    public string Name { get; }
}
