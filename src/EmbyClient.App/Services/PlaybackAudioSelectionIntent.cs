namespace EmbyClient.App.Services;

/// <summary>Separates a displayed default audio track from an explicit playback override.</summary>
public sealed class PlaybackAudioSelectionIntent
{
    public int? DisplayIndex { get; private set; }
    public int? RequestIndex { get; private set; }

    public void UseDefault(int? index)
    {
        if (index is < 0) throw new ArgumentOutOfRangeException(nameof(index));
        DisplayIndex = index;
        RequestIndex = null;
    }

    public void Select(int index)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        DisplayIndex = RequestIndex = index;
    }
}
