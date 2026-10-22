using System;
using System.IO;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace StormDsiDecryptor;

public class BannerInfo
{
    public string TitleJapanese { get; set; } = string.Empty;
    public string TitleEnglish { get; set; } = string.Empty;
    public string BestTitle => !string.IsNullOrWhiteSpace(TitleEnglish) ? TitleEnglish : TitleJapanese;
    public BitmapSource? Icon { get; set; }
    public byte[]? IconPng { get; set; }
}

public static class NdsBannerParser
{
    public static BannerInfo? Parse(Stream stream, uint bannerOffset)
    {
        if (bannerOffset == 0 || bannerOffset + 0x840 > stream.Length)
            return null;

        try
        {
            byte[] bannerData = new byte[0x840];
            stream.Seek(bannerOffset, SeekOrigin.Begin);
            int read = stream.Read(bannerData, 0, 0x840);
            if (read < 0x240) return null;

            return ParseFromBuffer(bannerData);
        }
        catch
        {
            return null;
        }
    }

    public static BannerInfo? Parse(byte[] rom)
    {
        if (rom.Length < 0x6C) return null;
        uint bannerOffset = BitConverter.ToUInt32(rom, 0x68);
        if (bannerOffset == 0 || bannerOffset + 0x840 > rom.Length)
            return null;

        byte[] bannerData = new byte[0x840];
        Array.Copy(rom, bannerOffset, bannerData, 0, 0x840);
        return ParseFromBuffer(bannerData);
    }

    public static BannerInfo? ParseFromBuffer(byte[] bannerData)
    {
        try
        {
            var info = new BannerInfo();

            // 1. Decode titles (Japanese at 0x240, English at 0x340)
            if (bannerData.Length >= 0x440)
            {
                info.TitleJapanese = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x240, 256));
                info.TitleEnglish = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x340, 256));
            }

            // 2. Decode 32x32 4bpp tiled icon
            byte[] iconPixels = new byte[512];
            Array.Copy(bannerData, 0x0020, iconPixels, 0, 512);

            // 3. Decode 16-color palette (16 x BGR555)
            uint[] palette = new uint[16];
            for (int i = 0; i < 16; i++)
            {
                ushort c = BitConverter.ToUInt16(bannerData, 0x0220 + i * 2);
                byte r = (byte)(((c >> 0) & 0x1F) * 255 / 31);
                byte g = (byte)(((c >> 5) & 0x1F) * 255 / 31);
                byte b = (byte)(((c >> 10) & 0x1F) * 255 / 31);
                byte a = (i == 0) ? (byte)0 : (byte)255;
                // BGRA format for WPF
                palette[i] = ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
            }

            // 4. Render 32x32 image (16 tiles of 8x8 pixels)
            byte[] bgraData = new byte[32 * 32 * 4];

            for (int tile = 0; tile < 16; tile++)
            {
                int tileX = (tile % 4) * 8;
                int tileY = (tile / 4) * 8;
                int tileOffset = tile * 32;

                for (int row = 0; row < 8; row++)
                {
                    int rowOffset = tileOffset + row * 4;
                    for (int colPair = 0; colPair < 4; colPair++)
                    {
                        byte val = iconPixels[rowOffset + colPair];
                        int p0 = val & 0x0F;
                        int p1 = (val >> 4) & 0x0F;

                        int px0 = tileX + colPair * 2;
                        int px1 = px0 + 1;
                        int py = tileY + row;

                        int destOffset0 = (py * 32 + px0) * 4;
                        int destOffset1 = (py * 32 + px1) * 4;

                        uint col0 = palette[p0];
                        uint col1 = palette[p1];

                        bgraData[destOffset0 + 0] = (byte)(col0 & 0xFF);         // B
                        bgraData[destOffset0 + 1] = (byte)((col0 >> 8) & 0xFF);  // G
                        bgraData[destOffset0 + 2] = (byte)((col0 >> 16) & 0xFF); // R
                        bgraData[destOffset0 + 3] = (byte)((col0 >> 24) & 0xFF); // A

                        bgraData[destOffset1 + 0] = (byte)(col1 & 0xFF);
                        bgraData[destOffset1 + 1] = (byte)((col1 >> 8) & 0xFF);
                        bgraData[destOffset1 + 2] = (byte)((col1 >> 16) & 0xFF);
                        bgraData[destOffset1 + 3] = (byte)((col1 >> 24) & 0xFF);
                    }
                }
            }

            // Create frozen WPF BitmapSource
            var bs = BitmapSource.Create(32, 32, 96, 96, PixelFormats.Bgra32, null, bgraData, 32 * 4);
            bs.Freeze();
            info.Icon = bs;

            // Encode to PNG bytes for caching
            using (var ms = new MemoryStream())
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bs));
                encoder.Save(ms);
                info.IconPng = ms.ToArray();
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    public static BitmapSource FromPngBytes(byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return DefaultCartridgeIcon;
        try
        {
            using var ms = new MemoryStream(bytes);
            var decoder = new PngBitmapDecoder(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();
            return frame;
        }
        catch
        {
            return DefaultCartridgeIcon;
        }
    }

    private static string CleanTitle(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int nullIdx = raw.IndexOf('\0');
        if (nullIdx >= 0) raw = raw.Substring(0, nullIdx);
        return raw.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }

    private static BitmapSource? _defaultCartridgeIcon;
    public static BitmapSource DefaultCartridgeIcon
    {
        get
        {
            if (_defaultCartridgeIcon != null) return _defaultCartridgeIcon;
            int w = 32, h = 32;
            byte[] bgra = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = (y * w + x) * 4;
                    bool border = ((x == 4 || x == 27) && (y >= 4 && y <= 27)) ||
                                  ((y == 4 || y == 27) && (x >= 4 && x <= 27));
                    bool body = (x > 4 && x < 27) && (y > 4 && y < 27);
                    bool notch = (x >= 24 && x <= 27) && (y >= 4 && y <= 7);
                    bool pin = (y >= 23 && y <= 25) && (x % 3 == 0) && (x >= 8 && x <= 23);

                    if (notch) continue;
                    if (border)
                    {
                        bgra[idx + 0] = 0xF6; bgra[idx + 1] = 0x5C; bgra[idx + 2] = 0x8B; bgra[idx + 3] = 0xFF; // ThemeAccent
                    }
                    else if (pin)
                    {
                        bgra[idx + 0] = 0x00; bgra[idx + 1] = 0xD0; bgra[idx + 2] = 0xFF; bgra[idx + 3] = 0xFF; // Gold contact
                    }
                    else if (body)
                    {
                        bgra[idx + 0] = 0x2B; bgra[idx + 1] = 0x13; bgra[idx + 2] = 0x17; bgra[idx + 3] = 0xE0; // CardBg
                    }
                }
            }
            var bs = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, bgra, w * 4);
            bs.Freeze();
            _defaultCartridgeIcon = bs;
            return _defaultCartridgeIcon;
        }
    }
}
