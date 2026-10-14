# Native ASS renderer sources and notices

The application supplies an independent libass 0.17.5 Windows x64 shared library for local ASS/SSA subtitle rendering. It is built from source with MSVC and Meson through vcpkg commit `e456309491fd875487bf02b8a0dca76c1b1b00cc`. This dependency graph is separate from the existing FFmpeg runtime and its retained supplier notices.

The authoritative versions, source archive SHA512 digests, dependency relationships, and license identifiers are recorded in `native/FFmpegInteropX/ass.dependencies.lock.json`. The official libass tag resolves to commit `4a05d8127f525943ebf45fdc6497c9e665947f0d`. Its source archive is available at <https://github.com/libass/libass/archive/0.17.5.tar.gz>. The independently published release source archive is also recorded in the lock; its bytes and digest differ from the tag archive used by the build.

| Component | Version | License | Corresponding source |
| --- | --- | --- | --- |
| libass | 0.17.5 | ISC | https://github.com/libass/libass/tree/0.17.5 |
| FreeType | 2.14.3 | FTL OR GPL-2.0-or-later | https://gitlab.freedesktop.org/freetype/freetype/-/tree/VER-2-14-3 |
| FriBidi | 1.0.17 | LGPL-2.1-or-later AND Unicode-3.0 | https://github.com/fribidi/fribidi/tree/v1.0.17 |
| HarfBuzz | 14.5.1 | MIT-Modern-Variant AND MIT | https://github.com/harfbuzz/harfbuzz/tree/14.5.1 |
| Brotli | 1.2.0 | MIT | https://github.com/google/brotli/tree/v1.2.0 |
| bzip2 | 1.0.8, vcpkg revision 6 | bzip2-1.0.6 | https://sourceware.org/pub/bzip2/bzip2-1.0.8.tar.gz |
| libpng | 1.6.59 | libpng-2.0 | https://github.com/pnggroup/libpng/tree/v1.6.59 |
| zlib | 1.3.2, vcpkg revision 2 | Zlib | https://github.com/madler/zlib/tree/v1.3.2 |

All notices under this directory are copied verbatim from the corresponding official source or release archive. Their source URLs and SHA256 digests are recorded in the adjacent third-party manifest. FreeType license texts were obtained from the project's GitHub mirror at the matching tag. FreeType is distributed under the FreeType License (FTL); both upstream alternative license texts and the additional BDF, PCF, and gzip source notices are retained. Its required attribution is: "Portions of this software are copyright © 2026 The FreeType Project (https://freetype.org). All rights reserved."

FriBidi is supplied as a separate dynamically linked DLL. Its corresponding source, Unicode data notices, and LGPL license text are included or linked above. Downstream patches for dependencies are available in the `ports` tree of the exact vcpkg commit at <https://github.com/microsoft/vcpkg/tree/e456309491fd875487bf02b8a0dca76c1b1b00cc/ports>. The local libass overlay and Windows triplet are retained under `native/FFmpegInteropX/AssNative`; the overlay fixes the Windows font provider and disables auxiliary programs without modifying libass source. These sources and build instructions allow the supplied library DLLs to be rebuilt and replaced with compatible versions.

Build the dependencies with `scripts/Prepare-AssDependencies.ps1`, or use the integrated `scripts/Build-DecoderNative.ps1`. The Windows triplet builds shared libraries with the static MSVC CRT. Binary caches are disabled; source archives and the vcpkg executable are checked against the lock. Build receipts record compiler identity, actual DLL hashes, owner libraries, and PE imports. The application publish and package paths retain every runtime DLL from the isolated target installation, including transitive FreeType dependencies.

The renderer uses the Windows DirectWrite/GDI system font provider and x64 assembly optimizations. libass produces `ass-9.dll` and the MSVC import library `ass.lib`. Optional Unicode automatic line breaking through `ASS_FEATURE_WRAP_UNICODE` is disabled because the pinned official graph does not include libunibreak. UTF-8 text, bidirectional text, shaping, embedded fonts, and standard ASS wrapping remain available through the locked dependencies.

The vcpkg MIT license is retained here as build provenance. Its Windows executable is pinned to the official `2026-09-26` release with SHA256 `2a4ae1467b9e797964c60c919d057b9e5dc1ec03f4b9632c1150026db25201f2`. Meson, NASM, CMake, Python, Ninja, and pkg-config acquisition metadata and helper scripts are fixed by the same vcpkg Git commit; these build tools are not application runtime payloads.
