// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Reflection;

/// <summary>
///     Decides from <see cref="OptInAttribute" /> whether a test runs.
/// </summary>
internal static class OptInFilter
{
    /// <summary>
    ///     Gets the names a test needs: those of its suite first, then its own, each once.
    /// </summary>
    /// <param name="method">The test method, as reflected from its suite type.</param>
    /// <returns>The required names, empty when the test needs no opt-in.</returns>
    internal static IReadOnlyList<string> RequiredNames(MethodInfo method)
    {
        var suiteAttributes = method.ReflectedType is { } suite
            ? suite.GetCustomAttributes<OptInAttribute>(true)
            : [];
        return
        [
            .. suiteAttributes
                .Concat(method.GetCustomAttributes<OptInAttribute>(true))
                .Select(attribute => attribute.Name.Trim())
                .Where(name => name.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
        ];
    }

    /// <summary>
    ///     Gets why a test does not run, or <see langword="null" /> when it runs.
    /// </summary>
    /// <param name="method">The test method, as reflected from its suite type.</param>
    /// <param name="optedIn">The value of <see cref="OptInAttribute.ENVIRONMENT_VARIABLE" />, or <see langword="null" /> when it is not set.</param>
    /// <returns><c>needs opt-in &lt;names&gt;</c> with every name the test needs, when one of them is not listed.</returns>
    internal static string? SkipReason(MethodInfo method, string? optedIn)
    {
        var required = RequiredNames(method);
        if (required.Count == 0)
            return null;

        var listed = (optedIn ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return required.All(name => listed.Contains(name, StringComparer.OrdinalIgnoreCase))
            ? null
            : $"needs opt-in {string.Join(", ", required)} ({OptInAttribute.ENVIRONMENT_VARIABLE})";
    }
}
