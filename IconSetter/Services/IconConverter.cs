using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;

namespace IconSetter.Services
{
    public static class IconConverter
    {
        private static readonly int[] StandardSizes = { 256, 128, 64, 48, 32, 16 };

        /// <summary>
        /// Converts a PNG/JPG/BMP source image into a multi-resolution .ico
        /// containing 256/128/64/48/32 px frames (16 is added by EnrichIco on demand).
        /// </summary>
        public static void ConvertToIco(string inputPath, string outputPath)
        {
            using var src = new System.Drawing.Bitmap(inputPath);
            WriteIco(src, outputPath, new[] { 256, 128, 64, 48, 32 });
        }

        /// <summary>
        /// If an existing .ico only contains a subset of the standard sizes (commonly just a
        /// single 256x256 frame), rebuilds it with the full size set generated from its largest
        /// frame. Optionally keeps a ".bak" copy of the original file.
        /// </summary>
        /// <returns>true if the file was rewritten, false if it already had all standard sizes.</returns>
        public static bool EnrichIco(string icoPath, bool keepBackup)
        {
            var existingSizes = GetFrameSizes(icoPath);
            bool missingAny = false;
            foreach (var size in StandardSizes)
            {
                if (!existingSizes.Contains(size)) { missingAny = true; break; }
            }
            if (!missingAny) return false;

            using var largest = LoadLargestFrameAsBitmap(icoPath);
            if (largest == null) return false;

            string backupPath = icoPath + ".bak";
            if (keepBackup)
            {
                File.Copy(icoPath, backupPath, overwrite: true);
            }

            WriteIco(largest, icoPath, StandardSizes);
            return true;
        }

        private static HashSet<int> GetFrameSizes(string icoPath)
        {
            var sizes = new HashSet<int>();
            using var fs = new FileStream(icoPath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            br.ReadUInt16();
            br.ReadUInt16();
            int count = br.ReadUInt16();
            for (int i = 0; i < count; i++)
            {
                byte width = br.ReadByte();
                br.ReadByte(); // height
                br.ReadByte(); br.ReadByte();
                br.ReadUInt16(); br.ReadUInt16();
                br.ReadInt32(); br.ReadInt32();
                sizes.Add(width == 0 ? 256 : width);
            }
            return sizes;
        }

        private static System.Drawing.Bitmap? LoadLargestFrameAsBitmap(string icoPath)
        {
            using var fs = new FileStream(icoPath, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            br.ReadUInt16();
            br.ReadUInt16();
            int count = br.ReadUInt16();

            int bestSize = 0, bestLength = 0, bestOffset = 0;
            for (int i = 0; i < count; i++)
            {
                byte width = br.ReadByte();
                br.ReadByte();
                br.ReadByte(); br.ReadByte();
                br.ReadUInt16(); br.ReadUInt16();
                int bytesInRes = br.ReadInt32();
                int imageOffset = br.ReadInt32();

                int size = width == 0 ? 256 : width;
                if (size > bestSize)
                {
                    bestSize = size;
                    bestLength = bytesInRes;
                    bestOffset = imageOffset;
                }
            }

            if (bestSize == 0) return null;

            fs.Position = bestOffset;
            byte[] data = br.ReadBytes(bestLength);

            using var ms = new MemoryStream(data);
            // PNG-encoded frame
            if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                return new System.Drawing.Bitmap(ms);
            }

            // Otherwise it's a raw BMP-style DIB frame; System.Drawing can still decode via Bitmap(Stream)
            // for most icon frames, but as a final fallback wrap it back through GDI's icon loader.
            try
            {
                return new System.Drawing.Bitmap(ms);
            }
            catch
            {
                using var icon = new System.Drawing.Icon(icoPath, bestSize, bestSize);
                return icon.ToBitmap();
            }
        }

        private static void WriteIco(System.Drawing.Bitmap src, string outputPath, int[] sizes)
        {
            using var fs = new FileStream(outputPath, FileMode.Create);
            using var bw = new BinaryWriter(fs);

            bw.Write((short)0);
            bw.Write((short)1);
            bw.Write((short)sizes.Length);

            int offset = 6 + (16 * sizes.Length);

            var imageData = new List<byte[]>();
            var widths = new List<byte>();
            var heights = new List<byte>();
            var sizesInBytes = new List<uint>();
            var offsets = new List<uint>();

            foreach (int size in sizes)
            {
                using var bmp = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using var g = System.Drawing.Graphics.FromImage(bmp);

                g.Clear(System.Drawing.Color.Transparent);
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

                float scale = Math.Min((float)size / src.Width, (float)size / src.Height);
                int newW = (int)(src.Width * scale);
                int newH = (int)(src.Height * scale);
                int offsetX = (size - newW) / 2;
                int offsetY = (size - newH) / 2;

                g.DrawImage(src, offsetX, offsetY, newW, newH);

                using var ms = new MemoryStream();
                bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                byte[] pngData = ms.ToArray();
                imageData.Add(pngData);

                widths.Add((byte)(size == 256 ? 0 : size));
                heights.Add((byte)(size == 256 ? 0 : size));
                sizesInBytes.Add((uint)pngData.Length);
                offsets.Add((uint)offset);

                offset += pngData.Length;
            }

            for (int i = 0; i < sizes.Length; i++)
            {
                bw.Write(widths[i]);
                bw.Write(heights[i]);
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((short)1);
                bw.Write((short)32);
                bw.Write(sizesInBytes[i]);
                bw.Write(offsets[i]);
            }

            foreach (var data in imageData)
                bw.Write(data);
        }

        /// <summary>
        /// Extracts the largest frame (usually 256x256 PNG) from an .ico file and returns it as
        /// a BitmapSource for sharp preview rendering.
        /// </summary>
        public static BitmapSource? LoadLargestIconFrame(string path)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var br = new BinaryReader(fs);
            br.ReadUInt16();
            br.ReadUInt16();
            int count = br.ReadUInt16();

            int bestSize = 0, bestLength = 0, bestOffset = 0;

            for (int i = 0; i < count; i++)
            {
                byte width = br.ReadByte();
                br.ReadByte();
                br.ReadByte(); br.ReadByte();
                br.ReadUInt16(); br.ReadUInt16();
                int bytesInRes = br.ReadInt32();
                int imageOffset = br.ReadInt32();

                int size = width == 0 ? 256 : width;
                if (size > bestSize)
                {
                    bestSize = size;
                    bestLength = bytesInRes;
                    bestOffset = imageOffset;
                }
            }

            if (bestSize <= 0) return null;

            fs.Position = bestOffset;
            byte[] data = br.ReadBytes(bestLength);

            if (data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47)
            {
                using var ms = new MemoryStream(data);
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = ms;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            using var ms2 = new MemoryStream(data);
            var decoder = new BmpBitmapDecoder(ms2, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames[0];
        }
    }
}
