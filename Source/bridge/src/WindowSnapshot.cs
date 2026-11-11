using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using SolidWorks.Interop.sldworks;

namespace IsolatedSwBridge
{
    internal sealed partial class CadAdapter
    {
        [StructLayout(LayoutKind.Sequential)] private struct WindowBounds { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowBounds bounds);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr window, IntPtr context, uint flags);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        private Dictionary<string, object> CaptureWindow(DocEntry document)
        {
            // GetWindowRect must use physical pixels, matching PrintWindow's bitmap output.
            // Restore the bridge thread's previous DPI context immediately after capture.
            IntPtr previous = SetThreadDpiAwarenessContext(new IntPtr(-4));
            try { return CapturePhysicalWindow(document); }
            finally { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        }
        private Dictionary<string, object> CapturePhysicalWindow(DocEntry document)
        {
            if (!Object.ReferenceEquals(app.IActiveDoc2, document.Doc))
                throw new Fault("VALIDATION_FAILED", "Window capture requires the requested document to be active; restore the target document first.");
            var frame = app.Frame() as IFrame;
            if (frame == null) throw new Fault("API_FAILED", "SolidWorks frame unavailable.");
            var window = new IntPtr(frame.GetHWndx64()); uint windowPid;
            GetWindowThreadProcessId(window, out windowPid);
            WindowBounds bounds;
            if (windowPid != (uint)pid || IsIconic(window) || !GetWindowRect(window, out bounds))
                throw new Fault("API_FAILED", "The attached SolidWorks window is unavailable or minimized.");
            int width = bounds.Right - bounds.Left, height = bounds.Bottom - bounds.Top;
            if (width < 100 || height < 100 || width > 8192 || height > 8192)
                throw new Fault("API_FAILED", "Window dimensions are outside the supported capture range.");
            string directory = Paths.Within(options.OutputRoot, Path.Combine(options.OutputRoot, "inspections", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory); string path = Path.Combine(directory, "solidworks-window.png");
            using (var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr context = graphics.GetHdc(); bool captured;
                try { captured = PrintWindow(window, context, 2); }
                finally { graphics.ReleaseHdc(context); }
                if (!captured) throw new Fault("API_FAILED", "SolidWorks window capture failed; no successful screenshot was inferred.");
                bitmap.Save(path, ImageFormat.Png);
            }
            if (!Object.ReferenceEquals(app.IActiveDoc2, document.Doc))
                throw new Fault("DOC_REVISION_MISMATCH", "Active document changed during window capture.");
            return J.Obj("path", path, "format", "png", "width_px", width, "height_px", height,
                "source", "solidworks_window", "method", "PrintWindow_RENDERFULLCONTENT", "pid", pid,
                "document_id", document.Id, "revision", Revision(document), "captured_at", J.Now(),
                "sha256", J.FileHash(path), "visual_content_verified", false);
        }
    }
}
