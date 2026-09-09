using System.Text.Json;

namespace EmbyClient.NativeProbe;

public sealed partial class App
{
    private string? _complexSubtitleCredentialsPath;
    private string? _complexSubtitleManifestPath;
    private string? _complexSubtitleCaseId;
    private bool _complexSubtitleHttpProfileControl;
    private string? _complexSubtitleExpectedServerId;

    private void ConfigureComplexSubtitleMode(string credentialsPath, string manifestPath, string caseId,
        string mode, string? expectedServerId = null)
    {
        _complexSubtitleCredentialsPath = Path.GetFullPath(credentialsPath);
        _complexSubtitleManifestPath = Path.GetFullPath(manifestPath);
        _complexSubtitleCaseId = caseId;
        _complexSubtitleHttpProfileControl = mode == "--complex-subtitle-http-profile-control";
        _complexSubtitleExpectedServerId = expectedServerId;
    }

    private async Task RunComplexSubtitleModeAsync()
    {
        var output = Path.GetDirectoryName(_resultPath!)!;
        try
        {
            var report = await ComplexSubtitleProbe.RunAsync(_window!.DispatcherQueue, _element!,
                _complexSubtitleCredentialsPath!, _complexSubtitleManifestPath!, _complexSubtitleCaseId!, output,
                progressiveHttpProfileControl: _complexSubtitleHttpProfileControl,
                expectedServerId: _complexSubtitleExpectedServerId);
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
