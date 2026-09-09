# Packaging the Native AOT Client

The project can produce a structurally verified, **unsigned development MSIX** from an existing Native AOT publish directory. This is a review artifact. Package installation, activation under package identity, upgrade/uninstall behavior, Store acceptance, and clean-machine playback are separate gates and have not been validated by this packaging workflow.

The script does not build or republish the application, modify its project or source manifest, change certificate trust, sign anything, register an application, or install an MSIX. The original publish directory is read-only throughout packaging.

## Run the packaging script

Prerequisites:

- Windows x64 and PowerShell 7 or later.
- Windows SDK `makeappx.exe` and `makepri.exe` in a versioned `bin/<version>/x64` directory. The script selects the newest installed matching tools, or accepts `-SdkBinDirectory`.
- A previously built and verified Native AOT, Windows App SDK self-contained output directory. The default is `artifacts/aot`.
- No concurrent publishing into that input directory. The script detects changed source hashes and file sets, but it is not a substitute for publishing an immutable release snapshot.

From the repository root:

```powershell
.\scripts\Package.ps1
```

Optional inputs:

```powershell
.\scripts\Package.ps1 `
    -PublishDirectory artifacts/aot `
    -OutputDirectory artifacts/packages `
    -Version 0.1.0.1 `
    -SdkBinDirectory 'C:\Program Files (x86)\Windows Kits\10\bin\10.0.26100.0\x64'
```

Relative paths resolve against the repository root. `-ManifestPath` can select another reviewed source manifest. `-Version` changes only the staged MSIX manifest; it does not rebuild the executable or alter its embedded version. Package versions have four numeric components, each between 0 and 65535. Only x64 is currently implemented. `-IncludeSymbols` includes published PDB files; they are omitted by default and should normally be retained as separate diagnostic artifacts.

This script does not invoke `Publish-Aot.ps1`. Run the publish stage separately when a fresh executable is needed, and finish any application build or UI validation before packaging that output.

## Generated artifacts

Every run creates a unique UTC timestamp and random suffix, avoiding replacement of an earlier review candidate:

```text
artifacts/
  packages/
    EmbyClient.Windows_<version>_x64_unsigned_<run>.msix
    EmbyClient.Windows_<version>_x64_unsigned_<run>.msix.sha256
    EmbyClient.Windows_<version>_x64_unsigned_<run>.msix.review.json
  packaging/<run>/
    payload/
      AppxManifest.xml
      EmbyClient.App.exe
      EmbyClient.App.pri
      resources.pri
      ...original runtime libraries, assets, and localized resources...
    resource-input/EmbyClient.App.pri
    unpacked/
    priconfig.xml
    source-pri.xml
    package-pri.xml
    makepri-*.log
    makeappx-*.log
    package-review.json
```

All generated paths must remain below the repository's `artifacts` directory. The script rejects overlapping input/output paths and reparse points in the publish tree or generated path ancestors. It performs no recursive deletion. Staging, tool logs, resource dumps, and unpacked output remain available for review.

Only use a candidate for further release work when the script exits successfully and produces its checksum and review JSON. A failed run can leave partial staging or a package awaiting validation; that is not a completed candidate.

The review JSON includes package identity, SHA256, tool versions, source and payload inventories, resource counts, and explicit `Signed`, `InstallationVerified`, and `PackageRuntimeVerified` values. These three flags remain `false` in this workflow.

## Package identity and desktop activation

The source manifest is `src/EmbyClient.App/Package.appxmanifest`. The current identity is:

| Field | Current value |
| --- | --- |
| Name | `EmbyClient.Windows` |
| Publisher | `CN=EmbyClient.Development` |
| Version | `0.1.0.0`, unless overridden in staging |
| ProcessorArchitecture | `x64` |
| Application ID | `App` |
| Target device family | `Windows.Desktop` |
| Declared minimum OS | `10.0.19041.0` |
| Declared maximum tested OS | `10.0.26100.0` |

The publisher is a **development placeholder**, not a chosen production signing identity. The declared OS values are manifest settings, not evidence of successful installation or runtime testing across those versions.

The staged manifest resolves the template executable to `EmbyClient.App.exe`, removes the unresolved managed `EntryPoint` token, and uses the desktop application attributes appropriate for the declared minimum OS:

```xml
<Application Id="App"
             Executable="EmbyClient.App.exe"
             uap10:RuntimeBehavior="packagedClassicApp"
             uap10:TrustLevel="mediumIL">
```

The script declares the `uap10` namespace and preserves `runFullTrust`, native visual assets, and `Windows.Desktop`. It refuses external framework `PackageDependency` entries in this self-contained packaging path. The current application is a desktop full-trust process, not an AppContainer application. See Microsoft's [manual MSIX component guidance](https://learn.microsoft.com/en-us/windows/msix/desktop/desktop-to-uwp-manual-conversion) and [Application manifest schema](https://learn.microsoft.com/en-us/uwp/schemas/appxpackage/uapmanifestschema/element-application).

## Native dependencies and WinRT activation

The package copies every published file other than optional symbols, retaining the directory layout and all native runtime dependencies. It requires the application executable, its PRI, `Microsoft.ui.xaml.dll`, and `Microsoft.WindowsAppRuntime.dll` as basic completeness checks. It also checks for a native x64 PE shape and rejects a managed application DLL/runtimeconfig companion. These checks detect common wrong-input cases; they do not independently prove that an arbitrary executable was built using Native AOT.

The previously verified publish was produced with both `SelfContained=true` and `WindowsAppSDKSelfContained=true`. These are separate settings. A Native AOT executable alone does not include every native Windows App SDK DLL, localized resource, or optional OS capability. The package retains the complete publish output instead of selecting a small guessed DLL list. See [self-contained Windows App SDK deployment](https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps).

The SDK's self-contained targets merge WinRT/COM activation fragments into the executable's embedded application manifest and disable framework package references and redundant WinMD harvesting. The script preserves that executable byte-for-byte. It does not duplicate those activation registrations into the MSIX manifest. This follows the installed `Microsoft.WindowsAppSDK.Base` 2.0.4 `Microsoft.WindowsAppSDK.SelfContained.targets` implementation and the documented registration-free initialization behavior. Packaged activation remains an explicit later runtime test.

## Preserve the complete resource index

The published `EmbyClient.App.pri` contains the application's embedded XBF, qualified icon assets, and merged WinUI/WinUIEx resources. The manifest refers to logical names such as `Assets\Square150x150Logo.png`, while the published physical image is `Assets\Square150x150Logo.scale-200.png`.

Generating a new `resources.pri` containing only icons would be incorrect: the default resource manager prefers the package-level `resources.pri` and would not automatically merge the adjacent application PRI. See the [Windows App SDK 2.4.0 resource-manager implementation](https://github.com/microsoft/WindowsAppSDK/blob/v2.4.0/dev/MRTCore/mrt/Microsoft.Windows.ApplicationModel.Resources/src/Helper.cpp#L40-L108).

Instead, the script supplies the existing complete application PRI to MakePri's `PRI` indexer and creates one package-level `resources.pri` under the package identity. The original application PRI remains in the payload. The indexer preserves names, qualifiers, and values while merging the top-level resource map. The custom configuration does not split automatic language or scale resource packages. See [MakePri format-specific indexers](https://learn.microsoft.com/en-us/windows/uwp/app-resources/makepri-exe-format-specific-indexers) and [MakePri configuration](https://learn.microsoft.com/en-us/windows/uwp/app-resources/makepri-exe-configuration).

The script dumps both indexes and checks that every resource key survives, that the package map matches its identity, and that `Files/App.xbf` exists. It does not resize or replace the application's artwork.

## What validation establishes

The script performs these checks:

1. Validate the expected x64 native publish shape and mandatory payload files.
2. Copy input files to isolated staging and compare their SHA256 values.
3. Generate the desktop manifest and complete package PRI, then compare resource key sets.
4. Run `makeappx pack` with default semantic validation enabled. It never uses `/nv` or `/nfv`.
5. Run `makeappx unpack` into a fresh review directory and compare the SHA256 of every staged payload file.
6. Recheck the original publish file set, source hashes, and source-manifest hash for concurrent changes.
7. Confirm required package metadata exists and `AppxSignature.p7x` does not exist.

Microsoft explicitly describes MakeAppx semantic validation as limited; a successful package is not guaranteed to install. See [MakeAppx packaging and validation](https://learn.microsoft.com/en-us/windows/msix/package/create-app-package-with-makeappx-tool). No registration, installation, launch, codec check, GPU check, update, uninstall, signature verification, or Store submission occurs here.

## Initial local evidence

On 2026-09-09, the script successfully packaged the existing locally verified AOT output without rebuilding the application. The initial candidate contained 298 copied source files plus the staged manifest and package PRI, and retained all 256 indexed resource keys. MakeAppx and MakePri were `10.0.26100.8249` from the installed `10.0.26100.0/x64` SDK tools directory.

The first candidate was approximately 51.1 MiB, excluding PDB files. Its detailed evidence is in its generated `package-review.json`; later runs have their own inventories and checksums. The original AOT executable and native libraries passed a byte-for-byte unpack round trip. Read-only PE import inspection also found the app's normal Windows/UCRT imports and WinUI's bundled native dependencies; this was not a clean-machine dependency or runtime test.

The later `20260909-004422857-0cf49f41` candidate packages executable SHA-256 `803C17E0937E2196A127A72286C1962F563A0C0A323B3D4566DDA9A667FA4EDA`, including the complete notices directory and SBOM. Structural verification passed with 330 source files, 332 payload files, and all 287 resource keys preserved. The 53,972,634-byte unsigned package has SHA-256 `A9AF2021278DA61DF257AB767964E0C69342335FC2EE299460BDCF8C4EC2FB39`. It remains a development checkpoint with no signing, installation, or packaged-runtime claim; later source changes require a new publish and package.

The `20260909-023602459-581a8eef` candidate packages executable SHA-256 `313A94C3C7BE6821B489E49A2A7AC705617D47CC53EFEA3984641C262305D659`, including the new queue/diagnostics UI and default-track recovery fix. Its 54,159,589-byte MSIX has SHA-256 `08BE0EFCA64E20B82A4A7423737449B1BFD88C6F785088E204E5251189D7A6BC`. Structural validation preserves 289 resource keys, including the two new dialogs. Source hashes remained unchanged during packaging. Signing, installation, and packaged runtime remain unverified.

## Hosted structural evidence

Read-only inspection on 2026-09-09 confirmed that both hosted runs below completed locked restore, the Release build, all **306 tests** (API 47, media transport 65, platform 68, playback 126), Native AOT/SBOM publication, unsigned MSIX structural verification, and all artifact uploads. The build reported zero errors and one upstream generated WinUIEx `Icon` warning (`CS0618`).

| Hosted run and source | MSIX filename | MSIX SHA-256 reported by `Package.ps1` |
| --- | --- | --- |
| [34300925860](https://github.com/moooyo/emby-client-winui3/actions/runs/34300925860), `023b97dce33633e14aafd63b16b750a21c2e73f1` | `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-015546017-7b5542e9.msix` | `71D6D92B89E1CFC84CF2356809AE5DC09113C3E5F605D5D44A517CA1ED6E4C26` |
| [34301423262](https://github.com/moooyo/emby-client-winui3/actions/runs/34301423262), `a2e4e4338e1b1086defe8a97c096ffee87a917f9` | `EmbyClient.Windows_0.1.0.0_x64_unsigned_20260909-020245296-941750f5.msix` | `996D6E72C283B43F15D9485D1E23CAB68FAB1CABDE89FE61FE1AE8F591E0A278` |

The corresponding uploaded MSIX archives include the package, checksum, and review JSON. The earlier [artifact 10084962519](https://github.com/moooyo/emby-client-winui3/actions/runs/34300925860/artifacts/10084962519) is 54,161,198 bytes with archive digest `sha256:1df4a462932a54ec1d2336b79d3652643a53dc12d6f8660fe8ac4281cf6ebac9`. The latest [artifact 10085120746](https://github.com/moooyo/emby-client-winui3/actions/runs/34301423262/artifacts/10085120746), named `emby-client-windows-x64-msix-unsigned-a2e4e4338e1b1086defe8a97c096ffee87a917f9`, is 54,167,100 bytes with archive digest `sha256:b859fec3d237a5b355c9ae13dcbb10bc3f2ec338982010b4cc843132a66d1f3e`. Archive size/digest and contained MSIX size/digest are different identities; no contained package length is inferred from the archive metadata.

The audit used `gh run view` job/step conclusions and logs plus the Actions artifacts API. It did not download, locally rehash, sign, or install either package. Both packaging logs explicitly report structural verification success while leaving installation and packaged runtime behavior unverified. [CI evidence](ci.md#hosted-packaging-receipts) records the AOT and log archive metadata as well.

These exact heads precede the new queue editor, diagnostics/Retry UI, and subsequent network fixes. Their success establishes the hosted packaging pipeline for those revisions, not acceptance of later source changes or an install-tested release. A fresh candidate and its own receipt remain necessary for later revisions.

## Future single-project MSIX path

The application already has `EnableMsixTooling=true`, and Windows App SDK supports single-project MSIX. The documented automated build switch is `GenerateAppxPackageOnBuild=true`; a packaged configuration also needs the intended `WindowsPackageType` and signing settings. That route recompiles or republishes the application and should receive its own build, AOT, packaging, and runtime validation. It was deliberately not executed during this stage while the main development task was testing native playback. See [single-project MSIX automation](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/single-project-msix#automate-building-and-packaging-your-single-project-msix-app).

The current script is the direct, inspectable path for packaging an already verified immutable output. It produces an `.msix`, not a Store `.msixupload` or an architecture bundle.

## Formal signing and distribution gate

The next release action begins with review of the exact unsigned package, checksum, staged identity, and inventory. Before signing or publishing, the project owner must approve the production package name/publisher, certificate or managed signing service, distribution channel, and the concrete candidate. `CN=EmbyClient.Development` must not silently become a production identity.

After that explicit approval, a separate signing step can copy the reviewed unsigned candidate to a signed-output path, use Windows SDK SignTool or an approved signing service, verify the signature and publisher match, and generate a new signed-package checksum. Keep passwords and private keys out of commands, logs, source control, and review JSON. This script intentionally provides no signing, certificate-installation, trust-modification, or application-installation switch. See [MSIX signing overview](https://learn.microsoft.com/en-us/windows/msix/package/signing-package-overview).

Only after signing and deployment authorization should an isolated supported Windows environment test installation, package activation, library login, actual playback/session reporting, settings behavior, upgrade, and uninstall. Until that evidence exists, describe this output as an **unsigned package with structural verification**, not an install-tested or release-ready application.
