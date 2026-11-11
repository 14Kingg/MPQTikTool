# Build dynamic DLLs with the static MSVC runtime so the portable ZIP does not
# require users to install the Visual C++ Redistributable separately.
set(VCPKG_TARGET_ARCHITECTURE x64)
set(VCPKG_CRT_LINKAGE static)
set(VCPKG_LIBRARY_LINKAGE dynamic)
set(VCPKG_PLATFORM_TOOLSET v143)
