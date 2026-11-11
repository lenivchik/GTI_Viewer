using FirebirdViewer.Localization;
using FirebirdViewer.ViewModels;

namespace FirebirdViewer.Models;

/// <summary>One well from the WELLBORES table.</summary>
public sealed class WellInfo : ObservableObject
{
    public long WellId { get; init; }
    public string Name { get; init; } = "";
    public string? Cluster { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Cluster)
        ? Name
        : Loc.F("Well_WithCluster", Name, Cluster);

    /// <summary>Re-read <see cref="Display"/> after the interface language changed.</summary>
    public void RefreshDisplay() => OnPropertyChanged(nameof(Display));

    public override string ToString() => Display;
}

/// <summary>One race (спуск) from the RACES table.</summary>
public sealed class RaceInfo : ObservableObject
{
    public long RaceId { get; init; }
    public long WellId { get; init; }
    public int Number { get; init; }
    public System.DateTime? StartTime { get; init; }
    public System.DateTime? StopTime { get; init; }

    public string Display
    {
        get
        {
            if (IsAllRaces) return Loc.T("Race_All");
            var range = "";
            if (StartTime.HasValue && StopTime.HasValue)
                range = $"{StartTime:dd.MM.yyyy} – {StopTime:dd.MM.yyyy}";
            else if (StartTime.HasValue)
                range = Loc.F("Race_Since", StartTime.Value.ToString("dd.MM.yyyy"));
            var number = Loc.F("Race_Number", Number);
            return string.IsNullOrEmpty(range) ? number : $"{number}  ·  {range}";
        }
    }

    /// <summary>Re-read <see cref="Display"/> after the interface language changed.</summary>
    public void RefreshDisplay() => OnPropertyChanged(nameof(Display));

    public override string ToString() => Display;

    /// <summary>Sentinel used in the combo to mean "all races for this well".</summary>
    public static RaceInfo AllRaces { get; } = new()
    {
        RaceId = -1,
        WellId = 0,
        Number = 0
    };

    public bool IsAllRaces => RaceId < 0;
}
