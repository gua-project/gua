cmake_minimum_required(VERSION 3.20)

project(gua LANGUAGES C CXX)

include(cmake/GuaRemoteBuild.cmake)

set(GUA_VERSION "0.5.0-preview.3" CACHE STRING "Gua component version")
set(GUA_BUILD_ID "development" CACHE STRING "Reproducible Gua build identifier")
set(GUA_GODOT_PLUGIN_VERSION "0.4.0" CACHE STRING "Gua Godot plugin version")
add_compile_definitions(
    GUA_VERSION="${GUA_VERSION}"
    GUA_BUILD_ID="${GUA_BUILD_ID}"
    GUA_GODOT_PLUGIN_VERSION="${GUA_GODOT_PLUGIN_VERSION}"
)

include(CTest)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>DLL")
endif()

option(GUA_BUILD_EXAMPLES "Build Gua examples." ON)
option(GUA_BUILD_IMGUI_EXAMPLE "Build the Win32 DirectX 11 ImGui example." ON)
option(GUA_BUILD_GODOT "Build the Godot GDExtension adapter." ON)
if(WIN32 OR CMAKE_SYSTEM_NAME STREQUAL "Linux" OR CMAKE_SYSTEM_NAME STREQUAL "Darwin")
    set(GUA_DESKTOP_NATIVE ON)
else()
    set(GUA_DESKTOP_NATIVE OFF)
endif()

if(GUA_ENABLE_REMOTE_AUTOMATION AND GUA_DESKTOP_NATIVE AND NOT EMSCRIPTEN)
    set(GUA_BUILD_WS_BRIDGE_DEFAULT ON)
else()
    set(GUA_BUILD_WS_BRIDGE_DEFAULT OFF)
endif()
option(GUA_BUILD_WS_BRIDGE "Build the native Inspector WebSocket transport." ${GUA_BUILD_WS_BRIDGE_DEFAULT})
if(GUA_BUILD_WS_BRIDGE AND NOT GUA_ENABLE_REMOTE_AUTOMATION)
    message(FATAL_ERROR "GUA_BUILD_WS_BRIDGE requires the explicit remote automation opt-in")
endif()
if(GUA_ENABLE_REMOTE_AUTOMATION AND GUA_DESKTOP_NATIVE AND NOT EMSCRIPTEN AND NOT GUA_BUILD_WS_BRIDGE)
    message(FATAL_ERROR "The selected native remote variant requires its WebSocket transport; use a remote-OFF development build for local-only use")
endif()
gua_finalize_remote_build()

add_subdirectory(native/gua-core)
add_subdirectory(native/gua-imgui)
add_subdirectory(native/gua-testing)
if(WIN32 OR CMAKE_SYSTEM_NAME STREQUAL "Linux")
    add_subdirectory(native/gua-game-policy)
endif()
if(GUA_BUILD_WS_BRIDGE AND GUA_DESKTOP_NATIVE AND NOT EMSCRIPTEN)
    add_subdirectory(native/gua-ws-bridge)
endif()
if(GUA_DESKTOP_NATIVE OR EMSCRIPTEN)
    add_subdirectory(native/gua-runtime)
endif()

if(GUA_BUILD_GODOT AND (GUA_DESKTOP_NATIVE OR EMSCRIPTEN))
    add_subdirectory(native/gua-godot)
elseif(GUA_BUILD_GODOT)
    message(STATUS "Skipping native/gua-godot because this platform is not a supported native or Web target.")
endif()

if(GUA_BUILD_EXAMPLES)
    if(WIN32 AND GUA_BUILD_IMGUI_EXAMPLE AND TARGET gua-ws-bridge AND GUA_DISTRIBUTION_FLAVOR MATCHES "^(development|qa)$")
        add_subdirectory(examples/cpp-imgui)
    elseif(NOT WIN32)
        message(STATUS "Skipping examples/cpp-imgui because it requires Win32 and DirectX 11.")
    endif()
    if(TARGET gua-ws-bridge AND GUA_DISTRIBUTION_FLAVOR MATCHES "^(development|qa)$")
        add_subdirectory(examples/native-bridge)
    else()
        message(STATUS "Skipping examples/native-bridge because the native WebSocket bridge is unavailable.")
    endif()
endif()
