using System.Text.Json;
using System.Text.Json.Serialization;

namespace EmbyClient.Api;

/// <summary>The playback fields returned by the official Sessions endpoint.</summary>
public sealed record PlaybackSessionInfo
{
    public string? Id { get; init; }
    public string? UserId { get; init; }
    public string? DeviceId { get; init; }
    public BaseItemDto? NowPlayingItem { get; init; }
    public PlayerStateInfo? PlayState { get; init; }
    public TranscodingInfo? TranscodingInfo { get; init; }

    // SDK 4.10.1.0 does not declare PlaySessionId here. Preserve it only if the server returns it.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

public sealed record PlayerStateInfo
{
    public long? PositionTicks { get; init; }
    public string? MediaSourceId { get; init; }
    public string? PlayMethod { get; init; }
    public int? AudioStreamIndex { get; init; }
    public int? SubtitleStreamIndex { get; init; }
    public bool? IsPaused { get; init; }

    // The official PlayerStateInfo schema also omits PlaySessionId.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}

/// <summary>Server-reported scalar transcode data; missing values remain unknown.</summary>
public sealed record TranscodingInfo
{
    public string? AudioCodec { get; init; }
    public string? VideoCodec { get; init; }
    public string? SubProtocol { get; init; }
    public string? Container { get; init; }
    public bool? IsVideoDirect { get; init; }
    public bool? IsAudioDirect { get; init; }
    public int? Bitrate { get; init; }
    public int? AudioBitrate { get; init; }
    public int? VideoBitrate { get; init; }
    public float? Framerate { get; init; }
    public double? CompletionPercentage { get; init; }
    public double? TranscodingPositionTicks { get; init; }
    public double? TranscodingStartPositionTicks { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public int? AudioChannels { get; init; }
    public string[]? TranscodeReasons { get; init; }
    public string? VideoDecoder { get; init; }
    public bool? VideoDecoderIsHardware { get; init; }
    public string? VideoDecoderMediaType { get; init; }
    public string? VideoDecoderHwAccel { get; init; }
    public string? VideoEncoder { get; init; }
    public bool? VideoEncoderIsHardware { get; init; }
    public string? VideoEncoderMediaType { get; init; }
    public string? VideoEncoderHwAccel { get; init; }
}
