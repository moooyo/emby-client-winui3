# FFmpegInteropX Desktop FFmpeg provenance

Recorded on 2026-10-01 by inspecting restored packages and upstream source. This record is not playback, build, packaging, or license-compliance acceptance evidence.

This directory records the original restored NuGet payload. The decoder API extension now replaces its runtime DLLs with the [locked selected FFmpeg family](../Devenvy-FFmpeg-8.1.3.0/README.md), whose source, build configuration, and external-library graph differ. Keep this historical package/source record; it does not describe the replacement binaries.

## Package and source identity

The product references `FFmpegInteropX` 2.1.0.81200, which resolves `FFmpegInteropX.Desktop.Lib` 2.1.0 and `FFmpegInteropX.Desktop.FFmpeg` 8.1.2. The two wrapper packages declare `Apache-2.0`. Their repository metadata identifies [FFmpegInteropX commit 841fc8e79346a235906f1dbd45be53034b2e2f52](https://github.com/ffmpeginteropx/FFmpegInteropX/tree/841fc8e79346a235906f1dbd45be53034b2e2f52); the original Apache license is retained in each wrapper's versioned notice directory.

The native package declares `LGPL-2.1-or-later AND Zlib AND MIT`, rather than the wrapper's Apache expression. Its repository metadata identifies [FFmpeg commit 38b88335f99e76ed89ff3c93f877fdefce736c13](https://github.com/FFmpeg/FFmpeg/tree/38b88335f99e76ed89ff3c93f877fdefce736c13). The complete eight files under `licenses/` were copied byte-for-byte from the [exact native NuGet archive](https://api.nuget.org/v3-flatcontainer/ffmpeginteropx.desktop.ffmpeg/8.1.2/ffmpeginteropx.desktop.ffmpeg.8.1.2.nupkg). Their original names and contents are preserved, including licensing material for components not called directly by the app.

The x64 `avcodec-62.dll` contains a configuration string with `--enable-shared`, `--enable-d3d11va`, `--disable-dxva2`, `--disable-gpl`, `--disable-version3`, `--disable-encoders`, `--disable-programs`, and `--disable-devices`. It enables zlib, bzlib, lzma, libxml2, iconv, libdav1d, and OpenSSL. This was a static byte inspection, without loading or executing the DLL. It agrees with the [pinned build configuration](https://github.com/ffmpeginteropx/FFmpegInteropX/blob/841fc8e79346a235906f1dbd45be53034b2e2f52/Build/FFmpegConfig.sh) and [build script](https://github.com/ffmpeginteropx/FFmpegInteropX/blob/841fc8e79346a235906f1dbd45be53034b2e2f52/Build-FFmpeg.ps1). The parent repository's gitlinks record these enabled library sources:

| Component | Source revision |
| --- | --- |
| zlib | [8b861857759df1d1eafff995682b95e57657ed05](https://github.com/ffmpeginteropx/zlib/tree/8b861857759df1d1eafff995682b95e57657ed05) |
| bzip2 | [8d1ad89f6cc129685bb9ea43e38cc25c97b9d2b4](https://github.com/ffmpeginteropx/bzip2/tree/8d1ad89f6cc129685bb9ea43e38cc25c97b9d2b4) |
| libiconv | [d601c3e8e69db72980edd4794e90177f0a74f86d](https://github.com/ffmpeginteropx/libiconv/tree/d601c3e8e69db72980edd4794e90177f0a74f86d) |
| liblzma | [76a4518a3b74da3a9e424d4d925c1738a7eefe46](https://github.com/ffmpeginteropx/liblzma/tree/76a4518a3b74da3a9e424d4d925c1738a7eefe46) |
| libxml2 | [b885bd9c38318ccc969a79416a71cc8430d8bf19](https://github.com/ffmpeginteropx/libxml2/tree/b885bd9c38318ccc969a79416a71cc8430d8bf19) |
| dav1d | [99172b11470776177939c3d2bc366fe8d904eab7](https://code.videolan.org/videolan/dav1d/-/tree/99172b11470776177939c3d2bc366fe8d904eab7) |
| OpenSSL | [ad4910fad22d57c6d685b0ca83bdb7b2bf69d8fd](https://github.com/openssl/openssl/tree/ad4910fad22d57c6d685b0ca83bdb7b2bf69d8fd) |

These gitlinks are source provenance from the wrapper's pinned repository, not a separately reproduced build of the native archive. Keep the actual release DLLs, archive hashes, source archives, build scripts, and any downstream changes together when producing corresponding-source material.

## Supplementary source notices

The package's `iconv.txt` is the GPLv3 text. The pinned libiconv [README](source-notices/libiconv-README) explicitly distinguishes the LGPL library/header files from the GPL command-line program and documentation. The missing library license is retained as [libiconv-COPYING.LIB](source-notices/libiconv-COPYING.LIB), the GNU Library General Public License version 2 text; preserve it together with the package's original GPL text. The DLL configuration disables programs. A GPL text in the notice bundle does not establish that the product includes the iconv command-line program.

The package's `liblzma.txt` is an LGPLv2.1 text. The pinned [liblzma-COPYING](source-notices/liblzma-COPYING) explains its public-domain library code and separately licensed build/tools files. Both records are retained; this README does not relicense either the package or its sources.

[FFmpeg-LICENSE.md](source-notices/FFmpeg-LICENSE.md) preserves FFmpeg's component-license explanation. Its IJG-derived files require executable-distribution attribution: **This software is based in part on the work of the Independent JPEG Group.** The repository uses the selected package without patching its FFmpeg DLLs. Source details remain in the pinned [jfdctfst.c](https://github.com/FFmpeg/FFmpeg/blob/38b88335f99e76ed89ff3c93f877fdefce736c13/libavcodec/jfdctfst.c), [jfdctint_template.c](https://github.com/FFmpeg/FFmpeg/blob/38b88335f99e76ed89ff3c93f877fdefce736c13/libavcodec/jfdctint_template.c), and [jrevdct.c](https://github.com/FFmpeg/FFmpeg/blob/38b88335f99e76ed89ff3c93f877fdefce736c13/libavcodec/jrevdct.c). Record any future changes to those sources with the distributed documentation as directed by FFmpeg's license explanation.

## Distribution materials

The notice tree is attribution material, not complete corresponding source. The selected LGPLv2.1 distribution route must address sections 4 and 6 of [the retained license](licenses/ffmpeg.txt): recipients need the appropriate corresponding library source, including modifications, interface definitions, and compilation/installation scripts. When DLLs are offered for download, section 4 permits equivalent source access from the same place. A bare upstream repository link is not a maintained corresponding-source archive.

For the combined application, retain the library-use notice and license and permit modification for the customer's own use and reverse engineering to debug such modifications. Section 6(b)'s shared-library route requires an interface-compatible modified library to work when installed by the user. NativeAOT publication of the managed app does not itself satisfy or invalidate that route: the shipped FFmpeg libraries must remain separately dynamically linked and practically replaceable. If the release cannot meet section 6(b), use another applicable section 6 route, such as section 6(a)'s source/object materials allowing relinking; a section 6(c) written offer has a minimum three-year term and the specified cost limit. The selected route, download location, source bundle, and DLL-replacement instructions still require release-specific review. See [FFmpeg's official distribution guidance](https://ffmpeg.org/legal.html).

Preserve all notices for the enabled external libraries, including the OpenSSL Apache text and the libiconv LGPL text. Audit static library combinations and any changes against their own terms; the native package's NuGet expression is not a substitute for the full component notices. This is an engineering record of upstream terms and release requirements, not legal advice or a declaration that a release is compliant.
