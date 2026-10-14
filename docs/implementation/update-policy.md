# Updates and servicing

This is the update policy for the Windows x64 development client. Settings provides an explicit public-release check, a separate cancellable download, and a separate install-and-restart action for complete unpackaged Native AOT releases. There is no background update polling or installation. MSIX remains the intended packaged format; the production publisher, signing service, and Store/direct-download channel still require an owner decision.

## Development artifacts

CI produces a complete unsigned Native AOT folder and a structurally verified unsigned MSIX candidate. Each artifact identifies its source revision. CI artifacts expire after seven days and are development checkpoints, not a supported release channel.

For a development-folder update, close the application, extract the entire new build into a separate directory, and launch its executable from that directory. Do not overwrite native DLLs, resources, notices, or the executable while the old process is running. Keep each complete output together. A single executable copied from the folder is not a valid deployment.

The client does not download or execute updates in the background or change Windows trust settings. An explicit Settings check contacts the configured repository's public GitHub Releases API using an isolated unauthenticated transport. It does not store passwords. Saved sign-ins remain protected for the Windows user; compatibility and data-location behavior between packaged and unpackaged builds must be tested before advertising migration between those formats.

## Explicit unpackaged release updates

The release source is `moooyo/emby-client-winui3`. Only a published stable numeric version newer than the running client can be downloaded. A compatible asset is a complete ZIP named `emby-client-windows-x64-aot-<version>.zip`, `emby-client-windows-x64-aot-<tag>.zip`, or `emby-client-windows-x64-aot.zip`; attach exactly one compatible ZIP to a release. MSIX files, single executable files, and expired CI artifacts are not accepted by the folder-release installer.

Create the ZIP from a completed, verified Native AOT publish with:

```powershell
pwsh -NoProfile -File ./scripts/Package-Release.ps1 -PublishDirectory ./artifacts/aot -Version 0.1.0
```

The script preserves the complete application, runtime, native decoder libraries, assets, notices, and SBOM. It adds `release-update-manifest.json` with the matching version, architecture, file paths, lengths, and SHA256 hashes, and writes a ZIP `.sha256` sidecar. Generated files remain under `artifacts`; the script does not build, sign, publish to GitHub, install, or launch an application. The release version must match the application's SBOM. Publish the ZIP and sidecar together under the matching stable tag after the required release acceptance checks. No GitHub release-publishing workflow is currently configured.

Download begins only when the user chooses it. The service limits metadata, checksum, compressed and expanded sizes, entry counts, redirects, and duration, and propagates cancellation through body reads and extraction. It accepts only HTTPS URLs for this repository and GitHub's release download hosts. It checks the GitHub SHA256 asset digest and a recognized published sidecar when provided; a checksum verifies transfer integrity and is not a publisher signature. It also validates the entire manifest inventory, rejects unsafe Windows paths, links, collisions, extra or missing files, and checks the x64 Native AOT application and required self-contained payload. ZIPs without the manifest are not update candidates even when their asset names match. A failed or cancelled download removes only its owned incomplete stage and leaves the running application intact.

A successful download retains a separate validated stage and `RECOVERY.txt`. Installation begins only after the user confirms **Install and restart**. The client rechecks staged hashes, generates its own trusted PowerShell helper outside the downloaded payload, and waits for the helper to accept its installation plan before asking the app to exit. The helper waits for that exact process to exit without terminating it, verifies and copies the full payload into a new per-user version directory, and retains the complete previous application directory. It does not overwrite the running executable or native DLLs, request elevation, register automatic startup, or change certificate trust.

The helper creates or updates only the current user's `Lumen Emby Client.lnk` in the Start Menu, targeting the new version directory with its application icon. An unrelated existing shortcut with that fixed name blocks installation instead of being overwritten. A previous owned shortcut is retained for recovery. Existing portable shortcuts remain pointed at their original directories; use the new Start Menu entry for subsequent normal launches. The installation confirmation shows the destination directory and Start Menu name.

`install.log` and `RECOVERY.txt` remain in the stage. If installation fails before a new process is created, the helper attempts to restore the owned shortcut and restart the previous executable. Once a new process has been created, later activation or runtime failures require inspection and a compatible manual recovery; process creation does not establish a healthy application. Application data is preserved, and no automatic settings downgrade or data rollback is promised. Windows process handoff, shortcut creation/recovery, clean activation, and upgrade behavior still require Windows acceptance before this path is advertised as a supported release channel.

## Installed releases

After choosing the production identity and distribution channel, use the Windows MSIX upgrade model with a stable package name/publisher and increasing four-part package versions. A Store channel would use Store delivery. A direct-download channel could later use an approved signed MSIX/App Installer setup. Neither channel is configured or published by the current repository.

Every installed release must first pass installation, activation, playback, upgrade from the preceding supported release, and uninstall on a clean supported Windows image. Test protected settings, device identity, window placement, and diagnostics data explicitly. Do not infer these results from a successful MakeAppx invocation or from running the unpackaged developer folder. See [packaging](packaging.md) for the concrete candidate and signing boundaries.

## Data compatibility and rollback

Prefer additive settings changes. Before introducing a breaking settings format, add a versioned migration, preserve the prior complete settings file, and document which application versions can read the result. A downgrade that silently drops unknown fields is not a supported rollback strategy.

Development-folder rollback is permitted only when the earlier build remains compatible with the settings it will read. Keep the complete prior application directory for that purpose. Installed MSIX rollback and downgrade require their own signed-package and data-compatibility procedure; no automatic rollback is promised. If migration is unsupported, document a fresh sign-in path instead of suggesting that users copy decrypted tokens or passwords.

## Servicing responsibility

The release output contains Native AOT runtime code and self-contained Windows App SDK dependencies. Maintainers must review applicable servicing releases, intentionally update pinned versions and lock files, and rebuild/retest the output. Updating a separately installed .NET SDK or runtime does not replace code already compiled into this application.

For each candidate, retain the source revision, resolved dependencies, complete original notices, SBOM, artifact/package checksums, build logs, and applicable native/UI/installation receipts. Changes to the playback engine, transport, source lifecycle, or native dependencies require relevant playback validation; a dependency-version edit alone is not release acceptance. Signing is a separate release step with protected credentials, never a pull-request requirement.
