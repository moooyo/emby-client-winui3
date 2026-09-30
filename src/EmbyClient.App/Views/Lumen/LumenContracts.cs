using EmbyClient.Api;

namespace EmbyClient.App.Views.Lumen;

public sealed class LumenPlayRequestEventArgs(
    BaseItemDto item,
    long startPositionTicks = 0,
    bool addToQueue = false,
    string? mediaSourceId = null,
    int? audioStreamIndex = null,
    int? subtitleStreamIndex = null) : EventArgs
{
    public BaseItemDto Item { get; } = item;
    public long StartPositionTicks { get; } = startPositionTicks;
    public bool AddToQueue { get; } = addToQueue;
    public string? MediaSourceId { get; } = mediaSourceId;
    public int? AudioStreamIndex { get; } = audioStreamIndex;
    public int? SubtitleStreamIndex { get; } = subtitleStreamIndex;
}
