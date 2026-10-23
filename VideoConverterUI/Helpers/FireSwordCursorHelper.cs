using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace VideoConverterUI.Helpers;

public static class FireSwordCursorHelper
{
    private static Cursor? _fireSwordCursor;

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr LoadCursorFromFile(string lpFileName);

    public static Cursor GetFireSwordCursor()
    {
        if (_fireSwordCursor != null) return _fireSwordCursor;

        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            
            // Search all possible locations for .ani or .cur files
            string[] searchDirectories = new[]
            {
                baseDir,
                Path.Combine(baseDir, "Resources"),
                Path.GetFullPath(Path.Combine(baseDir, "..")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "VideoConverterUI")),
                Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "VideoConverterUI", "Resources")),
            };

            string[] fileNames = new[]
            {
                "epic fire sword.ani",
                "firesword.ani",
                "firesword.cur",
                "epic fire sword.cur",
                "firesword.png"
            };

            foreach (var dir in searchDirectories)
            {
                if (!Directory.Exists(dir)) continue;

                foreach (var fileName in fileNames)
                {
                    string fullPath = Path.Combine(dir, fileName);
                    if (File.Exists(fullPath))
                    {
                        try
                        {
                            // Use Win32 LoadCursorFromFile for .ani and .cur files
                            if (fullPath.EndsWith(".ani", StringComparison.OrdinalIgnoreCase) ||
                                fullPath.EndsWith(".cur", StringComparison.OrdinalIgnoreCase))
                            {
                                IntPtr hCursor = LoadCursorFromFile(fullPath);
                                if (hCursor != IntPtr.Zero)
                                {
                                    _fireSwordCursor = CursorInteropHelper.Create(new SafeFileHandle(hCursor, true));
                                    return _fireSwordCursor;
                                }
                            }
                            else if (fullPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                            {
                                _fireSwordCursor = CreateCursorFromBitmapFile(fullPath);
                                return _fireSwordCursor;
                            }
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"Failed to load cursor from {fullPath}: {ex.Message}");
                        }
                    }
                }
            }

            // Fallback Vector Generation
            return GenerateVectorFallbackCursor();
        }
        catch
        {
            return Cursors.Cross;
        }
    }

    private static Cursor GenerateVectorFallbackCursor()
    {
        int width = 32;
        int height = 32;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(120, 255, 69, 0)), null, new Point(18, 14), 12, 12);
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(160, 255, 140, 0)), null, new Point(22, 10), 8, 8);

            var fireBrush1 = new SolidColorBrush(Color.FromRgb(255, 69, 0));  
            var fireBrush2 = new SolidColorBrush(Color.FromRgb(255, 165, 0)); 
            var fireBrush3 = new SolidColorBrush(Color.FromRgb(255, 215, 0)); 
            var whiteCore  = new SolidColorBrush(Colors.White);

            dc.DrawRectangle(fireBrush1, null, new Rect(12, 14, 3, 3));
            dc.DrawRectangle(fireBrush2, null, new Rect(15, 11, 4, 4));
            dc.DrawRectangle(fireBrush3, null, new Rect(18, 8, 3, 3));
            dc.DrawRectangle(fireBrush2, null, new Rect(21, 5, 3, 3));

            var guardBrush = new SolidColorBrush(Color.FromRgb(255, 215, 0)); 
            dc.DrawRectangle(guardBrush, null, new Rect(7, 21, 8, 3));

            var hiltBrush = new SolidColorBrush(Color.FromRgb(100, 50, 20));  
            dc.DrawRectangle(hiltBrush, null, new Rect(5, 24, 4, 5));

            dc.DrawEllipse(guardBrush, null, new Point(5, 29), 2.5, 2.5);

            var penBladeCore = new Pen(whiteCore, 2);
            var penFireEdge = new Pen(fireBrush2, 4);
            var penFireOuter = new Pen(fireBrush1, 6);

            dc.DrawLine(penFireOuter, new Point(10, 20), new Point(26, 4));
            dc.DrawLine(penFireEdge, new Point(10, 20), new Point(26, 4));
            dc.DrawLine(penBladeCore, new Point(10, 20), new Point(26, 4));

            dc.DrawEllipse(whiteCore, null, new Point(27, 3), 2, 2);
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var msPng = new MemoryStream();
        encoder.Save(msPng);
        byte[] pngBytes = msPng.ToArray();

        using var msCur = new MemoryStream();
        using var writer = new BinaryWriter(msCur);

        writer.Write((ushort)0);
        writer.Write((ushort)2);
        writer.Write((ushort)1);

        writer.Write((byte)width);
        writer.Write((byte)height);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)27);
        writer.Write((ushort)3);
        writer.Write((uint)pngBytes.Length);
        writer.Write((uint)22);

        writer.Write(pngBytes);
        writer.Flush();

        msCur.Position = 0;
        _fireSwordCursor = new Cursor(msCur);
        return _fireSwordCursor;
    }

    private static Cursor CreateCursorFromBitmapFile(string path)
    {
        byte[] pngBytes = File.ReadAllBytes(path);
        using var msCur = new MemoryStream();
        using var writer = new BinaryWriter(msCur);

        writer.Write((ushort)0); 
        writer.Write((ushort)2); 
        writer.Write((ushort)1); 

        writer.Write((byte)32);  
        writer.Write((byte)32);  
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)0); 
        writer.Write((ushort)0); 
        writer.Write((uint)pngBytes.Length);
        writer.Write((uint)22);

        writer.Write(pngBytes);
        writer.Flush();
        msCur.Position = 0;
        return new Cursor(msCur);
    }
}
