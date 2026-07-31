using CaYaScreenBridge.Core.Config;
using CaYaScreenBridge.Core.Diagnostics;
using Xunit;

namespace CaYaScreenBridge.Core.Tests;

public sealed class ConfigImportExportTests
{
    [Fact]
    public void ExportedSettingsCanBeImportedWithoutLosingProfilesOrResistance()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "CaYaScreenBridge.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var store = new ConfigStore(directory, NullLogSink.Instance);
            string exportPath = Path.Combine(directory, "export.json");

            var edge = new DisplayResistanceSettings { StableId = "DISPLAY-A" };
            edge.Right.UseCustomResistance = true;
            edge.Right.ResistanceMm = 17.5;
            edge.Right.SpeedAdaptive = true;

            var config = new AppConfig
            {
                Enabled = false,
                Transition = new TransitionSettings
                {
                    BorderResistanceMm = 6,
                    SpeedAdaptiveResistance = true,
                    DisplayResistance = new List<DisplayResistanceSettings> { edge },
                },
                Profiles = new List<LayoutProfile>
                {
                    new() { Id = "profile-a", Name = "Desk" },
                },
            };

            store.Export(config, exportPath);
            AppConfig imported = store.Import(exportPath);

            Assert.False(imported.Enabled);
            Assert.True(imported.Transition.SpeedAdaptiveResistance);
            Assert.Single(imported.Transition.DisplayResistance);
            Assert.Equal(17.5, imported.Transition.DisplayResistance[0].Right.ResistanceMm, 6);
            Assert.True(imported.Transition.DisplayResistance[0].Right.SpeedAdaptive);
            Assert.Single(imported.Profiles);
            Assert.Equal("profile-a", imported.Profiles[0].Id);
            Assert.Equal(AppConfig.CurrentSchemaVersion, imported.SchemaVersion);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void InvalidImportDoesNotProduceAConfiguration()
    {
        string directory = Path.Combine(
            Path.GetTempPath(),
            "CaYaScreenBridge.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            string invalidPath = Path.Combine(directory, "invalid.json");
            File.WriteAllText(invalidPath, "not-json");
            var store = new ConfigStore(directory, NullLogSink.Instance);

            Assert.Throws<InvalidDataException>(() => store.Import(invalidPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
