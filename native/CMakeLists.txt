cmake_minimum_required(VERSION 3.20)

# Oldest macOS with ScreenCaptureKit. SCScreenshotManager (macOS 14+) is used when
# available at runtime, with a CGDisplayCreateImage fallback on 12.3–13.
set(CMAKE_OSX_DEPLOYMENT_TARGET "12.3" CACHE STRING "Minimum macOS version")

project(ScreenGuardCapture LANGUAGES CXX OBJCXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_OBJCXX_STANDARD 17)

# Compiler flags
set(CMAKE_CXX_FLAGS "${CMAKE_CXX_FLAGS} -Wall -Wextra")
set(CMAKE_CXX_FLAGS_RELEASE "-O2 -DNDEBUG")

# Source files
set(SOURCES
    src/screenguard_api.cpp
    src/mouse_monitor.cpp
    src/input_classifier.cpp
    src/screen_capture.mm
    src/gpu_detector.mm
)

# Create shared library
add_library(screencapture SHARED ${SOURCES})

# Include directories
target_include_directories(screencapture PUBLIC include)

# Link macOS frameworks (ScreenCaptureKit weak-linked for the pre-14 fallback path)
target_link_libraries(screencapture
    "-framework CoreGraphics"
    "-framework CoreFoundation"
    "-framework IOKit"
    "-framework Metal"
    "-framework Foundation"
    "-framework ApplicationServices"
    "-weak_framework ScreenCaptureKit"
)

# Visibility: hide all symbols by default, export only those marked
set_target_properties(screencapture PROPERTIES
    CXX_VISIBILITY_PRESET hidden
    C_VISIBILITY_PRESET hidden
    OUTPUT_NAME "screencapture"
    PREFIX "lib"
    SUFFIX ".dylib"
)

# Install target (optional)
install(TARGETS screencapture LIBRARY DESTINATION lib)
install(FILES include/screenguard_api.h DESTINATION include)
