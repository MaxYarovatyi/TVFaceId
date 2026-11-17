using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Core.Imaging
{
    public static class ImageLoader
    {
        public static (byte[] Bgra, int Width, int Height) LoadAsBgra(string path)
        {
            using var src = new Bitmap(path);
            return ToBgra(src);
        }

        public static (byte[] Bgra, int Width, int Height) LoadAsBgra(byte[] imageBytes)
        {
            using var ms = new MemoryStream(imageBytes);
            using var src = new Bitmap(ms);
            return ToBgra(src);
        }

        private static (byte[] Bgra, int Width, int Height) ToBgra(Bitmap src)
        {
            using var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                g.DrawImage(src, 0, 0, src.Width, src.Height);

            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
            bmp.UnlockBits(data);
            return (bgra, w, h);
        }
    }
}