#!/usr/bin/env bash
set -euo pipefail
# chromaprint — audio fingerprinting library (LGPL-2.1). Enables FFmpeg's chromaprint
# muxer. Consumes the kissfft static lib built just before (FFT_LIB=kissfft — the
# avfft/avtx backends would need FFmpeg's own libs, which is circular). C++ — the C++
# runtime is added to EXTRA_LIBS for FFmpeg's static link. FFmpeg finds it via a plain
# -lchromaprint (no pkg-config needed).
# SOURCED by scripts/build.sh (shares env; appends --enable-* to CONFIGURE_FLAGS).
# Not a standalone script.

[[ "${BUILD_CHROMAPRINT}" == "1" ]] || { echo "Skipping chromaprint (not needed for ${RID})."; return 0; }

# FFT_LIB=kissfft: chromaprint compiles kissfft's kiss_fft.c + kiss_fftr.c from source. Point
# its FindKissFFT.cmake straight at the kissfft source fetched by kissfft.sh via
# -DKISSFFT_SOURCE_DIR (pre-setting the cache var makes its find_path a no-op — so it works
# identically on native and cross toolchains, avoiding find-root-path/install-layout issues).
# The avfft/avtx FFT backends would need FFmpeg's own libs (circular), so kissfft it is. TOOLS/TESTS off.
build_cmake_dep chromaprint \
  -DBUILD_TOOLS=OFF -DBUILD_TESTS=OFF \
  -DFFT_LIB=kissfft -DKISSFFT_SOURCE_DIR="${WORK_DIR}/kissfft"

# chromaprint is C++; pkg-config isn't used (FFmpeg links a plain -lchromaprint), so its
# C++ runtime must be added for FFmpeg's static --enable-chromaprint link (libstdc++ on
# GNU/mingw, libc++ on Apple/NDK).
case "${PLATFORM:-linux}" in
  apple|android) EXTRA_LIBS="${EXTRA_LIBS:-} -lc++" ;;
  *)             EXTRA_LIBS="${EXTRA_LIBS:-} ${CXX_RT_LIB--lstdc++}" ;;
esac

# On Windows/mingw, chromaprint.h decorates its API with __declspec(dllimport) unless
# CHROMAPRINT_NODLL is defined — but we build a STATIC libchromaprint.a, whose symbols are
# undecorated. Without this define, FFmpeg's configure probe (and the chromaprint-muxer
# compile) look for __imp_chromaprint_* and fail with "chromaprint not found". The macro is
# guarded by _WIN32/_WIN64 in the header, so this define is an inert no-op on other platforms.
case " ${EXTRA_CFLAGS:-} " in
  *" -DCHROMAPRINT_NODLL "*) ;;
  *) EXTRA_CFLAGS="${EXTRA_CFLAGS:-} -DCHROMAPRINT_NODLL" ;;
esac

CONFIGURE_FLAGS+=(--enable-chromaprint)
echo "chromaprint (audio fingerprinting) enabled."
