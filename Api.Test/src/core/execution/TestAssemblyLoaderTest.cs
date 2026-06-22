namespace GdUnit4.Tests.Core.Execution;

using System;
using System.Diagnostics;
using System.IO;
using System.Text;

using GdUnit4.Core.Execution;

using static Assertions;

[TestSuite]
public sealed class TestAssemblyLoaderTest
{
    private string? TestTempDirectory { get; set; }

    [Before]
    public void Before()
    {
        TestTempDirectory = Path.Combine(Path.GetTempPath(), "gdunit_test_assembly_loader_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(TestTempDirectory);
    }

    [After]
    public void After()
    {
        try
        {
            if (TestTempDirectory != null && Directory.Exists(TestTempDirectory))
                Directory.Delete(TestTempDirectory, true);
        }
        catch
        {
            // Ignore cleanup failures from loaded assemblies.
        }
    }

    [TestCase]
    public void ResolveTypeUsesAlreadyLoadedAssembly()
    {
        var assemblyPath = typeof(TestAssemblyLoaderTest).Assembly.Location;

        var resolvedType = TestAssemblyLoader.ResolveType(assemblyPath, typeof(TestAssemblyLoaderTest).FullName!);

        AssertObject(resolvedType).IsEqual(typeof(TestAssemblyLoaderTest));
    }

    [TestCase]
    public void ResolveTypeLoadsExternalAssemblyAndDependencyFromAssemblyDirectory()
    {
        var externalAssembly = BuildExternalTestAssembly();

        var resolvedType = TestAssemblyLoader.ResolveType(externalAssembly.AssemblyPath, externalAssembly.TypeName);

        AssertThat(resolvedType.FullName).IsEqual(externalAssembly.TypeName);
        AssertThat(resolvedType.BaseType!.FullName).IsEqual(externalAssembly.BaseTypeName);
    }

    [TestCase]
    public void ResolveTypeReportsMissingExternalAssemblyPath()
    {
        var missingAssemblyPath = Path.Combine(TestTempDirectory!, "missing", "ExternalTests.dll");

        AssertThrown(() => TestAssemblyLoader.ResolveType(missingAssemblyPath, "ExternalTests.MissingSuite"))
            .StartsWithMessage($"Cannot load test assembly '{Path.GetFullPath(missingAssemblyPath)}'");
    }

    private (string AssemblyPath, string TypeName, string BaseTypeName) BuildExternalTestAssembly()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var dependencyName = "ExternalDependency" + suffix;
        var testsName = "ExternalTests" + suffix;
        var dependencyDirectory = Path.Combine(TestTempDirectory!, dependencyName);
        var testDirectory = Path.Combine(TestTempDirectory!, testsName);
        Directory.CreateDirectory(dependencyDirectory);
        Directory.CreateDirectory(testDirectory);

        File.WriteAllText(
            Path.Combine(dependencyDirectory, dependencyName + ".csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>{{dependencyName}}</AssemblyName>
                <RootNamespace>{{dependencyName}}</RootNamespace>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(dependencyDirectory, "ExternalBase.cs"),
            $$"""
            namespace {{dependencyName}};

            public abstract class ExternalBase
            {
                public string Source => "external-dependency";
            }
            """);
        File.WriteAllText(
            Path.Combine(testDirectory, testsName + ".csproj"),
            $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
                <AssemblyName>{{testsName}}</AssemblyName>
                <RootNamespace>{{testsName}}</RootNamespace>
                <Nullable>enable</Nullable>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../{{dependencyName}}/{{dependencyName}}.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(
            Path.Combine(testDirectory, "ExternalSuite.cs"),
            $$"""
            namespace {{testsName}};

            public sealed class ExternalSuite : {{dependencyName}}.ExternalBase
            {
            }
            """);

        RunDotnetBuild(Path.Combine(testDirectory, testsName + ".csproj"));
        return (
            Path.Combine(testDirectory, "bin", "Debug", "net9.0", testsName + ".dll"),
            testsName + ".ExternalSuite",
            dependencyName + ".ExternalBase");
    }

    private static void RunDotnetBuild(string projectPath)
    {
        var output = new StringBuilder();
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", $"build \"{projectPath}\" --nologo --verbosity quiet")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(projectPath)!
            },
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data != null)
                _ = output.AppendLine(args.Data);
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data != null)
                _ = output.AppendLine(args.Data);
        };

        AssertThat(process.Start()).OverrideFailureMessage("Failed to start dotnet build for external assembly loader test.").IsTrue();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var completed = process.WaitForExit(60000);
        if (!completed)
            process.Kill(true);

        AssertThat(completed).OverrideFailureMessage("dotnet build timed out for external assembly loader test.\n" + output).IsTrue();
        AssertThat(process.ExitCode).OverrideFailureMessage("dotnet build failed for external assembly loader test.\n" + output).IsEqual(0);
    }
}
