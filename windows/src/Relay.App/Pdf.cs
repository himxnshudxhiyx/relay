using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Relay.App;

/// <summary>
/// Renders PDF pages to bitmaps with PDFium, the engine inside Chrome (the
/// bblanchon.PDFium packages ship pdfium.dll for Windows). Avalonia has no
/// PDF view of its own; this is the Windows stand-in for PDFKit.
/// </summary>
static partial class Pdf
{
    /// <summary>Past this, pages stay unrendered: Open shows the whole file in a real viewer.</summary>
    const int MaxPages = 60;
    static bool _initialized;

    /// <param name="scale">Pixels per PDF point (1 point = 1/72 inch).</param>
    public static List<Bitmap> Render(byte[] data, double scale)
    {
        var pages = new List<Bitmap>();
        try
        {
            if (!_initialized)
            {
                FPDF_InitLibrary();
                _initialized = true;
            }
            var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                var doc = FPDF_LoadMemDocument(pin.AddrOfPinnedObject(), data.Length, null);
                if (doc == IntPtr.Zero) return pages;
                try
                {
                    var count = Math.Min(FPDF_GetPageCount(doc), MaxPages);
                    for (var i = 0; i < count; i++)
                        if (RenderPage(doc, i, scale) is { } page) pages.Add(page);
                }
                finally
                {
                    FPDF_CloseDocument(doc);
                }
            }
            finally
            {
                pin.Free();
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // No PDFium for this platform: Preview falls back to its "Save it to inspect it" message.
        }
        return pages;
    }

    static WriteableBitmap? RenderPage(IntPtr doc, int index, double scale)
    {
        var page = FPDF_LoadPage(doc, index);
        if (page == IntPtr.Zero) return null;
        try
        {
            var width = Math.Max(1, (int)(FPDF_GetPageWidthF(page) * scale));
            var height = Math.Max(1, (int)(FPDF_GetPageHeightF(page) * scale));
            var bitmap = FPDFBitmap_Create(width, height, 1);
            if (bitmap == IntPtr.Zero) return null;
            try
            {
                FPDFBitmap_FillRect(bitmap, 0, 0, width, height, 0xFFFFFFFF);
                const int Annotations = 0x01, LcdText = 0x02;
                FPDF_RenderPageBitmap(bitmap, page, 0, 0, width, height, 0, Annotations | LcdText);
                var stride = FPDFBitmap_GetStride(bitmap);
                var buffer = FPDFBitmap_GetBuffer(bitmap);
                // PDFium writes BGRA, which is what Avalonia's Bgra8888 expects.
                var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
                using var locked = result.Lock();
                for (var y = 0; y < height; y++)
                    unsafe
                    {
                        Buffer.MemoryCopy((byte*)buffer + (long)y * stride, (byte*)locked.Address + (long)y * locked.RowBytes, locked.RowBytes, Math.Min(stride, locked.RowBytes));
                    }
                return result;
            }
            finally
            {
                FPDFBitmap_Destroy(bitmap);
            }
        }
        finally
        {
            FPDF_ClosePage(page);
        }
    }

    const string Lib = "pdfium";

    [LibraryImport(Lib)] private static partial void FPDF_InitLibrary();
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)] private static partial IntPtr FPDF_LoadMemDocument(IntPtr data, int size, string? password);
    [LibraryImport(Lib)] private static partial void FPDF_CloseDocument(IntPtr doc);
    [LibraryImport(Lib)] private static partial int FPDF_GetPageCount(IntPtr doc);
    [LibraryImport(Lib)] private static partial IntPtr FPDF_LoadPage(IntPtr doc, int index);
    [LibraryImport(Lib)] private static partial void FPDF_ClosePage(IntPtr page);
    [LibraryImport(Lib)] private static partial float FPDF_GetPageWidthF(IntPtr page);
    [LibraryImport(Lib)] private static partial float FPDF_GetPageHeightF(IntPtr page);
    [LibraryImport(Lib)] private static partial IntPtr FPDFBitmap_Create(int width, int height, int alpha);
    [LibraryImport(Lib)] private static partial void FPDFBitmap_FillRect(IntPtr bitmap, int left, int top, int width, int height, uint color);
    [LibraryImport(Lib)] private static partial void FPDF_RenderPageBitmap(IntPtr bitmap, IntPtr page, int x, int y, int width, int height, int rotate, int flags);
    [LibraryImport(Lib)] private static partial IntPtr FPDFBitmap_GetBuffer(IntPtr bitmap);
    [LibraryImport(Lib)] private static partial int FPDFBitmap_GetStride(IntPtr bitmap);
    [LibraryImport(Lib)] private static partial void FPDFBitmap_Destroy(IntPtr bitmap);
}
