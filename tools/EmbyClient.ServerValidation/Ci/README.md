# Manual official-server protocol CI

This lane exercises the existing `ApiProbe` against a disposable official Emby Server. It is separate from the Windows application's build/package workflow and does not open a player, control a desktop, decode video, install MSIX, or establish native subtitle/rendering acceptance.

## Host and source reuse

The [manual workflow](../../../.github/workflows/emby-protocol.yml) runs only through `workflow_dispatch` on `main`, with read-only repository permissions and no production credentials. It uses a disposable `ubuntu-24.04` GitHub-hosted x64 runner. The runner supplies a local Linux Docker daemon; the script fixes all Docker calls to `unix:///var/run/docker.sock`, removes inherited context/TLS overrides, and requires Docker Engine 28.0.0 or newer. It does not install Docker or use a remote daemon. The official [Ubuntu runner inventory](https://github.com/actions/runner-images/blob/main/images/ubuntu/Ubuntu2404-Readme.md) listed Docker 28.0.4 when this route was prepared; the image is rolling, so the runtime check remains mandatory.

`ApiProbe.Ci.csproj` directly links the existing four C# files in `../ApiProbe/` and references the same `EmbyClient.Api` and `EmbyClient.Playback` projects. This runner is a framework-dependent .NET 10 CLI without a RID or app host. It retains the source-generated JSON and compatibility analyzers. It does not copy or fork the protocol implementation. The original `win-x64` Native AOT probe and Windows product projects are unchanged; passing this lane cannot replace their Windows/AOT checks.

The SDK comes from `global.json` and is installed into a separate `RUNNER_TEMP/dotnet-protocol` directory. Its actual version must match exactly. Restore uses the CI project's lock file without propagating a Linux RID through the shared projects. CLI outputs and NuGet/intermediate files also use runner-temporary directories. The workflow installs Ubuntu's FFmpeg package solely to generate synthetic input and records its actual version; it does not bundle that tool into the application.

## Disposable service and media

The server version is fixed to **4.9.5.0**, using the official image's recorded multi-platform digest and an explicit `linux/amd64` platform:

```text
emby/embyserver@sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a
```

This digest was already recorded in the [official-server environment notes](../README.md). The runner additionally checks the image's actual OS, architecture, repository digest, and image ID. The [official Emby download page](https://emby.media/docker-server.html) identifies this publisher; its [image documentation](https://hub.docker.com/r/emby/embyserver) documents the `/config` volume and `UID`, `GID`, and `GIDLIST` settings.

The orchestration creates exactly one owned container and one unique internal bridge network. It publishes only `127.0.0.1:19096` to container port `8096/tcp`; no UDP, HTTPS, host networking, Docker socket, GPU, user media, or production configuration is mounted. Only the run's private configuration directory and read-only synthetic-media directory are mounted. The container has CPU, memory, process-count, and Docker-log limits. Ownership labels, exact names/IDs, network isolation, port bindings, mounts, and the image ID are inspected before startup and again before/after the protocol probe. Docker's [internal networks](https://docs.docker.com/reference/cli/docker/network/create/#internal) and [localhost publication rules](https://docs.docker.com/engine/network/port-publishing/) define the intended boundary. The Docker version floor accounts for the documented older localhost-publication behavior.

FFmpeg generates one 60-second, 640 x 360, SDR H.264 baseline/yuv420p MP4 with one default AAC stereo 48 kHz track. FFprobe checks those characteristics. A matching `.eng.srt` file contains three generated cues. Both files receive hashes in the safe summary. This is protocol input, not a visual or audible acceptance fixture.

The private configuration starts with the wizard incomplete, a random per-run server name, and remote access/UPnP/updates disabled. Setup verifies the expected name and exact version, records only a SHA-256 of the generated server identity, and then uses the established startup API sequence to create separate temporary administrator and ordinary playback users. Passwords are generated in memory; the playback credential file is mode 0600 under a mode 0700 runner-temporary directory. No password or token is put in command arguments, workflow inputs, environment variables, or console output. Emby Connect and Premiere credentials are never supplied.

The movie library uses the container path `/mnt/protocol-media`. Internet metadata, subtitle download, chapter extraction, and realtime monitoring are disabled. Readiness requires the ordinary account to see a library and exactly one video item, with a single source, a positive runtime, readable favorite/played booleans, video, audio, and an actual text subtitle stream. The script reads back nonadministrator/non-disabled playback/remux/transcode permissions. It never elevates the probe account to make a check pass. Item and subtitle indexes are discovered from this new server, not copied from earlier local receipts.

The existing Windows named-pipe Docker and WSL namespace scripts are deliberately not invoked. Their host checks and filesystem paths describe different environments. All API requests here use the fixed loopback origin without proxy inheritance or redirects. The API probe still enforces its own loopback-only media-transfer boundary; an unexpected container-address media URL fails that check rather than being silently rewritten.

## Acceptance and exported data

The original probe has 33 required baseline steps, four supplemental steps, and three dynamically added text-subtitle steps. Its exit code alone can therefore accept fewer than 40 successful checks. This CI wrapper requires the exact recorded **40-step set**, every step `Passed`, no unknown/duplicate/missing step, exit code zero, and the expected server version. This includes all three encoding-cleanup checks, user-state restoration, logout, and rejection of the retired token. Synthetic lifecycle reports remain explicitly distinct from decoded playback. The probe reads only the first HLS segment; it does not test tail completion or ASS/PGS presentation.

The only upload path is `artifacts/emby-protocol-ci-summary/summary.json`. The wrapper reconstructs that file from fixed fields: orchestration stage/outcome, source revision/hashes, exact tool/image identities, fixture hashes/categories, isolation booleans, server-identity hash, fixed check names/statuses, numeric HTTP statuses/counts, and cleanup outcome. It excludes the probe's arbitrary evidence dictionary and raw error text. The original probe report, Docker/FFmpeg/CLI output, configuration, credentials, media, and server logs remain private temporary data and are never uploaded. Artifacts expire after seven days.

## Timeouts and cleanup

The workflow has a 35-minute job timeout. The server/probe step has a 20-minute limit, the Python flow has an 18-minute operation deadline, and subprocesses, HTTP calls, startup/scan polling, and cleanup have separate finite limits. The probe keeps its existing eight-minute cancellation deadline and receives up to eleven minutes to finish its independent cleanup before the outer process limit applies.

The Python `finally` block stops/removes only the exact labeled container, then removes only its labeled network and its checked run directory. It verifies absence after removal. A failed Docker command is not treated as proof that a resource is absent. A graceful-stop failure still attempts forced removal of the owned container and its anonymous volumes. There is no Docker prune, unrelated listener shutdown, broad resource deletion, or local WSL change. A separate workflow `always()` step retries the same owner-checked cleanup if the run failed or was interrupted. Resource state is persisted before creation, allowing cleanup to find a resource whose create call timed out before returning its ID.

An abrupt runner/VM loss can prevent either cleanup path from executing; the hosted runner is disposable, and the workflow does not claim a successful cleanup in that case. A cleanup failure makes the orchestration fail and preserves the private ownership file for the workflow's retry. The summary is the only retained artifact.

## Current validation boundary

The new CLI project completed a local locked restore and Release framework-dependent publication on 2026-09-09 with zero warnings/errors and unchanged lock files. The CLI was not run. Ten standard-library unit tests passed locally without Docker or API calls, covering exact report completeness, supplemental-step failures, safe field reconstruction, and rejection of foreign cleanup identities. The workflow repeats these tests before creating a server. The workflow and Linux orchestration were prepared by static review; no Docker server, API probe process, or new hosted workflow was started for those checks. The first dispatched run must establish Linux execution, official-container initialization, media URL behavior, all 40 checks, and owner cleanup. Earlier Windows/WSL API results are not transferred to this lane, and later protocol success will not alter the separate native/UI/installation acceptance gates.
