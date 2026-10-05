using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Classify.Interop;

/// <summary>
/// 屏幕捕获：Win32 BitBlt 抓取指定显示器内容并编码为 JPEG（质量可调）。
/// 纯框架内 API（user32/gdi32 + WPF Imaging），零外部包依赖。
/// </summary>
public static class ScreenCapture
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;

    /// <summary>抓取 hdc 对应屏幕范围为 JPEG 文件。返回是否成功。</summary>
    public static bool CaptureToJpeg(string filePath, int x, int y, int width, int height, long quality = 70)
    {
        IntPtr screenDC = IntPtr.Zero, memDC = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDC = GetDC(IntPtr.Zero);
            memDC = CreateCompatibleDC(screenDC);
            bmp = CreateCompatibleBitmap(screenDC, width, height);
            if (bmp == IntPtr.Zero) return false;
            old = SelectObject(memDC, bmp);
            BitBlt(memDC, 0, 0, width, height, screenDC, x, y, SRCCOPY | CAPTUREBLT);
            SelectObject(memDC, old);
            old = IntPtr.Zero;

            var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                bmp, IntPtr.Zero, Int32Rect.Empty, null);
            var encoder = new JpegBitmapEncoder { QualityLevel = (int)Math.Clamp(quality, 1, 100) };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var fs = File.Create(filePath);
            encoder.Save(fs);
            return true;
        }
        catch (Exception ex)
        {
            App.Log.Error("屏幕捕获失败", ex);
            return false;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDC, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDC != IntPtr.Zero) DeleteDC(memDC);
            if (screenDC != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDC);
        }
    }
}
