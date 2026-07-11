using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Sledge.BspEditor.Rendering.Viewport;
using Sledge.Rendering.Engine;
using Sledge.Rendering.Viewports;
using Veldrid;
using GdiPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace HammerTime.Mcp.Plugin
{
    /// <summary>
    /// GPU-readback and reliable capture helpers for viewport screenshots.
    /// Nothing here throws during resolution of the render device (returns null instead);
    /// the actual capture methods throw so the capture tier chain can advance.
    /// </summary>
    internal static class ViewportReadback
    {
        private static bool _deviceResolved;
        private static GraphicsDevice _device;
        private static readonly object _deviceLock = new object();

        /// <summary>
        /// Cached reflection access to the internal <c>Engine.Instance.Device</c>.
        /// The reflection runs at most once for the lifetime of the process.
        /// </summary>
        public static GraphicsDevice TryGetDevice()
        {
            if (_deviceResolved) return _device;
            lock (_deviceLock)
            {
                if (_deviceResolved) return _device;
                try
                {
                    var instanceProp = typeof(Engine).GetProperty("Instance", BindingFlags.NonPublic | BindingFlags.Static);
                    var instance = instanceProp?.GetValue(null) as Engine;
                    _device = instance?.Device;
                }
                catch
                {
                    _device = null;
                }
                _deviceResolved = true;
                return _device;
            }
        }

        /// <summary>
        /// Resolve the <see cref="IViewport"/> that lives inside the map document control panel.
        /// </summary>
        public static IViewport TryGetViewport(ViewportMapDocumentControl control)
        {
            try
            {
                return control?.Control?.Controls?.OfType<IViewport>().FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Capture the viewport's resolved scene texture directly from the GPU. This omits the
        /// ImGui overlay layer (entity names, gizmos, MCP overlay highlights) which is drawn
        /// straight to the swapchain. Runs the copy inside a render-thread pause.
        /// </summary>
        public static Bitmap CaptureGpu(IViewport viewport, EngineInterface engine)
        {
            if (engine == null) throw new InvalidOperationException("Render engine interface is not available for GPU capture.");
            var device = TryGetDevice();
            if (device == null) throw new InvalidOperationException("Render device is not available for GPU capture.");

            var source = viewport?.ViewportRenderTexture?.GetTexture();
            if (source == null) throw new InvalidOperationException("Viewport render texture is not available for GPU capture.");

            var width = source.Width;
            var height = source.Height;
            if (width == 0 || height == 0) throw new InvalidOperationException("Viewport render texture has no size.");

            var factory = device.ResourceFactory;
            Veldrid.Texture staging = null;
            CommandList cl = null;
            using (engine.Pause())
            {
                try
                {
                    staging = factory.CreateTexture(TextureDescription.Texture2D(
                        width, height, 1, 1, source.Format, TextureUsage.Staging, TextureSampleCount.Count1));
                    cl = factory.CreateCommandList();
                    cl.Begin();
                    cl.CopyTexture(source, staging);
                    cl.End();
                    device.SubmitCommands(cl);
                    device.WaitForIdle();

                    var map = device.Map(staging, MapMode.Read);
                    var bitmap = new Bitmap((int)width, (int)height, GdiPixelFormat.Format32bppArgb);
                    try
                    {
                        var rect = new Rectangle(0, 0, (int)width, (int)height);
                        var data = bitmap.LockBits(rect, ImageLockMode.WriteOnly, GdiPixelFormat.Format32bppArgb);
                        try
                        {
                            var rowBytes = (int)width * 4;
                            var row = new byte[rowBytes];
                            for (var y = 0; y < height; y++)
                            {
                                var src = IntPtr.Add(map.Data, (int)(y * map.RowPitch));
                                var dst = IntPtr.Add(data.Scan0, y * data.Stride);
                                // B8_G8_R8_A8_UNorm rows map 1:1 onto GDI 32bppArgb (BGRA) rows.
                                Marshal.Copy(src, row, 0, rowBytes);
                                Marshal.Copy(row, 0, dst, rowBytes);
                            }
                        }
                        finally
                        {
                            bitmap.UnlockBits(data);
                        }
                    }
                    catch
                    {
                        bitmap.Dispose();
                        throw;
                    }
                    finally
                    {
                        device.Unmap(staging);
                    }

                    return bitmap;
                }
                finally
                {
                    cl?.Dispose();
                    staging?.Dispose();
                }
            }
        }

        /// <summary>
        /// Raise the inactive render rate and wait for a couple of fresh frames so an unfocused
        /// viewport has re-rendered before capture. Safe to call from a threadpool thread.
        /// </summary>
        public static async Task WaitForFreshFrame(IViewport viewport, EngineInterface engine, int maxWaitMs)
        {
            if (viewport == null || engine == null || maxWaitMs <= 0) return;

            var previousFps = engine.InactiveTargetFps;
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var count = 0;
            EventHandler<long> handler = (s, frame) =>
            {
                if (Interlocked.Increment(ref count) >= 2) tcs.TrySetResult(true);
            };

            try
            {
                engine.InactiveTargetFps = Math.Max(previousFps, 120);
                viewport.OnUpdate += handler;
                await Task.WhenAny(tcs.Task, Task.Delay(maxWaitMs)).ConfigureAwait(false);
            }
            finally
            {
                viewport.OnUpdate -= handler;
                engine.InactiveTargetFps = previousFps;
            }
        }

        /// <summary>
        /// Sample the bitmap sparsely and report whether it is (almost) entirely black,
        /// which usually means the frame had not been rendered yet.
        /// </summary>
        public static bool IsMostlyBlack(Bitmap bitmap)
        {
            if (bitmap == null || bitmap.Width == 0 || bitmap.Height == 0) return false;

            // At least every 16th pixel on each axis, but never more than ~64x64 = 4096 samples.
            var stepX = Math.Max(16, bitmap.Width / 64);
            var stepY = Math.Max(16, bitmap.Height / 64);

            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, GdiPixelFormat.Format32bppArgb);
            try
            {
                var total = 0;
                var dark = 0;
                for (var y = 0; y < bitmap.Height; y += stepY)
                {
                    var rowPtr = IntPtr.Add(data.Scan0, y * data.Stride);
                    for (var x = 0; x < bitmap.Width; x += stepX)
                    {
                        var off = x * 4;
                        var b = Marshal.ReadByte(rowPtr, off);
                        var g = Marshal.ReadByte(rowPtr, off + 1);
                        var r = Marshal.ReadByte(rowPtr, off + 2);
                        total++;
                        if (Math.Max(r, Math.Max(g, b)) < 8) dark++;
                    }
                }
                return total > 0 && dark >= total * 0.995;
            }
            finally
            {
                bitmap.UnlockBits(data);
            }
        }

        /// <summary>
        /// Encode a bitmap to png or jpeg and return the base64 payload plus mime type.
        /// </summary>
        public static (string base64, string mimeType) Encode(Bitmap bitmap, string format, int jpegQuality)
        {
            if (string.Equals(format, "jpeg", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(format, "jpg", StringComparison.OrdinalIgnoreCase))
            {
                var codec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);
                if (codec != null)
                {
                    var quality = Math.Max(1, Math.Min(100, jpegQuality));
                    using (var ep = new EncoderParameters(1))
                    {
                        ep.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
                        using (var stream = new MemoryStream())
                        {
                            bitmap.Save(stream, codec, ep);
                            return (Convert.ToBase64String(stream.ToArray()), "image/jpeg");
                        }
                    }
                }
            }

            using (var stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return (Convert.ToBase64String(stream.ToArray()), "image/png");
            }
        }
    }
}
