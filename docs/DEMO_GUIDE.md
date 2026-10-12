# ScreenGuard — Demo Video Recording Guide & Script

This guide provides a structured, professional **3-to-5 minute demonstration script and walkthrough** to record for your evaluation.

---

## 📋 Pre-Recording Checklist

Before starting your screen recording:
1. Open a terminal window in the project folder:
   ```bash
   cd screenguard-pii-masker
   ```
2. Open your web browser to the interactive demo:
   * Local: `open demo/index.html`
   * Online: `https://deeps72-ux.github.io/screenguard-pii-masker/`
3. Have the output folder open in Finder (or VS Code) to show saved images:
   ```bash
   open app/output
   ```

---

## 🎬 Video Recording Script (Section by Section)

### Phase 1: Introduction & Architecture Overview (0:00 – 0:50)

* **What to Show on Screen:**
  Display [`README.md`](../README.md) or [`docs/HLD.md`](HLD.md) showing the dual-layer architecture diagram.
* **What to Say:**
  > *"Hello everyone! Today I'm presenting **ScreenGuard**, a production-grade macOS screen capture and real-time visual PII redaction engine.*
  >
  > *In enterprise intelligence and process capture, monitoring user actions is critical, but unredacted captures risk exposing sensitive data like SSNs, credit cards, and emails. ScreenGuard solves this with a high-performance, privacy-first dual-layer architecture:*
  >
  > *1. **C++ Native Layer (`libscreencapture.dylib`)**: Directly hooks into macOS Quartz Event Services to capture clicks session-wide, disambiguates **External Mouse vs. Built-in Trackpad**, enumerates graphics devices with the **Metal API**, and captures displays via **ScreenCaptureKit**.*
  >
  > *2. **C# .NET 8 Application Layer**: Receives raw pixel buffers via zero-copy P/Invoke callbacks, executes stream-based OCR, detects PII using regex and Luhn checksum validation, applies visual masks with crimson badges, and writes side-by-side comparison artifacts to disk."*

---

### Phase 2: Live Pipeline Execution & Benchmark (0:50 – 2:00)

* **What to Show on Screen:**
  Terminal window.
* **What to Do in Terminal:**
  Run the synthetic benchmark command:
  ```bash
  cd app && dotnet run -- --benchmark
  ```
* **What to Say:**
  > *"Let's execute the full end-to-end pipeline in our terminal using the `--benchmark` command.*
  >
  > *Notice the output in the terminal:*
  > * *The C++ native layer initializes and queries Apple's Metal API, detecting our Apple M1 Integrated GPU (iGPU).*
  > * *The IOKit HID classifier initializes our pointing devices.*
  > * *Our in-memory OCR engine processes the capture in under 35 milliseconds.*
  > * *8 PII entities are detected: Social Security Numbers, corporate emails, phone numbers, server IP addresses, and dates of birth.*
  > * *It automatically generates 4 artifacts on disk: the original capture, the redacted screenshot, a composite side-by-side comparison, and a compliance JSON report."*

* **What to Show on Screen:**
  Open the saved images in `app/output/`:
  1. Show `output/original/` (original screenshot with raw PII).
  2. Show `output/masked/` (clean black redaction bars with red borders and `[REDACTED: SSN]` badges).
  3. Show `output/comparison/` (composite view with original on left, masked on right, and metadata footer).
  4. Show `output/reports/` (open the JSON report showing exact bounding box coordinates).

---

### Phase 3: Interactive Web Showcase & Sandbox (2:00 – 3:15)

* **What to Show on Screen:**
  Browser showing `demo/index.html` (or the GitHub Pages deployment).
* **What to Demonstrate:**
  1. **Interactive Comparison Slider:**
     * Drag the slider back and forth:
       > *"Here is our interactive web showcase. Evaluators can drag this split-slider handle to dynamically inspect the redaction boundaries between the original screenshot and the masked frame."*
     * Click the **Side-by-Side Dual View** tab to show the full dual-canvas presentation.
  2. **Live In-Browser PII Sandbox:**
     * Click the preset buttons: **Employee HR**, **Financial**, and **Medical Patient**.
     * Type in a custom number like `123-45-6789` or credit card `4111 2222 3333 4444`.
     * Click **Scan & Redact PII**:
       > *"Our client-side engine matches the exact regex and Luhn algorithm rules of our C# backend, showing instant visual redaction in real time."*
  3. **Hardware & GPU Simulator:**
     * Click **Simulate Trackpad Click** &rarr; shows `kCGMouseEventSubtype: 3 (NX_SUBTYPE_MOUSE_TOUCH)`.
     * Click **Simulate External Mouse Click** &rarr; shows `kCGMouseEventSubtype: 0 (NX_SUBTYPE_DEFAULT)`.
     * Toggle the Metal GPU architecture buttons (Apple Silicon unified SoC vs. Intel Dual-GPU vs. Thunderbolt eGPU).

---

### Phase 4: High-Level (HLD) & Low-Level Design (LLD) (3:15 – 4:30)

* **What to Show on Screen:**
  Open [`docs/HLD.md`](HLD.md) and [`docs/LLD.md`](LLD.md).
* **What to Say:**
  > *"For architectural depth, we have documented complete High-Level and Low-Level Design specifications:*
  >
  > *In the **HLD**, we outline:*
  > * *The decoupled architecture ensuring C++ handles OS event taps on a dedicated background POSIX thread with its own `CFRunLoop`, completely eliminating UI stuttering.*
  > * *The multi-tier OCR strategy with fallback mechanisms.*
  > * *Strict local-only processing guaranteeing zero data leakage.*
  >
  > *In the **LLD**, we detail the precise 64-bit Darwin ABI memory layout:*
  > * *The `ScreenGuardCapture` struct is exactly 56 bytes, with padding fields at offset 12 and offset 28 ensuring 8-byte pointer and double alignment across C++ and C# P/Invoke.*
  > * *Our streaming OCR pipeline avoids writing temporary files to disk, piping PNG bytes in memory directly to Tesseract's `stdin` and parsing TSV coordinates from `stdout`.*
  > * *Our regex engine incorporates negative lookaheads for SSA invalid SSN prefixes and Luhn Modulo 10 verification for credit cards to prevent false positives.*
  > * *Memory lifecycle is strictly managed: C# frees the native heap using `ScreenGuardFreeCapture()` in a `finally` block to prevent leaks."*

---

### Phase 5: Conclusion & Wrap-Up (4:30 – 5:00)

* **What to Show on Screen:**
  Return to the GitHub repository page or README.
* **What to Say:**
  > *"To summarize:*
  > * *All requirements are fully implemented and verified.*
  > * *The project is open-source and hosted on GitHub under `Deeps72-ux/screenguard-pii-masker`.*
  > * *GitHub Actions CI automatically builds and deploys our interactive web demo on every push.*
  > * *Thank you for watching, and I look forward to your feedback!"*

---

## 💡 Quick Tips for Recording
* **Resolution:** Record at 1080p (1920x1080) or 1440p for clear text readability.
* **Audio:** Use a clean microphone with noise suppression if possible.
* **Pacing:** Pause for 1–2 seconds between terminal commands so viewers can read the logs.
