using System.Text.Json.Serialization;

namespace CaYaScreenBridge.Core.Config;

/// <summary>
/// A saved calibration for one display arrangement. Keyed by the configuration id so that a laptop
/// docked at home and the same laptop docked at the office each keep their own calibration.
/// </summary>
public sealed class LayoutProfile
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DateTimeOffset LastUsedUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<DisplayOverride> Displays { get; set; } = new();

    public DisplayOverride? Find(string stableId) =>
        Displays.FirstOrDefault(d => string.Equals(d.StableId, stableId, StringComparison.OrdinalIgnoreCase));

    public DisplayOverride GetOrCreate(string stableId)
    {
        DisplayOverride? existing = Find(stableId);
        if (existing is not null)
        {
            return existing;
        }

        var created = new DisplayOverride { StableId = stableId };
        Displays.Add(created);
        return created;
    }
}

/// <summary>
/// Per display corrections the user made in the layout editor. Every field is nullable: null means
/// "keep whatever the system reported", which is what lets a partial calibration coexist with EDID
/// data for the displays that were never touched.
/// </summary>
public sealed class DisplayOverride
{
    public string StableId { get; set; } = string.Empty;

    public string? Label { get; set; }

    public double? PhysicalWidthMm { get; set; }

    public double? PhysicalHeightMm { get; set; }

    public double? PhysicalLeftMm { get; set; }

    public double? PhysicalTopMm { get; set; }

    [JsonIgnore]
    public bool HasSize => PhysicalWidthMm is > 1 && PhysicalHeightMm is > 1;

    [JsonIgnore]
    public bool HasLocation => PhysicalLeftMm.HasValue && PhysicalTopMm.HasValue;

    public void ClearSize()
    {
        PhysicalWidthMm = null;
        PhysicalHeightMm = null;
    }

    public void ClearLocation()
    {
        PhysicalLeftMm = null;
        PhysicalTopMm = null;
    }
}
