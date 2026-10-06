if(EMSCRIPTEN)
    set(GUA_RUNTIME_LIBRARY_TYPE STATIC)
else()
    set(GUA_RUNTIME_LIBRARY_TYPE SHARED)
endif()

add_library(gua-runtime ${GUA_RUNTIME_LIBRARY_TYPE}
    src/runtime.cpp
)

add_library(gua::runtime ALIAS gua-runtime)

if(GUA_DESKTOP_NATIVE AND GUA_BUILD_GODOT)
    # A distributed Godot addon contains per-platform Debug and Release
    # GDExtensions side by side. Embed the runtime so each library is
    # self-contained and does not depend on a configuration-ambiguous shared
    # runtime beside the addon binaries.
    add_library(gua-runtime-godot STATIC
        src/runtime.cpp
    )
    add_library(gua::runtime-godot ALIAS gua-runtime-godot)
    set_target_properties(gua-runtime-godot PROPERTIES POSITION_INDEPENDENT_CODE ON)
endif()

target_include_directories(gua-runtime
    PUBLIC
        include
)

target_link_libraries(gua-runtime
    PUBLIC
        gua::core
)

if(TARGET gua-ws-bridge)
    target_link_libraries(gua-runtime PUBLIC gua::ws-bridge)
    target_compile_definitions(gua-runtime PRIVATE GUA_RUNTIME_WITH_WS=1)
else()
    target_compile_definitions(gua-runtime PRIVATE GUA_RUNTIME_WITH_WS=0)
endif()

target_compile_features(gua-runtime PUBLIC cxx_std_20)

if(TARGET gua-runtime-godot)
    target_include_directories(gua-runtime-godot PUBLIC include)
    target_link_libraries(gua-runtime-godot PUBLIC gua::core)
    if(TARGET gua-ws-bridge)
        target_link_libraries(gua-runtime-godot PUBLIC gua::ws-bridge)
        target_compile_definitions(gua-runtime-godot PRIVATE GUA_RUNTIME_WITH_WS=1)
    else()
        target_compile_definitions(gua-runtime-godot PRIVATE GUA_RUNTIME_WITH_WS=0)
    endif()
    target_compile_features(gua-runtime-godot PUBLIC cxx_std_20)
endif()

set_target_properties(gua-runtime
    PROPERTIES
        OUTPUT_NAME "gua_runtime"
        WINDOWS_EXPORT_ALL_SYMBOLS ON
)

if(APPLE)
    set_target_properties(gua-runtime PROPERTIES BUILD_RPATH "@loader_path" INSTALL_RPATH "@loader_path")
elseif(UNIX AND NOT EMSCRIPTEN)
    set_target_properties(gua-runtime PROPERTIES BUILD_RPATH "$ORIGIN" INSTALL_RPATH "$ORIGIN")
endif()

if(EMSCRIPTEN)
    # Godot loads the Web GDExtension as a side module. Every static archive
    # linked into that module must use relocatable WebAssembly code.
    set_target_properties(gua-runtime PROPERTIES POSITION_INDEPENDENT_CODE ON)
endif()

if(MSVC)
    target_compile_options(gua-runtime
        PRIVATE
            /W4
            /permissive-
            /Zc:__cplusplus
            /utf-8
    )
    if(TARGET gua-runtime-godot)
        target_compile_options(gua-runtime-godot
            PRIVATE
                /W4
                /permissive-
                /Zc:__cplusplus
                /utf-8
        )
    endif()
else()
    target_compile_options(gua-runtime
        PRIVATE
            -Wall
            -Wextra
            -Wpedantic
    )
    if(TARGET gua-runtime-godot)
        target_compile_options(gua-runtime-godot PRIVATE -Wall -Wextra -Wpedantic)
    endif()
endif()

if(BUILD_TESTING)
    add_executable(gua-runtime-guarded-profile-tests tests/guarded_profile_tests.cpp)
    target_link_libraries(gua-runtime-guarded-profile-tests PRIVATE gua-runtime)
    target_compile_options(gua-runtime-guarded-profile-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-runtime-guarded-profile-tests COMMAND gua-runtime-guarded-profile-tests)
    add_executable(gua-runtime-spatial-tests tests/spatial_client_tests.cpp)
    target_link_libraries(gua-runtime-spatial-tests PRIVATE gua-runtime)
    target_compile_features(gua-runtime-spatial-tests PRIVATE cxx_std_20)
    target_compile_definitions(gua-runtime-spatial-tests PRIVATE GUA_SPATIAL_HOST_FIXTURE="${CMAKE_SOURCE_DIR}/protocol/fixtures/spatial-host-r1.json")
    target_compile_options(gua-runtime-spatial-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<CXX_COMPILER_ID:MSVC>:/EHsc> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-runtime-spatial-tests COMMAND gua-runtime-spatial-tests)
    add_executable(gua-runtime-screenshot-tests tests/screenshot_request_tests.cpp)
    target_link_libraries(gua-runtime-screenshot-tests PRIVATE gua-runtime)
    target_compile_features(gua-runtime-screenshot-tests PRIVATE cxx_std_20)
    target_compile_options(gua-runtime-screenshot-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-runtime-screenshot-tests COMMAND gua-runtime-screenshot-tests)
endif()

if(WIN32)
    add_custom_command(TARGET gua-runtime POST_BUILD
        COMMAND ${CMAKE_COMMAND} -E make_directory
            "${CMAKE_SOURCE_DIR}/examples/dotnet-godot/addons/gua/bin"
        COMMAND ${CMAKE_COMMAND} -E copy_if_different
            "$<TARGET_FILE:gua-runtime>"
            "${CMAKE_SOURCE_DIR}/examples/dotnet-godot/addons/gua/bin/$<TARGET_FILE_NAME:gua-runtime>"
    )
endif()
