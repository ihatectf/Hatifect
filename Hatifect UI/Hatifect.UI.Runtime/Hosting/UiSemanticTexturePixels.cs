namespace Hatifect.UI.Runtime.Hosting;

/// <summary>Converts decoded, straight-alpha RGBA pixels to the UI renderer's premultiplied format.</summary>
internal static class UiSemanticTexturePixels
{
    internal static void PremultiplyRgba(Span<byte> pixels)
    {
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("RGBA data must contain complete pixels.", nameof(pixels));

        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            int alpha = pixels[offset + 3];
            pixels[offset] = (byte)((pixels[offset] * alpha + 127) / 255);
            pixels[offset + 1] = (byte)((pixels[offset + 1] * alpha + 127) / 255);
            pixels[offset + 2] = (byte)((pixels[offset + 2] * alpha + 127) / 255);
        }
    }
}
