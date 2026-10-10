using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CompanionDeck.Host.Launcher;

/// <summary>
/// Pulls the shell's icon for a file and returns it as a trimmed PNG. Goes through the system
/// image list rather than ExtractAssociatedIcon so we get the jumbo (256px) variant where an
/// application provides one - a 32px icon upscaled onto a tablet tile looks obviously cheap.
/// Windows pads jumbo entries with transparency when the real artwork is smaller, so the result
/// is cropped back to its opaque bounds before encoding.
/// </summary>
[SupportedOSPlatform("windows")]
public static class IconExtractor
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfoW(string path, uint attrs, ref SHFILEINFO info, uint size, uint flags);

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(int iImageList, ref Guid riid, out IImageList ppv);

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);

    [ComImport, Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add();
        [PreserveSig] int ReplaceIcon();
        [PreserveSig] int SetOverlayImage();
        [PreserveSig] int Replace();
        [PreserveSig] int AddMasked();
        [PreserveSig] int Draw();
        [PreserveSig] int Remove();
        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }

    private const uint SHGFI_SYSICONINDEX = 0x4000;
    private const uint SHGFI_USEFILEATTRIBUTES = 0x0010;
    private const int SHIL_JUMBO = 0x4;      // 256px
    private const int SHIL_EXTRALARGE = 0x2; // 48px
    private const int ILD_TRANSPARENT = 0x1;

    public static byte[]? GetPng(string path, int maxSize = 96)
    {
        foreach (var list in new[] { SHIL_JUMBO, SHIL_EXTRALARGE })
        {
            try
            {
                var png = TryList(path, list, maxSize);
                if (png is not null) return png;
            }
            catch (Exception ex) { Log.Debug($"icon {list} for {path}: {ex.Message}"); }
        }
        return null;
    }

    private static byte[]? TryList(string path, int listId, int maxSize)
    {
        var info = new SHFILEINFO();
        var res = SHGetFileInfoW(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                                 SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES);
        if (res == IntPtr.Zero) return null;

        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(listId, ref iid, out var imageList) != 0 || imageList is null) return null;

        IntPtr hIcon = IntPtr.Zero;
        try
        {
            if (imageList.GetIcon(info.iIcon, ILD_TRANSPARENT, out hIcon) != 0 || hIcon == IntPtr.Zero)
                return null;

            using var icon = Icon.FromHandle(hIcon);
            using var full = icon.ToBitmap();
            using var cropped = Trim(full);
            using var scaled = Fit(cropped, maxSize);

            using var ms = new MemoryStream();
            scaled.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            if (hIcon != IntPtr.Zero) DestroyIcon(hIcon);
            if (Marshal.IsComObject(imageList)) Marshal.ReleaseComObject(imageList);
        }
    }

    /// <summary>Crops away fully transparent margins, which jumbo icons are mostly made of.</summary>
    private static Bitmap Trim(Bitmap src)
    {
        int minX = src.Width, minY = src.Height, maxX = -1, maxY = -1;

        var data = src.LockBits(new Rectangle(0, 0, src.Width, src.Height),
                                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* baseAddr = (byte*)data.Scan0;
                for (int y = 0; y < src.Height; y++)
                {
                    byte* row = baseAddr + y * data.Stride;
                    for (int x = 0; x < src.Width; x++)
                    {
                        if (row[x * 4 + 3] <= 8) continue;   // alpha
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
        }
        finally { src.UnlockBits(data); }

        if (maxX < 0) return (Bitmap)src.Clone();   // fully transparent; nothing to trim

        var rect = new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
        return src.Clone(rect, PixelFormat.Format32bppArgb);
    }

    private static Bitmap Fit(Bitmap src, int max)
    {
        if (src.Width <= max && src.Height <= max) return (Bitmap)src.Clone();

        double scale = (double)max / Math.Max(src.Width, src.Height);
        int w = Math.Max(1, (int)Math.Round(src.Width * scale));
        int h = Math.Max(1, (int)Math.Round(src.Height * scale));

        var dest = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dest);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(src, 0, 0, w, h);
        return dest;
    }
}
