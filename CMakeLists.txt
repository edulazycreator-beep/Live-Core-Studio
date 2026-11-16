cmake_minimum_required(VERSION 3.25)

project(LiveCoreStudioNative
    VERSION 0.1.0
    DESCRIPTION "Live Core Studio native media engine"
    LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

option(LIVECORE_BUILD_TESTS "Build native unit tests" ON)
option(LIVECORE_WARNINGS_AS_ERRORS "Treat compiler warnings as errors" ON)

# All binaries land in one folder so the app can find the engine next to it.
set(CMAKE_RUNTIME_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/bin)
set(CMAKE_LIBRARY_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)
set(CMAKE_ARCHIVE_OUTPUT_DIRECTORY ${CMAKE_BINARY_DIR}/lib)

add_library(livecore_warnings INTERFACE)
if(MSVC)
    target_compile_options(livecore_warnings INTERFACE /W4 /permissive- /utf-8 /Zc:__cplusplus)
    target_compile_definitions(livecore_warnings INTERFACE _WIN32_WINNT=0x0A00 WIN32_LEAN_AND_MEAN NOMINMAX UNICODE _UNICODE)
    if(LIVECORE_WARNINGS_AS_ERRORS)
        target_compile_options(livecore_warnings INTERFACE /WX)
    endif()
else()
    target_compile_options(livecore_warnings INTERFACE -Wall -Wextra -Wpedantic -Wshadow -Wconversion -Wno-sign-conversion)
    if(WIN32)
        target_compile_definitions(livecore_warnings INTERFACE _WIN32_WINNT=0x0A00 UNICODE _UNICODE)
    endif()
    if(LIVECORE_WARNINGS_AS_ERRORS)
        target_compile_options(livecore_warnings INTERFACE -Werror)
    endif()
endif()

add_subdirectory(native/LiveCore.MediaCore)
add_subdirectory(native/LiveCore.MediaEngine)

if(LIVECORE_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests/native)
endif()
