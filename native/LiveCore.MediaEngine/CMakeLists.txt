add_executable(LiveCore.MediaEngine
    src/main.cpp
    src/Engine.cpp
    src/Platform.cpp)

target_link_libraries(LiveCore.MediaEngine PRIVATE LiveCore.MediaCore livecore_warnings)
target_compile_definitions(LiveCore.MediaEngine PRIVATE LIVECORE_ENGINE_VERSION="${PROJECT_VERSION}")

if(MINGW)
    # Self-contained binary when cross-compiling (no libstdc++/winpthread DLLs to ship).
    target_link_options(LiveCore.MediaEngine PRIVATE -static)
endif()

install(TARGETS LiveCore.MediaEngine RUNTIME DESTINATION bin)
