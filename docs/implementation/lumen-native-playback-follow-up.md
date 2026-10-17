# Native playback follow-up

Current authority: the [2026-10-17 code closeout](current-code-closeout-20261017.md).
Final16 is **BuildPassed**: Release/Debug, fresh Native AOT, package/runtime and
offline schema checks have actual exit 0. Its 30,613,504-byte EXE is
D72B6237...4651; inventories are 675/260/24/595. All 2,430 product cases and the
114-input NativeProbe compilation use matching complete prior compiled-closure
evidence, with no reruns for this UI-only delta.

**BoundedNavigationObserved** covers three short 005000/000001 End/Home rounds,
the next Right action and normal exit 0 after the Home anchor/scroller repair.
Formal06 is **OriginalFiniteUiWorkloadCovered_WithObservationLimit**: its finite
UI sequence, fixed idle windows and in-cap sign-out/normal exit are covered;
tail warm returned to the real first viewport without repeating its numeric
tooltip. The resource result is **InvestigateHighRetention / PerformanceNotAccepted**:
two increments exceed the 32 MiB investigation threshold and library-cycle
handles keep rising, with attribution unresolved. Final owned cleanup and
system restoration are complete.

The user renewed unattended continuation. The earlier physical-Escape stop and
goal-service blocked receipt are historical; no new goal-service status is
claimed. **LANDeferred** still covers discovery, public Save and account
restoration. Earlier candidates and all failed/aborted attempts retain their
own scope. CI, release, licensing/signing and installation/distribution remain
outside this acceptance scope. The user separately authorized committing and
pushing the current source snapshot to `main`; source publication does not change
the recorded acceptance results.

Date: 2026-10-13. This record covers the requested complete ASS/SSA renderer,
actual audio passthrough, server thumbnail images and buffered media duration.
It supersedes the corresponding source-only gaps in the earlier V2 ledger.
The earlier `final-02` verification receipt applies to its own snapshot only.

The later [Windows continuation](lumen-windows-continuation.md) has explicit
user authorization for local Windows compilation, tests, UI/UX checks and
computer use. It records the subsequent build, test and owned application
observations separately. The implementation and remote-only verification
sections below describe the original native follow-up checkpoint; their
source, authorization and historical receipts are not retroactively changed
by the continuation. Consult the linked record for current acceptance and
remaining output limits.

The resumed publish-04 [app-04 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/UI-REVIEW.md)
now records static ASS layers in wide, 2560 x 1440 fullscreen, restored and
ordinary 780 x 894 windows, the narrow final Ended state at `1:00 / 1:00`,
twelve server BIF JPEGs at 0, 5, ..., 55 seconds and the named Home action
tooltips. The separate [app-05 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/UI-REVIEW.md)
accepts both embedded PGS cues, first-cue absence by 18.7904166 seconds, Off
clearing, seek-before-cue recovery,
narrow/fullscreen/restored layout and Back clearing. Reselecting at the paused
mid-cue position did not alone restore the earlier bitmap. The wide/fullscreen
HUD crosses some lower glyph pixels; narrow/restored samples show the complete
second cue. Both fresh runs
have actual retained-handle exit 0 after normal title-bar Close:
[app-04](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-04/app-exit-final.json),
[app-05](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-05/app-exit-final.json).
These finite observations do not establish complete continuous ASS animation,
all SAR/PiP geometry or physical output hardware behavior.

The current native-05 [AudioProbe run-02](../../artifacts/lumen-windows-continuation-20261013/native-audio-probe/run-02/ACCEPTANCE.md)
passed four PCM policy/rejection cases, paused seek/resume and cleanup, with an
actual retained-handle exit 0. BH900 PRO rejected AC3/EAC3 exclusive formats;
AAC used the unsupported-codec PCM path. No carrier frames were submitted.
Successful receiver bitstream, active-carrier controls, software-volume
bitstream-to-PCM replacement and physical HDR remain unverified. The app-04
information panel reported Buffer and Dropped frames as Unavailable; no
numerical buffered interval or presentation-drop measurement is claimed.

[Publish-05](../../artifacts/lumen-windows-continuation-20261013/run-02/copy-feedback-publish-05/RESULTS.md)
contains only the two Copy feedback calls changed to `ShowPlayerToast` after
the app-04 toast visually overlapped the panel Close area. Normal Debug/AOT
compilation and package/SBOM readback exited 0; exactly one of 668 production
files changed and all 24 native runtime files remain identical. The
[app-06 review](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/UI-REVIEW.md)
accepts bottom Copy success feedback clear of Close in the 780 x 894 window,
subsequent feedback disappearance, later panel Close, a complete PGS first cue
during `1x` playback, Back to details and
[normal same-handle exit 0](../../artifacts/lumen-acceptance/windows-continuation-20261013/app-06/app-exit-final.json).
This targeted feedback/basic-PGS regression does not rerun the earlier
publish-04 lifecycle matrix. Clipboard contents, failure feedback and Close
while feedback is visible remain outside the observed scope.

The [final non-UI audit](../../artifacts/lumen-windows-continuation-20261013/run-02/final-verification-02/app06-final-non-ui-20261014/APP06-NON-UI-AUDIT.md)
records full app-06/publish-05 payload readback and a fresh 668-source/24-runtime
manifest matching the publish-05 freeze. Its initial static-SVG privacy-guard
refusal remains recorded with the later complete readback. The
[final owned cleanup](../../artifacts/lumen-acceptance/windows-continuation-20261013/owned-cleanup-final-20261014.json)
records canonical fixture Stop command exits 0, absent owned processes and
released ports. The outer collector naturally exited 1 after pipe closure and
its deferred wrapper guard; that does not change the original launcher's exit
0, and no collector stop signal was sent. The scoped local continuation is
complete. Previous 2,294-unit, HTTP, native and audio results retain their own
earlier source scope. Historical app-03 actual exit and AOT03 build exit remain
Unknown.

## Implemented source paths

| Requirement | Source path | Availability and fallback |
| --- | --- | --- |
| Complete ASS/SSA styles, drawings, animation, karaoke and fonts | A separately pinned libass 0.17.5 renderer, `SubtitleProviderLibass`, versioned subtitle C ABI and the managed ASS owner | Original external ASS/SSA is preferred, followed by an exactly mapped embedded stream. Local scripts retain their complete content. Actual container font attachments and script font records are supplied to libass. A missing renderer retains the old explicitly limited native timed-text fallback. |
| Original subtitle timing and seek recovery | Actual video sample timestamps schedule bounded reference cues; the currently presented cue renders the real libass image | Full scripts support arbitrary script-time lookup. Embedded streams replay only bounded, previously demuxed chunks, preserving their read order, PTS, duration and layer. A first seek into an unread interval cannot establish a preceding overlapping embedded event; the complete external script is preferred for this reason. The client does not scan the entire video to construct a subtitle dataset. |
| AC3, EAC3, DTS, DTS-HD and TrueHD output | The selected shared-demux audio provider sends real AVPackets through FFmpeg's public `spdif` muxer to WASAPI | The current endpoint must accept and initialize the exact IEC 61937 format in exclusive mode. The unchanged original source and exact native audio index are required. Unsupported formats or endpoints use decoded PCM. Transcode delivery disables passthrough. |
| Audio/video synchronization and controls | WASAPI follows the same agile `MediaPlaybackSession` position, state and rate as the existing video/HDR path | Hardware audio clock observations detect drift. Pause, buffering, seek, audio delay and device changes operate on bounded queues and generations. Software volume, mute and non-unit playback rate first reopen the same source as PCM while retaining the playback ID, item position, pause state and imported subtitle. |
| Actual output diagnostics | A versioned native observation exposes the endpoint, accepted output format, carrier/content parameters and submitted frames | Requested policy and source codec never establish output. Bitstream is reported only after real carrier data has been accepted and the WASAPI client has started. In-flight output failures are explicit, and a local PCM backend replacement does not create another server playback session. |
| Real video thumbnail strip | Authenticated `GET /Videos/{Id}/index.bif?Width=240` and a bounded V0 BIF reader | At most 12 actual JPEG samples are shown at their server timestamps. The entire supplied index and image structure are checked. Unsupported or absent packages retain real chapter artwork and thumbnail metadata. Account, item and source ownership prevent stale images. No local video extraction or fabricated frame is used. |
| Buffered media seconds | `MediaPlaybackSession.GetBufferedRanges()` and a portable interval policy | Only the valid contiguous range containing the actual native URL position contributes buffered-ahead seconds. Empty, invalid or unsupported ranges remain unavailable. Buffering percentage and playback rate are not converted into guessed seconds. |

The libass dependency lock uses an exact vcpkg baseline, source versions,
source hashes, license provenance and a separate Windows runtime closure.
Native builds check the real libass import and all versioned C ABI exports.
Package and SBOM validation keep the seven FFmpeg libraries separate from the
additional libass dependencies. The optional libunibreak-based
`ASS_FEATURE_WRAP_UNICODE` feature is disabled in this pinned build.

FFmpeg's public muxer can require complete MAT or EAC3 groups. A missing major
sync, an incomplete final group that cannot be drained through the public
interface, a changed packet format or a bounded queue failure must request
PCM recovery. Partial compressed data is not passed off as a complete stream,
and the client does not read private FFmpeg muxer structures.

## Explicit remaining boundaries

- External binary VobSub pair import is unchanged: `.idx` carries the index and
  `.sub` carries subtitle images. Embedded VobSub remains a separate supported
  native path. A text `.sub` file is not a binary VobSub pair.
- The current MediaPlayer/FFmpegInterop path exposes decoded-frame observations,
  but no verified presentation dropped-frame counter. The dropped-frame row
  remains unavailable.
- Normalized audio waveform samples, media-time bitrate curves and normalized
  bitmap subtitle cue timelines remain unavailable from the audited Emby API.
  No local media analysis, OCR, synthetic samples or substitute service is added.
- BIF images depend on a server-generated package and an identifiable source.
  Twelve images are a layout maximum, not a promise that every server or every
  edition supplies that data.

## Original remote verification scope

Only `ssh test-env` was authorized for execution in the original follow-up
task, whose verification host was Linux. Its portable-test preparation and
supplemented managed semantic scope are recorded
in the [new verification directory](../../artifacts/lumen-native-playback-20261013/remote-verification/).
Its [runbook](../../artifacts/lumen-native-playback-20261013/remote-verification/RUNBOOK.md)
defines the frozen input and historical generated-XAML provenance boundaries.

Windows native compilation, regenerated XAML, WinUI/AOT packaging, actual ASS
image presentation, WASAPI receiver output, video/audio synchronization and
hardware/display acceptance require a Windows verification environment.
Those checks were blocked on that designated Linux environment. Source
implementation and Linux semantic or portable results did not certify them.
No local build, application launch, installation, commit, push or release was
performed for this follow-up.
