#!/usr/bin/env bash
set -euo pipefail
# libvpl (oneVPL) — Intel Video Processing Library dispatcher (MIT) for Quick
# Sync Video (QSV) hardware transcode.
# SOURCED by scripts/build.sh (shares its environment; appends its --enable-*
# to CONFIGURE_FLAGS where applicable). Not a standalone script.

if [[ "${BUILD_VPL_SOURCE}" == "1" ]]; then
  echo "Building libvpl (static)..."
  cd "${WORK_DIR}" || exit 1
  rm -rf libvpl

  clone_dep libvpl "${WORK_DIR}/libvpl"
  cd libvpl || exit 1
  cmake -G Ninja -B build \
    ${CMAKE_CROSS_ARGS[@]+"${CMAKE_CROSS_ARGS[@]}"} \
    -DCMAKE_INSTALL_PREFIX="${DEPS_DIR}" \
    -DCMAKE_INSTALL_LIBDIR=lib \
    -DBUILD_DISPATCHER=ON \
    -DBUILD_DEV=ON \
    -DBUILD_PREVIEW=OFF \
    -DBUILD_TOOLS=OFF \
    -DBUILD_TOOLS_ONEVPL_EXPERIMENTAL=OFF \
    -DINSTALL_EXAMPLE_CODE=OFF \
    -DBUILD_SHARED_LIBS=OFF \
    -DBUILD_TESTS=OFF \
    -DCMAKE_C_FLAGS="-static-libgcc -O2" \
    -DCMAKE_CXX_FLAGS="-static-libgcc -static-libstdc++ -O2"
  cmake --build build -j"$(${NPROC})"
  cmake --install build

  # Windows needs the COM/GDI system libs; Linux's dispatcher just needs libdl
  # (it dlopens the QSV runtime) and libstdc++ (it is C++).
  case "${RID}" in
    win-*) VPL_PRIVATE_LIBS="-lole32 -lgdi32 -luuid -lstdc++" ;;
    *)     VPL_PRIVATE_LIBS="-lstdc++ -ldl" ;;
  esac
  cat > "${DEPS_DIR}/lib/pkgconfig/vpl.pc" <<PKGCONFIG
prefix=${DEPS_DIR}
libdir=\${prefix}/lib
includedir=\${prefix}/include

Name: libvpl
Description: Intel Video Processing Library (oneVPL)
Version: 2.16
Libs: -L\${libdir} -lvpl ${VPL_PRIVATE_LIBS}
Cflags: -I\${includedir} -I\${includedir}/vpl
PKGCONFIG
else
  echo "Skipping libvpl from source (not needed for ${RID})."
fi
