using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace StormDsDecryptor;

public class BannerInfo
{
    public ushort Version { get; set; }
    public string TitleJapanese { get; set; } = string.Empty;
    public string TitleEnglish { get; set; } = string.Empty;
    public string TitleFrench { get; set; } = string.Empty;
    public string TitleGerman { get; set; } = string.Empty;
    public string TitleItalian { get; set; } = string.Empty;
    public string TitleSpanish { get; set; } = string.Empty;
    public string TitleChinese { get; set; } = string.Empty;
    public string TitleKorean { get; set; } = string.Empty;
    public bool HasAnimatedIcon { get; set; }
    public int AnimatedFrameCount { get; set; } = 1;

    public string BestTitle =>
        !string.IsNullOrWhiteSpace(TitleEnglish) ? TitleEnglish :
        !string.IsNullOrWhiteSpace(TitleJapanese) ? TitleJapanese :
        !string.IsNullOrWhiteSpace(TitleFrench) ? TitleFrench :
        TitleGerman;

    public byte[]? IconPng { get; set; }
    public string Title => BestTitle;
    public byte[]? PngBytes => IconPng;
    public bool IsAnimated => HasAnimatedIcon;
    public System.Collections.Generic.Dictionary<string, string> AllTitles
    {
        get
        {
            var d = new System.Collections.Generic.Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(TitleJapanese)) d["JP"] = TitleJapanese;
            if (!string.IsNullOrWhiteSpace(TitleEnglish))  d["EN"] = TitleEnglish;
            if (!string.IsNullOrWhiteSpace(TitleFrench))   d["FR"] = TitleFrench;
            if (!string.IsNullOrWhiteSpace(TitleGerman))   d["DE"] = TitleGerman;
            if (!string.IsNullOrWhiteSpace(TitleItalian))  d["IT"] = TitleItalian;
            if (!string.IsNullOrWhiteSpace(TitleSpanish))  d["ES"] = TitleSpanish;
            if (!string.IsNullOrWhiteSpace(TitleChinese))  d["ZH"] = TitleChinese;
            if (!string.IsNullOrWhiteSpace(TitleKorean))   d["KO"] = TitleKorean;
            return d;
        }
    }
}

public static class NdsBannerParser
{
    private static readonly uint[] Crc32Table = InitCrc32Table();
    private static byte[]? _defaultCartridgePng;
    private static ImageSource? _defaultCartridgeImage;
    private static readonly ConcurrentDictionary<int, ImageSource> IconCache = new();

    private static uint[] InitCrc32Table()
    {
        uint[] table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            table[i] = c;
        }
        return table;
    }

    public static uint ComputeCrc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    public static int ReadUpTo(Stream stream, byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = stream.Read(buffer, offset + total, count - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }

    public static BannerInfo? Parse(Stream stream, uint bannerOffset)
    {
        if (bannerOffset + 0x240 > stream.Length)
            return null;

        try
        {
            int maxRead = (int)Math.Min(0x23C0, stream.Length - bannerOffset);
            byte[] bannerData = new byte[maxRead];
            if (stream.CanSeek)
                stream.Seek(bannerOffset, SeekOrigin.Begin);
            int read = ReadUpTo(stream, bannerData, 0, maxRead);
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
        if (bannerOffset == 0 || bannerOffset + 0x240 > rom.Length)
            return null;

        int maxLen = (int)Math.Min(0x23C0, rom.Length - bannerOffset);
        byte[] bannerData = new byte[maxLen];
        Array.Copy(rom, bannerOffset, bannerData, 0, maxLen);
        return ParseFromBuffer(bannerData);
    }

    public static BannerInfo? ParseFromBuffer(byte[] bannerData)
    {
        try
        {
            if (bannerData.Length < 0x240) return null;
            var info = new BannerInfo
            {
                Version = BitConverter.ToUInt16(bannerData, 0x00)
            };

            if (bannerData.Length >= 0x340) info.TitleJapanese = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x240, 256));
            if (bannerData.Length >= 0x440) info.TitleEnglish  = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x340, 256));
            if (bannerData.Length >= 0x540) info.TitleFrench   = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x440, 256));
            if (bannerData.Length >= 0x640) info.TitleGerman   = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x540, 256));
            if (bannerData.Length >= 0x740) info.TitleItalian  = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x640, 256));
            if (bannerData.Length >= 0x840) info.TitleSpanish  = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x740, 256));
            if (bannerData.Length >= 0x940 && info.Version >= 0x0002) info.TitleChinese = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x840, 256));
            if (bannerData.Length >= 0xA40 && info.Version >= 0x0003) info.TitleKorean  = CleanTitle(Encoding.Unicode.GetString(bannerData, 0x940, 256));

            int tileBaseOffset = 0x0020;
            int paletteBaseOffset = 0x0220;

            if (info.Version >= 0x0103 && bannerData.Length >= 0x23C0)
            {
                int animSteps = 0;
                for (int s = 0; s < 64; s++)
                {
                    ushort token = BitConverter.ToUInt16(bannerData, 0x2340 + s * 2);
                    if (token == 0) break;
                    animSteps++;
                }
                if (animSteps > 0)
                {
                    info.HasAnimatedIcon = animSteps > 1;
                    info.AnimatedFrameCount = Math.Max(1, animSteps);
                }

                // Check if standard NTR icon (0x0020..0x0240) is blank; if so, use DSi TWL animated frame 0
                int nonZeroNtr = 0;
                for (int i = 0x0020; i < 0x0240; i++)
                {
                    if (bannerData[i] != 0) nonZeroNtr++;
                }
                if (nonZeroNtr < 16)
                {
                    ushort firstToken = BitConverter.ToUInt16(bannerData, 0x2340);
                    int bmpIdx = (firstToken >> 8) & 0x07;
                    int palIdx = (firstToken >> 11) & 0x07;
                    tileBaseOffset = 0x1240 + bmpIdx * 512;
                    paletteBaseOffset = 0x2240 + palIdx * 32;
                }
            }

            // Decode 16-color BGR555 palette into RGBA
            Span<uint> palette = stackalloc uint[16];
            for (int i = 0; i < 16; i++)
            {
                ushort c = BitConverter.ToUInt16(bannerData, paletteBaseOffset + i * 2);
                uint r = (uint)(((c >> 0) & 0x1F) * 255 / 31);
                uint g = (uint)(((c >> 5) & 0x1F) * 255 / 31);
                uint b = (uint)(((c >> 10) & 0x1F) * 255 / 31);
                uint a = (i == 0) ? 0u : 255u;
                palette[i] = (r << 24) | (g << 16) | (b << 8) | a;
            }

            // Decode 32x32 4bpp tiles and upscale 4x Nearest-Neighbor to 128x128 for razor-sharp retro pixels
            const int scale = 4;
            const int outW = 32 * scale;
            const int outH = 32 * scale;
            byte[] rgba128 = new byte[outW * outH * 4];

            for (int tile = 0; tile < 16; tile++)
            {
                int tileX = (tile % 4) * 8;
                int tileY = (tile / 4) * 8;
                int tileOffset = tileBaseOffset + tile * 32;

                for (int row = 0; row < 8; row++)
                {
                    int rowOffset = tileOffset + row * 4;
                    int py = tileY + row;

                    for (int colPair = 0; colPair < 4; colPair++)
                    {
                        byte val = bannerData[rowOffset + colPair];
                        uint col0 = palette[val & 0x0F];
                        uint col1 = palette[(val >> 4) & 0x0F];

                        int px0 = tileX + colPair * 2;
                        int px1 = px0 + 1;

                        WriteScaledPixel(rgba128, outW, px0, py, scale, col0);
                        WriteScaledPixel(rgba128, outW, px1, py, scale, col1);
                    }
                }
            }

            info.IconPng = EncodeRgbaToPng(rgba128, outW, outH);
            return info;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteScaledPixel(byte[] buf, int width, int x, int y, int scale, uint rgba)
    {
        byte r = (byte)((rgba >> 24) & 0xFF);
        byte g = (byte)((rgba >> 16) & 0xFF);
        byte b = (byte)((rgba >> 8) & 0xFF);
        byte a = (byte)(rgba & 0xFF);

        for (int dy = 0; dy < scale; dy++)
        {
            int rowStart = ((y * scale + dy) * width + x * scale) * 4;
            for (int dx = 0; dx < scale; dx++)
            {
                int idx = rowStart + dx * 4;
                buf[idx + 0] = r;
                buf[idx + 1] = g;
                buf[idx + 2] = b;
                buf[idx + 3] = a;
            }
        }
    }

    public static byte[] EncodeRgbaToPng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[0..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(ms, "IHDR"u8, ihdr);

        int stride = width * 4;
        byte[] rawScanlines = new byte[height * (stride + 1)];
        for (int y = 0; y < height; y++)
        {
            rawScanlines[y * (stride + 1)] = 0;
            System.Buffer.BlockCopy(rgba, y * stride, rawScanlines, y * (stride + 1) + 1, stride);
        }

        using (var idatMs = new MemoryStream())
        {
            using (var zlib = new ZLibStream(idatMs, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(rawScanlines, 0, rawScanlines.Length);
            }
            WritePngChunk(ms, "IDAT"u8, idatMs.ToArray());
        }

        WritePngChunk(ms, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream s, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBuf, (uint)data.Length);
        s.Write(lenBuf);
        s.Write(type);
        if (!data.IsEmpty) s.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in type) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data) crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc = ~crc;

        Span<byte> crcBuf = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBuf, crc);
        s.Write(crcBuf);
    }

    public static byte[] DefaultCartridgePng
    {
        get
        {
            if (_defaultCartridgePng != null) return _defaultCartridgePng;
            const int w = 32, h = 32, scale = 4;
            byte[] rgba = new byte[(w * scale) * (h * scale) * 4];
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    bool border = ((x == 4 || x == 27) && (y >= 4 && y <= 27)) ||
                                  ((y == 4 || y == 27) && (x >= 4 && x <= 27));
                    bool body = (x > 4 && x < 27) && (y > 4 && y < 27);
                    bool notch = (x >= 24 && x <= 27) && (y >= 4 && y <= 7);
                    bool pin = (y >= 22 && y <= 25) && (x % 3 == 0) && (x >= 8 && x <= 23);
                    bool label = (x >= 8 && x <= 23) && (y >= 8 && y <= 18);

                    if (notch) continue;
                    uint col = 0;
                    if (border) col = 0x8B5CF6FFu;      // Violet Accent
                    else if (pin) col = 0xF59E0BFFu;    // Gold Contacts
                    else if (label) col = 0x38BDF8FFu;  // Cyan Cartridge Screen
                    else if (body) col = 0x1E1738FFu;   // Cartridge Body

                    if (col != 0)
                        WriteScaledPixel(rgba, w * scale, x, y, scale, col);
                }
            }
            _defaultCartridgePng = EncodeRgbaToPng(rgba, w * scale, h * scale);
            return _defaultCartridgePng;
        }
    }

    public static ImageSource GetOrCreateBitmapImage(byte[]? bytes) => FromPngBytes(bytes);

    // Synchronous zero-defect WriteableBitmap decoder for 128x128 RGBA PNGs (eliminates WinUI 3 async stream disposal bug)
    public static ImageSource FromPngBytes(byte[]? bytes)
    {
        try
        {
            byte[] target = (bytes != null && bytes.Length > 32) ? bytes : DefaultCartridgePng;
            int hash = (int)ComputeCrc32(target);
            if (IconCache.TryGetValue(hash, out var cached))
                return cached;

            var wb = TryDecodeOurPngToWriteableBitmap(target);
            if (wb != null)
            {
                IconCache[hash] = wb;
                return wb;
            }

            // Fallback: keep MemoryStream alive (NOT disposed with 'using') so async WIC decoding always succeeds
            var bmp = new BitmapImage();
            var persistentMs = new MemoryStream(target, writable: false);
            bmp.SetSource(persistentMs.AsRandomAccessStream());
            IconCache[hash] = bmp;
            return bmp;
        }
        catch
        {
            if (_defaultCartridgeImage != null) return _defaultCartridgeImage;
            _defaultCartridgeImage = (ImageSource?)TryDecodeOurPngToWriteableBitmap(DefaultCartridgePng) ?? new BitmapImage();
            return _defaultCartridgeImage;
        }
    }

    private static WriteableBitmap? TryDecodeOurPngToWriteableBitmap(byte[] png)
    {
        try
        {
            if (png.Length < 45 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
                return null;

            int width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            int height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            if (width <= 0 || height <= 0 || width > 512 || height > 512)
                return null;

            int pos = 8;
            using var idatCombined = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int chunkLen = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                pos += 8;
                if (pos + chunkLen > png.Length) break;
                if (type == "IDAT")
                {
                    idatCombined.Write(png, pos, chunkLen);
                }
                else if (type == "IEND")
                {
                    break;
                }
                pos += chunkLen + 4; // skip CRC
            }

            if (idatCombined.Length == 0) return null;
            idatCombined.Position = 0;

            int stride = width * 4;
            int expectedLen = height * (stride + 1);
            byte[] scanlines = new byte[expectedLen];
            using (var zlib = new ZLibStream(idatCombined, CompressionMode.Decompress))
            {
                int read = ReadUpTo(zlib, scanlines, 0, expectedLen);
                if (read < expectedLen) return null;
            }

            byte[] bgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * (stride + 1) + 1;
                int dstRow = y * stride;
                for (int x = 0; x < width; x++)
                {
                    byte r = scanlines[srcRow + x * 4 + 0];
                    byte g = scanlines[srcRow + x * 4 + 1];
                    byte b = scanlines[srcRow + x * 4 + 2];
                    byte a = scanlines[srcRow + x * 4 + 3];

                    // Premultiply alpha for WriteableBitmap BGRA8
                    if (a == 255)
                    {
                        bgra[dstRow + x * 4 + 0] = b;
                        bgra[dstRow + x * 4 + 1] = g;
                        bgra[dstRow + x * 4 + 2] = r;
                        bgra[dstRow + x * 4 + 3] = 255;
                    }
                    else if (a > 0)
                    {
                        bgra[dstRow + x * 4 + 0] = (byte)((b * a) / 255);
                        bgra[dstRow + x * 4 + 1] = (byte)((g * a) / 255);
                        bgra[dstRow + x * 4 + 2] = (byte)((r * a) / 255);
                        bgra[dstRow + x * 4 + 3] = a;
                    }
                }
            }

            var wb = new WriteableBitmap(width, height);
            using (var pixelStream = wb.PixelBuffer.AsStream())
            {
                pixelStream.Write(bgra, 0, bgra.Length);
            }
            wb.Invalidate();
            return wb;
        }
        catch
        {
            return null;
        }
    }

    private static string CleanTitle(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        int nullIdx = raw.IndexOf('\0');
        if (nullIdx >= 0) raw = raw.Substring(0, nullIdx);
        return raw.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Trim();
    }
}
