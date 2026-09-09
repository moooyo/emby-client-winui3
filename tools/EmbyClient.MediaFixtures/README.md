# Media fixture generator

This Windows-only .NET 10 console tool creates `fixture-h264-aac.mp4` with Windows media APIs. It requires the Windows media components and H.264/AAC encoders available on a standard Windows installation. It does not use FFmpeg, download media, or require third-party packages.

The generated fixture defaults to ten seconds of video: five solid colors of equal duration, with a 440 Hz sine-wave audio track. `--duration-seconds` accepts a whole number from 1 through 180 and changes both the video and audio duration. The requested format is H.264, 1280 × 720, 30 fps, and stereo AAC at 48 kHz. The target video and audio bitrates are 2,000,000 and 128,000 bits per second respectively. Windows can report a slightly different frame rate and duration in the rendered metadata; the tool prints the actual values for fixture consumers.

Run from the repository root in PowerShell:

```powershell
dotnet run --project .\tools\EmbyClient.MediaFixtures\EmbyClient.MediaFixtures.csproj
```

The default output is `tools\EmbyClient.MediaFixtures\artifacts\fixture-h264-aac.mp4`. Specify a different directory with:

```powershell
dotnet run --project .\tools\EmbyClient.MediaFixtures\EmbyClient.MediaFixtures.csproj -- --output-dir D:\MediaFixtures
```

For longer interactive playback checks, generate a separate sixty-second fixture without replacing a running fixture server's files:

```powershell
dotnet run --project .\tools\EmbyClient.MediaFixtures\EmbyClient.MediaFixtures.csproj --configuration Release -- --duration-seconds 60 --output-dir .\tools\EmbyClient.MediaFixtures\artifacts\sixty-seconds
```

The two options may appear in either order. Invalid, missing, or repeated options are rejected before any output directory or media file is created. Stop the active client and coordinate the fixture server's media directory before switching to a new generated pair; this tool does not restart a running server.

For a published executable outside the source tree, the default output is the executable directory's `artifacts` subdirectory. Relative `--output-dir` paths resolve against the current working directory.

The tool creates a temporary 16-bit PCM WAV file, uses it as a `BackgroundAudioTrack`, and renders `MediaClip.CreateFromColor` clips with `MediaComposition.RenderToFileAsync`. It only replaces the destination MP4 after Windows reports `TranscodeFailureReason.None` and the generated file has been reopened successfully to check its size, duration, codecs, dimensions, frame rate, sample rate, and channel count. An existing destination remains intact if rendering or these checks fail. Temporary files are removed afterward. A failure produces an error message and exit code 1; an empty placeholder is never accepted as a fixture.

On success, console output records the render result, absolute output paths, file length, duration, video encoding, and audio encoding. The companion `fixture-h264-aac.json` file contains the actual metadata read from the rendered file, written directly with `Utf8JsonWriter` without reflection-based serialization. It is prepared in a temporary file before the MP4 and metadata are moved to their final names. The previous MP4 is backed up during replacement and restored if metadata publication fails; if restoration itself fails, the error identifies the preserved backup path.

The metadata fields are `FileName`, `FileLength` (bytes), `DurationTicks` (100-nanosecond units), `Width`, `Height`, `VideoCodec`, `AudioCodec`, `AudioChannels`, `AudioSampleRate` (Hz), `FrameRateNumerator`, `FrameRateDenominator`, and `Synthetic` (always `true`). Fixture consumers should use these actual values instead of assuming that encoded duration or frame rate exactly matches the requested values.

## Microsoft API references

- [Call Windows Runtime APIs from desktop apps](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-enhance)
- [MediaClip.CreateFromColor](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediaclip.createfromcolor?view=winrt-26100)
- [BackgroundAudioTrack.CreateFromFileAsync](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.backgroundaudiotrack.createfromfileasync?view=winrt-26100)
- [MediaComposition.RenderToFileAsync](https://learn.microsoft.com/en-us/uwp/api/windows.media.editing.mediacomposition.rendertofileasync?view=winrt-26100)
- [MediaEncodingProfile.CreateMp4](https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaproperties.mediaencodingprofile.createmp4?view=winrt-26100)
- [AudioEncodingProperties.CreateAac](https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaproperties.audioencodingproperties.createaac?view=winrt-26100)
- [MediaEncodingProfile.CreateFromFileAsync](https://learn.microsoft.com/en-us/uwp/api/windows.media.mediaproperties.mediaencodingprofile.createfromfileasync?view=winrt-26100)
