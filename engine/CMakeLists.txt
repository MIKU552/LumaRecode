find_program(CARGO_EXECUTABLE cargo REQUIRED)
find_package(Threads REQUIRED)

set(LUMA_RUST_TARGET "" CACHE STRING "Rust target triple; empty selects the native CMake target")
if(NOT LUMA_RUST_TARGET)
    string(TOLOWER "${CMAKE_SYSTEM_PROCESSOR}" luma_processor)
    if(luma_processor MATCHES "aarch64|arm64")
        set(luma_cpu aarch64)
    else()
        set(luma_cpu x86_64)
    endif()
    if(WIN32)
        set(LUMA_RUST_TARGET "${luma_cpu}-pc-windows-msvc")
    elseif(APPLE)
        set(LUMA_RUST_TARGET "${luma_cpu}-apple-darwin")
    else()
        set(LUMA_RUST_TARGET "${luma_cpu}-unknown-linux-gnu")
    endif()
endif()

if(CMAKE_BUILD_TYPE STREQUAL "Debug")
    set(luma_rust_profile debug)
    set(luma_rust_profile_arguments)
else()
    set(luma_rust_profile release)
    set(luma_rust_profile_arguments --release)
endif()
set(luma_rust_target_dir "${CMAKE_CURRENT_BINARY_DIR}/rust")
if(WIN32)
    set(luma_rust_library "${luma_rust_target_dir}/${LUMA_RUST_TARGET}/${luma_rust_profile}/luma_hdr10plus.lib")
else()
    set(luma_rust_library "${luma_rust_target_dir}/${LUMA_RUST_TARGET}/${luma_rust_profile}/libluma_hdr10plus.a")
endif()
file(GLOB_RECURSE luma_rust_sources CONFIGURE_DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/rust/*.rs")
set(luma_rust_environment "CARGO_TARGET_DIR=${luma_rust_target_dir}")
if(NOT CMAKE_BUILD_TYPE STREQUAL "Debug")
    # Encoded flags preserve a source path containing spaces as one rustc
    # argument. Use the same environment for production and contract tests.
    list(APPEND luma_rust_environment "CARGO_ENCODED_RUSTFLAGS=--remap-path-prefix=${PROJECT_SOURCE_DIR}=/_/src")
endif()
add_custom_command(OUTPUT "${luma_rust_library}"
    COMMAND "${CMAKE_COMMAND}" -E env ${luma_rust_environment}
        "${CARGO_EXECUTABLE}" build --locked --manifest-path "${CMAKE_CURRENT_SOURCE_DIR}/rust/Cargo.toml"
        --target "${LUMA_RUST_TARGET}" ${luma_rust_profile_arguments}
    DEPENDS ${luma_rust_sources} rust/Cargo.toml rust/hdr10plus/Cargo.toml rust/Cargo.lock
        "${PROJECT_SOURCE_DIR}/rust-toolchain.toml"
    WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}"
    COMMENT "Building bounded Rust metadata module (${LUMA_RUST_TARGET})"
    VERBATIM)
add_custom_target(luma_rust_build DEPENDS "${luma_rust_library}")
add_library(luma_rust STATIC IMPORTED GLOBAL)
set_target_properties(luma_rust PROPERTIES IMPORTED_LOCATION "${luma_rust_library}")
add_dependencies(luma_rust luma_rust_build)

add_subdirectory(c)
add_library(luma_core SHARED cpp/output_policy.cpp cpp/hdr10plus.cpp cpp/process.cpp $<TARGET_OBJECTS:luma_c>)
if(WIN32)
    target_sources(luma_core PRIVATE cpp/process_windows.cpp)
    target_compile_definitions(luma_core PRIVATE _WIN32_WINNT=0x0A00)
else()
    target_sources(luma_core PRIVATE cpp/process_posix.cpp)
endif()
target_include_directories(luma_core PUBLIC include)
target_compile_definitions(luma_core PRIVATE LUMA_CORE_BUILD)
target_link_libraries(luma_core PRIVATE luma_rust Threads::Threads ${CMAKE_DL_LIBS})
set_target_properties(luma_core PROPERTIES OUTPUT_NAME LumaRecodeCore
    C_VISIBILITY_PRESET hidden CXX_VISIBILITY_PRESET hidden VISIBILITY_INLINES_HIDDEN ON)
if(WIN32)
    # Match rustc --print=native-static-libs, including synchronization APIs that
    # newer local SDKs can also resolve indirectly through their umbrella libs.
    target_link_libraries(luma_core PRIVATE userenv ntdll ws2_32 dbghelp synchronization)
elseif(APPLE)
    find_library(LUMA_PROC_LIBRARY proc REQUIRED)
    target_link_libraries(luma_core PRIVATE "${LUMA_PROC_LIBRARY}")
    target_link_options(luma_core PRIVATE "LINKER:-exported_symbols_list,${CMAKE_CURRENT_SOURCE_DIR}/exports.macos")
else()
    target_link_libraries(luma_core PRIVATE m)
    target_link_options(luma_core PRIVATE "LINKER:--version-script=${CMAKE_CURRENT_SOURCE_DIR}/exports.map")
endif()
foreach(luma_target luma_c luma_core)
    if(MSVC)
        target_compile_options(${luma_target} PRIVATE /W4 /WX /utf-8)
        target_compile_definitions(${luma_target} PRIVATE _CRT_SECURE_NO_WARNINGS NOMINMAX WIN32_LEAN_AND_MEAN)
        target_compile_options(${luma_target} PRIVATE "$<$<NOT:$<CONFIG:Debug>>:/experimental:deterministic>")
        target_compile_options(${luma_target} PRIVATE "$<$<NOT:$<CONFIG:Debug>>:/pathmap:${PROJECT_SOURCE_DIR}=/_/src>")
    else()
        target_compile_options(${luma_target} PRIVATE -Wall -Wextra -Wpedantic -Werror)
        target_compile_options(${luma_target} PRIVATE "$<$<NOT:$<CONFIG:Debug>>:-ffile-prefix-map=${PROJECT_SOURCE_DIR}=/_/src>")
    endif()
endforeach()
if(MSVC)
    target_link_options(luma_core PRIVATE "$<$<NOT:$<CONFIG:Debug>>:/PDBALTPATH:LumaRecodeCore.pdb>")
endif()
file(GENERATE OUTPUT "${CMAKE_CURRENT_BINARY_DIR}/build-info.json" CONTENT
    "{\n  \"abi_version\": 1,\n  \"system\": \"${CMAKE_SYSTEM_NAME}\",\n  \"processor\": \"${CMAKE_SYSTEM_PROCESSOR}\",\n  \"compiler\": \"${CMAKE_CXX_COMPILER_ID}\",\n  \"compiler_version\": \"${CMAKE_CXX_COMPILER_VERSION}\",\n  \"c_standard\": 17,\n  \"cpp_standard\": 23,\n  \"rust_target\": \"${LUMA_RUST_TARGET}\"\n}\n")

if(BUILD_TESTING)
    add_executable(luma_lifecycle_checks cpp/process.cpp c/data.c c/utf8.c tests/process_failure_checks.cpp)
    target_compile_definitions(luma_lifecycle_checks PRIVATE LUMA_CORE_STATIC)
    target_include_directories(luma_lifecycle_checks PRIVATE include)
    target_link_libraries(luma_lifecycle_checks PRIVATE Threads::Threads)
    add_test(NAME process_failure_contracts COMMAND luma_lifecycle_checks)
    add_executable(luma_process_helper tests/process_helper.cpp)
    add_executable(luma_process_checks tests/process_checks.cpp)
    if(MSVC)
        target_compile_options(luma_process_helper PRIVATE /utf-8)
        target_compile_options(luma_process_checks PRIVATE /utf-8)
        target_compile_definitions(luma_process_helper PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
    endif()
    target_link_libraries(luma_process_checks PRIVATE luma_core Threads::Threads)
    add_test(NAME process_contracts COMMAND luma_process_checks $<TARGET_FILE:luma_process_helper>)
    set_tests_properties(process_contracts PROPERTIES TIMEOUT 45)
    add_executable(luma_c_example examples/format.c)
    target_link_libraries(luma_c_example PRIVATE luma_core)
    add_test(NAME c_example COMMAND luma_c_example)
    add_executable(luma_c_checks tests/c_checks.c)
    target_link_libraries(luma_c_checks PRIVATE luma_core)
    add_test(NAME c_contracts COMMAND luma_c_checks)
    add_executable(luma_core_checks tests/core_checks.cpp)
    target_link_libraries(luma_core_checks PRIVATE luma_core)
    add_test(NAME cpp_rust_contracts COMMAND luma_core_checks)
    add_test(NAME rust_metadata
        COMMAND "${CMAKE_COMMAND}" -E env ${luma_rust_environment}
        "${CARGO_EXECUTABLE}" test --locked --manifest-path "${CMAKE_CURRENT_SOURCE_DIR}/rust/Cargo.toml"
        --target "${LUMA_RUST_TARGET}" ${luma_rust_profile_arguments})
    set_tests_properties(rust_metadata PROPERTIES WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}")
    if(APPLE)
        find_program(XCRUN_EXECUTABLE xcrun REQUIRED)
        set(luma_swift_check "${CMAKE_CURRENT_BINARY_DIR}/luma_swift_checks")
        add_custom_command(OUTPUT "${luma_swift_check}"
            COMMAND "${XCRUN_EXECUTABLE}" swiftc -swift-version 6 -target arm64-apple-macos27.0
                -I "${CMAKE_CURRENT_SOURCE_DIR}/include" -L "${CMAKE_CURRENT_BINARY_DIR}"
                -lLumaRecodeCore -Xlinker -rpath -Xlinker @loader_path
                "${CMAKE_CURRENT_SOURCE_DIR}/tests/swift_checks.swift" -o "${luma_swift_check}"
            DEPENDS luma_core tests/swift_checks.swift include/lumarecode/core.h
                include/lumarecode/hdr10plus.h include/lumarecode/process.h include/module.modulemap
            VERBATIM)
        add_custom_target(luma_swift_check_build ALL DEPENDS "${luma_swift_check}")
        add_test(NAME swift_contracts COMMAND "${luma_swift_check}" $<TARGET_FILE:luma_process_helper>)
        set_tests_properties(swift_contracts PROPERTIES TIMEOUT 35)
    endif()
endif()

install(TARGETS luma_core RUNTIME DESTINATION bin LIBRARY DESTINATION lib ARCHIVE DESTINATION lib)
install(DIRECTORY include/lumarecode DESTINATION include)
install(FILES include/module.modulemap DESTINATION include)
