# Official Emby API probe

This .NET 10 NativeAOT console tool exercises the repository's current `EmbyClient.Api` implementation against an official Emby Server. It also references `EmbyClient.Playback` and uses `ConservativeDeviceProfile.Create` for the application's normal and forced-transcoding negotiation flags.

The probe accepts only `http://127.0.0.1:19096`, optionally ending in `/emby` or `/emby/`. The default required server version is `4.9.5.0`. Use a disposable validation account and generated media library.

## Build and run

Local verification must be explicitly authorized for the current task. Otherwise, perform verification on the authorized `ssh test-env` environment.

```powershell
dotnet publish tools/EmbyClient.ServerValidation/ApiProbe/ApiProbe.csproj -c Release --self-contained true
& tools/EmbyClient.ServerValidation/ApiProbe/bin/Release/net10.0/win-x64/publish/ApiProbe.exe --credentials-file artifacts/emby-validation/official-user-credentials.json --output artifacts/emby-validation/api-probe-report.json
```

The project pins `win-x64` locally; do not add a command-line `-r` that propagates the RID into shared library restores. The credentials file must be ignored by Git. Paths under the repository's `artifacts/` directory are ignored. The local `.gitignore` also excludes `*.credentials.local.json` and `api-probe-report*.json` under this tool directory. Supply this JSON structure without placing secrets in command arguments:

```json
{
  "ServerUrl": "http://127.0.0.1:19096",
  "Username": "validation-user",
  "Password": "REPLACE_IN_IGNORED_FILE"
}
```

Options:

- `--credentials-file <path>` is required.
- `--output <path>` defaults to `api-probe-report.json` beside the credentials file.
- `--item-id <id>` selects a specific video; otherwise the first video in the sorted library query is used.
- `--expected-version <version>` changes the exact required server version.
- `--help` prints usage without contacting a server.

Exit codes are `0` for all required checks passing, `1` for a required failure or blocked dependency, and `2` for invalid input. Optional supplemental readback failures remain visible in the JSON report. An eight-minute cancellation deadline bounds the main flow; cleanup uses independent request timeouts.

## Coverage and interpretation

The tool calls the actual public client methods for public server information, valid and invalid authentication, current user, authenticated system information, capabilities, views, item listing/details/search/empty results/paging, latest/resume/next-up queries, reversible favorite and played booleans, playback negotiation, lifecycle reports, encoding cleanup, and logout/token invalidation.

The normal playback request allows original streaming. The forced request disables direct streaming and audio/video copy, then requires a negotiated HLS protocol. Both use the current application profile. The internal `PlaybackRequestFactory` and the native engine are not invoked.

HTTP transfer checks use `BuildVideoStreamUri`, `ResolveMediaUri`, and `GetMediaRequestHeaders` from the real client. They validate an authenticated HTTP 206 response with a matching content range, HLS master/media playlists, and a complete MPEG-TS segment with transport sync bytes. Relative HLS references are resolved against their parent playlist. All transfers stay on the allowed loopback origin; redirects are disabled and response sizes are bounded. The authenticated `/Sessions` readbacks are explicitly supplemental HTTP checks because the public client currently exposes no session-query method.

When the selected source exposes a text subtitle, the probe also requests that stream index using the application's external-WebVTT profile. It requires external delivery, reads the negotiated URL (or the same WebVTT fallback path used by the application), and validates `text/vtt`, a `WEBVTT` header, and a cue timestamp separator. This confirms HTTP subtitle delivery only; native rendering and synchronization remain untested.

Playback start/progress/stop reports are synthetic API contract probes. The reports use a paused and muted session and synthetic one-second progress. They do not indicate that a decoder ran, that the native UI opened, or that a first frame appeared. Native UI and first-frame results are always `NotRun` in the report.

Favorite and played booleans are read before mutation, toggled, read back, and restored. Cleanup repeats restoration after lifecycle reporting. The public client cannot restore historical play count or last-played timestamps; the report states that limitation and records original/final resume positions. This is why the account and media must be disposable.

Console output contains only fixed check names and statuses. JSON evidence contains selected booleans/counts/version data. Neither output includes tokens, passwords, usernames, raw authentication responses, media URLs, server file paths, or raw exception messages. Credentials remain in the supplied local file until its owner removes it.
