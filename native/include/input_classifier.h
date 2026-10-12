/**
 * @file input_classifier.h
 * @brief Hardware-level input device classification via IOKit HID Manager.
 *
 * Uses IOKit HID to enumerate and classify pointing devices:
 *   - External Mouse  (HID Usage 0x02 — kHIDUsage_GD_Mouse)
 *   - Trackpad        (HID Usage 0x05 — kHIDUsage_GD_TouchPad)
 *
 * Tracks which device type most recently generated button input,
 * allowing the mouse monitor to tag each click with its source device.
 */

#ifndef INPUT_CLASSIFIER_H
#define INPUT_CLASSIFIER_H

#include <IOKit/hid/IOHIDManager.h>
#include <atomic>
#include <mutex>
#include <string>
#include <vector>
#include <thread>

/**
 * Classified input source type.
 */
enum class InputSource {
    Unknown       = 0,
    ExternalMouse = 1,
    Trackpad      = 2
};

/**
 * Information about a connected HID pointing device.
 */
struct HIDDeviceInfo {
    IOHIDDeviceRef device;
    InputSource    type;
    std::string    name;
};

/**
 * Classifies input devices using IOKit HID Manager.
 *
 * Runs on a dedicated background thread with its own CFRunLoop.
 * When a button press is detected on a HID device, it records the
 * device type so the mouse monitor can query it.
 */
class InputClassifier {
public:
    InputClassifier();
    ~InputClassifier();

    // Non-copyable
    InputClassifier(const InputClassifier&) = delete;
    InputClassifier& operator=(const InputClassifier&) = delete;

    /**
     * Start the IOKit HID Manager on a background thread.
     * @return true if started successfully.
     */
    bool start();

    /**
     * Stop the HID Manager and join the background thread.
     */
    void stop();

    /**
     * Get the most recent input source type (thread-safe).
     * This is updated whenever a HID button press is detected.
     */
    InputSource getLastInputSource() const;

    /**
     * Manually record an input from a specific source type.
     */
    void recordInput(InputSource source);

    /**
     * Get a snapshot of all currently connected pointing devices.
     */
    std::vector<HIDDeviceInfo> getConnectedDevices() const;

private:
    // IOKit HID callbacks
    static void deviceMatchedCallback(void* context, IOReturn result,
                                       void* sender, IOHIDDeviceRef device);
    static void deviceRemovedCallback(void* context, IOReturn result,
                                       void* sender, IOHIDDeviceRef device);
    static void inputValueCallback(void* context, IOReturn result,
                                    void* sender, IOHIDValueRef value);

    InputSource classifyDevice(IOHIDDeviceRef device) const;
    std::string getDeviceName(IOHIDDeviceRef device) const;
    void runLoopThread();

    IOHIDManagerRef          m_hidManager = nullptr;
    CFRunLoopRef             m_runLoop    = nullptr;
    std::thread              m_thread;
    std::atomic<bool>        m_running{false};

    mutable std::mutex       m_devicesMutex;
    std::vector<HIDDeviceInfo> m_devices;

    std::atomic<InputSource> m_lastInputSource{InputSource::Unknown};
};

#endif /* INPUT_CLASSIFIER_H */
