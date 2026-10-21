# ScreenGuard — Screen Capture & PII Masker

[![CI & GitHub Pages Deployment](https://github.com/Deeps72-ux/screenguard-pii-masker/actions/workflows/deploy.yml/badge.svg)](https://github.com/Deeps72-ux/screenguard-pii-masker/actions)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![Platform](https://img.shields.io/badge/Platform-macOS%2012.3%2B%20%7C%20Windows%2010%2F11-black.svg)]()
[![.NET 8](https://img.shields.io/badge/.NET-8.0%20LTS-purple.svg)]()
[![Live Demo](https://img.shields.io/badge/Live%20Demo-GitHub%20Pages-00F0FF.svg)](https://deeps72-ux.github.io/screenguard-pii-masker/)

A production-grade macOS screen capture engine that monitors global mouse click events, classifies pointing hardware (**External Mouse vs Built-in Trackpad**), enumerates graphics devices via the Metal API (**Integrated iGPU vs Discrete dGPU vs External eGPU**), extracts text via in-memory streaming OCR, detects sensitive personal information (PII), and applies visual redaction with side-by-side comparison artifacts.

---

## 📚 Technical Documentation & Demo

| Document | Purpose | Link |
| :--- | :--- | :--- |
| **📐 High-Level Design (HLD)** | System architecture, NFRs, component decomposition, security | [docs/HLD.md](docs/HLD.md) |
| **🔬 Low-Level Design (LLD)** | 64-bit struct ABI alignment, class diagrams, regex state machines, streaming OCR | [docs/LLD.md](docs/LLD.md) |
| **🎥 Demo Video Recording Guide** | Step-by-step 3-5 min presentation script and demo commands | [docs/DEMO_GUIDE.md](docs/DEMO_GUIDE.md) |
| **🌐 Interactive Web Demo** | Live split-slider, in-browser PII sandbox, hardware simulator | [Live GitHub Pages Demo](https://deeps72-ux.github.io/screenguard-pii-masker/) |

---

## 🏗️ Architecture Overview

The system employs a dual-layer native/managed architecture:
- **C++ Native Core (`libscreencapture.dylib`)**: Handles low-level OS event taps, hardware classification, Metal GPU enumeration, and high-performance ScreenCaptureKit frame capture.
- **C# Application Layer (`ScreenGuard.App`)**: High-productivity orchestrator handling raw pixel marshaling, OCR extraction, regex PII detection, visual redaction painting, side-by-side comparison composites, and JSON audit logging.

```
┌─────────────────────────────────────────────────────────────────────────────┐
│                       C++ Native Layer (libscreencapture.dylib)             │
│                                                                             │
│   ┌────────────────────┐     ┌───────────────────────┐                      │
│   │ CGEventTap Monitor │ ──> │ Hardware Classifier   │                      │
│   │ (kCGSessionEvent)  │     │ • kCGMouseEventSubtype│                      │
│   └────────────────────┘     │ • IOKit HID Manager   │                      │
│             │                └───────────────────────┘                      │
│             ▼                                                               │
│   ┌────────────────────┐     ┌───────────────────────┐                      │
│   │ ScreenCaptureKit   │     │ Metal GPU Enumerator  │                      │
│   │ (SCScreenshotMgr)  │     │ (MTLCopyAllDevices)   │                      │
│   └────────────────────┘     └───────────────────────┘                      │
│             │                                                               │
│             └───────────────┬───────────────────────────────────────────────┘
│                             │ Exported C API (P/Invoke Function Pointer)
│                             ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│                       C# Application Layer (.NET 8)                         │
│                                                                             │
│   ┌────────────────────┐     ┌───────────────────────┐                      │
│   │ NativeInterop      │ ──> │ ImageConverter        │                      │
│   │ (56-byte struct)   │     │ (BGRA → SKBitmap)     │                      │
│   └────────────────────┘     └───────────────────────┘                      │
│                                          │                                  │
│                                          ▼                                  │
│   ┌────────────────────┐     ┌───────────────────────┐                      │
│   │ PiiDetector        │ <── │ OcrEngine             │                      │
│   │ (SSN, Email, CC)   │     │ (Stdin Streamed TSV)  │                      │
│   └────────────────────┘     └───────────────────────┘                      │
│             │                                                               │
│             ▼                                                               │
│   ┌────────────────────┐     ┌───────────────────────┐                      │
│   │ PiiMasker          │ ──> │ ImageComparer         │                      │
│   │ (Black Bar+Badges) │     │ (Side-by-Side + JSON) │                      │
│   └────────────────────┘     └───────────────────────┘                      │
│                                          │                                  │
│                                          ▼                                  │
│                              ┌───────────────────────┐                      │
│                              │ DiskWriter            │                      │
│                              │ {output}/original/    │                      │
│                              │ {output}/masked/      │                      │
│                              │ {output}/comparison/  │                      │
│                              │ {output}/reports/     │                      │
│                              └───────────────────────┘                      │
└─────────────────────────────────────────────────────────────────────────────┘
```

---

## 🎯 Key Capabilities & Technical Decisions

### 1. Global Mouse Click Event Tap (C++ Layer)
* Implemented in `native/src/mouse_monitor.cpp` using `CGEventTapCreate(kCGSessionEventTap, kCGHeadInsertEventTap, kCGEventTapOptionListenOnly, ...)`.
* Runs on a dedicated background thread with its own `CFRunLoop` so it never blocks the main UI.
* Captures both left and right mouse clicks system-wide.

### 2. External Mouse vs. Trackpad Classification
* **Primary Hardware Indicator:** Inspects `kCGMouseEventSubtype` on the Quartz event:
  * `3` (`NX_SUBTYPE_MOUSE_TOUCH`): Touchpad / Multitouch / Magic Trackpad event.
  * `0` (`NX_SUBTYPE_DEFAULT`): Physical external mouse event.
* **Secondary IOKit HID Classifier:** In `native/src/input_classifier.cpp`, monitors device matching dictionaries:
  * `kHIDUsage_GD_TouchPad` (`0x05`) or device product name containing `"Trackpad"` (e.g. `Apple Internal Keyboard / Trackpad`).
  * `kHIDUsage_GD_Mouse` (`0x02`) or `"Mouse"`.

### 3. GPU Detection (Metal API: iGPU vs dGPU)
* Implemented in `native/src/gpu_detector.mm` using Apple's Metal framework (`MTLCopyAllDevices()`).
* Classifies processors using device capabilities:
  * `isLowPower == true` or Apple Silicon unified SoC &rarr; **Integrated GPU (iGPU)**.
  * Multiple devices with `isLowPower == false` &rarr; **Discrete GPU (dGPU)**.
  * `isRemovable == true` (Thunderbolt eGPU enclosure) &rarr; **External GPU (eGPU)**.

### 4. P/Invoke 64-Bit Struct Alignment (56 Bytes)
Memory alignment between C++ `ScreenGuardCapture` and C# `ScreenGuardCaptureStruct` matches exactly:
```
Offset 0x00: Width       (4 bytes)
Offset 0x04: Height      (4 bytes)
Offset 0x08: Stride      (4 bytes)
Offset 0x0C: [Padding]   (4 bytes for 8-byte pointer alignment)
Offset 0x10: PixelData   (8 bytes - uint8_t* / IntPtr)
Offset 0x18: InputSource (4 bytes)
Offset 0x1C: [Padding]   (4 bytes for 8-byte double alignment)
Offset 0x20: ClickX      (8 bytes - double)
Offset 0x28: ClickY      (8 bytes - double)
Offset 0x30: TimestampMs (8 bytes - int64)
Total: 56 bytes
```

### 5. Stream-Based OCR Engine (Zero Temp Files)
* Uses Tesseract with Apple Silicon NEON optimization.
* Pipes raw PNG bytes in-memory directly to Tesseract's `stdin` and parses TSV word coordinates from `stdout`.
* Avoids disk round-trips and `/tmp` symlink permissions issues.

### 6. PII Detection Rules
* **SSN:** SSA range-validated format (`\b(?!000|666|9\d{2})\d{3}[-\s]?(?!00)\d{2}[-\s]?(?!0000)\d{4}\b`).
* **Email:** RFC 5322 pattern.
* **Phone Numbers:** US domestic and international E.164 formats.
* **Credit Card Numbers:** Visa, Mastercard, AMEX, Discover with **Luhn Checksum Algorithm** verification.
* **IP Addresses:** IPv4 octet validation (`0.0.0.0` - `255.255.255.255`).
* **Date of Birth:** `MM/DD/YYYY`, `YYYY-MM-DD`.
* **Multi-Token Window:** Catches hyphenated/spaced tokens split across OCR words.

### 7. Visual Redaction & Side-by-Side Comparison
* Non-destructive deep copy via SkiaSharp.
* Solid black block + crimson outline (`#E53935`) + audit badge (`[REDACTED: SSN]`).
* Side-by-side composite comparison image (`capture_{timestamp}_comparison.png`).
* Machine-readable JSON audit report (`capture_{timestamp}_report.json`).

---

## 🚀 Quick Start Guide

### Prerequisites
* macOS 12.3+ (ScreenCaptureKit screenshots on 14+, Core Graphics fallback on 12.3–13)
* Xcode Command Line Tools (`xcode-select --install`)
* CMake 3.20+ (`brew install cmake`)
* .NET 8 SDK (`brew install dotnet@8`)
* Tesseract (`brew install tesseract`)

### 1. Clone & Build
```bash
git clone https://github.com/Deeps72-ux/screenguard-pii-masker.git
cd screenguard-pii-masker

# Build the C++ native dylib
./scripts/build_native.sh

# Download the on-device name-recognition model (~109 MB, pinned + checksum-verified)
./scripts/download_models.sh

# Build the .NET application
cd app
dotnet build -c Release
```

### 2. Run Modes

#### Synthetic Benchmark Mode (Recommended for instant testing)
Generates a realistic test capture with 8 confidential customer PII records, executes the full OCR & redaction pipeline, and saves all comparison artifacts:
```bash
dotnet run -- --benchmark
```

#### Live Interactive Monitor Mode
Listens to live mouse clicks (External Mouse or Trackpad), captures the display, and redacts PII on every click:
```bash
dotnet run
```
*(Requires **Input Monitoring** and **Screen Recording** for your terminal (or ScreenGuard.app) in **System Settings &rarr; Privacy & Security**; quit and reopen the terminal after granting. The app checks both at startup and exits with a clear message if either is missing.)*

Clicks are captured on a background worker and processed from a queue, so bursts of clicks never stall the system click listener. The display under the cursor is captured at its native pixel resolution (e.g. 2560×1600 on a Retina panel), and click coordinates in reports are in that image's pixels.

#### Single Test Capture
```bash
dotnet run -- --test
```

#### Scan an Existing Image
```bash
dotnet run -- --scan path/to/screenshot.png
```

#### Other Options
```bash
dotnet run -- --output ./my-output    # custom output directory (also accepted as a bare argument)
dotnet run -- --no-original           # do not keep the unmasked original screenshot
```

### Redaction Safety
* PII patterns match within a single OCR text line and mask only the matched value (labels such as `SSN:` stay readable).
* Card numbers next to a "Credit Card" / "Card Number" label are masked even if the Luhn checksum fails; unlabelled numbers must pass Luhn.
* If OCR is unavailable or fails for a capture, the masked output redacts the **entire frame** (`"redactionMode": "full-frame"` in the report) rather than passing PII through.
* A field whose label is readable but whose value OCR garbled (text selection, caret, cursor) is still masked, including labels clipped at the left screen edge.
* OCR runs twice per capture (as captured, and a high-contrast copy) to recover text inside selection highlights.

### Person Names (on-device NER)
Names with a label (`Customer Name:`) or title (`Dr.`) are matched by patterns. Free-standing names — a LinkedIn feed, a chat list, an email header — are found by a BERT named-entity model ([dslim/bert-base-NER](https://huggingface.co/dslim/bert-base-NER), int8 ONNX, MIT) running locally through ONNX Runtime on macOS and Windows. Only person names are masked; companies and places are left visible. The model adds ~0.1–0.7 s per capture.

If the model is missing the app prints a warning and falls back to label/title-based names (`"nameRecognition": "labels-only"` in the report).

**Known limits:** a lone first name in an ambiguous context (e.g. `Manas` in a sentence fragment) may be missed; occasional false positives on product names (e.g. `Ray`). Values whose label is entirely off-screen *and* that are themselves partial (e.g. `…5-6789`) cannot be recognised as PII.

---

## 📂 Artifact Output Structure

```
output/
├── original/       # Raw unmodified screenshots (PNG)
├── masked/         # Redacted screenshots with PII black blocks
├── comparison/     # Side-by-side composite comparison images
└── reports/        # Full JSON audit reports with coordinates
```

### Sample Audit Report JSON
```json
{
  "captureId": "capture_20261012_204801_284",
  "inputSource": "Trackpad",
  "clickCoordinates": { "x": 600, "y": 350 },
  "dimensions": { "width": 1200, "height": 700 },
  "piiSummary": {
    "totalDetected": 8,
    "types": { "SSN": 2, "Email": 2, "IPAddress": 2, "DateOfBirth": 1, "Phone": 1 }
  },
  "piiRegions": [
    {
      "type": "SSN",
      "maskedPreview": "12*******89",
      "boundingBox": { "x": 51, "y": 115, "width": 346, "height": 69 }
    }
  ]
}
```

---

## 📦 Packaging & GitHub Releases Distribution

ScreenGuard is distributed as standalone, self-contained packages for both **macOS** and **Microsoft Windows (x64)**. The macOS app bundles the .NET runtime, the Tesseract engine (no Homebrew needed) and the name-recognition model. The Windows package bundles the .NET runtime and Tesseract; OCR additionally needs the [Microsoft Visual C++ 2015–2022 Redistributable (x64)](https://aka.ms/vs/17/release/vc_redist.x64.exe), which most Windows machines already have.

### 1. Build Local Release Packages
```bash
# Package for macOS (Apple Silicon arm64 .app, .dmg, .zip)
./scripts/package.sh arm64 1.0.0

# Intel Macs: run on an Intel Mac with Homebrew tesseract installed
./scripts/package.sh x64 1.0.0

# Package for Windows (win-x64 standalone .zip with ScreenGuard.exe)
./scripts/package_windows.sh 1.0.0

# Generated artifacts in dist/:
#   • dist/ScreenGuard.app
#   • dist/ScreenGuard-macOS-arm64-v1.0.0.dmg
#   • dist/ScreenGuard-macOS-arm64-v1.0.0.zip
#   • dist/ScreenGuard-Windows-x64-v1.0.0.zip
```

### 2. Automated Distribution via GitHub Actions
Creating and pushing a Git tag triggers the [.github/workflows/release.yml](.github/workflows/release.yml) workflow:
```bash
git tag v1.0.0
git push origin v1.0.0
```
This builds the macOS (arm64 DMG + ZIP) package on a macOS runner and the Windows (x64 ZIP) package on a Windows runner, runs the OCR/masking benchmark on each (and a live GDI capture on Windows), and attaches the packages to the GitHub Release only if verification passes.

---

## 🏛️ SOLID Architecture & Platform Abstraction

ScreenGuard strictly implements the **SOLID** software engineering principles:
* **Single Responsibility (SRP):** Distinct services for capture, OCR extraction, PII pattern detection, image redaction, side-by-side comparison, and disk writing.
* **Open/Closed (OCP):** Open to new operating systems or capture backends without modifying existing domain logic.
* **Liskov Substitution (LSP):** `WindowsGlobalInputMonitor` and `MacGlobalInputMonitor` seamlessly substitute `IGlobalInputMonitor`.
* **Interface Segregation (ISP):** Small, focused interfaces (`IScreenCaptureService`, `IGlobalInputMonitor`, `IGpuDetector`, `IPiiDetector`, `IPiiMasker`, `IImageComparer`, `IDiskWriter`).
* **Dependency Inversion (DIP):** High-level orchestrators depend strictly on abstractions via `PlatformFactory`.

| Operating System | Frame Capture Provider | Global Input Monitor | GPU Hardware Detector |
| :--- | :--- | :--- | :--- |
| **macOS 12.3+** | `MacScreenCaptureService` (ScreenCaptureKit, per-display, native resolution) | `MacGlobalInputMonitor` (CGEventTap + IOKit HID) | `MacGpuDetector` (Metal API) |
| **Windows 10/11** | `WindowsScreenCaptureService` (Win32 GDI / BitBlt, per-monitor DPI aware) | `WindowsGlobalInputMonitor` (SetWindowsHookEx + Raw Input touchpad detection) | `WindowsGpuDetector` (WMI) |

## 👥 Author & Repository
* **GitHub:** [@Deeps72-ux](https://github.com/Deeps72-ux)
* **Repository:** [https://github.com/Deeps72-ux/screenguard-pii-masker](https://github.com/Deeps72-ux/screenguard-pii-masker)
* **Live Demo:** [https://deeps72-ux.github.io/screenguard-pii-masker/](https://deeps72-ux.github.io/screenguard-pii-masker/)
* **High-Level Design:** [docs/HLD.md](docs/HLD.md)
* **Low-Level Design:** [docs/LLD.md](docs/LLD.md)
* **Demo Recording Script:** [docs/DEMO_GUIDE.md](docs/DEMO_GUIDE.md)
