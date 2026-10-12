/**
 * @file mouse_monitor.h
 * @brief Global mouse click event monitoring via Quartz Event Taps (CGEventTap).
 *
 * Runs a CGEventTap on a dedicated background thread to capture all mouse
 * click events system-wide. Requires Input Monitoring permission in
 * System Settings → Privacy & Security.
 */

#ifndef MOUSE_MONITOR_H
#define MOUSE_MONITOR_H

#include <functional>
#include <thread>
#include <atomic>
#include <cstdint>
#include <CoreGraphics/CoreGraphics.h>

/**
 * Represents a mouse click event captured by the event tap.
 */
struct MouseClickEvent {
    double   x;             /**< Screen X coordinate */
    double   y;             /**< Screen Y coordinate */
    int64_t  timestampMs;   /**< Unix epoch milliseconds */
    bool     isLeftClick;   /**< true = left click, false = right click */
    int64_t  subtype;       /**< kCGMouseEventSubtype: 3 for Touch/Trackpad, 0 for Mouse */
};

/**
 * Callback type for mouse click notifications.
 */
using MouseClickCallback = std::function<void(const MouseClickEvent&)>;

/**
 * Monitors global mouse click events using macOS CGEventTap.
 *
 * Usage:
 *   MouseMonitor monitor;
 *   monitor.start([](const MouseClickEvent& e) {
 *       // handle click
 *   });
 *   // ... later
 *   monitor.stop();
 */
class MouseMonitor {
public:
    MouseMonitor();
    ~MouseMonitor();

    // Non-copyable
    MouseMonitor(const MouseMonitor&) = delete;
    MouseMonitor& operator=(const MouseMonitor&) = delete;

    /**
     * Start monitoring mouse clicks on a background thread.
     *
     * @param callback  Function to invoke on each click event.
     * @return  true if started successfully, false on failure.
     */
    bool start(MouseClickCallback callback);

    /**
     * Stop monitoring and join the background thread.
     */
    void stop();

    /**
     * Check if the monitor is currently running.
     */
    bool isRunning() const;

private:
    static CGEventRef eventCallback(CGEventTapProxy proxy, CGEventType type,
                                     CGEventRef event, void* refcon);
    void runLoopThread();

    MouseClickCallback   m_callback;
    CFMachPortRef        m_eventTap      = nullptr;
    CFRunLoopSourceRef   m_runLoopSource = nullptr;
    CFRunLoopRef         m_runLoop       = nullptr;
    std::thread          m_thread;
    std::atomic<bool>    m_running{false};
};

#endif /* MOUSE_MONITOR_H */
