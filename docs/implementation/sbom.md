# CycloneDX package SBOM

[`Generate-Sbom.ps1`](../../scripts/Generate-Sbom.ps1) creates a CycloneDX 1.6 JSON bill of materials from the existing app restore. It requires PowerShell 7 and the actual cached NuGet archives. It does not restore packages, compile the application, query a server, install tools, or change project files.

```powershell
.\scripts\Generate-Sbom.ps1
.\scripts\Generate-Sbom.ps1 -OutputPath artifacts/sbom/release.cdx.json
```

The default output is `artifacts/sbom/emby-client-win-x64.cdx.json`. Output must be a JSON file beneath the repository's `artifacts` directory; reparse points in that output path are rejected. An existing regular output file is replaced. `-AssetsPath` and `-LockPath` can select preserved restore inputs, but the assets must identify `EmbyClient.App` and contain exactly one restored .NET 10 Windows x64 target. The app project supplies the first-party application name/version. Preserve those inputs with the release evidence.

## Contents and reproducibility

The generator reads the app's `packages.lock.json`, `obj/project.assets.json`, and matching cached package nuspecs. It records the resolved product package graph and two framework downloads: `Microsoft.NETCore.App.Runtime.NativeAOT.win-x64` and `Microsoft.Windows.SDK.NET.Ref`. It rejects asset versions absent from the app lock file, missing package archives, or nuspec identities that disagree with the resolved package. It does not silently substitute another installed version.

Each component has a NuGet package URL (`pkg:nuget/...@...`), an actual SHA-512 digest of the cached `.nupkg` archive, the package's license expression/file/URL metadata, and distribution/source links. A license file is represented as a named NuGet license document; no SPDX identifier is inferred from a repository license. The original upstream texts remain in the separate [notice inventory](dependency-inventory.md).

NuGet lock `contentHash` values are retained as named properties, separately from the actual archive digest. They are not assumed to be interchangeable. Metadata also records SHA-256 digests of the restore inputs and each nuspec. Dependency relationships connect the app's direct product references and selected framework packs to the filtered package graph.

Compiler, linker, Windows SDK build/packaging packages are explicitly excluded by their reviewed package identities. Test and probe projects are not read, and passing their assets is rejected. LibVLC and SharpDX therefore do not enter the product SBOM. The exclusion list is included in metadata and must be reviewed when the product dependency graph changes. Windows App SDK deployment/meta-packages remain included because their MSBuild targets can supply self-contained runtime payload.

The output has sorted component/dependency lists and recursively sorted JSON object keys. It intentionally omits variable timestamps and random serial numbers, both optional in the schema, so identical inputs and cache archives produce repeatable bytes. No machine-specific cache paths are embedded. A different restore-input hash or package archive changes the inventory even when the displayed package version is unchanged.

This is a **package-level inventory, not an exact component analysis of the executable after trimming**. It can include packages whose managed code is trimmed, and it does not establish which native SDK features execute. C#/WinRT bundled inside the targeting pack is covered by that package's presence and the separate notice inventory; the generator does not invent a standalone NuGet dependency for it. First-party project references, embedded native subcomponents, optional OS codecs, separately installed browser runtimes, and final binary provenance require separate release review. No vulnerability or license-compatibility conclusion is produced.

## Schema validation

The structure was implemented against the official [CycloneDX 1.6 schema](https://github.com/CycloneDX/specification/blob/1.6/schema/bom-1.6.schema.json), including its referenced [SPDX schema](https://github.com/CycloneDX/specification/blob/1.6/schema/spdx.schema.json) and [JSON signature schema](https://github.com/CycloneDX/specification/blob/1.6/schema/jsf-0.82.schema.json). The generator itself performs JSON structure readback and reports that schema validation is separate.

The following commands download only the official schemas into `artifacts` and use the existing PowerShell validator. They do not install a global tool:

```powershell
$schemaDirectory = Join-Path (Get-Location) 'artifacts/sbom-schema'
New-Item -ItemType Directory -Force -Path $schemaDirectory | Out-Null
foreach ($name in @('bom-1.6.schema.json', 'spdx.schema.json', 'jsf-0.82.schema.json')) {
    Invoke-WebRequest ('https://raw.githubusercontent.com/CycloneDX/specification/1.6/schema/' + $name) -OutFile (Join-Path $schemaDirectory $name)
}
if (-not (Test-Json -LiteralPath artifacts/sbom/emby-client-win-x64.cdx.json -SchemaFile (Join-Path $schemaDirectory 'bom-1.6.schema.json'))) {
    throw 'CycloneDX schema validation failed.'
}
```

On 2026-09-09, the current restored graph produced 18 components. JSON readback and the official schema validation passed using the installed `Test-Json`; Python `jsonschema` and Node `ajv` were not installed and were not required. Schema validity establishes document format, not completeness of the runtime payload. If a future environment lacks a working schema validator, report schema validation as unperformed rather than equating JSON readback with validation.

`Publish-Aot.ps1` invokes this script after successful Native AOT publication and copies `emby-client-win-x64.cdx.json` into the output directory. The app project also copies `licenses/third-party/`. The packaging script preserves both as part of its complete publish-directory snapshot. Calling `dotnet publish` directly bypasses the SBOM generation step; use the repository publishing script for a distribution candidate.
