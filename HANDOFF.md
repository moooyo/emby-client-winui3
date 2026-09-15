# V7 WinUI handoff

Updated: 2026-09-15. Workspace: `D:\Code\emby-client-winui3`. Branch: `codex/v7-non-ui-completion`.

## Current state

The user resumed and explicitly authorized local UI testing. The isolated native acceptance batch is complete. All defects reproduced during this batch were repaired and their affected scenarios retested. This is scoped development acceptance, not complete accessibility, compatibility, or release certification.

The [native report](docs/implementation/v7-native-acceptance-20260915.md) records twenty journey scopes, individual candidate identities, failed attempts, actual screenshots, and NotObserved requirements. The [machine-readable receipt](docs/implementation/verification/v7-ui-20260915.json) records final build, tests, package, and cleanup. Detailed local artifacts are under `artifacts/ui-v7-acceptance-20260915-072317`.

## Final outputs

- Native AOT: `artifacts/ui-v7-acceptance-20260915-072317/candidate-accepted/EmbyClient.App.exe`, 19,998,720 bytes, SHA-256 `ECC9578458C70D1BCAC6E337DDEC0A16552F5A53040EA95F2E92EA68E5405CA6`. Preserve its complete directory.
- Unsigned MSIX: `artifacts/ui-v7-acceptance-20260915-072317/packages/EmbyClient.Windows_0.1.0.0_x64_unsigned_20260915-091421469-ef32a64f.msix`, SHA-256 `7FF32711E0C372E4276DFD6912B72BD97EEB1C8670539117C7C2513358CF1A5E`. Structural verification passed; it was not signed or installed.
- Release solution build passed with one existing generated WinUIEx Icon CS0618 warning. All 610 tests passed: API 53, AppState 46, MediaTransport 125, Platform 244, Playback 142. Zero failed/skipped. All 188 recorded inputs remained unchanged through final checks.

## Native findings repaired

- Concrete WinRT brush projections avoid the startup crash; app-only generated bindable metadata exposes MediaCardViewModel.AutomationLabel to runtime XAML bindings.
- Library sizing reconnects the ScrollViewer after layout and uses actual panel-available width. Active poster bindings retain the managed container until existing cleanup, preventing the reproduced maximize/restore poster loss. The lifetime mechanism is an inference from source; the before/after native scenario was observed.
- Narrow cast previews show complete people only, with all six available in the expanded grid. Primary Play and diagnostic event controls have useful accessible names. Unnegotiated retry audio reads Audio tracks unavailable.
- Both queue entry points refresh realized rows after layout with explicit projections. Removing the first item now updates ordinal, total, and Next in queue; keyboard removal preserves the paused position and returns focus when empty.

Earlier candidates establish scoped sign-in/recovery, Light/Dark details, media fields, person/work/back, canceled account dialogs, actual colored video, injected opening failure/retry, queue edits, episode controls, one natural continuation, fullscreen/auto-hide, diagnostic JSON export, catalog error/retry, pagination, and search. Final ECC95784 repeats Home, both queue regressions, resume return, and poster maximize/restore. Do not relabel earlier screenshots as final-candidate evidence.

## Ownership and authorization

Local Windows build/test/runtime/UI verification was explicitly authorized in this task. All native work used Computer Use and synthetic media/account data with process-only `EMBY_CLIENT_DATA_ROOT`. The task-owned profile was reused between candidates; normal user settings were not touched.

Final app PID 11380 exited normally after Alt+F4. Fixture run `20260915-080117309-2be7ccf9` was stopped through its checked ownership record; ports 18978/18979 are released. No app or fixture remains running from this acceptance batch. A future test must start a fresh owned fixture and retain its new ownership record. Never replay old PIDs.

## Remaining coverage and release gates

High contrast, reduced effects, 200% system text, Narrator/full keyboard focus coverage, in-flight/late account cancellation in native UI, stale-response races, multiple seasons and playable sources/tracks, long-session memory, real-server/device breadth, signed installation/upgrades, and clean-machine playback remain unverified. The 25 transaction cancellation tests and earlier protocol/native evidence do not fill these UI gaps.

The previous non-UI handoff is preserved at `artifacts/ui-v7-acceptance-20260915-072317/HANDOFF-before-ui-acceptance.md`; the non-UI baseline commit is `ba85f2aa010e1a33f5ec79fb87dd2917b6c58187`. No remote push, production signing, installation, or deployment was requested.

Use Chinese for user-facing conversation and English for code/comments/documentation. The shell is PowerShell. Preserve the approved V7 design and existing implementation; do not restart the mockup phase.
