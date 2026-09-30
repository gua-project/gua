add_library(gua-core
    src/gua.cpp
    src/value.cpp
    src/semantic_lint.cpp
)

add_library(gua::core ALIAS gua-core)

add_library(gua SHARED
    src/gua.cpp
    src/value.cpp
    src/semantic_lint.cpp
)

add_library(gua::shared ALIAS gua)

target_include_directories(gua-core
    PUBLIC
        include
)

target_include_directories(gua
    PUBLIC
        include
)

target_compile_features(gua-core PUBLIC cxx_std_20)
target_compile_features(gua PUBLIC cxx_std_20)

# gua-core is linked into the shared gua-runtime library on desktop platforms.
set_target_properties(gua-core PROPERTIES POSITION_INDEPENDENT_CODE ON)

set_target_properties(gua
    PROPERTIES
        OUTPUT_NAME "gua"
        WINDOWS_EXPORT_ALL_SYMBOLS ON
)

if(APPLE)
    set_target_properties(gua PROPERTIES BUILD_RPATH "@loader_path" INSTALL_RPATH "@loader_path")
elseif(UNIX)
    set_target_properties(gua PROPERTIES BUILD_RPATH "$ORIGIN" INSTALL_RPATH "$ORIGIN")
endif()

if(MSVC)
    target_compile_options(gua-core
        PRIVATE
            /W4
            /permissive-
            /Zc:__cplusplus
            /utf-8
            /EHsc
    )
    target_compile_options(gua
        PRIVATE
            /W4
            /permissive-
            /Zc:__cplusplus
            /utf-8
            /EHsc
    )
else()
    target_compile_options(gua-core
        PRIVATE
            -Wall
            -Wextra
            -Wpedantic
    )
    target_compile_options(gua
        PRIVATE
            -Wall
            -Wextra
            -Wpedantic
    )
endif()

if(BUILD_TESTING)
    add_executable(gua-semantic-lint-tests tests/semantic_lint_tests.cpp)
    target_link_libraries(gua-semantic-lint-tests PRIVATE gua-core)
    target_compile_definitions(gua-semantic-lint-tests PRIVATE GUA_LINT_FIXTURE="${PROJECT_SOURCE_DIR}/protocol/fixtures/semantic-lint-v1.json")
    target_compile_options(gua-semantic-lint-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-semantic-lint-tests COMMAND gua-semantic-lint-tests)
    add_executable(gua-semantic-lint-c-tests tests/semantic_lint_c_tests.c)
    target_link_libraries(gua-semantic-lint-c-tests PRIVATE gua)
    target_compile_options(gua-semantic-lint-c-tests PRIVATE $<$<C_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<C_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-semantic-lint-c-tests COMMAND gua-semantic-lint-c-tests)
    add_executable(gua-value-c-tests tests/value_c_tests.c)
    target_link_libraries(gua-value-c-tests PRIVATE gua)
    target_compile_options(gua-value-c-tests PRIVATE $<$<C_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<C_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-value-c-tests COMMAND gua-value-c-tests)
    add_executable(gua-value-tests tests/value_tests.cpp)
    target_link_libraries(gua-value-tests PRIVATE gua-core)
    target_compile_definitions(gua-value-tests PRIVATE GUA_VALUE_FIXTURE="${PROJECT_SOURCE_DIR}/protocol/fixtures/value-v1.json")
    target_compile_options(gua-value-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<CXX_COMPILER_ID:MSVC>:/utf-8> $<$<CXX_COMPILER_ID:MSVC>:/EHsc> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-value-tests COMMAND gua-value-tests)
    add_executable(gua-core-state-tests tests/state_model_tests.cpp)
    target_link_libraries(gua-core-state-tests PRIVATE gua-core)
    target_compile_features(gua-core-state-tests PRIVATE cxx_std_20)
    target_compile_options(gua-core-state-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-core-state-tests COMMAND gua-core-state-tests)

endif()

if(BUILD_TESTING)
    add_executable(gua-observe-tests tests/observe_tests.cpp)
    target_link_libraries(gua-observe-tests PRIVATE gua-core)
    target_compile_definitions(gua-observe-tests PRIVATE GUA_OBSERVE_FIXTURE="${PROJECT_SOURCE_DIR}/protocol/fixtures/observe-v1.json")
    target_compile_options(gua-observe-tests PRIVATE $<$<CXX_COMPILER_ID:MSVC>:/UNDEBUG> $<$<CXX_COMPILER_ID:MSVC>:/EHsc> $<$<NOT:$<CXX_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-observe-tests COMMAND gua-observe-tests)
    add_executable(gua-observe-c-tests tests/observe_c_tests.c)
    target_link_libraries(gua-observe-c-tests PRIVATE gua)
    target_compile_options(gua-observe-c-tests PRIVATE $<$<C_COMPILER_ID:MSVC>:/UNDEBUG> $<$<NOT:$<C_COMPILER_ID:MSVC>>:-UNDEBUG>)
    add_test(NAME gua-observe-c-tests COMMAND gua-observe-c-tests)
endif()
