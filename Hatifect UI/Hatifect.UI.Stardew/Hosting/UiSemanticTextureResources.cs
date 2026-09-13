using System.Buffers;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using Hatifect.UI.Runtime.Hosting;

namespace Hatifect.UI.Stardew;

internal static class UiSemanticTextureResources
{
    internal static UiSemanticTextureCatalog<Texture2D> Create() => new(
        Decode,
        () =>
        {
            var texture = new Texture2D(Game1.graphics.GraphicsDevice, 2, 2);
            try { texture.SetData(new[] { Color.Magenta, Color.Black, Color.Black, Color.Magenta }); return texture; }
            catch { texture.Dispose(); throw; }
        });

    private static Texture2D Decode(Stream stream)
    {
        // The game's MonoGame FromStream overload does not premultiply PNG alpha.
        // Normalize once at registration; SpriteBatch uses premultiplied blending.
        var texture = Texture2D.FromStream(Game1.graphics.GraphicsDevice, stream);
        try
        {
            if (texture.Format != SurfaceFormat.Color)
                throw new InvalidOperationException("PNG textures must decode to RGBA pixels.");
            int byteCount = checked(texture.Width * texture.Height * 4);
            byte[] pixels = ArrayPool<byte>.Shared.Rent(byteCount);
            try
            {
                texture.GetData(0, null, pixels, 0, byteCount);
                UiSemanticTexturePixels.PremultiplyRgba(pixels.AsSpan(0, byteCount));
                texture.SetData(0, null, pixels, 0, byteCount);
                return texture;
            }
            finally { ArrayPool<byte>.Shared.Return(pixels); }
        }
        catch { texture.Dispose(); throw; }
    }
}
