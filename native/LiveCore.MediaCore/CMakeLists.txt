add_library(LiveCore.MediaCore STATIC
    src/Log.cpp
    src/FlatJson.cpp
    src/Protocol.cpp
    src/PipeServerWin32.cpp
    src/PipeServerPosix.cpp)

target_include_directories(LiveCore.MediaCore PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(LiveCore.MediaCore PRIVATE livecore_warnings)
set_target_properties(LiveCore.MediaCore PROPERTIES OUTPUT_NAME livecore_mediacore)
