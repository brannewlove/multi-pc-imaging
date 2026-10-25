cmake_minimum_required(VERSION 3.15)
project(MultiPcImagingClient CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# WinPE 런타임 종속성 제거: MSVC 정적 런타임 링크 (/MT, /MTd)
if (MSVC)
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    add_compile_options(/W4 /EHsc)
endif()

include_directories(src)

set(SOURCES
    src/main.cpp
    src/hw_scanner.cpp
    src/net_client.cpp
    src/partition_engine.cpp
    src/deploy_engine.cpp
)

add_executable(PeImagingAgent ${SOURCES})

if (NOT MSVC)
    target_link_options(PeImagingAgent PRIVATE -static -static-libgcc -static-libstdc++)
endif()

target_link_libraries(PeImagingAgent PRIVATE
    ws2_32
    iphlpapi
)
