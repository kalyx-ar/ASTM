cmake_minimum_required(VERSION 3.20)

project(remoteusb VERSION 0.3.0 LANGUAGES C CXX)

# C++17 keeps the core buildable with the v141_xp toolset (Windows XP) and with ESP-IDF.
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(REMOTEUSB_BUILD_TESTS "Build the native unit and integration tests" ON)
option(REMOTEUSB_ENABLE_TLS "Build the TLS transport with Mbed TLS" ON)
option(REMOTEUSB_WINDOWS_XP "Target Windows XP (v141_xp toolset): own entropy source instead of BCryptGenRandom" OFF)
set(REMOTEUSB_MBEDTLS_SOURCE_DIR "" CACHE PATH "Local Mbed TLS 4.1.1 source tree (offline builds); downloaded and verified when empty")

if(REMOTEUSB_ENABLE_TLS)
    # Mbed TLS 4.1 LTS (Apache-2.0), maintained until at least March 2029. Pinned to the release archive and its SHA-256
    # published at https://github.com/Mbed-TLS/mbedtls/releases/tag/mbedtls-4.1.1. Update both together.
    include(FetchContent)
    set(ENABLE_PROGRAMS OFF CACHE BOOL "" FORCE)
    set(ENABLE_TESTING OFF CACHE BOOL "" FORCE)
    set(MBEDTLS_FATAL_WARNINGS OFF CACHE BOOL "" FORCE)
    set(DISABLE_PACKAGE_CONFIG_AND_INSTALL ON CACHE BOOL "" FORCE)
    set(TF_PSA_CRYPTO_USER_CONFIG_FILE "${CMAKE_CURRENT_SOURCE_DIR}/tls/config/remoteusb_tf_psa_crypto_user_config.h" CACHE FILEPATH "" FORCE)
    # threading_alt.h must be visible to every Mbed TLS translation unit.
    include_directories(BEFORE "${CMAKE_CURRENT_SOURCE_DIR}/tls/config")
    if(REMOTEUSB_WINDOWS_XP)
        add_compile_definitions(REMOTEUSB_TLS_CUSTOM_ENTROPY _WIN32_WINNT=0x0501)
    endif()
    if(REMOTEUSB_MBEDTLS_SOURCE_DIR)
        set(FETCHCONTENT_SOURCE_DIR_MBEDTLS "${REMOTEUSB_MBEDTLS_SOURCE_DIR}")
    endif()
    FetchContent_Declare(mbedtls
        URL https://github.com/Mbed-TLS/mbedtls/releases/download/mbedtls-4.1.1/mbedtls-4.1.1.tar.bz2
        URL_HASH SHA256=3359a349e23db3d5536fcee032ae7b2ecbfc08972fab643089b5cbf2a375c98c
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE)
    FetchContent_MakeAvailable(mbedtls)
endif()

add_library(remoteusb_core STATIC
    core/src/cbor.cpp
    core/src/link.cpp
    core/src/arbiter.cpp
    core/src/json.cpp
    core/src/config.cpp
    core/src/config_cbor.cpp
    core/src/events.cpp
    core/src/state.cpp
    core/src/supervisor.cpp
    platform/common/files.cpp
    core/src/agent.cpp
    platform/common/net.cpp
    platform/common/tcp_device.cpp)

if(REMOTEUSB_ENABLE_TLS)
    target_sources(remoteusb_core PRIVATE tls/src/tls_mbedtls.cpp tls/src/threading_alt.cpp)
    if(REMOTEUSB_WINDOWS_XP)
        target_sources(remoteusb_core PRIVATE tls/src/entropy_win32_xp.c)
    endif()
    target_link_libraries(remoteusb_core PUBLIC mbedtls mbedx509 tfpsacrypto)
else()
    target_sources(remoteusb_core PRIVATE tls/src/tls_disabled.cpp)
endif()

if(WIN32)
    target_sources(remoteusb_core PRIVATE platform/win32/serial_win32.cpp platform/win32/protect_win32.cpp platform/win32/devices_win32.cpp)
    target_link_libraries(remoteusb_core PUBLIC ws2_32 crypt32 setupapi cfgmgr32)
    # 0x0501 = Windows XP: fail the build if an API newer than the oldest supported target slips into RemoteUSB code.
    target_compile_definitions(remoteusb_core PUBLIC WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0501)
else()
    target_sources(remoteusb_core PRIVATE platform/posix/serial_posix.cpp platform/posix/protect_posix.cpp platform/posix/devices_posix.cpp)
    find_package(Threads REQUIRED)
    target_link_libraries(remoteusb_core PUBLIC Threads::Threads)
endif()

target_include_directories(remoteusb_core PUBLIC core/include)
target_compile_definitions(remoteusb_core PUBLIC REMOTEUSB_VERSION="${PROJECT_VERSION}")

# Warnings as errors apply to RemoteUSB sources only (C++); Mbed TLS keeps its own settings.
if(MSVC)
    target_compile_options(remoteusb_core PUBLIC $<$<COMPILE_LANGUAGE:CXX>:/W4 /WX /permissive- /utf-8>)
else()
    target_compile_options(remoteusb_core PUBLIC $<$<COMPILE_LANGUAGE:CXX>:-Wall -Wextra -Wpedantic -Werror>)
endif()

add_executable(remoteusb app/main.cpp)
target_link_libraries(remoteusb PRIVATE remoteusb_core)

if(REMOTEUSB_BUILD_TESTS)
    enable_testing()
    add_executable(remoteusb-tests tests/test_main.cpp)
    target_link_libraries(remoteusb-tests PRIVATE remoteusb_core)
    target_compile_definitions(remoteusb-tests PRIVATE REMOTEUSB_VECTORS="${CMAKE_CURRENT_SOURCE_DIR}/../conformance/link-v1/vectors.txt")
    add_test(NAME remoteusb-tests COMMAND remoteusb-tests)
endif()

install(TARGETS remoteusb RUNTIME DESTINATION bin)
