# Native Linux build of the Thetis DSP/radio-interface libraries.
#
#   libwdsp.so           - DSP engine (Warren Pratt, NR0V)
#   libChannelMaster.so  - radio protocol 1/2 networking, audio routing, VAC
#   libPA19.so           - Thetis' PortAudio fork + the PA_* wrappers used by
#                          the C# console (the Windows PA19.dll)
#
# rnnoise (NR3) and libspecbleach (NR4) are built from the sources bundled in
# "Project Files/lib/NR_Algorithms_x64/src" and linked in statically.
#
# See Linux/README.md for usage.

cmake_minimum_required(VERSION 3.16)
project(ThetisLinux C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_C_VISIBILITY_PRESET hidden)

if(NOT CMAKE_BUILD_TYPE)
  set(CMAKE_BUILD_TYPE Release CACHE STRING "" FORCE)
endif()

option(THETIS_NATIVE_ARCH "Optimise for the build machine's CPU (-march=native)" OFF)

get_filename_component(SRC_DIR "${CMAKE_CURRENT_SOURCE_DIR}/.." ABSOLUTE)
get_filename_component(LIB_DIR "${SRC_DIR}/../lib" ABSOLUTE)
set(NR_SRC "${LIB_DIR}/NR_Algorithms_x64/src")
set(COMPAT_DIR "${CMAKE_CURRENT_SOURCE_DIR}/compat")

find_package(Threads REQUIRED)
find_package(PkgConfig REQUIRED)
pkg_check_modules(FFTW3 REQUIRED IMPORTED_TARGET fftw3)
pkg_check_modules(FFTW3F REQUIRED IMPORTED_TARGET fftw3f)

# The DSP code was written against MSVC and relies on its permissiveness
# (implicit declarations, mismatched pointer types); keep those non-fatal.
set(LEGACY_C_FLAGS
  -Wno-implicit-function-declaration
  -Wno-incompatible-pointer-types
  -Wno-int-conversion
  -Wno-unused-result
  -Wno-pointer-to-int-cast
  -Wno-int-to-pointer-cast
  -Wno-attributes
  -fno-strict-aliasing
  -fcommon            # headers contain tentative definitions (MSVC / pre-GCC10 semantics)
)
if(THETIS_NATIVE_ARCH)
  add_compile_options(-march=native)
endif()

# --------------------------------------------------------------------------
# PortAudio: Thetis ships a modified PortAudio (renumbered sample formats,
# paFloat64 support) whose ABI the C# code depends on, so the system
# libportaudio cannot be used.  Build the bundled one statically and wrap it
# into libPA19.so exactly as PA19.dll is on Windows.
# --------------------------------------------------------------------------
set(PA_BUILD_SHARED_LIBS OFF CACHE BOOL "" FORCE)
set(PA_BUILD_TESTS OFF CACHE BOOL "" FORCE)
set(PA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(PA_USE_JACK ON CACHE BOOL "")
set(PA_USE_ALSA ON CACHE BOOL "")
set(PA_USE_PULSEAUDIO ON CACHE BOOL "")
add_subdirectory("${LIB_DIR}/portaudio-19.7.0" portaudio EXCLUDE_FROM_ALL)
set_target_properties(portaudio PROPERTIES POSITION_INDEPENDENT_CODE ON C_VISIBILITY_PRESET default)

add_library(PA19 SHARED "${LIB_DIR}/portaudio-19.7.0/build/msvc/PA19.cpp")
# -fpermissive: MSVC-style implicit function-pointer <-> void* conversions
target_compile_options(PA19 PRIVATE -fpermissive -include "${COMPAT_DIR}/include/win32compat.h")
target_include_directories(PA19 PRIVATE "${COMPAT_DIR}/include")
# export the whole PortAudio API from libPA19.so (ChannelMaster calls Pa_* directly)
target_link_libraries(PA19 PRIVATE -Wl,--whole-archive portaudio -Wl,--no-whole-archive Threads::Threads)
target_include_directories(PA19 PUBLIC "${LIB_DIR}/portaudio-19.7.0/include")
set_target_properties(PA19 PROPERTIES CXX_VISIBILITY_PRESET default)
target_link_options(PA19 PRIVATE -Wl,--no-undefined)

# --------------------------------------------------------------------------
# rnnoise (static, PIC)
# --------------------------------------------------------------------------
set(RNNOISE_DIR "${NR_SRC}/rnnoise")
add_library(rnnoise STATIC
  ${RNNOISE_DIR}/src/celt_lpc.c
  ${RNNOISE_DIR}/src/denoise.c
  ${RNNOISE_DIR}/src/kiss_fft.c
  ${RNNOISE_DIR}/src/nnet.c
  ${RNNOISE_DIR}/src/nnet_default.c
  ${RNNOISE_DIR}/src/parse_lpcnet_weights.c
  ${RNNOISE_DIR}/src/pitch.c
  ${RNNOISE_DIR}/src/rnn.c
  ${RNNOISE_DIR}/src/rnnoise_data_little.c   # same model as the Windows build
  ${RNNOISE_DIR}/src/rnnoise_tables.c
)
target_include_directories(rnnoise PUBLIC "${RNNOISE_DIR}/include"
  PRIVATE "${RNNOISE_DIR}/src" "${COMPAT_DIR}/rnnoise")
target_compile_options(rnnoise PRIVATE -O3)
target_link_libraries(rnnoise PRIVATE m)

# --------------------------------------------------------------------------
# libspecbleach (static, PIC)
# --------------------------------------------------------------------------
set(SPECBLEACH_DIR "${NR_SRC}/libspecbleach")
file(GLOB_RECURSE SPECBLEACH_SRCS "${SPECBLEACH_DIR}/src/*.c")
add_library(specbleach STATIC ${SPECBLEACH_SRCS})
target_include_directories(specbleach PUBLIC "${SPECBLEACH_DIR}/include" PRIVATE "${SPECBLEACH_DIR}/src")
target_link_libraries(specbleach PRIVATE PkgConfig::FFTW3F m)

# --------------------------------------------------------------------------
# Win32 compatibility layer (compiled into each shared library)
# --------------------------------------------------------------------------
add_library(win32compat STATIC "${COMPAT_DIR}/win32compat.c")
target_include_directories(win32compat PUBLIC "${COMPAT_DIR}/include")
target_link_libraries(win32compat PUBLIC Threads::Threads)
# MSVC treats __declspec & co. as keywords, and some headers use them before
# including <Windows.h>; force-include the compat header everywhere.
set(FORCE_COMPAT -include "${COMPAT_DIR}/include/win32compat.h")

# --------------------------------------------------------------------------
# wdsp
# --------------------------------------------------------------------------
file(GLOB WDSP_SRCS "${SRC_DIR}/wdsp/*.c")
add_library(wdsp SHARED ${WDSP_SRCS})
target_include_directories(wdsp PUBLIC "${SRC_DIR}/wdsp")
target_compile_options(wdsp PRIVATE ${LEGACY_C_FLAGS} ${FORCE_COMPAT})
target_link_libraries(wdsp PRIVATE
  win32compat rnnoise specbleach
  PkgConfig::FFTW3 PkgConfig::FFTW3F
  Threads::Threads m)
target_link_options(wdsp PRIVATE -Wl,--no-undefined)
# impulse_cache.h (included by every wdsp header user via comm.h) selects its
# 64-bit hash with _WIN64; match the Windows x64 build.  Nothing else in wdsp,
# ChannelMaster or the headers they include tests _WIN64.
if(CMAKE_SIZEOF_VOID_P EQUAL 8)
  target_compile_definitions(wdsp PUBLIC _WIN64)
endif()

# --------------------------------------------------------------------------
# ChannelMaster
# --------------------------------------------------------------------------
file(GLOB CM_SRCS "${SRC_DIR}/ChannelMaster/*.c")
add_library(ChannelMaster SHARED ${CM_SRCS} "${COMPAT_DIR}/asio_stub.c")
target_include_directories(ChannelMaster PRIVATE
  "${SRC_DIR}/ChannelMaster"
  # wdsp's headers include the NR3/NR4 library headers
  "${RNNOISE_DIR}/include" "${SPECBLEACH_DIR}/include")
target_compile_options(ChannelMaster PRIVATE ${LEGACY_C_FLAGS} ${FORCE_COMPAT})
target_link_libraries(ChannelMaster PRIVATE
  wdsp PA19 win32compat
  PkgConfig::FFTW3
  Threads::Threads m)
target_link_options(ChannelMaster PRIVATE -Wl,--no-undefined)
# version.c includes Console/Versions.cs, a C#/C polyglot keyed on WIN32
set_source_files_properties("${SRC_DIR}/ChannelMaster/version.c" PROPERTIES COMPILE_DEFINITIONS WIN32)
# exactly $ORIGIN (no build-tree path): wdsp and PA19 are always next to it
set_target_properties(ChannelMaster PROPERTIES BUILD_WITH_INSTALL_RPATH ON INSTALL_RPATH "\$ORIGIN")

# --------------------------------------------------------------------------
# tests
#   compat_test : unit tests for the Win32 compatibility layer
#   smoketest   : loads the three libraries and runs a wdsp receive channel
# --------------------------------------------------------------------------
enable_testing()
add_executable(compat_test "${CMAKE_CURRENT_SOURCE_DIR}/tests/compat_test.c")
target_link_libraries(compat_test PRIVATE win32compat m)
add_test(NAME compat COMMAND compat_test)

add_executable(thetis_smoketest "${CMAKE_CURRENT_SOURCE_DIR}/tests/smoketest.c")
target_link_libraries(thetis_smoketest PRIVATE ${CMAKE_DL_LIBS} m)
add_test(NAME smoketest COMMAND thetis_smoketest
  $<TARGET_FILE:wdsp> $<TARGET_FILE:ChannelMaster> $<TARGET_FILE:PA19>)

# Some C# declarations import "WDSP.dll" rather than "wdsp.dll"; Linux file
# names are case-sensitive, so provide the upper-case alias as well.
add_custom_command(TARGET wdsp POST_BUILD
  COMMAND ${CMAKE_COMMAND} -E create_symlink libwdsp.so "$<TARGET_FILE_DIR:wdsp>/libWDSP.so")

include(GNUInstallDirs)
install(TARGETS wdsp ChannelMaster PA19 LIBRARY DESTINATION ${CMAKE_INSTALL_LIBDIR}/thetis)
install(FILES "$<TARGET_FILE_DIR:wdsp>/libWDSP.so" DESTINATION ${CMAKE_INSTALL_LIBDIR}/thetis)
