cmake_minimum_required(VERSION 3.20)

project(atsm_agent VERSION 0.1.0 LANGUAGES CXX)

# C++17 keeps the core buildable with the v141_xp toolset (Windows XP) and with ESP-IDF.
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(ATSM_BUILD_TESTS "Build the native unit and integration tests" ON)

add_library(atsm_core STATIC
    core/src/cbor.cpp
    core/src/link.cpp
    core/src/arbiter.cpp
    core/src/json.cpp
    core/src/config.cpp
    core/src/agent.cpp
    platform/common/net.cpp
    platform/common/tcp_device.cpp)

if(WIN32)
    target_sources(atsm_core PRIVATE platform/win32/serial_win32.cpp)
    target_link_libraries(atsm_core PUBLIC ws2_32)
    # 0x0501 = Windows XP: fail the build if an API newer than the oldest supported target slips in.
    target_compile_definitions(atsm_core PUBLIC WIN32_LEAN_AND_MEAN NOMINMAX _WIN32_WINNT=0x0501)
else()
    target_sources(atsm_core PRIVATE platform/posix/serial_posix.cpp)
    find_package(Threads REQUIRED)
    target_link_libraries(atsm_core PUBLIC Threads::Threads)
endif()

target_include_directories(atsm_core PUBLIC core/include)

if(MSVC)
    target_compile_options(atsm_core PUBLIC /W4 /WX /permissive- /utf-8)
else()
    target_compile_options(atsm_core PUBLIC -Wall -Wextra -Wpedantic -Werror)
endif()

add_executable(atsm-agent app/main.cpp)
target_link_libraries(atsm-agent PRIVATE atsm_core)

if(ATSM_BUILD_TESTS)
    enable_testing()
    add_executable(atsm-native-tests tests/test_main.cpp)
    target_link_libraries(atsm-native-tests PRIVATE atsm_core)
    target_compile_definitions(atsm-native-tests PRIVATE ATSM_VECTORS="${CMAKE_CURRENT_SOURCE_DIR}/../conformance/link-v1/vectors.txt")
    add_test(NAME atsm-native-tests COMMAND atsm-native-tests)
endif()

install(TARGETS atsm-agent RUNTIME DESTINATION bin)
