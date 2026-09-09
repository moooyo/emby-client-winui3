# Updates and servicing

This is the initial update policy for the Windows x64 development client. An automatic updater is deferred by the implementation plan. MSIX is the intended installed format; the production publisher, signing service, and Store/direct-download channel still require an owner decision.

## Development artifacts

CI produces a complete unsigned Native AOT folder and a structurally verified unsigned MSIX candidate. Each artifact identifies its source revision. CI artifacts expire after seven days and are development checkpoints, not a supported release channel.

For a development-folder update, close the application, extract the entire new build into a separate directory, and launch its executable from that directory. Do not overwrite native DLLs, resources, notices, or the executable while the old process is running. Keep each complete output together. A single executable copied from the folder is not a valid deployment.

The client does not download or execute updates in the background, contact an update service, or change Windows trust settings. It does not store passwords. Saved sign-ins remain protected for the Windows user; compatibility and data-location behavior between installed and unpackaged builds must be tested before advertising migration between those formats.

## Installed releases

After choosing the production identity and distribution channel, use the Windows MSIX upgrade model with a stable package name/publisher and increasing four-part package versions. A Store channel would use Store delivery. A direct-download channel could later use an approved signed MSIX/App Installer setup. Neither channel is configured or published by the current repository.

Every installed release must first pass installation, activation, playback, upgrade from the preceding supported release, and uninstall on a clean supported Windows image. Test protected settings, device identity, window placement, and diagnostics data explicitly. Do not infer these results from a successful MakeAppx invocation or from running the unpackaged developer folder. See [packaging](packaging.md) for the concrete candidate and signing boundaries.

## Data compatibility and rollback

Prefer additive settings changes. Before introducing a breaking settings format, add a versioned migration, preserve the prior complete settings file, and document which application versions can read the result. A downgrade that silently drops unknown fields is not a supported rollback strategy.

Development-folder rollback is permitted only when the earlier build remains compatible with the settings it will read. Keep the complete prior application directory for that purpose. Installed MSIX rollback and downgrade require their own signed-package and data-compatibility procedure; no automatic rollback is promised. If migration is unsupported, document a fresh sign-in path instead of suggesting that users copy decrypted tokens or passwords.

## Servicing responsibility

The release output contains Native AOT runtime code and self-contained Windows App SDK dependencies. Maintainers must review applicable servicing releases, intentionally update pinned versions and lock files, and rebuild/retest the output. Updating a separately installed .NET SDK or runtime does not replace code already compiled into this application.

For each candidate, retain the source revision, resolved dependencies, complete original notices, SBOM, artifact/package checksums, build logs, and applicable native/UI/installation receipts. Changes to the playback engine, transport, source lifecycle, or native dependencies require relevant playback validation; a dependency-version edit alone is not release acceptance. Signing is a separate release step with protected credentials, never a pull-request requirement.
