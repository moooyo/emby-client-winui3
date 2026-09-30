using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;
using Windows.UI;

namespace EmbyClient.MediaFixtures;

internal static class Program
{
    private const string OutputFileName = "fixture-h264-aac.mp4";
    private const string MetadataFileName = "fixture-h264-aac.json";
    private const string ProjectFileName = "EmbyClient.MediaFixtures.csproj";
    private const int DefaultDurationSeconds = 10;
    private const uint VideoWidth = 1280;
    private const uint VideoHeight = 720;
    private const uint FramesPerSecond = 30;
    private const uint SampleRate = 48_000;
    private const ushort Channels = 2;
    private const long MaximumBackgroundImageBytes = 20 * 1024 * 1024;

    private static async Task<int> Main(string[] args)
    {
        try
        {
            if (args is ["--help"] or ["-h"])
            {
                Console.WriteLine("Usage: EmbyClient.MediaFixtures [--output-dir <directory>] [--duration-seconds <1..180>] [--background-image <absolute-jpeg-or-png-path>]");
                Console.WriteLine("Creates an H.264/AAC MP4 using Windows media APIs; the default duration is ten seconds.");
                Console.WriteLine("The default output directory is the project's artifacts directory.");
                return 0;
            }

            var options = ParseOptions(args);
            Directory.CreateDirectory(options.OutputDirectory);
            await GenerateAsync(options.OutputDirectory, options.DurationSeconds, options.BackgroundImage);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Media fixture generation failed: {exception}");
            return 1;
        }
    }

    private static GenerationOptions ParseOptions(string[] args)
    {
        string? outputDirectory = null;
        int? durationSeconds = null;
        string? backgroundImagePath = null;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--output-dir" when index + 1 < args.Length && outputDirectory is null:
                    outputDirectory = args[++index];
                    if (string.IsNullOrWhiteSpace(outputDirectory))
                        throw new ArgumentException("The output directory must not be empty.");
                    outputDirectory = Path.GetFullPath(outputDirectory);
                    break;
                case "--duration-seconds" when index + 1 < args.Length && durationSeconds is null:
                    if (!int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out var duration)
                        || duration is < 1 or > 180)
                        throw new ArgumentException("The duration must be a whole number from 1 through 180 seconds.");
                    durationSeconds = duration;
                    break;
                case "--background-image" when index + 1 < args.Length && backgroundImagePath is null:
                    backgroundImagePath = args[++index];
                    if (!Path.IsPathFullyQualified(backgroundImagePath))
                        throw new ArgumentException("The background image must be an explicit absolute JPEG or PNG path.");
                    backgroundImagePath = Path.GetFullPath(backgroundImagePath);
                    break;
                default:
                    throw new ArgumentException("Use --output-dir <directory>, --duration-seconds <1..180>, and/or --background-image <absolute-jpeg-or-png-path>, each at most once.");
            }
        }

        return new GenerationOptions(outputDirectory ?? GetDefaultOutputDirectory(), durationSeconds ?? DefaultDurationSeconds,
            backgroundImagePath is null ? null : ReadBackgroundImage(backgroundImagePath));
    }

    private static BackgroundImageInput ReadBackgroundImage(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length is < 4 or > MaximumBackgroundImageBytes)
            throw new ArgumentException("The background image must exist and be a JPEG or PNG no larger than 20 MiB.");
        var extension = file.Extension.ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png"))
            throw new ArgumentException("Only explicit JPEG and PNG background image files are supported.");
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[8];
        stream.ReadExactly(header[..(int)Math.Min(header.Length, file.Length)]);
        if (extension == ".png")
        {
            if (file.Length < 8 || !header.SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new ArgumentException("The background PNG signature is invalid.");
        }
        else
        {
            Span<byte> end = stackalloc byte[2];
            stream.Seek(-2, SeekOrigin.End);
            stream.ReadExactly(end);
            if (header[0] != 0xff || header[1] != 0xd8 || end[0] != 0xff || end[1] != 0xd9)
                throw new ArgumentException("The background JPEG markers are invalid.");
        }
        stream.Position = 0;
        return new BackgroundImageInput(path, Convert.ToHexString(SHA256.HashData(stream)), file.Length,
            extension == ".png" ? "PNG" : "JPEG");
    }

    private static string GetDefaultOutputDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, ProjectFileName)))
            {
                return Path.Combine(directory.FullName, "artifacts");
            }
        }

        return Path.Combine(AppContext.BaseDirectory, "artifacts");
    }

    private static async Task GenerateAsync(string outputDirectory, int durationSeconds, BackgroundImageInput? backgroundImage)
    {
        var outputPath = Path.Combine(outputDirectory, OutputFileName);
        var metadataPath = Path.Combine(outputDirectory, MetadataFileName);
        var temporaryId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        var audioPath = Path.Combine(outputDirectory, $".fixture-audio-{temporaryId}.wav");
        var renderFileName = $".fixture-render-{temporaryId}.mp4";
        var renderPath = Path.Combine(outputDirectory, renderFileName);
        var temporaryMetadataPath = Path.Combine(outputDirectory, $".fixture-metadata-{temporaryId}.json");

        try
        {
            WriteSineWave(audioPath, durationSeconds);
            var composition = new MediaComposition();
            if (backgroundImage is not null)
            {
                var imageFile = await StorageFile.GetFileFromPathAsync(backgroundImage.FullPath);
                composition.Clips.Add(await MediaClip.CreateFromImageFileAsync(imageFile, TimeSpan.FromSeconds(durationSeconds)));
                Console.WriteLine("Background: explicitly supplied static image; no animation or original film content is represented.");
            }
            else
            {
                Color[] colors =
                [
                    new() { A = 255, R = 32, G = 88, B = 176 },
                    new() { A = 255, R = 184, G = 55, B = 65 },
                    new() { A = 255, R = 34, G = 143, B = 96 },
                    new() { A = 255, R = 207, G = 128, B = 33 },
                    new() { A = 255, R = 112, G = 64, B = 173 }
                ];

                foreach (var color in colors)
                {
                    composition.Clips.Add(MediaClip.CreateFromColor(color,
                        TimeSpan.FromTicks(durationSeconds * TimeSpan.TicksPerSecond / colors.Length)));
                }
            }

            var audioFile = await StorageFile.GetFileFromPathAsync(audioPath);
            composition.BackgroundAudioTracks.Add(await BackgroundAudioTrack.CreateFromFileAsync(audioFile));

            var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD720p);
            profile.Video.Width = VideoWidth;
            profile.Video.Height = VideoHeight;
            profile.Video.Bitrate = 2_000_000;
            profile.Video.FrameRate.Numerator = FramesPerSecond;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.PixelAspectRatio.Numerator = 1;
            profile.Video.PixelAspectRatio.Denominator = 1;
            profile.Audio = AudioEncodingProperties.CreateAac(SampleRate, Channels, 128_000);

            var outputFolder = await StorageFolder.GetFolderFromPathAsync(outputDirectory);
            var renderFile = await outputFolder.CreateFileAsync(renderFileName, CreationCollisionOption.FailIfExists);
            Console.WriteLine($"Rendering {durationSeconds}s, {VideoWidth}x{VideoHeight}, {FramesPerSecond} fps, H.264/AAC...");
            var renderResult = await composition.RenderToFileAsync(renderFile, MediaTrimmingPreference.Precise, profile);
            Console.WriteLine($"Render result: {renderResult}");
            if (renderResult != TranscodeFailureReason.None)
            {
                throw new InvalidOperationException($"Windows media rendering returned {renderResult}.");
            }

            var fileSize = new FileInfo(renderPath).Length;
            var actualProfile = await MediaEncodingProfile.CreateFromFileAsync(renderFile);
            var videoProperties = await renderFile.Properties.GetVideoPropertiesAsync();
            VerifyOutput(fileSize, actualProfile, videoProperties.Duration, durationSeconds);
            WriteMetadata(temporaryMetadataPath, fileSize, actualProfile, videoProperties.Duration);
            if (backgroundImage is not null && ReadBackgroundImage(backgroundImage.FullPath) != backgroundImage)
                throw new InvalidOperationException("The read-only background source changed during media generation.");

            PublishOutput(renderPath, outputPath, temporaryMetadataPath, metadataPath, temporaryId);
            if (backgroundImage is not null)
                WriteBackgroundReceipt(Path.Combine(outputDirectory, "fixture-background.receipt.json"), backgroundImage,
                    outputPath, metadataPath, fileSize, actualProfile, videoProperties.Duration);
            Console.WriteLine($"Output: {outputPath}");
            Console.WriteLine($"Metadata: {metadataPath}");
            Console.WriteLine($"File length: {fileSize.ToString(CultureInfo.InvariantCulture)} bytes");
            Console.WriteLine($"Duration: {videoProperties.Duration.TotalSeconds.ToString("F7", CultureInfo.InvariantCulture)} seconds ({videoProperties.Duration.Ticks} ticks)");
            Console.WriteLine($"Video: {actualProfile.Video.Subtype}, {actualProfile.Video.Width}x{actualProfile.Video.Height}, " +
                $"{actualProfile.Video.FrameRate.Numerator}/{actualProfile.Video.FrameRate.Denominator} fps");
            Console.WriteLine($"Audio: {actualProfile.Audio.Subtype}, {actualProfile.Audio.SampleRate} Hz, {actualProfile.Audio.ChannelCount} channels");
        }
        finally
        {
            DeleteTemporaryFile(audioPath);
            DeleteTemporaryFile(renderPath);
            DeleteTemporaryFile(temporaryMetadataPath);
        }
    }

    private static void WriteBackgroundReceipt(string path, BackgroundImageInput image, string outputPath,
        string metadataPath, long fileSize, MediaEncodingProfile profile, TimeSpan duration)
    {
        using var output = File.OpenRead(outputPath);
        var outputHash = Convert.ToHexString(SHA256.HashData(output));
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("GeneratedAtUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteBoolean("Synthetic", true);
        writer.WriteString("SourceArtworkPath", image.FullPath);
        writer.WriteString("SourceArtworkSha256", image.Sha256);
        writer.WriteNumber("SourceArtworkBytes", image.Length);
        writer.WriteString("SourceArtworkFormat", image.Format);
        writer.WriteBoolean("SourceArtworkUnchanged", ReadBackgroundImage(image.FullPath) == image);
        writer.WriteString("SourceStatus", "User-provided design reference derivative; not original film content or verified copyright metadata.");
        writer.WriteString("VisualContent", "Static image for the entire clip; no motion is claimed.");
        writer.WriteString("AudioContent", "One synthetic 440 Hz stereo tone encoded as AAC; no alternate language track is claimed.");
        writer.WriteString("GeneratedPath", outputPath);
        writer.WriteString("GeneratedSha256", outputHash);
        writer.WriteString("MetadataPath", metadataPath);
        writer.WriteNumber("FileLength", fileSize);
        writer.WriteNumber("DurationTicks", duration.Ticks);
        writer.WriteString("VideoCodec", profile.Video.Subtype);
        writer.WriteNumber("Width", profile.Video.Width);
        writer.WriteNumber("Height", profile.Video.Height);
        writer.WriteNumber("FrameRateNumerator", profile.Video.FrameRate.Numerator);
        writer.WriteNumber("FrameRateDenominator", profile.Video.FrameRate.Denominator);
        writer.WriteString("AudioCodec", profile.Audio.Subtype);
        writer.WriteNumber("AudioSampleRate", profile.Audio.SampleRate);
        writer.WriteNumber("AudioChannels", profile.Audio.ChannelCount);
        writer.WriteBoolean("WindowsMediaReopenVerificationPassed", true);
        writer.WriteString("VerificationMethod", "RenderToFileAsync returned None; CreateFromFileAsync and GetVideoPropertiesAsync reopened the generated file; existing VerifyOutput checks passed.");
        writer.WriteString("Limitations", "No 4K, HDR, Dolby Vision, Atmos, alternate audio language, embedded subtitle, movement, or complete playback verification is asserted. External subtitle loading is a separate acceptance check.");
        writer.WriteEndObject();
    }

    private static void PublishOutput(string renderPath, string outputPath, string temporaryMetadataPath, string metadataPath, string temporaryId)
    {
        var backupPath = Path.Combine(Path.GetDirectoryName(outputPath)!, $".fixture-backup-{temporaryId}.mp4");
        var hasBackup = false;
        var videoPublished = false;

        try
        {
            if (File.Exists(outputPath))
            {
                File.Move(outputPath, backupPath);
                hasBackup = true;
            }

            File.Move(renderPath, outputPath);
            videoPublished = true;
            File.Move(temporaryMetadataPath, metadataPath, overwrite: true);
        }
        catch (Exception publicationException)
        {
            try
            {
                if (hasBackup)
                {
                    File.Move(backupPath, outputPath, overwrite: true);
                }
                else if (videoPublished)
                {
                    File.Delete(outputPath);
                }
            }
            catch (Exception rollbackException)
            {
                throw new AggregateException(
                    $"Publishing the fixture failed and the previous video could not be restored. Any original video backup remains at '{backupPath}'.",
                    publicationException,
                    rollbackException);
            }

            throw;
        }

        if (hasBackup)
        {
            DeleteTemporaryFile(backupPath);
        }
    }

    private static void WriteMetadata(string path, long fileSize, MediaEncodingProfile profile, TimeSpan duration)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("FileName", OutputFileName);
        writer.WriteNumber("FileLength", fileSize);
        writer.WriteNumber("DurationTicks", duration.Ticks);
        writer.WriteNumber("Width", profile.Video.Width);
        writer.WriteNumber("Height", profile.Video.Height);
        writer.WriteString("VideoCodec", profile.Video.Subtype);
        writer.WriteString("AudioCodec", profile.Audio.Subtype);
        writer.WriteNumber("AudioChannels", profile.Audio.ChannelCount);
        writer.WriteNumber("AudioSampleRate", profile.Audio.SampleRate);
        writer.WriteNumber("FrameRateNumerator", profile.Video.FrameRate.Numerator);
        writer.WriteNumber("FrameRateDenominator", profile.Video.FrameRate.Denominator);
        writer.WriteBoolean("Synthetic", true);
        writer.WriteEndObject();
    }

    private static void VerifyOutput(long fileSize, MediaEncodingProfile profile, TimeSpan duration, int durationSeconds)
    {
        if (fileSize <= 0 || profile.Video is null || profile.Audio is null)
        {
            throw new InvalidDataException("The rendered file is empty or does not contain both video and audio.");
        }

        if (!string.Equals(profile.Video.Subtype, MediaEncodingSubtypes.H264, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(profile.Audio.Subtype, MediaEncodingSubtypes.Aac, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Unexpected codecs: {profile.Video.Subtype}/{profile.Audio.Subtype}.");
        }

        if (Math.Abs(duration.TotalSeconds - durationSeconds) > 0.1 ||
            profile.Video.Width != VideoWidth || profile.Video.Height != VideoHeight ||
            profile.Video.FrameRate.Numerator == 0 || profile.Video.FrameRate.Denominator == 0 ||
            profile.Audio.SampleRate != SampleRate || profile.Audio.ChannelCount != Channels)
        {
            throw new InvalidDataException(
                $"Unexpected media properties: {duration.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture)} seconds; " +
                $"{profile.Video.Width}x{profile.Video.Height}; " +
                $"{profile.Video.FrameRate.Numerator}/{profile.Video.FrameRate.Denominator} fps; " +
                $"{profile.Audio.SampleRate} Hz; {profile.Audio.ChannelCount} channels.");
        }
    }

    private static void WriteSineWave(string path, int durationSeconds)
    {
        const ushort bitsPerSample = 16;
        const double frequency = 440;
        const double amplitude = 0.15;
        var sampleCount = checked((int)SampleRate * durationSeconds);
        var blockAlign = checked((ushort)(Channels * bitsPerSample / 8));
        var dataLength = checked(sampleCount * blockAlign);

        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(checked(36 + dataLength));
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((ushort)1);
        writer.Write(Channels);
        writer.Write(SampleRate);
        writer.Write(SampleRate * blockAlign);
        writer.Write(blockAlign);
        writer.Write(bitsPerSample);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);

        var fadeSamples = SampleRate / 20.0;
        for (var index = 0; index < sampleCount; index++)
        {
            var envelope = Math.Min(1, Math.Min(index, sampleCount - 1 - index) / fadeSamples);
            var sample = (short)Math.Round(short.MaxValue * amplitude * envelope * Math.Sin(2 * Math.PI * frequency * index / SampleRate));
            for (var channel = 0; channel < Channels; channel++)
            {
                writer.Write(sample);
            }
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException exception)
        {
            Console.Error.WriteLine($"Could not delete temporary file '{path}': {exception.Message}");
        }
        catch (UnauthorizedAccessException exception)
        {
            Console.Error.WriteLine($"Could not delete temporary file '{path}': {exception.Message}");
        }
    }

    private sealed record BackgroundImageInput(string FullPath, string Sha256, long Length, string Format);
    private sealed record GenerationOptions(string OutputDirectory, int DurationSeconds, BackgroundImageInput? BackgroundImage);
}
