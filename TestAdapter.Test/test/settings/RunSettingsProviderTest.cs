namespace GdUnit4.TestAdapter.Test.Settings;

using System.Collections.Generic;
using System.Xml;

using Microsoft.VisualStudio.TestPlatform.ObjectModel.Adapter;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Moq;

using TestAdapter.Settings;

[TestClass]
public class RunSettingsProviderTest
{
    private const string XML_SETTINGS_WITHOUT_ENV =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <RunSettings>
            <RunConfiguration>
                <MaxCpuCount>1</MaxCpuCount>
                <TestAdaptersPaths>.</TestAdaptersPaths>
                <ResultsDirectory>./TestResults</ResultsDirectory>
                <TargetFrameworks>net7.0;net8.0</TargetFrameworks>
                <!-- set default session timeout to 5m -->
                <TestSessionTimeout>500000</TestSessionTimeout>
                <TreatNoTestsAsError>true</TreatNoTestsAsError>
                <EnvironmentVariables>
                </EnvironmentVariables>
            </RunConfiguration>
        </RunSettings>
        """;

    private const string XML_SETTINGS_WITHOUT_ENVIRONMENT_VARIABLES_ENTRY =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <RunSettings>
            <RunConfiguration>
                <MaxCpuCount>1</MaxCpuCount>
                <TestAdaptersPaths>.</TestAdaptersPaths>
                <ResultsDirectory>./TestResults</ResultsDirectory>
                <TargetFrameworks>net7.0;net8.0</TargetFrameworks>
                <!-- set default session timeout to 5m -->
                <TestSessionTimeout>500000</TestSessionTimeout>
                <TreatNoTestsAsError>true</TreatNoTestsAsError>
            </RunConfiguration>
        </RunSettings>
        """;

    private const string GDUNIT_SETTINGS =
        """
        <GdUnit4>
            <Parameters>"--minimized"</Parameters>
            <DisplayName>FullyQualifiedName</DisplayName>
            <CaptureStdOut>true</CaptureStdOut>
            <CompileProcessTimeout>60000</CompileProcessTimeout>
            <TestCaseTimeout>300000</TestCaseTimeout>
            <UseUniqueLogFiles>true</UseUniqueLogFiles>
            <LogFileRoot>tmp/gdunit-runs-custom</LogFileRoot>
            <UseUniqueUserDataDir>true</UseUniqueUserDataDir>
            <RunnerSceneDirectory>Data/Testing/Generated/GdUnit4</RunnerSceneDirectory>
            <GodotProjectPath>D:\Projects\Outpostia</GodotProjectPath>
        </GdUnit4>
        """;

    private const string XML_SETTINGS =
        """
        <?xml version="1.0" encoding="utf-8"?>
        <RunSettings>
            <RunConfiguration>
                <MaxCpuCount>1</MaxCpuCount>
                <TestAdaptersPaths>.</TestAdaptersPaths>
                <ResultsDirectory>./TestResults</ResultsDirectory>
                <TargetFrameworks>net7.0;net8.0</TargetFrameworks>
                <!-- set default session timeout to 5m -->
                <TestSessionTimeout>500000</TestSessionTimeout>
                <TreatNoTestsAsError>true</TreatNoTestsAsError>
                <EnvironmentVariables>
                    <GODOT_BIN>D:\development\Godot_v4.2.2-stable_mono_win64\Godot_v4.2.2-stable_mono_win64.exe</GODOT_BIN>
                    <EnvValue>42</EnvValue>
                </EnvironmentVariables>
            </RunConfiguration>
        </RunSettings>
        """;

    [TestMethod]
    public void LoadSettingsKeepsTestCaseTimeoutDisabledWhenNotSpecified()
    {
        const string settingsWithoutTestCaseTimeout =
            """
            <GdUnit4>
                <CaptureStdOut>true</CaptureStdOut>
            </GdUnit4>
            """;

        var provider = new GdUnit4SettingsProvider();
        using var stringReader = new StringReader(settingsWithoutTestCaseTimeout);
        using var xmlReader = XmlReader.Create(stringReader);
        provider.Load(xmlReader);

        var runSettings = new Mock<IRunSettings>();
        _ = runSettings
            .Setup(settings => settings.GetSettings(GdUnit4Settings.RUN_SETTINGS_XML_NODE))
            .Returns(provider);
        var discoveryContext = new Mock<IDiscoveryContext>();
        _ = discoveryContext.SetupGet(context => context.RunSettings).Returns(runSettings.Object);

        var settings = GdUnit4SettingsProvider.LoadSettings(discoveryContext.Object);

        Assert.AreEqual(-1, settings.TestCaseTimeout);
    }

    [TestMethod]
    public void LoadSettingsReadsUniqueRuntimeSettings()
    {
        var provider = new GdUnit4SettingsProvider();
        using var stringReader = new StringReader(GDUNIT_SETTINGS);
        using var xmlReader = XmlReader.Create(stringReader);
        provider.Load(xmlReader);

        var runSettings = new Mock<IRunSettings>();
        _ = runSettings
            .Setup(settings => settings.GetSettings(GdUnit4Settings.RUN_SETTINGS_XML_NODE))
            .Returns(provider);
        var discoveryContext = new Mock<IDiscoveryContext>();
        _ = discoveryContext.SetupGet(context => context.RunSettings).Returns(runSettings.Object);

        var settings = GdUnit4SettingsProvider.LoadSettings(discoveryContext.Object);

        Assert.AreEqual("\"--minimized\"", settings.Parameters);
        Assert.AreEqual(DisplayNameOptions.FullyQualifiedName, settings.DisplayName);
        Assert.IsTrue(settings.CaptureStdOut);
        Assert.AreEqual(60000, settings.CompileProcessTimeout);
        Assert.AreEqual(300000, settings.TestCaseTimeout);
        Assert.IsTrue(settings.UseUniqueLogFiles);
        Assert.AreEqual("tmp/gdunit-runs-custom", settings.LogFileRoot);
        Assert.IsTrue(settings.UseUniqueUserDataDir);
        Assert.AreEqual("Data/Testing/Generated/GdUnit4", settings.RunnerSceneDirectory);
        Assert.AreEqual("D:\\Projects\\Outpostia", settings.GodotProjectPath);
        Assert.IsFalse(settings.ProjectSetupCache, "ProjectSetupCache stays disabled unless the runsettings enable it");
    }

    [TestMethod]
    [DataRow("true", true)]
    [DataRow("false", false)]
    public void LoadSettingsReadsProjectSetupCache(string configuredValue, bool expected)
    {
        // `dotnet test -- GdUnit4.ProjectSetupCache=false` reaches the adapter as this element
        var provider = new GdUnit4SettingsProvider();
        using var stringReader = new StringReader($"<GdUnit4><ProjectSetupCache>{configuredValue}</ProjectSetupCache></GdUnit4>");
        using var xmlReader = XmlReader.Create(stringReader);
        provider.Load(xmlReader);

        var runSettings = new Mock<IRunSettings>();
        _ = runSettings
            .Setup(settings => settings.GetSettings(GdUnit4Settings.RUN_SETTINGS_XML_NODE))
            .Returns(provider);
        var discoveryContext = new Mock<IDiscoveryContext>();
        _ = discoveryContext.SetupGet(context => context.RunSettings).Returns(runSettings.Object);

        var settings = GdUnit4SettingsProvider.LoadSettings(discoveryContext.Object);

        Assert.AreEqual(expected, settings.ProjectSetupCache);
    }

    [TestMethod]
    public void GetEnvironmentVariables()
    {
        var environmentVariables = RunSettingsProvider.GetEnvironmentVariables(XML_SETTINGS);

        var expected = new Dictionary<string, string>
        {
            { "GODOT_BIN", "D:\\development\\Godot_v4.2.2-stable_mono_win64\\Godot_v4.2.2-stable_mono_win64.exe" },
            { "EnvValue", "42" }
        };
        CollectionAssert.AreEqual(expected, environmentVariables);
    }

    [TestMethod]
    public void GetEnvironmentVariablesWithoutEnv()
    {
        var environmentVariables = RunSettingsProvider.GetEnvironmentVariables(XML_SETTINGS_WITHOUT_ENV);

        var expected = new Dictionary<string, string>();
        CollectionAssert.AreEqual(expected, environmentVariables);
    }

    [TestMethod]
    public void GetEnvironmentVariablesWithoutEnvironmentVariablesEntry()
    {
        var environmentVariables = RunSettingsProvider.GetEnvironmentVariables(XML_SETTINGS_WITHOUT_ENVIRONMENT_VARIABLES_ENTRY);

        var expected = new Dictionary<string, string>();
        CollectionAssert.AreEqual(expected, environmentVariables);
    }
}
