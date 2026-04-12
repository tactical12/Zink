using System;
using Windows.Graphics.DirectX.Direct3D11;
using SharpDX.Direct3D11;

namespace Zink.Services.Recording
{
    internal sealed class SurfaceWithInfo : IDisposable
    {
        private readonly Texture2D? _ownedTexture;

        public IDirect3DSurface Surface { get; }

        public SurfaceWithInfo(Texture2D ownedTexture, IDirect3DSurface surface)
        {
            _ownedTexture = ownedTexture;
            Surface = surface;
        }

        public void Dispose()
        {
            try
            {
                (Surface as IDisposable)?.Dispose();
            }
            catch
            {
            }

            try
            {
                _ownedTexture?.Dispose();
            }
            catch
            {
            }
        }
    }
}