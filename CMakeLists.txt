cmake_minimum_required(VERSION 3.10)
project(jctool-linux VERSION 1.0 LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 11)
set(CMAKE_C_STANDARD 99)
set(CMAKE_CXX_FLAGS_RELEASE "-O2 -Wall -Wextra")
set(CMAKE_C_FLAGS_RELEASE "-O2 -Wall -Wextra")

find_package(PkgConfig REQUIRED)
pkg_check_modules(HIDAPI REQUIRED hidapi-hidraw)

if(NOT HIDAPI_FOUND)
  message(FATAL_ERROR "hidapi-dev not found")
endif()

message(STATUS "HIDAPI found: ${HIDAPI_LIBRARIES}")

add_executable(jctool_test jctool_linux.cpp)
target_include_directories(jctool_test PRIVATE ${HIDAPI_INCLUDE_DIRS})
target_link_libraries(jctool_test PRIVATE ${HIDAPI_LIBRARIES})
target_compile_options(jctool_test PRIVATE -Wall -Wextra -O2)

add_executable(jctool_full jctool_linux_full.cpp)
target_include_directories(jctool_full PRIVATE ${HIDAPI_INCLUDE_DIRS})
target_link_libraries(jctool_full PRIVATE ${HIDAPI_LIBRARIES})
target_compile_options(jctool_full PRIVATE -Wall -Wextra -O2)

add_executable(jctool jctool_linux_interactive.cpp)
target_include_directories(jctool PRIVATE ${HIDAPI_INCLUDE_DIRS})
target_link_libraries(jctool PRIVATE ${HIDAPI_LIBRARIES})
target_compile_options(jctool PRIVATE -Wall -Wextra -O2)

install(TARGETS jctool_test jctool_full jctool DESTINATION bin)
install(FILES LINUX_BUILD.md README.md LICENSE DESTINATION share/doc/jctool)

message(STATUS "=== Joy-Con Toolkit (Linux CLI) ===")
message(STATUS "Build type: ${CMAKE_BUILD_TYPE}")
message(STATUS "Binaries: jctool_test, jctool_full, jctool")
message(STATUS "")
