cmake_minimum_required(VERSION 3.20)
project(zoomcheck_plugin_probe LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

set(ZOOM_PLUGIN_SDK_DIR "" CACHE PATH "Path to extracted Zoom Plugin SDK architecture folder containing export_h, lib, and bin")

add_executable(zoomcheck-plugin-probe
    src/main.cpp
)

target_compile_definitions(zoomcheck-plugin-probe PRIVATE
    WIN32
    UNICODE
    _UNICODE
    NOMINMAX
    WIN32_LEAN_AND_MEAN
)

if(MSVC)
    target_compile_options(zoomcheck-plugin-probe PRIVATE /W4 /utf-8)
endif()

target_link_libraries(zoomcheck-plugin-probe PRIVATE
    bcrypt
    ws2_32
    winhttp
    shell32
)

if(ZOOM_PLUGIN_SDK_DIR)
    message(STATUS "Building against Zoom Plugin SDK at: ${ZOOM_PLUGIN_SDK_DIR}")
    target_include_directories(zoomcheck-plugin-probe PRIVATE "${ZOOM_PLUGIN_SDK_DIR}/export_h")
    target_link_libraries(zoomcheck-plugin-probe PRIVATE "${ZOOM_PLUGIN_SDK_DIR}/lib/zToolSuiteIPCProxy.lib")
    add_custom_command(TARGET zoomcheck-plugin-probe POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E copy_directory
            "${ZOOM_PLUGIN_SDK_DIR}/bin"
            "$<TARGET_FILE_DIR:zoomcheck-plugin-probe>"
    )
else()
    message(STATUS "ZOOM_PLUGIN_SDK_DIR not set; building with contract stub headers for offline verification")
    target_sources(zoomcheck-plugin-probe PRIVATE stubs/mock_sdk.cpp)
    target_include_directories(zoomcheck-plugin-probe PRIVATE stubs)
    target_compile_definitions(zoomcheck-plugin-probe PRIVATE ZOOM_PLUGIN_SDK_STUB=1)
endif()
