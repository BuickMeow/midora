using System.Runtime.InteropServices;

namespace Midora.Avalonia.Platform;

/// <summary>
/// Best-effort AppKit interop used only to vertically center the native macOS traffic
/// lights inside the taller custom title bar. Everything is optional: when the interop
/// is unavailable the native chrome is left exactly as AppKit laid it out.
/// </summary>
internal static class MacWindowChrome
{
    private const string ObjCLibrary = "/usr/lib/libobjc.A.dylib";
    private const double NativeTitleBarHeight = 28.0;

    [StructLayout(LayoutKind.Sequential)]
    private struct CGPoint
    {
        public double X;
        public double Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGSize
    {
        public double Width;
        public double Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect
    {
        public CGPoint Origin;
        public CGSize Size;
    }

    [DllImport(ObjCLibrary, EntryPoint = "sel_registerName")]
    private static extern IntPtr SelRegisterName(string name);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtr(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern IntPtr SendIntPtrNInt(IntPtr receiver, IntPtr selector, nint argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern byte SendByteNInt(IntPtr receiver, IntPtr selector, nint argument);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern CGRect SendCGRect(IntPtr receiver, IntPtr selector);

    [DllImport(ObjCLibrary, EntryPoint = "objc_msgSend")]
    private static extern void SendVoidCGRect(IntPtr receiver, IntPtr selector, CGRect rect);

    public static void CenterTrafficLights(nint nsWindow, double titleBarHeight)
    {
        if (!OperatingSystem.IsMacOS() ||
            RuntimeInformation.ProcessArchitecture != Architecture.Arm64 ||
            nsWindow == IntPtr.Zero ||
            titleBarHeight <= NativeTitleBarHeight)
        {
            return;
        }

        try
        {
            var respondsTo = SelRegisterName("respondsToSelector:");
            var standardButton = SelRegisterName("standardWindowButton:");
            if (SendByteNInt(nsWindow, respondsTo, standardButton) == 0)
            {
                return;
            }

            var frameSelector = SelRegisterName("frame");
            var setFrameSelector = SelRegisterName("setFrame:");
            var delta = (titleBarHeight - NativeTitleBarHeight) / 2.0;

            for (nint index = 0; index <= 2; index++)
            {
                var button = SendIntPtrNInt(nsWindow, standardButton, index);
                if (button == IntPtr.Zero)
                {
                    continue;
                }

                var frame = SendCGRect(button, frameSelector);
                frame.Origin.Y -= delta;
                SendVoidCGRect(button, setFrameSelector, frame);
            }
        }
        catch
        {
            // AppKit interop is cosmetic; never let it break window startup.
        }
    }
}
