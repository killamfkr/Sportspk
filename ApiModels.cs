namespace Jellyfin.Plugin.StreamedPk;

public sealed class ApiSport
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
}

public sealed class ApiSourceRef
{
    public string Source { get; set; } = string.Empty;

    public string Id { get; set; } = string.Empty;
}

public sealed class ApiTeamSide
{
    public string Name { get; set; } = string.Empty;

    public string Badge { get; set; } = string.Empty;
}

public sealed class ApiMatchTeams
{
    public ApiTeamSide? Home { get; set; }

    public ApiTeamSide? Away { get; set; }
}

public sealed class ApiMatch
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public long Date { get; set; }

    public string? Poster { get; set; }

    public bool Popular { get; set; }

    public ApiMatchTeams? Teams { get; set; }

    public List<ApiSourceRef>? Sources { get; set; }
}

public sealed class ApiStream
{
    public string Id { get; set; } = string.Empty;

    public int StreamNo { get; set; }

    public string Language { get; set; } = string.Empty;

    public bool Hd { get; set; }

    public string EmbedUrl { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;
}
