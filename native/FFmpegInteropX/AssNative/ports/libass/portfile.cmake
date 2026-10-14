# Based on the official libass port at vcpkg e456309491fd875487bf02b8a0dca76c1b1b00cc.
# The source archive and its checksum are unchanged. Only x64 Windows is supported.
vcpkg_from_github(
    OUT_SOURCE_PATH SOURCE_PATH
    REPO libass/libass
    REF ${VERSION}
    SHA512 8dfea8ef3043ace28efa8a6ae4d6a443fd19d49ad19a521a642e7ec3a7c6f97284cda16d479e168ce30bd560f7e05a0675c9f3fde872bb92ed650343499a57b1
)

vcpkg_find_acquire_program(PKGCONFIG)
get_filename_component(PKGCONFIG_EXE_PATH "${PKGCONFIG}" DIRECTORY)
vcpkg_add_to_path("${PKGCONFIG_EXE_PATH}")
vcpkg_find_acquire_program(NASM)
get_filename_component(NASM_EXE_PATH "${NASM}" DIRECTORY)
vcpkg_add_to_path("${NASM_EXE_PATH}")

vcpkg_configure_meson(
    SOURCE_PATH "${SOURCE_PATH}"
    OPTIONS
        -Ddirectwrite=enabled
        -Dfontconfig=disabled
        -Dcoretext=disabled
        -Dlibunibreak=disabled
        -Drequire-system-font-provider=true
        -Dasm=enabled
        -Dcheckasm=disabled
        -Dcompare=disabled
        -Dfuzz=disabled
        -Dprofile=disabled
        -Dtest=disabled
)

vcpkg_install_meson()
vcpkg_copy_pdbs()
vcpkg_fixup_pkgconfig()
vcpkg_install_copyright(FILE_LIST "${SOURCE_PATH}/COPYING")
