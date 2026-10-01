# Selected FFmpeg runtime provenance

Recorded on 2026-10-01. The application selects the `devenvy/ffmpeg` Windows x64 LGPL shared build from release [8.1.3.0](https://github.com/devenvy/ffmpeg/releases/tag/8.1.3.0). This source/notice record is not hardware acceptance or a completed release-compliance declaration.

## Locked binary and build identity

| Input | Identity |
| --- | --- |
| Runtime archive | [ffmpeg-8.1.3-win-x64-lgplv2.tar.gz](https://github.com/devenvy/ffmpeg/releases/download/8.1.3.0/ffmpeg-8.1.3-win-x64-lgplv2.tar.gz) |
| Runtime SHA-256 | `f437415870b78d6dad38b92de5fc3c62f3a5186a6e86ad1ebc0f2b97d45094e2` |
| Matching development archive | [ffmpeg-8.1.3-win-x64-lgplv2-dev.tar.gz](https://github.com/devenvy/ffmpeg/releases/download/8.1.3.0/ffmpeg-8.1.3-win-x64-lgplv2-dev.tar.gz) |
| Development SHA-256 | `c7c5a50d2afbe64bd4b06000403c0b0e929c35fdac0a1c3aa623d03bc82d7beb` |
| Runtime FFmpeg version | `8.1.3` |
| Build repository revision | [fcd83b13adbbbdf616f3a48a565f1913dbdfa403](https://github.com/devenvy/ffmpeg/tree/fcd83b13adbbbdf616f3a48a565f1913dbdfa403) |
| FFmpeg source release | [ffmpeg-8.1.3.tar.xz](https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz) |

The [native dependency lock](../../../native/FFmpegInteropX/dependencies.lock.json) fixes both archive hashes. Headers/import libraries come from the matching development archive. Product output uses one complete family: `avcodec-62.dll`, `avdevice-62.dll`, `avfilter-11.dll`, `avformat-62.dll`, `avutil-60.dll`, `swresample-6.dll`, and `swscale-9.dll`. The custom wrapper replaces the NuGet native wrapper; the original NuGet package still supplies the unchanged managed projection. Do not mix FFmpeg families.

The native build receipt records source fingerprint, toolchain, archive hashes, and DLL hashes. Preserve that release-specific receipt alongside the source materials. [build-configuration.txt](build-configuration.txt) is a static transcription of the selected DLL's configure string. It disables GPL, nonfree, and autodetected dependencies, does not enable `version3`, and includes the real QSV/AMF/CUVID decoders. Configuration and decoder presence do not prove successful hardware frames on every device.

## Original archive notices

The complete original `legal/` tree is retained byte-for-byte, including [LICENSE-NOTICE.txt](legal/LICENSE-NOTICE.txt), [SOURCE_OFFER.txt](legal/SOURCE_OFFER.txt), [COPYING.LGPLv2.1](legal/COPYING.LGPLv2.1), [COPYING.GPLv2](legal/COPYING.GPLv2), [LICENSE.md](legal/LICENSE.md), credits, and every supplied component notice under [legal/licenses/](legal/licenses/). Their original names, copyright statements, and metadata remain intact. Package-supplied wording is preserved as provenance; it is not adopted as a general App Store or distribution-compliance guarantee.

The selected FFmpeg build follows LGPLv2.1-or-later, with separate external-component terms. Shared FFmpeg DLLs incorporate several external libraries statically. [deps.json](build-source/deps.json), copied from the exact build revision, records component origins, versions, and conditional overrides for all build targets; it is a recipe inventory, not a claim that every entry is included in this Windows LGPL cell. The original build-script [LICENSE](build-source/LICENSE) is MIT and does not replace component licenses.

FFmpeg's IJG-derived files require executable-distribution attribution: **This software is based in part on the work of the Independent JPEG Group.** This repository does not patch the selected FFmpeg DLLs; downstream changes to the IJG-derived sources must be identified with release documentation as their terms require.

## Supplemental notices and selected license routes

The supplier's notice collection uses shallow filename discovery. These additional exact-version texts preserve notices referenced by that collection or embedded in installed headers:

- KISS FFT `131.2.0`: its supplied `COPYING` points to [LICENSES/BSD-3-Clause](source-notices/kissfft/LICENSES/BSD-3-Clause), retained here in full. The pinned [Chromaprint recipe](build-source/chromaprint.sh) selects `FFT_LIB=kissfft`, avoiding the GPL FFTW combination found in a rejected development candidate.
- Highway `1.4.0`: the complete supplied [LICENSE](legal/licenses/highway/LICENSE) offers Apache-2.0 or BSD-3-Clause. The pinned [Highway recipe](build-source/highway.sh) selects BSD-3-Clause. Its supplied Debian-format `copyright` file describes Apache-2.0 only; preserve that metadata with the complete dual-license text and this explicit selection.
- FreeType `VER-2-14-3`: the pinned [recipe](build-source/freetype.sh) uses the FreeType License route. The [FTL](source-notices/freetype/docs/FTL.TXT), alternative GPL text, BDF/PCF notices, and gzip header notice are retained under `source-notices/freetype/`. **This software uses the FreeType font engine, a product of the FreeType Project (https://freetype.org).** Its original copyrights and terms remain in the retained texts.
- NVIDIA codec headers `n13.1.15.0`: the original five headers under [source-notices/nv-codec-headers/](source-notices/nv-codec-headers/) retain their embedded MIT notices. Their terms concern the headers, not redistribution of installed NVIDIA driver DLLs.
- GNU compiler runtime: the selected binary reports `gcc 13-win32 (GCC)` and configures static libgcc/libstdc++ linkage. [COPYING3](source-notices/gcc/COPYING3) and [COPYING.RUNTIME](source-notices/gcc/COPYING.RUNTIME) retain the original GPLv3 text and GCC Runtime Library Exception 3.1 from the official GCC 13.4.0 source tag. These are supplemental runtime terms, not a claim that 13.4.0 is the exact supplier compiler patch or that all compiler object sources are inventoried. The runtime exception addresses eligible GCC compilation and linkage; it does not change FFmpeg's LGPL or external component terms. Preserve the supplier's actual compiler/package provenance with release source materials.

The archive retains VPL's MIT license and the complete AMD AMF MIT/IP notice. The [VPL recipe](build-source/libvpl.sh) selects a static dispatcher. Vendor driver libraries remain system dependencies and are not copied from a developer's installation into the product.

## Corresponding source and combined application

The supplier's `SOURCE_OFFER.txt` identifies the fixed build repository, its pinned dependency origins, and the FFmpeg source release. It is a source-access statement, not a three-year written offer under LGPLv2.1 section 6(c). Preserve it, but ensure the actual release offers the applicable corresponding library sources, changes, interface definitions, and build/install scripts with reliable equivalent access. A notice tree or set of upstream links alone does not establish that a release's complete source obligations are fulfilled.

For the combined application, retain library-use notice, LGPL text, and terms permitting library modification and reverse engineering for debugging such modifications. The intended LGPLv2.1 section 6(b) shared-library route requires an interface-compatible modified library installed by the user to work in practice. Keep FFmpeg DLLs separately linked and practically replaceable. If the release cannot satisfy that route, provide an applicable alternative, such as section 6(a)'s source/object materials allowing relinking. NativeAOT publication does not remove these requirements.

Record the exact release's source download location, selected route, replacement/relinking instructions, and relevant output hashes. The [Apache wrapper fork](../FFmpegInteropX.NativeFork-841fc8e/README.md) has its own tracked source/build provenance. See [FFmpeg's official distribution guidance](https://ffmpeg.org/legal.html) and original component terms before distribution.
