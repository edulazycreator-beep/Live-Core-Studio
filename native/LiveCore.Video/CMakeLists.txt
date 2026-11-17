# Portable part: the switcher state machine (unit-tested on every platform).
add_library(LiveCore.VideoCore STATIC src/Switcher.cpp)
target_include_directories(LiveCore.VideoCore PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(LiveCore.VideoCore PRIVATE livecore_warnings)
set_target_properties(LiveCore.VideoCore PROPERTIES OUTPUT_NAME livecore_videocore)

# Windows pipeline: sources (test patterns, Media Foundation capture), buses, monitor feeds.
if(LIVECORE_ENABLE_VIDEO)
    add_library(LiveCore.Video STATIC src/Sources.cpp src/VideoPipeline.cpp)
    target_include_directories(LiveCore.Video PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
    target_link_libraries(LiveCore.Video
        PUBLIC LiveCore.VideoCore LiveCore.Rendering LiveCore.MediaCore
        PRIVATE livecore_warnings mfplat mf mfreadwrite mfuuid ole32 winmm)
    target_compile_definitions(LiveCore.Video PUBLIC LIVECORE_HAS_VIDEO=1)
    set_target_properties(LiveCore.Video PROPERTIES OUTPUT_NAME livecore_video)
endif()
