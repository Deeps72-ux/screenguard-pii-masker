/**
 * @file input_classifier.cpp
 * @brief Implementation of hardware-level input device classification via IOKit HID.
 *
 * Uses IOHIDManager to:
 *   1. Enumerate all connected pointing devices (mice and trackpads)
 *   2. Classify each device based on its HID Usage code
 *   3. Track button press events to determine which device type was most recently active
 *
 * Device classification logic:
 *   - HID Usage 0x02 (Mouse) → ExternalMouse
 *   - HID Usage 0x05 (TouchPad) → Trackpad
 *   - HID Usage 0x01 (Pointer) with "Trackpad" in name → Trackpad
 *   - HID Usage 0x01 (Pointer) without "Trackpad" in name → ExternalMouse
 *
 * This allows the mouse monitor to tag each click event with its source device
 * (external mouse vs built-in/external trackpad).
 */

#include "input_classifier.h"
#include <iostream>
#include <algorithm>
#include <chrono>
#include <CoreFoundation/CoreFoundation.h>

InputClassifier::InputClassifier() {}

InputClassifier::~InputClassifier() {
    stop();
}

bool InputClassifier::start() {
    if (m_running.load()) return false;

    m_running.store(true);
    m_thread = std::thread(&InputClassifier::runLoopThread, this);

    // Wait for the thread to initialize
    std::this_thread::sleep_for(std::chrono::milliseconds(200));
    return m_hidManager != nullptr;
}

/**
 * Background thread that sets up the IOHIDManager and runs its CFRunLoop.
 *
 * Creates matching dictionaries for three device types:
 *   - Mouse (Generic Desktop, Usage 0x02)
 *   - Pointer (Generic Desktop, Usage 0x01) — some mice and trackpads report as this
 *   - TouchPad (Generic Desktop, Usage 0x05)
 */
void InputClassifier::runLoopThread() {
    m_hidManager = IOHIDManagerCreate(kCFAllocatorDefault, kIOHIDOptionsTypeNone);
    if (!m_hidManager) {
        std::cerr << "[SkanCapture] ERROR: Failed to create IOHIDManager." << std::endl;
        m_running.store(false);
        return;
    }

    int usagePage = kHIDPage_GenericDesktop;

    // --- Mouse matching dictionary (Usage 0x02) ---
    CFMutableDictionaryRef mouseDict = CFDictionaryCreateMutable(
        kCFAllocatorDefault, 0,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    CFNumberRef pageRef = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &usagePage);
    int mouseUsage = kHIDUsage_GD_Mouse;
    CFNumberRef mouseUsageRef = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &mouseUsage);
    CFDictionarySetValue(mouseDict, CFSTR(kIOHIDDeviceUsagePageKey), pageRef);
    CFDictionarySetValue(mouseDict, CFSTR(kIOHIDDeviceUsageKey), mouseUsageRef);

    // --- Pointer matching dictionary (Usage 0x01) ---
    CFMutableDictionaryRef pointerDict = CFDictionaryCreateMutable(
        kCFAllocatorDefault, 0,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    int pointerUsage = kHIDUsage_GD_Pointer;
    CFNumberRef pointerUsageRef = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &pointerUsage);
    CFDictionarySetValue(pointerDict, CFSTR(kIOHIDDeviceUsagePageKey), pageRef);
    CFDictionarySetValue(pointerDict, CFSTR(kIOHIDDeviceUsageKey), pointerUsageRef);

    // --- TouchPad matching dictionary (Usage 0x05) ---
    // Note: kHIDUsage_GD_TouchPad may not be defined in older SDKs; value is 0x05
    CFMutableDictionaryRef trackpadDict = CFDictionaryCreateMutable(
        kCFAllocatorDefault, 0,
        &kCFTypeDictionaryKeyCallBacks, &kCFTypeDictionaryValueCallBacks);
    int trackpadUsage = 0x05; // kHIDUsage_GD_TouchPad
    CFNumberRef trackpadUsageRef = CFNumberCreate(kCFAllocatorDefault, kCFNumberIntType, &trackpadUsage);
    CFDictionarySetValue(trackpadDict, CFSTR(kIOHIDDeviceUsagePageKey), pageRef);
    CFDictionarySetValue(trackpadDict, CFSTR(kIOHIDDeviceUsageKey), trackpadUsageRef);

    // Combine into an array of matching dictionaries
    CFDictionaryRef matchingDicts[] = { mouseDict, pointerDict, trackpadDict };
    CFArrayRef matchingArray = CFArrayCreate(kCFAllocatorDefault,
        (const void**)matchingDicts, 3, &kCFTypeArrayCallBacks);

    IOHIDManagerSetDeviceMatchingMultiple(m_hidManager, matchingArray);

    // Register callbacks for device connect/disconnect and input values
    IOHIDManagerRegisterDeviceMatchingCallback(m_hidManager, deviceMatchedCallback, this);
    IOHIDManagerRegisterDeviceRemovalCallback(m_hidManager, deviceRemovedCallback, this);
    IOHIDManagerRegisterInputValueCallback(m_hidManager, inputValueCallback, this);

    // Schedule on this thread's run loop
    m_runLoop = CFRunLoopGetCurrent();
    IOHIDManagerScheduleWithRunLoop(m_hidManager, m_runLoop, kCFRunLoopDefaultMode);
    IOHIDManagerOpen(m_hidManager, kIOHIDOptionsTypeNone);

    std::cout << "[SkanCapture] Input classifier started (IOKit HID Manager)." << std::endl;

    // Release CF objects (manager retains what it needs)
    CFRelease(matchingArray);
    CFRelease(mouseDict);
    CFRelease(pointerDict);
    CFRelease(trackpadDict);
    CFRelease(pageRef);
    CFRelease(mouseUsageRef);
    CFRelease(pointerUsageRef);
    CFRelease(trackpadUsageRef);

    // Run the loop in intervals to check the running flag
    while (m_running.load()) {
        CFRunLoopRunInMode(kCFRunLoopDefaultMode, 0.25, false);
    }

    // Cleanup
    IOHIDManagerUnscheduleFromRunLoop(m_hidManager, m_runLoop, kCFRunLoopDefaultMode);
    IOHIDManagerClose(m_hidManager, kIOHIDOptionsTypeNone);
    CFRelease(m_hidManager);
    m_hidManager = nullptr;
    m_runLoop = nullptr;
}

void InputClassifier::stop() {
    if (!m_running.load()) return;

    m_running.store(false);
    if (m_runLoop) {
        CFRunLoopStop(m_runLoop);
    }
    if (m_thread.joinable()) {
        m_thread.join();
    }

    std::cout << "[SkanCapture] Input classifier stopped." << std::endl;
}

InputSource InputClassifier::getLastInputSource() const {
    return m_lastInputSource.load();
}

void InputClassifier::recordInput(InputSource source) {
    m_lastInputSource.store(source);
}

std::vector<HIDDeviceInfo> InputClassifier::getConnectedDevices() const {
    std::lock_guard<std::mutex> lock(m_devicesMutex);
    return m_devices;
}

/**
 * Classify a HID device as ExternalMouse or Trackpad based on its Usage code
 * and product name.
 *
 * Classification priority:
 *   1. Usage 0x05 (TouchPad) → always Trackpad
 *   2. Product name contains "Trackpad" → Trackpad (Apple's built-in trackpad
 *      sometimes reports as Mouse/Pointer in HID but has "Trackpad" in name)
 *   3. Usage 0x02 (Mouse) or 0x01 (Pointer) → ExternalMouse
 */
InputSource InputClassifier::classifyDevice(IOHIDDeviceRef device) const {
    if (!device) return InputSource::Unknown;

    // Check the product name first (Apple's trackpad/mouse devices often have descriptive names)
    std::string name = getDeviceName(device);
    std::string lowerName = name;
    std::transform(lowerName.begin(), lowerName.end(), lowerName.begin(), ::tolower);

    if (lowerName.find("trackpad") != std::string::npos ||
        lowerName.find("touchpad") != std::string::npos) {
        return InputSource::Trackpad;
    }

    // Get the HID Usage value (check both DeviceUsage and PrimaryUsage)
    CFNumberRef usageRef = (CFNumberRef)IOHIDDeviceGetProperty(device, CFSTR(kIOHIDDeviceUsageKey));
    if (!usageRef) {
        usageRef = (CFNumberRef)IOHIDDeviceGetProperty(device, CFSTR(kIOHIDPrimaryUsageKey));
    }

    int usage = 0;
    if (usageRef) {
        CFNumberGetValue(usageRef, kCFNumberIntType, &usage);
    }

    // TouchPad usage (0x05) → definitely a trackpad
    if (usage == 0x05) {
        return InputSource::Trackpad;
    }

    // Mouse (0x02) or Pointer (0x01) usage
    if (usage == kHIDUsage_GD_Mouse || usage == kHIDUsage_GD_Pointer) {
        return InputSource::ExternalMouse;
    }

    // Name-based mouse heuristic
    if (lowerName.find("mouse") != std::string::npos) {
        return InputSource::ExternalMouse;
    }

    return InputSource::Unknown;
}

/**
 * Get the human-readable product name of a HID device.
 */
std::string InputClassifier::getDeviceName(IOHIDDeviceRef device) const {
    CFStringRef productRef = (CFStringRef)IOHIDDeviceGetProperty(device, CFSTR(kIOHIDProductKey));
    if (!productRef) return "Unknown Device";

    char name[256];
    if (CFStringGetCString(productRef, name, sizeof(name), kCFStringEncodingUTF8)) {
        return std::string(name);
    }
    return "Unknown Device";
}

/* ──────────────────────────────────────────────────────────────────────────── */
/*  IOKit HID Callbacks                                                        */
/* ──────────────────────────────────────────────────────────────────────────── */

/**
 * Called when a matching HID device is connected.
 * Adds the device to our tracked list and logs its classification.
 */
void InputClassifier::deviceMatchedCallback(void* context, IOReturn result,
                                             void* sender, IOHIDDeviceRef device) {
    (void)result;
    (void)sender;

    InputClassifier* self = static_cast<InputClassifier*>(context);

    HIDDeviceInfo info;
    info.device = device;
    info.type = self->classifyDevice(device);
    info.name = self->getDeviceName(device);

    {
        std::lock_guard<std::mutex> lock(self->m_devicesMutex);
        self->m_devices.push_back(info);
    }

    const char* typeStr = "Unknown";
    switch (info.type) {
        case InputSource::ExternalMouse: typeStr = "External Mouse"; break;
        case InputSource::Trackpad:      typeStr = "Trackpad";       break;
        default: break;
    }

    std::cout << "[SkanCapture] HID device connected: \"" << info.name
              << "\" → classified as: " << typeStr << std::endl;
}

/**
 * Called when a tracked HID device is disconnected.
 * Removes the device from our tracked list.
 */
void InputClassifier::deviceRemovedCallback(void* context, IOReturn result,
                                             void* sender, IOHIDDeviceRef device) {
    (void)result;
    (void)sender;

    InputClassifier* self = static_cast<InputClassifier*>(context);

    std::string removedName;
    {
        std::lock_guard<std::mutex> lock(self->m_devicesMutex);
        auto it = std::find_if(self->m_devices.begin(), self->m_devices.end(),
            [device](const HIDDeviceInfo& info) { return info.device == device; });

        if (it != self->m_devices.end()) {
            removedName = it->name;
            self->m_devices.erase(it);
        }
    }

    if (!removedName.empty()) {
        std::cout << "[SkanCapture] HID device disconnected: \"" << removedName << "\"" << std::endl;
    }
}

/**
 * Called for each HID input value (button press, axis movement, etc.).
 *
 * We only care about button presses (Usage Page 0x09 = Button, value = 1).
 * When a button press is detected, we record the device type so the mouse
 * monitor can query it when the corresponding CGEventTap callback fires.
 *
 * Timing: IOKit HID callbacks fire slightly before CGEventTap callbacks for
 * the same physical event, so the classification is available when needed.
 */
void InputClassifier::inputValueCallback(void* context, IOReturn result,
                                          void* sender, IOHIDValueRef value) {
    (void)result;
    (void)sender;

    InputClassifier* self = static_cast<InputClassifier*>(context);
    IOHIDElementRef element = IOHIDValueGetElement(value);

    // Only interested in button press events (Usage Page 0x09 = Button)
    uint32_t usagePage = IOHIDElementGetUsagePage(element);
    if (usagePage != kHIDPage_Button) return;

    // Only on button down (integer value = 1)
    CFIndex intValue = IOHIDValueGetIntegerValue(value);
    if (intValue != 1) return;

    // Classify the device that generated this button press
    IOHIDDeviceRef device = IOHIDElementGetDevice(element);
    InputSource source = self->classifyDevice(device);
    self->recordInput(source);
}
