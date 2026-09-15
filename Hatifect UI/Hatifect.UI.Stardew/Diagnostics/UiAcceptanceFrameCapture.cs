using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace Hatifect.UI.Stardew;

internal sealed class UiAcceptanceFrameCapture : DrawableGameComponent
{
    private readonly Action _onCompletedFrame;
    private readonly string _artifactDirectory;
    private readonly string _scenario;

    internal UiAcceptanceFrameCapture(Game game, string artifactDirectory, string scenario, Action onCompletedFrame)
        : base(game)
    {
        _artifactDirectory = artifactDirectory;
        _scenario = scenario;
        _onCompletedFrame = onCompletedFrame;
        DrawOrder = int.MaxValue;
    }

    internal string? Source { get; private set; }
    internal int Width { get; private set; }
    internal int Height { get; private set; }

    // GameRunner draws its components after each instance has composed world and UI buffers.
    // SMAPI's Rendered event runs earlier, inside the instance's _draw method.
    public override void Draw(GameTime gameTime) => _onCompletedFrame();

    internal void Capture(string? name = null)
    {
        string directory = Path.Combine(_artifactDirectory, "screenshots");
        Directory.CreateDirectory(directory);
        string fileName = name ?? _scenario.Replace('.', '-');
        string path = Path.Combine(directory, fileName + ".png");
        GraphicsDevice graphics = Game1.graphics.GraphicsDevice;
        if (graphics.RenderTargetCount != 0)
            throw new InvalidOperationException("Completed-frame capture requires the composed back buffer.");
        Source = "composed-back-buffer";
        Width = graphics.PresentationParameters.BackBufferWidth;
        Height = graphics.PresentationParameters.BackBufferHeight;
        var data = new Color[checked(Width * Height)];
        graphics.GetBackBufferData(data);
        using var texture = new Texture2D(graphics, Width, Height, false, SurfaceFormat.Color);
        texture.SetData(data);
        using FileStream stream = File.Create(path);
        texture.SaveAsPng(stream, Width, Height);
        stream.Flush(flushToDisk: true);

        // Retain the completed UI layer independently of native-window clipping/occlusion so
        // layout pixels can be compared with the final window frame without changing game state.
        if (Game1.game1.uiScreen is { IsDisposed: false } uiScreen)
        {
            string uiPath = Path.Combine(directory, fileName + "-ui-layer.png");
            using FileStream uiStream = File.Create(uiPath);
            uiScreen.SaveAsPng(uiStream, uiScreen.Width, uiScreen.Height);
            uiStream.Flush(flushToDisk: true);
        }
    }

}
