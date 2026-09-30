using System.Text;

namespace EmbyClient.App.Playback;

public sealed partial class NativePlaybackEngine
{
    private const int MaximumSubtitleDiagnosticBytes = 64 * 1024;
    private static readonly object SubtitleDiagnosticGate = new();

    private static void RecordSubtitleCapture(int tracks, int nativeCues, int textCues, int capturedCues,
        IEnumerable<string> wrapperTypes, Exception? exception = null) => RecordSubtitleDiagnostic(
        FormattableString.Invariant($"Capture tracks={tracks} nativeCues={nativeCues} textCues={textCues} capturedCues={capturedCues} wrappers={string.Join(',', wrapperTypes.OrderBy(value => value, StringComparer.Ordinal).Select(SubtitleDiagnosticName))} exception={SubtitleDiagnosticName(exception?.GetType().Name ?? "None")} hresult=0x{exception?.HResult ?? 0:X8}"));

    private static void RecordSubtitlePresentation(int characters) => RecordSubtitleDiagnostic(
        FormattableString.Invariant($"Present characters={characters}"));

    private static void RecordSubtitleFailure(string operation, Exception exception) => RecordSubtitleDiagnostic(
        FormattableString.Invariant($"{SubtitleDiagnosticName(operation)} exception={SubtitleDiagnosticName(exception.GetType().Name)} hresult=0x{exception.HResult:X8}"));

    private static string SubtitleDiagnosticName(string value) => new(value.Take(80)
        .Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

    private static void RecordSubtitleDiagnostic(string value)
    {
        try
        {
            // Isolated acceptance profiles opt in to bounded metadata; text, URLs, IDs, and credentials stay out.
            var root = Environment.GetEnvironmentVariable("EMBY_CLIENT_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root) || !Directory.Exists(root)) return;
            var path = Path.Combine(Path.GetFullPath(root), "native-subtitle-diagnostics.log");
            var line = FormattableString.Invariant($"{DateTimeOffset.UtcNow:O} {value}\n");
            var data = Encoding.ASCII.GetBytes(line);
            lock (SubtitleDiagnosticGate)
            {
                using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (stream.Length > MaximumSubtitleDiagnosticBytes - data.Length) return;
                stream.Seek(0, SeekOrigin.End);
                stream.Write(data);
            }
        }
        catch (Exception) { /* Observations cannot alter playback or resource ownership. */ }
    }
}
