#!/usr/bin/env bash
set -euo pipefail
# FreeType — font rasterization (FTL / BSD-like) for the drawtext filter.
# SOURCED by scripts/build.sh (shares its environment; appends its --enable-*
# to CONFIGURE_FLAGS where applicable). Not a standalone script.

if [[ "${BUILD_FREETYPE}" == "1" ]]; then
  build_cmake_dep freetype \
    -DFT_DISABLE_HARFBUZZ=ON -DFT_DISABLE_BZIP2=ON -DFT_DISABLE_BROTLI=ON
  CONFIGURE_FLAGS+=(--enable-libfreetype)
  echo "freetype (text rendering) enabled."
else
  echo "Skipping freetype."
fi
