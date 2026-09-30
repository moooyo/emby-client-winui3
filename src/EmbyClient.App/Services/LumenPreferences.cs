namespace EmbyClient.App.Services;

/// <summary>Client preferences stored independently of saved account credentials.</summary>
public sealed record LumenPreferences
{
    // Setter-backed properties preserve initializer defaults for omitted source-generated JSON fields.
    public string Theme { get; set; } = "Dark";
    public string Accent { get; set; } = "Gold";
    public bool HeroRotation { get; set; } = true;
    public int PosterColumns { get; set; } = 8;
    public bool ShowWatchedMarks { get; set; } = true;

    public bool AutoPlayNext { get; set; } = true;
    public int NextEpisodeCountdownSeconds { get; set; } = 15;
    public string IntroSkipMode { get; set; } = "ShowButton";
    public string ResumeMode { get; set; } = "Continue";
    public bool PreferDirectPlay { get; set; } = true;
    // Zero removes the client cap; authenticated server limits still apply.
    public long LocalMaxBitrate { get; set; }
    public long InternetMaxBitrate { get; set; } = 20_000_000;

    public bool HardwareDecoding { get; set; } = true;
    public string HdrMode { get; set; } = "Auto";
    public bool MatchRefreshRate { get; set; }

    public string SubtitleLanguage { get; set; } = "chi";
    public string SubtitleMode { get; set; } = "Smart";
    public string SubtitleSize { get; set; } = "Medium";
    public bool SubtitleOutline { get; set; } = true;
    public string SubtitlePosition { get; set; } = "Bottom";
}
