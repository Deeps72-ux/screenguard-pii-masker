/**
 * @file mouse_monitor.cpp
 * @brief Implementation of global mouse click monitoring via CGEventTap.
 *
 * Creates a Quartz Event Tap (kCGSessionEventTap) to listen for all mouse
 * click events system-wide. The tap runs on a dedicated background thread
 * with its own CFRunLoop.
 *
 * Key design decisions:
 *   - Uses kCGEventTapOptionListenOnly to avoid requiring Accessibility permissions
 *   - Runs on a separate thread so the main thread remains responsive
 *   - Re-enables the tap if macOS disables it due to timeout
 */

#include "mouse_monitor.h"
#include <iostream>
#include <chrono>

MouseMonitor::MouseMonitor() {}

MouseMonitor::~MouseMonitor() {
    stop();
}

/**
 * Static callback invoked by the CGEventTap for each matching event.
 *
 * This runs on the background thread's CFRunLoop. It extracts the click
 * coordinates and timestamp, then invokes the registered callback.
 */
CGEventRef MouseMonitor::eventCallback(CGEventTapProxy proxy, CGEventType type,
                                        CGEventRef event, void* refcon) {
    (void)proxy; // unused

    MouseMonitor* self = static_cast<MouseMonitor*>(refcon);

    // Handle event tap being disabled by the system (timeout or user input)
    if (type == kCGEventTapDisabledByTimeout || type == kCGEventTapDisabledByUserInput) {
        std::cerr << "[SkanCapture] Event tap was disabled by macOS. Re-enabling..." << std::endl;
        if (self->m_eventTap) {
            CGEventTapEnable(self->m_eventTap, true);
        }
        return event;
    }

    // Only process mouse down events
    if (type == kCGEventLeftMouseDown || type == kCGEventRightMouseDown) {
        CGPoint location = CGEventGetLocation(event);
        int64_t subtype = CGEventGetIntegerValueField(event, kCGMouseEventSubtype);

        auto now = std::chrono::system_clock::now();
        auto epochMs = std::chrono::duration_cast<std::chrono::milliseconds>(
            now.time_since_epoch()).count();

        MouseClickEvent clickEvent;
        clickEvent.x = location.x;
        clickEvent.y = location.y;
        clickEvent.timestampMs = epochMs;
        clickEvent.isLeftClick = (type == kCGEventLeftMouseDown);
        clickEvent.subtype = subtype;

        if (self->m_callback) {
            self->m_callback(clickEvent);
        }
    }

    // Always pass the event through (we're listen-only, but good practice)
    return event;
}

bool MouseMonitor::start(MouseClickCallback callback) {
    if (m_running.load()) {
        std::cerr << "[SkanCapture] Mouse monitor already running." << std::endl;
        return false;
    }

    m_callback = std::move(callback);
    m_running.store(true);

    // Start the event tap on a background thread
    m_thread = std::thread(&MouseMonitor::runLoopThread, this);

    // Give the thread a moment to initialize
    std::this_thread::sleep_for(std::chrono::milliseconds(200));

    return m_eventTap != nullptr;
}

/**
 * Background thread function that creates the event tap and runs the CFRunLoop.
 *
 * The tap is configured to listen for left and right mouse down events at the
 * session level, meaning it receives events from all applications.
 */
void MouseMonitor::runLoopThread() {
    // Event mask: listen for left and right mouse button presses
    CGEventMask eventMask =
        CGEventMaskBit(kCGEventLeftMouseDown) |
        CGEventMaskBit(kCGEventRightMouseDown);

    // Create the event tap at session level (all apps in the login session)
    // kCGEventTapOptionListenOnly = passive listener, doesn't require Accessibility
    m_eventTap = CGEventTapCreate(
        kCGSessionEventTap,
        kCGHeadInsertEventTap,
        kCGEventTapOptionListenOnly,
        eventMask,
        eventCallback,
        this
    );

    if (!m_eventTap) {
        std::cerr << "[SkanCapture] ERROR: Failed to create event tap.\n"
                  << "             Please grant Input Monitoring permission:\n"
                  << "             System Settings → Privacy & Security → Input Monitoring"
                  << std::endl;
        m_running.store(false);
        return;
    }

    // Create a CFRunLoop source from the event tap
    m_runLoopSource = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, m_eventTap, 0);
    if (!m_runLoopSource) {
        std::cerr << "[SkanCapture] ERROR: Failed to create CFRunLoopSource." << std::endl;
        CFRelease(m_eventTap);
        m_eventTap = nullptr;
        m_running.store(false);
        return;
    }

    // Get this thread's run loop and add the source
    m_runLoop = CFRunLoopGetCurrent();
    CFRunLoopAddSource(m_runLoop, m_runLoopSource, kCFRunLoopCommonModes);
    CGEventTapEnable(m_eventTap, true);

    std::cout << "[SkanCapture] Mouse event monitoring started successfully." << std::endl;
    std::cout << "[SkanCapture] Listening for mouse clicks (left + right button)." << std::endl;

    // Run the loop in intervals so we can check the running flag
    while (m_running.load()) {
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.25, false);
    }

    // Cleanup
    CGEventTapEnable(m_eventTap, false);
    CFRunLoopRemoveSource(m_runLoop, m_runLoopSource, kCFRunLoopCommonModes);
    CFRelease(m_runLoopSource);
    CFRelease(m_eventTap);
    m_runLoopSource = nullptr;
    m_eventTap = nullptr;
    m_runLoop = nullptr;
}

void MouseMonitor::stop() {
    if (!m_running.load()) return;

    m_running.store(false);

    // Wake the run loop so the thread can exit
    if (m_runLoop) {
        CFRunLoopStop(m_runLoop);
    }

    if (m_thread.joinable()) {
        m_thread.join();
    }

    std::cout << "[SkanCapture] Mouse event monitoring stopped." << std::endl;
}

bool MouseMonitor::isRunning() const {
    return m_running.load();
}
