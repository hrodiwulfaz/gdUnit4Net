// Copyright (c) 2025 Mike Schulze
// MIT License - See LICENSE file in the repository root for full license text

namespace GdUnit4.Core.Runners;

using System;
using System.Diagnostics.CodeAnalysis;

using Api;

using Godot;

/// <summary>
///     The GdUnit4Net test runner scene.
/// </summary>
public partial class GdUnit4TestRunnerSceneCore : SceneTree
{
    /// <inheritdoc />
    [SuppressMessage(
        "Reliability",
        "CA2000:Dispose objects before losing scope",
        Justification = "TestRunner disposal is managed by Godot internals.")]
    public override void _Initialize()
    {
        try
        {
            Root.AddChild(new TestRunner());
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            GD.PrintErr("Exception", e.Message);
            Quit(100); // Exit with error code
        }
    }

    // ReSharper disable once PartialTypeWithSinglePart
    private sealed partial class TestRunner : Node
    {
        private const string PIPE_NAME_ARGUMENT = "--pipe-name";
        private const string LOG_FILE_ARGUMENT = "--log-file";
        private const string GDUNIT_LOG_FILE_ARGUMENT = "--gdunit-log-file";
        private const string GODOT_LOG_PATH_SETTING = "debug/file_logging/log_path";

        public TestRunner()
        {
            Logger = new GodotLogger();
            ApplyResolvedLogFilePath();
            Server = new GodotGdUnit4RestServer(Logger, ResolvePipeName());
        }

        private ITestEngineLogger Logger { get; }

        private GodotGdUnit4RestServer Server { get; }

        public override void _Ready() => _ = Server.Start();

        public override void _Process(double delta) => _ = Server.Process();

        public override void _Notification(int what)
        {
            if (what == NotificationPredelete)
                Server.Stop();
        }

        private static string ResolvePipeName()
        {
            var pipeName = ResolveOptionalArgument(PIPE_NAME_ARGUMENT);
            if (string.IsNullOrWhiteSpace(pipeName))
                throw new InvalidOperationException("Missing required '--pipe-name' argument.");

            return pipeName;
        }

        private static void ApplyResolvedLogFilePath()
        {
            var logFilePath = ResolveOptionalArgument(LOG_FILE_ARGUMENT) ?? ResolveOptionalArgument(GDUNIT_LOG_FILE_ARGUMENT);
            if (!string.IsNullOrWhiteSpace(logFilePath))
                ProjectSettings.SetSetting(GODOT_LOG_PATH_SETTING, logFilePath);
        }

        private static string? ResolveOptionalArgument(string argumentName)
        {
            var args = OS.GetCmdlineArgs();
            for (var index = 0; index < args.Length; index++)
            {
                if (args[index] == argumentName && index < args.Length - 1)
                    return args[index + 1];

                var argumentPrefix = $"{argumentName}=";
                if (args[index].StartsWith(argumentPrefix, StringComparison.Ordinal))
                    return args[index][argumentPrefix.Length..];
            }

            return null;
        }
    }
}
