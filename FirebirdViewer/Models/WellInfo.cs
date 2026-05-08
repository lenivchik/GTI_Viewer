namespace FirebirdViewer.Models;

/// <summary>One well from the WELLBORES table.</summary>
public sealed class WellInfo
{
    public long WellId { get; init; }
    public string Name { get; init; } = "";
    public string? Cluster { get; init; }

    public string Display => string.IsNullOrWhiteSpace(Cluster)
        ? Name
        : $"{Name}  ·  куст {Cluster}";

    public override string ToString() => Display;
}

/// <summary>One race (спуск) from the RACES table.</summary>
public sealed class RaceInfo
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
            if (IsAllRaces) return "Все рейсы";
            var range = "";
            if (StartTime.HasValue && StopTime.HasValue)
                range = $"{StartTime:dd.MM.yyyy} – {StopTime:dd.MM.yyyy}";
            else if (StartTime.HasValue)
                range = $"с {StartTime:dd.MM.yyyy}";
            return string.IsNullOrEmpty(range) ? $"Рейс №{Number}" : $"Рейс №{Number}  ·  {range}";
        }
    }

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
