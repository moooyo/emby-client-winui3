#!/usr/bin/env bash
set -euo pipefail
# highway — portable SIMD library (dual Apache-2.0 / BSD-3-Clause; we rely on the
# BSD-3-Clause option so nothing Apache-2.0 lands in the v2/LGPLv2.1 lane). Build
# dependency of libjxl; not consumed by FFmpeg directly. C++, but not FFmpeg-facing
# — the C++ runtime for the libjxl→highway chain is added by libjxl.sh. SOURCED by
# scripts/build.sh. Not a standalone script.

[[ "${BUILD_HIGHWAY}" == "1" ]] || { echo "Skipping highway (not needed for ${RID})."; return 0; }

build_cmake_dep highway \
  -DHWY_ENABLE_TESTS=OFF -DHWY_ENABLE_EXAMPLES=OFF -DHWY_ENABLE_CONTRIB=OFF

echo "highway (libjxl dependency) built."
