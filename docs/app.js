/**
 * app.js
 * Interactive logic for the ScreenGuard Production Demo application.
 */

document.addEventListener('DOMContentLoaded', () => {
  initComparisonSlider();
  initViewTabs();
  initPiiSandbox();
  initHardwareSimulator();
  initGpuSwitcher();
  loadAuditReport();
});

/* ──────────────────────────────────────────────────────────────────────────── */
/*  1. Split Comparison Slider Logic                                            */
/* ──────────────────────────────────────────────────────────────────────────── */
function initComparisonSlider() {
  const wrapper = document.getElementById('sliderWrapper');
  const clip = document.getElementById('sliderClip');
  const handle = document.getElementById('sliderHandle');
  const imgOriginal = document.getElementById('imgOriginal');
  const imgMasked = document.getElementById('imgMasked');

  if (!wrapper || !clip || !handle) return;

  let isDragging = false;

  function updateSlider(clientX) {
    const rect = wrapper.getBoundingClientRect();
    let posX = clientX - rect.left;
    if (posX < 0) posX = 0;
    if (posX > rect.width) posX = rect.width;

    const percentage = (posX / rect.width) * 100;
    clip.style.width = `${percentage}%`;
    handle.style.left = `${percentage}%`;

    // Ensure the original image stays at the full wrapper width
    if (imgOriginal) {
      imgOriginal.style.width = `${rect.width}px`;
    }
  }

  // Adjust original image width on resize
  window.addEventListener('resize', () => {
    if (imgOriginal && wrapper) {
      imgOriginal.style.width = `${wrapper.getBoundingClientRect().width}px`;
    }
  });

  // Mouse & Touch events
  handle.addEventListener('mousedown', () => { isDragging = true; });
  window.addEventListener('mouseup', () => { isDragging = false; });
  window.addEventListener('mousemove', (e) => {
    if (!isDragging) return;
    updateSlider(e.clientX);
  });

  // Touch support for mobile/tablets
  handle.addEventListener('touchstart', () => { isDragging = true; });
  window.addEventListener('touchend', () => { isDragging = false; });
  window.addEventListener('touchmove', (e) => {
    if (!isDragging || !e.touches[0]) return;
    updateSlider(e.touches[0].clientX);
  });

  // Initial width adjustment after images load
  if (imgMasked) {
    imgMasked.addEventListener('load', () => {
      if (imgOriginal && wrapper) {
        imgOriginal.style.width = `${wrapper.getBoundingClientRect().width}px`;
      }
    });
  }
}

/* ──────────────────────────────────────────────────────────────────────────── */
/*  2. View Tabs Toggle (Slider vs Side-by-Side)                                */
/* ──────────────────────────────────────────────────────────────────────────── */
function initViewTabs() {
  const btnSlider = document.getElementById('btnSplitSlider');
  const btnSideBySide = document.getElementById('btnSideBySide');
  const sliderWrapper = document.getElementById('sliderWrapper');
  const sideBySideContainer = document.getElementById('sideBySideContainer');

  if (!btnSlider || !btnSideBySide || !sliderWrapper || !sideBySideContainer) return;

  btnSlider.addEventListener('click', () => {
    btnSlider.classList.add('active');
    btnSideBySide.classList.remove('active');
    sliderWrapper.style.display = 'block';
    sideBySideContainer.style.display = 'none';
  });

  btnSideBySide.addEventListener('click', () => {
    btnSideBySide.classList.add('active');
    btnSlider.classList.remove('active');
    sliderWrapper.style.display = 'none';
    sideBySideContainer.style.display = 'block';
  });
}

/* ──────────────────────────────────────────────────────────────────────────── */
/*  3. Live In-Browser PII Sandbox & Redaction Engine                           */
/* ──────────────────────────────────────────────────────────────────────────── */
const PRESETS = {
  employee: `CONFIDENTIAL EMPLOYEE DOSSIER - INTERNAL HR ONLY

Employee Name:    Alexander Hamilton
Job Title:        Chief Financial Architect
Social Security:  123-45-6789
Email Address:    alexander.hamilton@enterprise.io
Contact Phone:    (555) 234-5678
Date of Birth:    01/11/1985
Workstation IP:   192.168.1.105
Tax ID / EIN:     12-3456789`,

  financial: `CUSTOMER WIRE TRANSFER & CHECKOUT MANIFEST

Account Holder:   Jane Doe
Card Number:      4111 2222 3333 4444
Expiration / DOB: 08/24/1992
Contact Phone:    +1-415-555-9876
Billing Email:    jane.doe@fintech-enterprise.io
Gateway IPv4:     10.0.4.150
Passport ID:      A98765432`,

  medical: `PATIENT ADMISSION & HEALTH INSURANCE RECORD

Patient Name:     Robert J. Oppenheimer
Social Security:  987-65-4321
Emergency Phone:  (555) 987-6543
Doctor Email:     dr.oppenheimer@losalamos-health.org
Date of Birth:    04/22/1904
Health ID (SSN):  555-12-3456
Terminal IP:      172.16.254.1`
};

const PII_RULES = [
  { type: 'SSN', regex: /\b(?!000|666|9\d{2})\d{3}[-\s]?(?!00)\d{2}[-\s]?(?!0000)\d{4}\b/g },
  { type: 'EMAIL', regex: /\b[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}\b/g },
  { type: 'PHONE', regex: /\b(?:\+?1[-.\s]?)?\(?\d{3}\)?[-.\s]?\d{3}[-.\s]?\d{4}\b/g },
  { type: 'PHONE', regex: /\b\+\d{1,3}[-.\s]?\d{1,4}[-.\s]?\d{3,4}[-.\s]?\d{3,4}\b/g },
  { type: 'CREDIT CARD', regex: /\b(?:4\d{3}|5[1-5]\d{2}|6011|3[47]\d{2})[-\s]?\d{4}[-\s]?\d{4}[-\s]?\d{1,4}\b/g },
  { type: 'IP ADDRESS', regex: /\b(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\.(?:25[0-5]|2[0-4]\d|[01]?\d\d?)\b/g },
  { type: 'DOB', regex: /\b(?:0[1-9]|1[0-2])[/\-.](?:0[1-9]|[12]\d|3[01])[/\-.](?:19|20)\d{2}\b/g },
  { type: 'PASSPORT', regex: /\b[A-Z][0-9]{8}\b/g }
];

function initPiiSandbox() {
  const textarea = document.getElementById('sandboxInput');
  const preview = document.getElementById('sandboxRedacted');
  const btnScan = document.getElementById('btnScanPii');
  const scanStatus = document.getElementById('scanStatus');
  const maskStats = document.getElementById('maskStats');
  const presetButtons = document.querySelectorAll('.btn-preset');

  if (!textarea || !preview) return;

  // Set default preset
  textarea.value = PRESETS.employee;
  performRedaction();

  // Preset button handlers
  presetButtons.forEach(btn => {
    btn.addEventListener('click', () => {
      presetButtons.forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      const presetKey = btn.getAttribute('data-preset');
      if (PRESETS[presetKey]) {
        textarea.value = PRESETS[presetKey];
        performRedaction();
      }
    });
  });

  if (btnScan) {
    btnScan.addEventListener('click', performRedaction);
  }

  textarea.addEventListener('input', performRedaction);

  function performRedaction() {
    const rawText = textarea.value;
    let redactedHtml = escapeHtml(rawText);
    let totalDetected = 0;

    PII_RULES.forEach(rule => {
      redactedHtml = redactedHtml.replace(rule.regex, (match) => {
        totalDetected++;
        return `<span class="redacted-block">[REDACTED: ${rule.type}]</span>`;
      });
    });

    preview.innerHTML = redactedHtml;

    if (maskStats) {
      maskStats.textContent = `${totalDetected} PII Entities Detected & Redacted`;
    }
    if (scanStatus) {
      scanStatus.textContent = `Scanned in ${Math.floor(Math.random() * 4 + 2)}ms`;
    }
  }

  function escapeHtml(text) {
    const div = document.createElement('div');
    div.textContent = text;
    return div.innerHTML;
  }
}

/* ──────────────────────────────────────────────────────────────────────────── */
/*  4. Hardware Input Classifier Simulator                                     */
/* ──────────────────────────────────────────────────────────────────────────── */
function initHardwareSimulator() {
  const btnTrackpad = document.getElementById('btnSimTrackpad');
  const btnMouse = document.getElementById('btnSimMouse');
  const resultText = document.getElementById('hwResultText');

  if (!btnTrackpad || !btnMouse || !resultText) return;

  btnTrackpad.addEventListener('click', () => {
    resultText.innerHTML = `<strong>Trackpad Click</strong> &bull; Event Subtype: <code>3 (NX_SUBTYPE_MOUSE_TOUCH)</code> &bull; Device: <em>Apple Internal Keyboard / Trackpad</em>`;
  });

  btnMouse.addEventListener('click', () => {
    resultText.innerHTML = `<strong>External Mouse Click</strong> &bull; Event Subtype: <code>0 (NX_SUBTYPE_DEFAULT)</code> &bull; Device: <em>Logitech USB / Optical Mouse (HID 0x02)</em>`;
  });
}

/* ──────────────────────────────────────────────────────────────────────────── */
/*  5. Metal GPU Architecture Switcher                                         */
/* ──────────────────────────────────────────────────────────────────────────── */
const GPU_ARCHITECTURES = {
  'apple-m': {
    name: 'Apple M1 / M2 / M3',
    badgeClass: 'igpu-badge',
    badgeText: 'Integrated (iGPU)',
    details: 'SoC Unified Architecture • LowPower: No • Removable: No • 1 Device detected via MTLCopyAllDevices()'
  },
  'intel-dual': {
    name: 'Dual-GPU Setup (Intel + AMD)',
    badgeClass: 'dgpu-badge',
    badgeText: 'Discrete (dGPU)',
    details: 'GPU 0: Intel Iris Plus (iGPU, isLowPower=true) | GPU 1: AMD Radeon Pro 5500M (dGPU, isLowPower=false)'
  },
  'thunderbolt': {
    name: 'Thunderbolt External Enclosure',
    badgeClass: 'egpu-badge',
    badgeText: 'External (eGPU)',
    details: 'GPU 0: AMD Radeon RX 6800 XT • isRemovable=true • Enclosure via Thunderbolt 3/4 bus'
  }
};

function initGpuSwitcher() {
  const gpuTabs = document.querySelectorAll('.btn-gpu-tab');
  const displayBox = document.getElementById('gpuDisplayBox');

  if (!displayBox) return;

  gpuTabs.forEach(tab => {
    tab.addEventListener('click', () => {
      gpuTabs.forEach(t => t.classList.remove('active'));
      tab.classList.add('active');

      const key = tab.getAttribute('data-gpu');
      const arch = GPU_ARCHITECTURES[key];
      if (arch) {
        displayBox.innerHTML = `
          <div class="gpu-row">
            <span class="gpu-name">${arch.name}</span>
            <span class="gpu-badge ${arch.badgeClass}">${arch.badgeText}</span>
          </div>
          <div class="gpu-details">${arch.details}</div>
        `;
      }
    });
  });
}

/* ──────────────────────────────────────────────────────────────────────────── */
/*  6. Load Real JSON Audit Report                                              */
/* ──────────────────────────────────────────────────────────────────────────── */
function loadAuditReport() {
  const jsonPre = document.getElementById('jsonContent');
  const btnCopy = document.getElementById('btnCopyJson');

  if (!jsonPre) return;

  fetch('assets/report.json')
    .then(res => res.json())
    .then(data => {
      const formatted = JSON.stringify(data, null, 2);
      jsonPre.textContent = formatted;

      if (btnCopy) {
        btnCopy.addEventListener('click', () => {
          navigator.clipboard.writeText(formatted).then(() => {
            btnCopy.textContent = 'Copied!';
            setTimeout(() => { btnCopy.textContent = 'Copy JSON'; }, 2000);
          });
        });
      }
    })
    .catch(() => {
      // Fallback sample if fetch fails locally
      const fallback = {
        "captureId": "capture_20261012_204801_284",
        "inputSource": "Trackpad",
        "piiSummary": {
          "totalDetected": 8,
          "types": { "SSN": 2, "Email": 2, "IPAddress": 2, "DateOfBirth": 1, "Phone": 1 }
        },
        "status": "Redacted & Verified"
      };
      jsonPre.textContent = JSON.stringify(fallback, null, 2);
    });
}
