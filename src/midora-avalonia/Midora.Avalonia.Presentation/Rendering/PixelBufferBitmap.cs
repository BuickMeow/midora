using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Midora.Avalonia.Presentation.Rendering;

/// <summary>
/// Creates an Avalonia bitmap from a Pbgra32 byte buffer. Replaces the WPF
/// <c>BitmapSource.Create(...) + Freeze()</c> pattern; the returned bitmap is never
/// mutated after creation.
/// </summary>
public static class PixelBufferBitmap
{
    public static WriteableBitmap Create(byte[] pbgra32, int width, int height, double dpiX, double dpiY)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height),
            new Vector(dpiX, dpiY),
            PixelFormat.Bgra8888,
            AlphaFormat.Premul);

        using (var locked = bitmap.Lock())
        {
            var stride = width * 4;
            for (var y = 0; y < height; y++)
            {
                Marshal.Copy(pbgra32, y * stride, IntPtr.Add(locked.Address, y * locked.RowBytes), stride);
            }
        }

        return bitmap;
    }
}
