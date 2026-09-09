# Official Emby Server validation environment

This directory prepares a disposable real Emby Server for client contract and playback validation. It supplements the synthetic fixture server; it does not turn fixture results into evidence of official server compatibility.

## Current evidence

On 2026-09-09, the initial inspection found no `docker`, `podman`, or `nerdctl` command and no listening TCP endpoint on 8096, 8920, or 19096. The existing WSL 2 Debian distribution used `mirrored` networking, so a NAT-isolation assumption was not available. No Windows portproxy entries were present.

An explicit user/network namespace plus a loopback-only bridge subsequently provided a usable isolation boundary. A Python probe first proved the complete Windows-to-WSL-to-namespace path and its cleanup. The official **Emby Server 4.9.5.0 Linux amd64** package was then extracted into a dedicated private `/tmp/emby-client-validation-<UUID>` directory without package installation or service registration. The server was started inside that namespace, initialized with separate temporary administrator/playback users, and scanned the generated sixty-second H.264/AAC clip plus an English SRT subtitle. Actual Windows requests verified public server information, successful playback-user login, a movie with video/audio/subtitle stream metadata, and logout. See the API probe's report for the broader contract results; native playback results belong to the Windows application's separate acceptance evidence.

The server's namespace contains only `lo`, no main-table routes, and only loopback local routes across all IPv4/IPv6 tables. The actual Emby process has the inner namespace identity; it differs from the outer bridge process. Emby's internal wildcard HTTP listener remains inside the isolated namespace. The outer bridge's actual listener is only `127.0.0.1:19096`; Windows can reach that endpoint. No WSL networking, firewall, router, or existing service configuration was changed.

The Docker PowerShell scripts were parsed and their missing-Docker failure path was exercised. The shared helper was checked with a harmless child PowerShell process to prove stderr stays out of returned stdout, and its file-lock exclusion/release behavior passed. An independent static review found no remaining actionable issues after fixes. **Docker container startup itself remains untested.**

The official Windows download page's package resolver selected stable release **4.9.5.0**. Its portable archive was downloaded into the ignored `artifacts/emby-validation/downloads` directory and extracted into `artifacts/emby-validation/portable-4.9.5.0`. The archive SHA-256 matched the release asset digest:

```text
6883356517a42316ef5b270081f81d8de4151fec384fbe1ec1b31d2cf77688f6
```

The official `emby/embyserver:4.9.5.0` Docker tag was resolved to this multi-platform manifest digest:

```text
sha256:734a6f03c7c783a9e566b08d09a2b6376f41229ff29f032a7e00302e0be98f8a
```

The executed Linux server came from the official `emby-server-deb_4.9.5.0_amd64.deb` asset, whose SHA-256 was checked before extraction and again by the startup script:

```text
1d718ffa0169c393de3eafda65b1b057a3db4ead93ffeb5883abd01735de9843
```

The scripts pin that digest. They do not follow `latest`, install Docker, modify firewall/router settings, configure a Windows service, stop existing listeners, or change other containers. They hold a shared exclusive workspace lock during start/stop and fix subsequent Docker calls to the local endpoint that passed inspection, even if the default context changes. Machine-readable standard output remains separate from Docker warnings. The Docker environment has not yet been available to verify the scripts' complete behavior.

## Why the Windows portable server was not started

The official Windows page supports launching `System/EmbyServer.exe` from the portable archive. However, the official network settings documentation describes **Local IP Address** as the address reported to apps, not as a guarantee that its HTTP listener binds only to that address. The published `LocalNetworkAddresses` configuration property likewise does not document such a guarantee. `EnableRemoteAccess=false` is an access policy and is not evidence of loopback-only binding.

Consequently, configuring `LocalNetworkAddresses` to `127.0.0.1` is insufficient evidence for starting the portable process on this developer machine. It must remain stopped until an official, verified listener-binding mechanism or an isolated execution environment is available. A wildcard listener should not be started first and inspected afterward.

## Verified WSL namespace workflow

This route requires an **existing** Debian WSL distribution with `unshare`, `ip`, `python3`, and `dpkg-deb`, plus permission to create an unprivileged user/network namespace. It does not install those tools or change the distribution's network configuration. It is an IP-network isolation mechanism for the trusted official binary, not a complete sandbox against malicious executables: the filesystem and pathname-based Unix sockets remain shared. Inherited proxy, desktop, WSL interop, and other integration environment variables are removed from child processes.

The previously downloaded official Debian package must be in `artifacts/emby-validation/downloads/emby-server-deb_4.9.5.0_amd64.deb`. Generate the sixty-second fixture using the command below, then run:

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.ServerValidation/wsl_start_official_server.py
```

Adapt only the repository path if the checkout is elsewhere. Keep this foreground invocation active throughout validation. The startup script holds an exclusive launch lock, checks port availability, verifies the pinned package hash, creates a private UUID directory, extracts the `.deb` with `dpkg-deb --extract`, copies only the generated media, writes initial settings, and launches the namespace runner. The server name includes the run UUID, and the current-runtime marker is published only after the namespace runner becomes ready. It follows the executable/environment paths in the package's official launcher while redirecting application data, home, temporary files, and cache into the task directory. It does not execute the package's service launcher or its installation scripts. Extraction and launch happen within one WSL invocation because `/tmp` content can disappear when an otherwise idle WSL instance stops.

The namespace runner first binds the outer endpoint to reserve the port, creates an independent user/network namespace, enables only its loopback interface, and checks all interfaces and route tables before starting Emby. A private Unix socket connects the inner HTTP relay to the outer loopback relay. It records `state.json` and `inner-state.json` in the task directory. It refuses an occupied outer port and never substitutes another address. Ctrl+C or SIGTERM stops its owned children and removes its own Unix socket; it does not shut down WSL. The startup wrapper archives local diagnostics under ignored `artifacts/emby-validation/wsl-runs/<UUID>` every two seconds and on exit; transient diagnostic-file access errors do not terminate the service. Raw server logs may contain authenticated request data and must be sanitized before sharing.

For a native UI acceptance session that outlives its initiating command, launch the WSL wrapper as an owned hidden Windows process with `Start-Process -WindowStyle Hidden`, redirect stdout/stderr to new files under the ignored artifacts directory, and retain its process ID. An earlier tool-owned execution session disappeared during UI acceptance, along with its WSL temporary directory, before exit-only logs could be copied. The following run used an independent hidden wrapper and continuous diagnostic snapshots. Do not interpret a missing tool handle or process as a still-running service; check the public endpoint and current owned process state.

Read the printed runtime directory or `artifacts/emby-validation/wsl-runtime-root.txt`, then initialize only that fresh owned server:

```powershell
$runtimeDirectory = (Get-Content .\artifacts\emby-validation\wsl-runtime-root.txt -Raw).Trim()
.\tools\EmbyClient.ServerValidation\Initialize-OfficialServer.ps1 -MediaDirectory "$runtimeDirectory/media"
```

The initializer checks the expected server version and unique run UUID in its public name, owned namespace state, isolated listener identity, and incomplete startup configuration. It uses the official setup/API endpoints to create `validation-admin` and nonadministrator `validation-user`, disable remote access/port mapping, and add only the task media folder with online metadata/image fetching disabled. It saves randomly generated local credentials in ignored files and never prints passwords or tokens. It refuses existing credential files so an earlier run cannot be silently overwritten. The running Emby public-info response does **not** expose `StartupWizardCompleted`; the initializer checks the owned server configuration instead.

Run the NativeAOT [API probe](ApiProbe/README.md) using `artifacts/emby-validation/official-user-credentials.json`. Use those same local credentials for native Windows UI acceptance, with the URL `http://127.0.0.1:19096`. Keep each test's device/session identity separate. Do not claim native frames, audio, or subtitle rendering from HTTP-only probe results.

After all callers finish, stop the exact owned namespace runner with the command below. The stop helper checks the runtime UUID, owner, current process command/namespace, and uses a Linux pidfd so PID reuse cannot redirect its signal. Add `--validate-only` to inspect ownership without stopping it.

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.ServerValidation/wsl_stop_official_server.py
```

## Additional generated media for native acceptance

Keep the initial `Fixture` movie intact. The following commands add a separate `Track Validation (2026)` movie using only that generated clip and the official server package's FFmpeg. Each FFmpeg/FFprobe process runs in another network namespace without external interfaces, with a minimal environment and task-local home/temp paths; nothing is installed. The generator stages both files outside the indexed media root and moves the completed movie directory into place only after validation. It refuses to replace an existing movie directory.

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.ServerValidation/wsl_generate_multitrack_media.py
.\tools\EmbyClient.ServerValidation\Refresh-MultiTrackValidationMedia.ps1
```

The movie has a 1280 × 720 H.264 version and an 854 × 480 H.264 version. Both contain two stereo, 48 kHz AAC tracks: English/default at 440 Hz and French at 880 Hz. The generator decodes the first second of each audio stream and compares its response at those two known target frequencies; the expected frequency must exceed the other by a factor of ten. This is a two-frequency discrimination check, not a full-spectrum peak search.

Files use the officially documented same-folder/same-prefix version convention:

```text
Track Validation (2026)/
  Track Validation (2026) - 720p.mp4
  Track Validation (2026) - 480p.mp4
```

The real Emby collection response exposes only the default source. The refresh helper reads item details to confirm that both sources are grouped under one primary movie ID. It saves source IDs and actual audio indexes to ignored `multitrack-api-evidence.json`; generated media properties and frequency evidence go to `multitrack-media-evidence.json`. IDs are assigned by the server and must be read from the current run's evidence.

For season/episode navigation and later automatic-next-episode acceptance, add an independent TV library containing two copies of the original generated clip:

```powershell
wsl --distribution Debian --exec /usr/bin/python3 /mnt/d/Code/emby-client-winui3/tools/EmbyClient.ServerValidation/wsl_create_episode_media.py
.\tools\EmbyClient.ServerValidation\Add-EpisodeValidationLibrary.ps1
```

The files are under a separate `tv-media/Validation Series (2026)/Season 01` root, with `S01E01` and `S01E02` in their names. Their hashes are checked against the original fixture. The library disables online metadata/image providers and does not alter either existing movie. The setup helper records actual series, season, episode, and media-source IDs in ignored `episode-api-evidence.json`. Sort/navigation must use episode numbers, not numeric item IDs: the tested server assigned the first episode a larger ID than the second. Before this validation user watched either episode, the series-filtered `NextUp` endpoint returned an empty collection; that observation does not prevent direct series/season browsing.

These additions establish generated stream properties, version grouping, and episode indexing. Actual native audio switching, version switching, and automatic-next-episode behavior require the Windows UI acceptance checks.

## Docker workflow

Prerequisites are PowerShell 7, an existing local Docker Desktop Linux-container engine, and Docker Engine 28.0.0 or newer. Older Docker Engine versions have a documented localhost-publication issue for neighboring hosts on the same network segment. The scripts refuse a remote Docker context and Windows containers.

Generate the project's sixty-second H.264/AAC synthetic media fixture first:

```powershell
dotnet run --project .\tools\EmbyClient.MediaFixtures\EmbyClient.MediaFixtures.csproj --configuration Release -- --duration-seconds 60 --output-dir .\tools\EmbyClient.MediaFixtures\artifacts\sixty-seconds
```

Start the official server:

```powershell
.\tools\EmbyClient.ServerValidation\Start-EmbyValidationServer.ps1
```

The script checks the engine, port, existing state, and fixture before creating a new run directory. It copies only the generated fixture into that directory. The container uses bridge networking and publishes only `127.0.0.1:19096:8096/tcp`. It mounts the run's own configuration directory at `/config` and its synthetic media directory read-only at `/mnt/validation-media`. No UDP, HTTPS, host networking, GPU, Docker socket, or user media mounts are exposed. Before starting, it inspects the requested port binding, network mode, privilege flag, and ownership label. It saves the exact container ID and Docker context in an ignored state file.

After startup, open `http://127.0.0.1:19096` and complete the official setup wizard. Use a new, temporary local account. Keep automatic port mapping and remote access disabled. Add a movie library whose only folder is `/mnt/validation-media`; disable online metadata/image providers for this generated clip. Do not link Emby Connect or enter a Premiere license. Retain generated credentials only in a local secret store or in process memory, never in tracked evidence.

Wait for the library scan to finish, then verify `/emby/System/Info/Public` reports `4.9.5.0` and that Docker inspection still shows the single loopback mapping. Only then configure the client to connect to `http://127.0.0.1:19096`. The first login must create the test user's actual server session rather than reusing a synthetic fixture token.

Stop the owned server after validation:

```powershell
.\tools\EmbyClient.ServerValidation\Stop-EmbyValidationServer.ps1
```

The stop script compares the recorded context, endpoint, ID, name, and ownership label before stopping/removing that container. It retains downloaded images and per-run configuration/media/logs. It does not recursively delete files. If the container was externally removed or the context changed, it fails closed so the saved state can be reviewed manually.

## Required real-server evidence

Record the client commit, server version/image digest, Windows version, and synthetic fixture metadata alongside sanitized results. Separate the HTTP contract evidence from the native player evidence:

| Area | Real-server evidence to record |
| --- | --- |
| Discovery and authentication | Public server information, successful/incorrect sign-in, current-user information, session registration, logout, and rejected old credentials |
| Library and details | User views, movies, item details, search, an empty result, pagination, and actual stream indexes |
| User data | Favorite and watched mutations, readback, rollback to the initial state, and resume position after stop |
| Original delivery | Capability-aware `PlaybackInfo`, authenticated byte-range request, native first frame/audio, pause, resume, seek, and natural completion |
| Server conversion | A server-returned HLS route, authenticated playlist/segment fetch, native first frame/audio, nonzero resume/seek, stop, and encoding cleanup |
| Tracks | Server-reported audio/subtitle indexes, selected track request, text subtitle delivery, and playback with subtitles disabled |
| Failure/lifecycle | Invalid token, unavailable server, replacement playback, cancellation, disposal, and no stale progress after stop |

Do not record passwords, access tokens, raw authentication responses, token-bearing URLs, or user media paths. If Emby rejects a feature because of licensing, record the denial without bypassing it. The free synthetic clip may establish a narrow H.264/AAC path; it cannot prove unrelated codecs, commercial media, hardware decoding, HDR, or broad Emby-version compatibility.

## Sources

- [Official Windows server download and portable instructions](https://emby.media/windows-server.html)
- [Official package resolver](https://emby.media/githubapi_1.js?v=2) and [release feed](https://emby.media/releases.json)
- [Official 4.9.5.0 release](https://github.com/MediaBrowser/Emby.Releases/releases/tag/4.9.5.0)
- [Official Linux server downloads](https://emby.media/linux-server.html) and [the executed Debian package](https://github.com/MediaBrowser/Emby.Releases/releases/download/4.9.5.0/emby-server-deb_4.9.5.0_amd64.deb)
- [Official Docker download page](https://emby.media/docker-server.html), [publisher image documentation](https://hub.docker.com/r/emby/embyserver), and [4.9.5.0 tag metadata](https://hub.docker.com/v2/repositories/emby/embyserver/tags/4.9.5.0)
- [Official network settings](https://emby.media/support/articles/Hosting-Settings.html) and [server configuration API](https://dev.emby.media/reference/pluginapi/MediaBrowser.Model.Configuration.ServerConfiguration.html)
- [Docker's localhost port-publishing behavior and version caveat](https://docs.docker.com/engine/network/port-publishing/)
- [Emby REST API](https://dev.emby.media/doc/restapi/index.html)
