cmake_minimum_required(VERSION 3.10)
project(jctool-linux VERSION 1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 99)
set(CMAKE_CXX_FLAGS_RELEASE "-O2 -Wall -Wextra")
set(CMAKE_C_FLAGS_RELEASE "-O2 -Wall -Wextra")

find_package(PkgConfig REQUIRED)
pkg_check_modules(HIDAPI REQUIRED hidapi-hidraw)

if(NOT HIDAPI_FOUND)
  message(FATAL_ERROR "hidapi-dev not found")
endif()

message(STATUS "HIDAPI found: ${HIDAPI_LIBRARIES}")

# Named jctool-cli: the repository root already has a jctool/ folder (the Windows sources).
add_executable(jctool-cli jctool_linux_interactive.cpp cli/jc_core.cpp cli/png.cpp)
set_source_files_properties(cli/jc_core.cpp PROPERTIES COMPILE_OPTIONS "-Wno-parentheses;-Wno-unused-variable;-Wno-unused-but-set-variable")
target_include_directories(jctool-cli PRIVATE ${HIDAPI_INCLUDE_DIRS})
target_link_libraries(jctool-cli PRIVATE ${HIDAPI_LIBRARIES})
target_compile_options(jctool-cli PRIVATE -Wall -Wextra -O2)

install(TARGETS jctool-cli DESTINATION bin)
install(FILES linux/README.md README.md LICENSE DESTINATION share/doc/jctool)

message(STATUS "=== Joy-Con Toolkit (Linux CLI) ===")
message(STATUS "Build type: ${CMAKE_BUILD_TYPE}")
message(STATUS "Binary: jctool-cli")
message(STATUS "")
