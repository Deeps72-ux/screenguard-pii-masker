using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace ScreenGuard.App.Platforms.Windows;

/// <summary>
/// Classifies clicks as Trackpad vs External Mouse using the Win32 Raw Input API.
///
/// The low-level mouse hook cannot tell a touchpad from a mouse: both arrive as ordinary
/// mouse input (MOUSEEVENTF_FROMTOUCH only marks touchscreen and pen). Raw Input can:
///   - Precision touchpads expose a HID digitizer collection (usage page 0x0D, usage 0x05)
///     that reports contacts; we record when the last contact report arrived.
///   - Real mice produce raw mouse button events tagged with their device handle.
///     Mouse input synthesized by Windows from a precision touchpad has no device handle.
///   - Legacy (non-precision) touchpads are recognised by well-known vendor ids in the
///     device name (Synaptics, ELAN, ALPS).
///
/// All methods must be called on the thread that owns the message loop (the hook thread).
/// </summary>
internal sealed class WindowsRawInputClassifier : IDisposable
{
    private const uint WM_INPUT = 0x00FF;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIM_TYPEMOUSE = 0;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const ushort RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001;
    private const ushort RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004;
    private const ushort HID_USAGE_PAGE_GENERIC = 0x01;
    private const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
    private const ushort HID_USAGE_PAGE_DIGITIZER = 0x0D;
    private const ushort HID_USAGE_DIGITIZER_TOUCH_PAD = 0x05;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private static readonly string[] LegacyTouchpadVendorTags = ["SYNA", "ELAN", "ALPS", "VID_06CB", "VID_04F3", "VID_044E"];

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceList(IntPtr pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand, IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
        int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private readonly WndProc _wndProc;
    private readonly string _className = "ScreenGuardRawInput_" + Guid.NewGuid().ToString("N");
    private readonly ConcurrentDictionary<IntPtr, bool> _isTouchpadDevice = new();
    private IntPtr _hwnd;
    private IntPtr _hInstance;

    // Environment.TickCount64 values of the most recent activity of each kind
    private long _lastTouchpadContactTick = long.MinValue / 2;
    private long _lastMouseButtonTick = long.MinValue / 2;
    private long _lastSynthesizedButtonTick = long.MinValue / 2;

    /// <summary>True when at least one touchpad (precision or legacy) is attached.</summary>
    public bool HasTouchpad { get; private set; }

    public WindowsRawInputClassifier()
    {
        _wndProc = WindowProc;
    }

    /// <summary>
    /// Creates a message-only window and registers for raw mouse and touchpad input.
    /// Returns false if Raw Input is unavailable; classification then falls back to "Unknown".
    /// </summary>
    public bool Start()
    {
        _hInstance = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
            hInstance = _hInstance,
            lpszClassName = _className
        };
        if (RegisterClassEx(ref wc) == 0)
            return false;

        _hwnd = CreateWindowEx(0, _className, "", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, _hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            return false;

        var devices = new[]
        {
            new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd },
            new RAWINPUTDEVICE { usUsagePage = HID_USAGE_PAGE_DIGITIZER, usUsage = HID_USAGE_DIGITIZER_TOUCH_PAD, dwFlags = RIDEV_INPUTSINK, hwndTarget = _hwnd }
        };
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            // Some systems reject the digitizer registration; mouse-only still helps
            if (!RegisterRawInputDevices([devices[0]], 1, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
                return false;
        }

        HasTouchpad = EnumerateTouchpads();
        return true;
    }

    /// <summary>
    /// Classify a click that happened at <paramref name="clickTick"/> (Environment.TickCount64).
    /// Call a few tens of milliseconds after the click so the matching raw input has arrived.
    /// </summary>
    public string Classify(long clickTick)
    {
        long mouse = Interlocked.Read(ref _lastMouseButtonTick);
        long synthesized = Interlocked.Read(ref _lastSynthesizedButtonTick);
        long touch = Interlocked.Read(ref _lastTouchpadContactTick);

        if (Math.Abs(mouse - clickTick) <= 150)
            return "External Mouse";
        if (Math.Abs(synthesized - clickTick) <= 150)
            return "Trackpad";
        if (clickTick - touch is >= -100 and <= 600)
            return "Trackpad";
        if (_hwnd == IntPtr.Zero)
            return "Unknown";
        return HasTouchpad ? "Unknown" : "External Mouse";
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_INPUT)
        {
            try { HandleRawInput(lParam); } catch { /* never let input processing throw */ }
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0 || size > 64 * 1024) return;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
            long now = Environment.TickCount64;

            if (header.dwType == RIM_TYPEHID)
            {
                if (IsTouchpad(header.hDevice))
                    Interlocked.Exchange(ref _lastTouchpadContactTick, now);
                return;
            }

            if (header.dwType != RIM_TYPEMOUSE)
                return;

            // RAWMOUSE: usFlags (2) + padding (2) + usButtonFlags (2) + usButtonData (2) ...
            ushort buttonFlags = (ushort)Marshal.ReadInt16(buffer, (int)headerSize + 4);
            if ((buttonFlags & (RI_MOUSE_LEFT_BUTTON_DOWN | RI_MOUSE_RIGHT_BUTTON_DOWN)) == 0)
                return;

            if (header.hDevice == IntPtr.Zero)
                Interlocked.Exchange(ref _lastSynthesizedButtonTick, now); // synthesized by Windows (precision touchpad)
            else if (IsTouchpad(header.hDevice))
                Interlocked.Exchange(ref _lastTouchpadContactTick, now);
            else
                Interlocked.Exchange(ref _lastMouseButtonTick, now);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool IsTouchpad(IntPtr hDevice) =>
        hDevice != IntPtr.Zero && _isTouchpadDevice.GetOrAdd(hDevice, ClassifyDevice);

    private static bool ClassifyDevice(IntPtr hDevice)
    {
        // Precision touchpad: HID digitizer collection with the touch pad usage
        uint infoSize = 32;
        IntPtr info = Marshal.AllocHGlobal((int)infoSize);
        try
        {
            Marshal.WriteInt32(info, 0, (int)infoSize); // RID_DEVICE_INFO.cbSize
            if (GetRawInputDeviceInfo(hDevice, RIDI_DEVICEINFO, info, ref infoSize) != unchecked((uint)-1))
            {
                uint type = (uint)Marshal.ReadInt32(info, 4);
                if (type == RIM_TYPEHID)
                {
                    // RID_DEVICE_INFO_HID: vendor(4) product(4) version(4) usagePage(2) usage(2)
                    ushort usagePage = (ushort)Marshal.ReadInt16(info, 20);
                    ushort usage = (ushort)Marshal.ReadInt16(info, 22);
                    if (usagePage == HID_USAGE_PAGE_DIGITIZER && usage == HID_USAGE_DIGITIZER_TOUCH_PAD)
                        return true;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(info);
        }

        // Legacy touchpads present as mice; recognise common touchpad vendors by device name
        string name = GetDeviceName(hDevice);
        return LegacyTouchpadVendorTags.Any(tag => name.Contains(tag, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetDeviceName(IntPtr hDevice)
    {
        uint chars = 0;
        GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, IntPtr.Zero, ref chars);
        if (chars == 0 || chars > 4096) return "";

        IntPtr buffer = Marshal.AllocHGlobal((int)chars * 2);
        try
        {
            return GetRawInputDeviceInfo(hDevice, RIDI_DEVICENAME, buffer, ref chars) == unchecked((uint)-1)
                ? ""
                : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private bool EnumerateTouchpads()
    {
        uint count = 0;
        uint entrySize = (uint)Marshal.SizeOf<RAWINPUTDEVICELIST>();
        GetRawInputDeviceList(IntPtr.Zero, ref count, entrySize);
        if (count == 0 || count > 1024) return false;

        IntPtr list = Marshal.AllocHGlobal((int)(count * entrySize));
        try
        {
            uint got = GetRawInputDeviceList(list, ref count, entrySize);
            if (got == unchecked((uint)-1)) return false;

            bool any = false;
            for (int i = 0; i < got; i++)
            {
                var entry = Marshal.PtrToStructure<RAWINPUTDEVICELIST>(list + (int)(i * entrySize));
                if ((entry.dwType == RIM_TYPEHID || entry.dwType == RIM_TYPEMOUSE) && IsTouchpad(entry.hDevice))
                    any = true;
            }
            return any;
        }
        finally
        {
            Marshal.FreeHGlobal(list);
        }
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero)
        {
            DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
        if (_hInstance != IntPtr.Zero)
        {
            UnregisterClass(_className, _hInstance);
            _hInstance = IntPtr.Zero;
        }
    }
}
