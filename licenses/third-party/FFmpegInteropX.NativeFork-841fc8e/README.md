# FFmpegInteropX native fork provenance

Recorded on 2026-10-01. The custom native `FFmpegInteropX.dll` starts from [upstream commit 841fc8e79346a235906f1dbd45be53034b2e2f52](https://github.com/ffmpeginteropx/FFmpegInteropX/tree/841fc8e79346a235906f1dbd45be53034b2e2f52). [LICENSE](LICENSE) preserves that upstream Apache-2.0 text. Existing NuGet wrapper notice directories are retained because those packages continue to provide the managed projection and dependency graph.

The tracked downstream source under `native/FFmpegInteropX/Source` adds per-source decoder API selection, vendor sample/device providers, accepted-sample observations, and three C ABI exports. It preserves the original IDL and managed projection ABI. The repository also supplies the Windows x64 build project, dependency lock, preparation/build scripts, and runtime override target. Native source files identify modified upstream files where applicable; the build receipt records the downstream source fingerprint and output hashes.

This is a modified wrapper component, distinct from the upstream NuGet native binary. Release materials must retain the original license/copyright text and document the downstream changes, with the tracked source and build scripts available alongside the wrapper's provenance. A build receipt must identify the actual repository revision and output DLL hash; this directory's upstream base revision alone does not identify every downstream change.

The separately linked FFmpeg DLLs and incorporated external libraries have their own terms. See [the selected FFmpeg runtime record](../Devenvy-FFmpeg-8.1.3.0/README.md); Apache-2.0 does not replace their LGPL or component notices.
