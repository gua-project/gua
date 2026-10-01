include(FetchContent)

set(GODOTCPP_API_VERSION "4.7" CACHE STRING "Godot API version for the Gua GDExtension." FORCE)
set(GUA_GODOT_CONFIGURATION "Debug" CACHE STRING "Build configuration for the Gua GDExtension (Debug or Release).")
set_property(CACHE GUA_GODOT_CONFIGURATION PROPERTY STRINGS Debug Release)
string(TOLOWER "${GUA_GODOT_CONFIGURATION}" GUA_GODOT_CONFIGURATION_LOWER)
if(GUA_GODOT_CONFIGURATION_LOWER STREQUAL "debug")
    set(GODOTCPP_TARGET "template_debug" CACHE STRING "Godot build target for the Gua GDExtension." FORCE)
elseif(GUA_GODOT_CONFIGURATION_LOWER STREQUAL "release")
    set(GODOTCPP_TARGET "template_release" CACHE STRING "Godot build target for the Gua GDExtension." FORCE)
else()
    message(FATAL_ERROR "GUA_GODOT_CONFIGURATION must be Debug or Release, got '${GUA_GODOT_CONFIGURATION}'.")
endif()
if(EMSCRIPTEN)
    # The distributed descriptor targets Godot's single-threaded Web template.
    # Keep godot-cpp and the side module on the same thread ABI.
    set(GODOTCPP_THREADS OFF CACHE BOOL "Enable threading support" FORCE)
endif()
set(GODOTCPP_USE_STATIC_CPP OFF CACHE BOOL "Use the same dynamic CRT policy as the rest of Gua." FORCE)
set(GODOTCPP_ENABLE_TESTING OFF CACHE BOOL "Disable godot-cpp integration tests." FORCE)
set(GODOTCPP_SYSTEM_HEADERS ON CACHE BOOL "Treat godot-cpp headers as external system headers." FORCE)

FetchContent_Declare(
    godot-cpp
    GIT_REPOSITORY https://github.com/godotengine/godot-cpp.git
    GIT_TAG ba0edfed90512ec64aba51d4295a3e7e30112f86
)

FetchContent_MakeAvailable(godot-cpp)

add_library(gua-godot SHARED
    src/gua_context.cpp
    src/spatial_host.cpp
    src/register_types.cpp
)

add_library(gua::godot ALIAS gua-godot)

target_include_directories(gua-godot
    PRIVATE
        include
)

if(GUA_DESKTOP_NATIVE)
    set(GUA_GODOT_RUNTIME_TARGET gua::runtime-godot)
else()
    set(GUA_GODOT_RUNTIME_TARGET gua::runtime)
endif()

target_link_libraries(gua-godot PRIVATE ${GUA_GODOT_RUNTIME_TARGET} godot::cpp)

target_compile_features(gua-godot PRIVATE cxx_std_20)

set(GUA_GODOT_ARCHITECTURE "" CACHE STRING "Godot addon architecture override (x86_64 or arm64).")
if(EMSCRIPTEN)
    set(GUA_GODOT_ARCH "wasm32")
elseif(GUA_GODOT_ARCHITECTURE)
    set(GUA_GODOT_ARCH "${GUA_GODOT_ARCHITECTURE}")
elseif(APPLE AND CMAKE_OSX_ARCHITECTURES AND NOT CMAKE_OSX_ARCHITECTURES MATCHES ";")
    set(GUA_GODOT_ARCH "${CMAKE_OSX_ARCHITECTURES}")
else()
    set(GUA_GODOT_ARCH "${CMAKE_SYSTEM_PROCESSOR}")
endif()
string(TOLOWER "${GUA_GODOT_ARCH}" GUA_GODOT_ARCH)
if(GUA_GODOT_ARCH MATCHES "^(amd64|x64|x86_64)$")
    set(GUA_GODOT_ARCH "x86_64")
elseif(GUA_GODOT_ARCH MATCHES "^(aarch64|arm64)$")
    set(GUA_GODOT_ARCH "arm64")
elseif(GUA_GODOT_ARCH STREQUAL "wasm32")
    # The Web descriptor is intentionally single-threaded wasm32.
else()
    message(FATAL_ERROR "Unsupported Godot desktop architecture '${GUA_GODOT_ARCH}'.")
endif()

if(EMSCRIPTEN)
    set(GUA_GODOT_OUTPUT_NAME "gua_godot.web.${GUA_GODOT_CONFIGURATION_LOWER}.wasm32")
elseif(WIN32)
    set(GUA_GODOT_OUTPUT_NAME "gua_godot.windows.${GUA_GODOT_CONFIGURATION_LOWER}.${GUA_GODOT_ARCH}")
elseif(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    set(GUA_GODOT_OUTPUT_NAME "gua_godot.linux.${GUA_GODOT_CONFIGURATION_LOWER}.${GUA_GODOT_ARCH}")
elseif(APPLE)
    set(GUA_GODOT_OUTPUT_NAME "gua_godot.macos.${GUA_GODOT_CONFIGURATION_LOWER}.${GUA_GODOT_ARCH}")
else()
    message(FATAL_ERROR "Unsupported Godot target platform '${CMAKE_SYSTEM_NAME}'.")
endif()

set_target_properties(gua-godot
    PROPERTIES
        OUTPUT_NAME "${GUA_GODOT_OUTPUT_NAME}"
        PREFIX ""
        RUNTIME_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        RUNTIME_OUTPUT_DIRECTORY_DEBUG "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        RUNTIME_OUTPUT_DIRECTORY_RELWITHDEBINFO "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        RUNTIME_OUTPUT_DIRECTORY_MINSIZEREL "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        LIBRARY_OUTPUT_DIRECTORY_DEBUG "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        LIBRARY_OUTPUT_DIRECTORY_RELWITHDEBINFO "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
        LIBRARY_OUTPUT_DIRECTORY_MINSIZEREL "${CMAKE_SOURCE_DIR}/examples/godot-gdscript/addons/gua/bin"
)

if(EMSCRIPTEN)
    set_target_properties(gua-godot PROPERTIES PREFIX "" SUFFIX ".wasm")
    target_link_options(gua-godot PRIVATE "-sSIDE_MODULE=1")
endif()

if(MSVC)
    target_compile_options(gua-godot
        PRIVATE
            /W4
            /permissive-
            /Zc:__cplusplus
            /utf-8
    )
else()
    target_compile_options(gua-godot
        PRIVATE
            -Wall
            -Wextra
            -Wpedantic
    )
endif()
