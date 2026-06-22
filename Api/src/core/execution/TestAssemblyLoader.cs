// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Execution;

using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;

internal static class TestAssemblyLoader
{
    private static readonly ConcurrentDictionary<string, byte> DependencySearchDirectories = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object ResolveLock = new();
    private static bool isResolverRegistered;

    internal static Type ResolveType(string assemblyPath, string managedType)
    {
        var fullAssemblyPath = Path.GetFullPath(assemblyPath);
        if (!File.Exists(fullAssemblyPath))
        {
            throw new FileNotFoundException(
                $"Cannot load test assembly '{fullAssemblyPath}' while resolving managed type '{managedType}'. Set <GodotProjectPath> to the real Godot project root and ensure the external test assembly path is valid.",
                fullAssemblyPath);
        }

        RegisterDependencyDirectory(Path.GetDirectoryName(fullAssemblyPath));
        RegisterResolver();

        var assemblyName = AssemblyName.GetAssemblyName(fullAssemblyPath);
        var loadedAssembly = FindLoadedAssembly(assemblyName);
        var loadedType = loadedAssembly?.GetType(managedType, false);
        if (loadedType != null)
            return loadedType;

        var assembly = loadedAssembly ?? LoadAssemblyFromPath(fullAssemblyPath, assemblyName, managedType);
        var type = assembly.GetType(managedType);
        if (type != null)
            return type;

        var alreadyLoadedType = FindTypeOnLoadedAssemblies(managedType);
        if (alreadyLoadedType != null)
            return alreadyLoadedType;

        throw new TypeLoadException(
            $"Could not find managed type '{managedType}' in test assembly '{fullAssemblyPath}'. Loaded assembly: '{assembly.FullName}'. Dependency search directories: {FormatDependencySearchDirectories()}.");
    }

    private static Assembly LoadAssemblyFromPath(string assemblyPath, AssemblyName assemblyName, string managedType)
    {
        try
        {
            return AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
        {
            var loadedAssembly = FindLoadedAssembly(assemblyName);
            if (loadedAssembly != null)
                return loadedAssembly;

            throw new InvalidOperationException(
                $"Failed to load test assembly '{assemblyPath}' while resolving managed type '{managedType}'. Dependency search directories: {FormatDependencySearchDirectories()}. Ensure test assembly dependencies are copied next to the external test DLL and <GodotProjectPath> points to the Godot project root.",
                ex);
        }
    }

    private static void RegisterResolver()
    {
        if (isResolverRegistered)
            return;

        lock (ResolveLock)
        {
            if (isResolverRegistered)
                return;

            AssemblyLoadContext.Default.Resolving += ResolveDependency;
            isResolverRegistered = true;
        }
    }

    private static Assembly? ResolveDependency(AssemblyLoadContext context, AssemblyName assemblyName)
    {
        var loadedAssembly = FindLoadedAssembly(assemblyName);
        if (loadedAssembly != null)
            return loadedAssembly;

        foreach (var directory in DependencySearchDirectories.Keys)
        {
            var candidatePath = Path.Combine(directory, assemblyName.Name + ".dll");
            if (!File.Exists(candidatePath))
                continue;

            try
            {
                return context.LoadFromAssemblyPath(Path.GetFullPath(candidatePath));
            }
            catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException)
            {
                Console.Error.WriteLine($"Failed to resolve dependency '{assemblyName.FullName}' from '{candidatePath}': {ex.Message}");
            }
        }

        return null;
    }

    private static Assembly? FindLoadedAssembly(AssemblyName assemblyName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var loadedName = assembly.GetName();
            if (AssemblyName.ReferenceMatchesDefinition(loadedName, assemblyName) || string.Equals(loadedName.Name, assemblyName.Name, StringComparison.Ordinal))
                return assembly;
        }

        return null;
    }

    private static Type? FindTypeOnLoadedAssemblies(string managedType)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var type = assembly.GetType(managedType, false);
            if (type != null)
                return type;
        }

        return null;
    }

    private static void RegisterDependencyDirectory(string? directory)
    {
        if (!string.IsNullOrWhiteSpace(directory))
            _ = DependencySearchDirectories.TryAdd(Path.GetFullPath(directory), 0);
    }

    private static string FormatDependencySearchDirectories()
    {
        var directories = DependencySearchDirectories.Keys.OrderBy(directory => directory, StringComparer.Ordinal).ToArray();
        return directories.Length == 0 ? "<none>" : string.Join(", ", directories);
    }
}
