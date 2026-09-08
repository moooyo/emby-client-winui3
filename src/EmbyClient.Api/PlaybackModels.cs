namespace EmbyClient.Api;

public sealed record PlaybackInfoRequest
{
    public string? UserId { get; init; }
    public string? MediaSourceId { get; init; }
    public long? MaxStreamingBitrate { get; init; }
    public long? StartTimeTicks { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public int? MaxAudioChannels { get; init; }
    public DeviceProfile? DeviceProfile { get; init; }
    public bool? EnableDirectPlay { get; init; }
    public bool? EnableDirectStream { get; init; }
    public bool? EnableTranscoding { get; init; }
    public bool? AllowVideoStreamCopy { get; init; }
    public bool? AllowAudioStreamCopy { get; init; }
    public bool? AllowInterlacedVideoStreamCopy { get; init; }
    public bool? IsPlayback { get; init; }
    public bool? AutoOpenLiveStream { get; init; }
    public string? LiveStreamId { get; init; }
    public string? CurrentPlaySessionId { get; init; }
}

public sealed record PlaybackInfoResponse
{
    public MediaSourceInfo[] MediaSources { get; init; } = [];
    public string? PlaySessionId { get; init; }
    public string? ErrorCode { get; init; }
}

public sealed record MediaSourceInfo
{
    public string? Id { get; init; }
    public string? Name { get; init; }
    public string? Container { get; init; }
    public long? Bitrate { get; init; }
    public long? Size { get; init; }
    public long? RunTimeTicks { get; init; }
    public string? Protocol { get; init; }
    public string? Path { get; init; }
    public bool? IsRemote { get; init; }
    public bool? SupportsDirectPlay { get; init; }
    public bool? SupportsDirectStream { get; init; }
    public bool? SupportsTranscoding { get; init; }
    public string? DirectStreamUrl { get; init; }
    public bool? AddApiKeyToDirectStreamUrl { get; init; }
    public string? TranscodingUrl { get; init; }
    public string? TranscodingContainer { get; init; }
    public string? TranscodingSubProtocol { get; init; }
    public Dictionary<string, string>? RequiredHttpHeaders { get; init; }
    public int? DefaultAudioStreamIndex { get; init; }
    public int? DefaultSubtitleStreamIndex { get; init; }
    public MediaStream[] MediaStreams { get; init; } = [];
    public bool? RequiresOpening { get; init; }
    public string? OpenToken { get; init; }
    public bool? RequiresClosing { get; init; }
    public string? LiveStreamId { get; init; }
    public bool? IsInfiniteStream { get; init; }
    public long? ContainerStartTimeTicks { get; init; }

    public override string ToString() => nameof(MediaSourceInfo);
}

public sealed record MediaStream
{
    public int Index { get; init; }
    public string? Type { get; init; }
    public string? Codec { get; init; }
    public string? Language { get; init; }
    public string? DisplayTitle { get; init; }
    public string? Title { get; init; }
    public bool? IsDefault { get; init; }
    public bool? IsForced { get; init; }
    public bool? IsHearingImpaired { get; init; }
    public bool? IsExternal { get; init; }
    public bool? IsTextSubtitleStream { get; init; }
    public int? Channels { get; init; }
    public string? ChannelLayout { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Profile { get; init; }
    public double? Level { get; init; }
    public int? BitDepth { get; init; }
    public bool? IsInterlaced { get; init; }
    public string? VideoRange { get; init; }
    public string? DeliveryMethod { get; init; }
    public string? DeliveryUrl { get; init; }

    public override string ToString() => $"{nameof(MediaStream)} {{ Index = {Index}, Type = {Type} }}";
}

public sealed record DeviceProfile
{
    public string? Name { get; init; }
    public long? MaxStreamingBitrate { get; init; }
    public long? MaxStaticBitrate { get; init; }
    public DirectPlayProfile[]? DirectPlayProfiles { get; init; }
    public TranscodingProfile[]? TranscodingProfiles { get; init; }
    public SubtitleProfile[]? SubtitleProfiles { get; init; }
    public CodecProfile[]? CodecProfiles { get; init; }
    public ContainerProfile[]? ContainerProfiles { get; init; }
}

public sealed record DirectPlayProfile
{
    public string? Type { get; init; }
    public string? Container { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
}

public sealed record TranscodingProfile
{
    public string? Type { get; init; }
    public string? Container { get; init; }
    public string? Protocol { get; init; }
    public string? VideoCodec { get; init; }
    public string? AudioCodec { get; init; }
    public string? Context { get; init; }
    public string? MaxAudioChannels { get; init; }
    public int? MinSegments { get; init; }
    public int? SegmentLength { get; init; }
    public bool? BreakOnNonKeyFrames { get; init; }
    public bool? CopyTimestamps { get; init; }
}

public sealed record SubtitleProfile
{
    public string? Format { get; init; }
    public string? Method { get; init; }
    public string? Container { get; init; }
    public string? Language { get; init; }
    public string? Protocol { get; init; }
    public bool? AllowChunkedResponse { get; init; }
}

public sealed record CodecProfile
{
    public string? Type { get; init; }
    public string? Codec { get; init; }
    public string? Container { get; init; }
    public ProfileCondition[]? Conditions { get; init; }
    public ProfileCondition[]? ApplyConditions { get; init; }
}

public sealed record ContainerProfile
{
    public string? Type { get; init; }
    public string? Container { get; init; }
    public ProfileCondition[]? Conditions { get; init; }
}

public sealed record ProfileCondition
{
    public string? Condition { get; init; }
    public string? Property { get; init; }
    public string? Value { get; init; }
    public bool? IsRequired { get; init; }
}

public record PlaybackStartInfo
{
    public string? ItemId { get; init; }
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public string? LiveStreamId { get; init; }
    public string? SessionId { get; init; }
    public long? PositionTicks { get; init; }
    public long? RunTimeTicks { get; init; }
    public bool? CanSeek { get; init; }
    public bool? IsPaused { get; init; }
    public bool? IsMuted { get; init; }
    public int? VolumeLevel { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public string? PlayMethod { get; init; }
    public string? EventName { get; init; }
    public double? PlaybackRate { get; init; }
    public string? RepeatMode { get; init; }
    public int? PlaylistIndex { get; init; }
    public int? PlaylistLength { get; init; }
}

public sealed record PlaybackProgressInfo : PlaybackStartInfo;

public sealed record PlaybackStopInfo
{
    public string? ItemId { get; init; }
    public string? MediaSourceId { get; init; }
    public string? PlaySessionId { get; init; }
    public string? LiveStreamId { get; init; }
    public string? SessionId { get; init; }
    public long? PositionTicks { get; init; }
    public bool? Failed { get; init; }
    public int? PlaylistIndex { get; init; }
    public int? PlaylistLength { get; init; }
}

public sealed record LiveStreamRequest
{
    public string? OpenToken { get; init; }
    public string? UserId { get; init; }
    public string? PlaySessionId { get; init; }
    public long? ItemId { get; init; }
    public DeviceProfile? DeviceProfile { get; init; }
    public long? MaxStreamingBitrate { get; init; }
    public long? StartTimeTicks { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public int? MaxAudioChannels { get; init; }
    public bool? EnableDirectPlay { get; init; }
    public bool? EnableDirectStream { get; init; }
    public bool? EnableTranscoding { get; init; }
    public bool? AllowVideoStreamCopy { get; init; }
    public bool? AllowAudioStreamCopy { get; init; }

    public override string ToString() => nameof(LiveStreamRequest);
}

public sealed record LiveStreamResponse
{
    public MediaSourceInfo? MediaSource { get; init; }
}

public sealed record ClientCapabilities
{
    public string[] PlayableMediaTypes { get; init; } = ["Video"];
    public string[] SupportedCommands { get; init; } = [];
    public bool SupportsMediaControl { get; init; }
    public bool SupportsSync { get; init; }
    public DeviceProfile? DeviceProfile { get; init; }
}
