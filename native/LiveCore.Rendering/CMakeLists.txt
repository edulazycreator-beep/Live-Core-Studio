# Direct3D 11 rendering (Windows only).
add_library(LiveCore.Rendering STATIC src/D3D.cpp)
target_include_directories(LiveCore.Rendering PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(LiveCore.Rendering PUBLIC d3d11 dxgi d3dcompiler PRIVATE livecore_warnings)
set_target_properties(LiveCore.Rendering PROPERTIES OUTPUT_NAME livecore_rendering)
