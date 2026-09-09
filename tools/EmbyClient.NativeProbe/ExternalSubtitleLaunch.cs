using System.Text.Json;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _externalSubtitleCredentialsPath;

    private async Task RunExternalSubtitleModeAsync()
    {
        var output = Path.GetDirectoryName(_resultPath!)!;
        try
        {
            var report = await ExternalSubtitleProbe.RunAsync(_window!.DispatcherQueue, _element!,
                _externalSubtitleCredentialsPath!, output);
            Environment.ExitCode = report.Status == "Passed" ? 0 : 1;
        }
        catch (Exception error)
        {
            var report = new ExternalSubtitleReport
            {
                Status = "Failed", ErrorCode = "Launch_" + ErrorCode(error), FinishedUtc = DateTimeOffset.UtcNow
            };
            File.WriteAllBytes(Path.Combine(output, "external-subtitle-launch-failure.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, ExternalSubtitleJsonContext.Default.ExternalSubtitleReport));
            Environment.ExitCode = 1;
        }
        finally { _window!.Close(); Exit(); }
    }
}
