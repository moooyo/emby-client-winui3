# Lumen native acceptance environment

This Windows-only helper prepares a fresh task-owned synthetic fixture and a normal Windows-user-protected saved account. It links the production `AccountStore`, `ConnectionService`, and their generated settings JSON context. It does not add a test credential bypass to the application, automate authentication dialogs, inspect real saved accounts, install services, or control the desktop.

Only the rich fixture's explicit IPv4 loopback identity is accepted. The helper checks the synthetic response header, bounded statistics, server ID, version, and public name before sending the fixed development `demo` credentials. A normal `ConnectionService.SignInAsync` protects the token, and a second production `RestoreAsync` verifies the saved account. Tokens and exception payloads are never printed. The profile and credential-free receipt stay under a fresh `artifacts/lumen-acceptance/<run>/` directory.

## Build

Run only in an authorized Windows verification environment. Build into isolated outputs so an active runtime cannot lock the normal project build outputs:

```powershell
dotnet build tools/EmbyClient.FixtureServer/EmbyClient.FixtureServer.csproj --configuration Release --output artifacts/lumen-acceptance/tool-build/fixture
dotnet build tools/LumenAcceptance/LumenAcceptance.csproj --configuration Release --output artifacts/lumen-acceptance/tool-build/bootstrap
dotnet test --project tools/LumenAcceptance/Tests/LumenAcceptance.Tests.csproj --configuration Release
./tools/LumenAcceptance/Test-EnvironmentSafety.ps1
```

The focused tests exercise URL/output restrictions and rejection of unowned, expired, changed-identity, or nonisolated official receipts. The PowerShell checks observe only their existing test shell to verify PID/start-time/hash guards without launching or stopping a process. In particular, a `ConvertFrom-Json`-projected `DateTime` must retain its UTC value and subsecond precision instead of round-tripping through a culture-dependent string. These tests do not establish Windows token protection, API compatibility, native UI, or playback pixels; those need the separate running bootstrap and native acceptance observations.

## Start

Supply the actual generated H.264/AAC fixture directory and the external design handoff directory. Artwork remains outside production application assets and is served by the synthetic image API.

```powershell
$workspace = (Get-Location).Path
./tools/LumenAcceptance/Start-Environment.ps1 `
    -FixtureExecutable (Join-Path $workspace 'artifacts/lumen-acceptance/tool-build/fixture/EmbyClient.FixtureServer.exe') `
    -BootstrapExecutable (Join-Path $workspace 'artifacts/lumen-acceptance/tool-build/bootstrap/EmbyClient.LumenAcceptance.exe') `
    -MediaDirectory 'D:/Code/emby-client-winui3/tools/EmbyClient.MediaFixtures/artifacts/sixty-seconds' `
    -ArtworkDirectory 'D:/Code/design_handoff_emby_player_ui'
```

The launcher refuses occupied ports and existing run directories. It copies the fixture payload, starts only that copy as an owned hidden process, checks its actual IPv4 loopback listener, and saves its PID, start time, executable path/hash, media hashes, and run ID before bootstrap. No password or token is passed on a command line. `-FailFirstPlaybackInfo` enables the existing one-shot opening failure for a separate Retry scenario.

`-DesignCatalog` selects the independent visual-baseline catalog documented in
`../EmbyClient.FixtureServer/Lumen-Design-Catalog.md`. It requires the explicit
`LumenDesignCatalog` statistics flag and its separate allowlisted server ID/name.
The bootstrap CLI receives `--design-catalog true`; it cannot combine this mode
with official credentials. Ordinary rich-fixture mode remains unchanged and
rejects a design-mode endpoint. This is test data selection, not a production
authentication bypass or proof of visual fidelity.

`-SubtitleFile '<absolute-owned-file.zh.vtt>'` forwards an optional real external
WebVTT snapshot to the fixture. It is off by default. The launcher checks an
existing absolute `.vtt` file, the 4 MiB bound, strict UTF-8, header/separator/cue
marker, and source SHA-256 before starting. Readiness requires the fixture's
configured flag and snapshot hash to match; ownership records the explicit path,
hash, size, and unchanged-source check after bootstrap. The bootstrap's normal
synthetic identity/account validation does not assume a two-track source, so an
honest optional third external subtitle stream does not bypass or weaken it.
Native cue parsing and visible rendering remain separate application checks.

Launch the normal application with process-only `EMBY_CLIENT_DATA_ROOT` pointing to the returned `ProfileDirectory`. Keep the entire published application folder together and use that folder as its working directory. The application should restore its normal last-used account; this helper does not drive the application or claim UI acceptance.

## Stop

```powershell
./tools/LumenAcceptance/Stop-Environment.ps1 -RunDirectory '<absolute returned run directory>' -ValidateOnly
./tools/LumenAcceptance/Stop-Environment.ps1 -RunDirectory '<absolute returned run directory>'
```

Stop validates the exact workspace/run, executable path/hash, PID, and start time before signaling. It never kills another listener or deletes evidence. A reused PID or unexpected port listener fails closed. Close the acceptance application through its normal UI before stopping the fixture, then record the application's exit separately. Never replay a previous run's PID.

## Optional Frozen App Launch

`Start-App.ps1` requires PowerShell 7.4 or later. Use it only when the normal
WinUI output is complete and no build is writing that directory:

```powershell
./tools/LumenAcceptance/Start-App.ps1 `
    -PublishDirectory '<absolute complete application output in this workspace>' `
    -ProfileDirectory '<absolute prepared acceptance profile>'
```

The helper creates a new candidate directory under the acceptance artifacts,
hashes every source file, copies the entire payload, then verifies both source
stability and copied hashes before launch. It rejects reparse-point payloads and
ordinary or unprepared account directories. `Start-Process -Environment` sets
`EMBY_CLIENT_DATA_ROOT` for the child only; no global environment variable is
changed. The process is started with `-WindowStyle Hidden`, and the normal WinUI
activation path owns its visible window.

`app-inputs.json` records the frozen output inventory. `app-ownership.json`
records executable path/hash, PID, precise UTC start time/ticks, profile, and
snapshot identity. These are launch records, not proof of a targetable window,
rendered frames, native acceptance, or a successfully restored application view.
The UI owner must select the returned actual window and record those observations.
If receipt writing fails after launch, the helper emits the owned identity and
fails without signaling any process; retain the output and let the UI owner
close only the verified application. This helper does not automate the desktop.

## Evidence boundary

The fixture supplies development metadata and actual generated MP4 bytes, not an official Emby implementation. Duplicate synthetic source aliases use the same measured video and must not be represented as different resolutions/codecs/tracks. Build and bootstrap checks do not prove native WinUI bindings, frame presentation, focus, responsive layouts, audible output, real-server transcoding, subtitles, or installed-package behavior. Record native screenshots and UI actions against the exact application candidate separately; retain failed attempts without promoting them to a later candidate.

## Explicit Owned Official Mode

When the separately authorized `LumenOfficialValidation` deployment is ready, the same tool can bootstrap only its dedicated test account. This is an explicit mode, not a fallback from synthetic validation:

```powershell
& ./artifacts/lumen-acceptance/tool-build/bootstrap/EmbyClient.LumenAcceptance.exe `
    --workspace-root '<absolute workspace root>' `
    --run-directory '<new absolute child of artifacts/lumen-acceptance>' `
    --server-url 'http://127.0.0.1:<owned local SSH tunnel port>' `
    --official-credentials-file '<absolute artifacts/lumen-official-validation/credentials.json>' `
    --theme Dark
```

The credential file contains the generated test username/password, expected server ID/version, loopback URL, and ownership receipt path. It is read in-process only, never passed on a command line or printed. The tool rejects credential/receipt paths outside the dedicated backend artifacts, reparse-point paths, expired receipts, an unexpected official image/package, public Docker ports, or a mismatched live SSH PID/start time/path/hash. It then checks public server ID/version before sending the test password. `ExpectedIsAdministrator` defaults to `false`, preserving the ordinary test account restriction. A separately authorized metadata-editing trial may explicitly set it to `true` only for the dedicated `lumen-owned-admin` username. Both sign-in and a new production restore verify the exact expected role; the credential-free bootstrap receipt records `IsAdministrator`. No other administrator username is accepted.

The supported source allowlist is official Emby `4.9.5.0`, either its previously pinned OCI image digest with an internal bridge and no Docker published ports, or its pinned Debian package SHA-256 inside a verified private network namespace. Changing that source requires a deliberate allowlist update. A passing official bootstrap proves scoped API authentication and Windows protected-account restore, not actual WinUI interaction, rendering, subtitles, real media breadth, or broad Emby compatibility. Keep the dedicated backend alive during UI acceptance and use its own ownership-aware cleanup afterward.
