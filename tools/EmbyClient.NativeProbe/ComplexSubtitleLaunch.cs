using System.Text.Json;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _complexSubtitleCredentialsPath;
    private string? _complexSubtitleManifestPath;
    private string? _complexSubtitleCaseId;
    private bool _complexSubtitleHttpProfileControl;

    private async Task RunComplexSubtitleModeAsync()
    {
        var output = Path.GetDirectoryName(_resultPath!)!;
        try
        {
            var report = await ComplexSubtitleProbe.RunAsync(_window!.DispatcherQueue, _element!,
                _complexSubtitleCredentialsPath!, _complexSubtitleManifestPath!, _complexSubtitleCaseId!, output,
                progressiveHttpProfileControl: _complexSubtitleHttpProfileControl);
            Environment.ExitCode = report.Status is "Passed" or "ControlPassed" ? 0 : 1;
        }
        catch (Exception error)
        {
            var report = new ComplexSubtitleReport
            {
                ExecutionMode = _complexSubtitleHttpProfileControl ? "ProgressiveHttpProfileControl" : "DefaultSubtitleProfile",
                ProfileControl = _complexSubtitleHttpProfileControl, DefaultEncodingProfileUsed = !_complexSubtitleHttpProfileControl,
                Scope = "Launch or final report failure; this record does not establish a media compatibility outcome.",
                Status = "Failed", ErrorCode = "Launch_" + (error is ComplexSubtitleOutputException ? "ProbeOutputFailure" : ErrorCode(error)),
                FailureStage = "LaunchOrFinalReportWrite", ErrorHResult = error.HResult,
                ErrorType = error is ComplexSubtitleOutputException outputError ? outputError.OriginalType : error.GetType().Name,
                OutputFailureOperation = (error as ComplexSubtitleOutputException)?.Operation, FinishedUtc = DateTimeOffset.UtcNow
            };
            File.WriteAllBytes(Path.Combine(output, "complex-subtitle-launch-failure.json"),
                JsonSerializer.SerializeToUtf8Bytes(report, ComplexSubtitleJsonContext.Default.ComplexSubtitleReport));
            Environment.ExitCode = 1;
        }
        finally { _window!.Close(); Exit(); }
    }
}
