cmake_minimum_required(VERSION 4.0)
# Rust's MSVC standard library uses the dynamic release CRT in both profiles.
# Keep every native object on that allocator/runtime, including Debug builds.
set(CMAKE_MSVC_RUNTIME_LIBRARY MultiThreadedDLL)
project(LumaRecode VERSION 0.1.0 LANGUAGES C CXX)

include(CTest)
set(CMAKE_C_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_C_EXTENSIONS OFF)
set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
set(CMAKE_EXPORT_COMPILE_COMMANDS ON)

add_subdirectory(engine)
