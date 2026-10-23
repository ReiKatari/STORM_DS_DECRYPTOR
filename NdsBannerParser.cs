using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace StormDsDecryptor;

public record BannerAnimFrame(
    byte[] PngBytes,
    byte[] PixelIndices128,
    uint[] PaletteRgba16,
    int DurationMs,
    int BmpIndex,
    int PalIndex,
    bool FlipX,
    bool FlipY);

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
    public List<BannerAnimFrame> AnimFrames { get; set; } = new();

    public string BestTitle =>
        !string.IsNullOrWhiteSpace(TitleEnglish) ? TitleEnglish :
        !string.IsNullOrWhiteSpace(TitleJapanese) ? TitleJapanese :
        !string.IsNullOrWhiteSpace(TitleFrench) ? TitleFrench :
        TitleGerman;

    public byte[]? IconPng { get; set; }
    public string Title => BestTitle;
    public byte[]? PngBytes => IconPng;
    public bool IsAnimated => HasAnimatedIcon;

    public Dictionary<string, string> AllTitles
    {
        get
        {
            var d = new Dictionary<string, string>();
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
    private static readonly ushort[] Crc16Table = InitCrc16Table();
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

    private static ushort[] InitCrc16Table()
    {
        ushort[] table = new ushort[256];
        for (int i = 0; i < 256; i++)
        {
            ushort crc = (ushort)i;
            for (int b = 0; b < 8; b++)
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            table[i] = crc;
        }
        return table;
    }

    public static ushort ComputeCrc16(ReadOnlySpan<byte> data)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in data)
            crc = (ushort)((crc >> 8) ^ Crc16Table[(crc ^ b) & 0xFF]);
        return crc;
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

            // 1. Decode DSi Animated Sequence (0x1240..0x23C0) if present
            if (info.Version >= 0x0103 && bannerData.Length >= 0x23C0)
            {
                for (int s = 0; s < 64; s++)
                {
                    ushort token = BitConverter.ToUInt16(bannerData, 0x2340 + s * 2);
                    int durTicks = token & 0xFF;
                    if (durTicks == 0) break;

                    int bmpIdx = (token >> 8) & 0x07;
                    int palIdx = (token >> 11) & 0x07;
                    bool flipX = (token & (1 << 14)) != 0;
                    bool flipY = (token & (1 << 15)) != 0;

                    int tOff = 0x1240 + bmpIdx * 512;
                    int pOff = 0x2240 + palIdx * 32;

                    // Verify bitmap is not completely zero
                    bool hasData = false;
                    for (int k = 0; k < 512; k++)
                    {
                        if (bannerData[tOff + k] != 0) { hasData = true; break; }
                    }
                    if (!hasData && s == 0) break;

                    var (pngBytes, idx128, pal16) = DecodeFrameToPngAndIndices(bannerData, tOff, pOff, flipX, flipY);
                    int durMs = Math.Max(33, (int)Math.Round(durTicks * 1000.0 / 60.0));
                    info.AnimFrames.Add(new BannerAnimFrame(pngBytes, idx128, pal16, durMs, bmpIdx, palIdx, flipX, flipY));
                }

                if (info.AnimFrames.Count > 0)
                {
                    info.HasAnimatedIcon = info.AnimFrames.Count > 1;
                    info.AnimatedFrameCount = info.AnimFrames.Count;
                }
            }

            // 2. Decode standard NTR icon (or fallback to DSi frame 0 if NTR area is blank)
            int nonZeroNtr = 0;
            for (int i = 0x0020; i < 0x0240; i++)
            {
                if (bannerData[i] != 0) nonZeroNtr++;
            }

            if (nonZeroNtr >= 16)
            {
                var (ntrPng, ntrIdx, ntrPal) = DecodeFrameToPngAndIndices(bannerData, 0x0020, 0x0220, false, false);
                info.IconPng = ntrPng;
                if (info.AnimFrames.Count == 0)
                {
                    info.AnimFrames.Add(new BannerAnimFrame(ntrPng, ntrIdx, ntrPal, 100, 0, 0, false, false));
                }
            }
            else if (info.AnimFrames.Count > 0)
            {
                info.IconPng = info.AnimFrames[0].PngBytes;
            }
            else
            {
                var (defPng, defIdx, defPal) = DecodeFrameToPngAndIndices(bannerData, 0x0020, 0x0220, false, false);
                info.IconPng = defPng;
                info.AnimFrames.Add(new BannerAnimFrame(defPng, defIdx, defPal, 100, 0, 0, false, false));
            }

            return info;
        }
        catch
        {
            return null;
        }
    }

    private static (byte[] PngBytes, byte[] Indices128, uint[] Palette16) DecodeFrameToPngAndIndices(
        byte[] bannerData,
        int tileBaseOffset,
        int paletteBaseOffset,
        bool flipX,
        bool flipY)
    {
        uint[] palette = new uint[16];
        for (int i = 0; i < 16; i++)
        {
            ushort c = BitConverter.ToUInt16(bannerData, paletteBaseOffset + i * 2);
            uint r = (uint)(((c >> 0) & 0x1F) * 255 / 31);
            uint g = (uint)(((c >> 5) & 0x1F) * 255 / 31);
            uint b = (uint)(((c >> 10) & 0x1F) * 255 / 31);
            uint a = (i == 0) ? 0u : 255u;
            palette[i] = (r << 24) | (g << 16) | (b << 8) | a;
        }

        const int scale = 4;
        const int outW = 32 * scale;
        const int outH = 32 * scale;
        byte[] rgba128 = new byte[outW * outH * 4];
        byte[] indices128 = new byte[outW * outH];

        for (int tile = 0; tile < 16; tile++)
        {
            int tileX = (tile % 4) * 8;
            int tileY = (tile / 4) * 8;
            int tileOffset = tileBaseOffset + tile * 32;

            for (int row = 0; row < 8; row++)
            {
                int rowOffset = tileOffset + row * 4;
                int py = tileY + row;
                int dstY = flipY ? (31 - py) : py;

                for (int colPair = 0; colPair < 4; colPair++)
                {
                    byte val = bannerData[rowOffset + colPair];
                    int idx0 = val & 0x0F;
                    int idx1 = (val >> 4) & 0x0F;

                    int px0 = tileX + colPair * 2;
                    int px1 = px0 + 1;

                    int dstX0 = flipX ? (31 - px0) : px0;
                    int dstX1 = flipX ? (31 - px1) : px1;

                    WriteScaledPixelAndIndex(rgba128, indices128, outW, dstX0, dstY, scale, palette[idx0], (byte)idx0);
                    WriteScaledPixelAndIndex(rgba128, indices128, outW, dstX1, dstY, scale, palette[idx1], (byte)idx1);
                }
            }
        }

        byte[] png = EncodeRgbaToPng(rgba128, outW, outH);
        return (png, indices128, palette);
    }

    private static void WriteScaledPixelAndIndex(byte[] rgbaBuf, byte[] idxBuf, int width, int x, int y, int scale, uint rgba, byte palIdx)
    {
        byte r = (byte)((rgba >> 24) & 0xFF);
        byte g = (byte)((rgba >> 16) & 0xFF);
        byte b = (byte)((rgba >> 8) & 0xFF);
        byte a = (byte)(rgba & 0xFF);

        for (int dy = 0; dy < scale; dy++)
        {
            int rowStart = (y * scale + dy) * width;
            for (int dx = 0; dx < scale; dx++)
            {
                int cell = rowStart + (x * scale + dx);
                idxBuf[cell] = palIdx;
                int idx = cell * 4;
                rgbaBuf[idx + 0] = r;
                rgbaBuf[idx + 1] = g;
                rgbaBuf[idx + 2] = b;
                rgbaBuf[idx + 3] = a;
            }
        }
    }

    // ==================== TRUE ANIMATED GIF89a EXPORTER (128x128) ====================

    public static byte[] ExportAnimatedGif(IReadOnlyList<BannerAnimFrame> frames)
    {
        if (frames == null || frames.Count == 0)
            return Array.Empty<byte>();

        const int width = 128;
        const int height = 128;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        // 1. GIF89a Header & Logical Screen Descriptor
        bw.Write(Encoding.ASCII.GetBytes("GIF89a"));
        bw.Write((ushort)width);
        bw.Write((ushort)height);
        bw.Write((byte)0xF3); // Global Color Table Flag=1, Color Res=7, Sort=0, GCT Size=3 (16 colors)
        bw.Write((byte)0);    // Background color index
        bw.Write((byte)0);    // Pixel aspect ratio

        // Global Color Table from Frame 0
        WriteGifPalette16(bw, frames[0].PaletteRgba16);

        // 2. Netscape 2.0 Infinite Loop Application Extension
        if (frames.Count > 1)
        {
            bw.Write(new byte[] { 0x21, 0xFF, 0x0B });
            bw.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write(new byte[] { 0x03, 0x01, 0x00, 0x00, 0x00 }); // Loop forever
        }

        // 3. Write each animation frame
        foreach (var frame in frames)
        {
            // Graphic Control Extension
            ushort delayCentiseconds = (ushort)Math.Max(2, (int)Math.Round(frame.DurationMs / 10.0));
            bw.Write((byte)0x21);
            bw.Write((byte)0xF9);
            bw.Write((byte)0x04);
            bw.Write((byte)0x09); // Disposal=2 (Restore to bg), Transparent Color Flag=1
            bw.Write(delayCentiseconds);
            bw.Write((byte)0);    // Transparent color index = 0
            bw.Write((byte)0);    // Block terminator

            // Image Descriptor
            bw.Write((byte)0x2C);
            bw.Write((ushort)0);
            bw.Write((ushort)0);
            bw.Write((ushort)width);
            bw.Write((ushort)height);
            bw.Write((byte)0x83); // Local Color Table Flag=1, Size=3 (16 colors)

            WriteGifPalette16(bw, frame.PaletteRgba16);

            // LZW Minimum Code Size = 7 (128-symbol uncompressed clear-code blocks for 100% lossless simplicity)
            WriteGifUncompressedLzw7(bw, frame.PixelIndices128);
        }

        // GIF Trailer
        bw.Write((byte)0x3B);
        bw.Flush();
        return ms.ToArray();
    }

    private static void WriteGifPalette16(BinaryWriter bw, uint[] pal16)
    {
        for (int i = 0; i < 16; i++)
        {
            if (i == 0 || i >= pal16.Length)
            {
                bw.Write((byte)14);
                bw.Write((byte)10);
                bw.Write((byte)26);
            }
            else
            {
                uint c = pal16[i];
                bw.Write((byte)((c >> 24) & 0xFF));
                bw.Write((byte)((c >> 16) & 0xFF));
                bw.Write((byte)((c >> 8) & 0xFF));
            }
        }
    }

    private static void WriteGifUncompressedLzw7(BinaryWriter bw, byte[] indices)
    {
        // With LZW minimum code size = 7:
        // Clear Code = 128 (0x80), EOI Code = 129 (0x81), pixel values 0..15 fit in 8-bit codes!
        // By emitting Clear Code (0x80) every 120 pixels, the LZW code width stays strictly 8 bits (1 byte per code)!
        bw.Write((byte)7); // LZW Minimum Code Size = 7

        using var codeStream = new MemoryStream(indices.Length + 512);
        for (int i = 0; i < indices.Length; i++)
        {
            if (i % 120 == 0)
                codeStream.WriteByte(128); // Clear code
            codeStream.WriteByte((byte)(indices[i] & 0x0F));
        }
        codeStream.WriteByte(129); // EOI code

        byte[] allCodes = codeStream.ToArray();
        int pos = 0;
        while (pos < allCodes.Length)
        {
            int chunk = Math.Min(255, allCodes.Length - pos);
            bw.Write((byte)chunk);
            bw.Write(allCodes, pos, chunk);
            pos += chunk;
        }
        bw.Write((byte)0); // Sub-block terminator
    }

    // ==================== BANNER & HEADER STUDIO (EDITOR & PNG QUANTIZER) ====================

    public static (bool Success, string Message) SaveBannerAndHeaderChanges(
        string romPath,
        string newInternalTitle,
        string newGameCode,
        Dictionary<string, string> editedTitles,
        byte[]? replacementPngBytes,
        bool createBackup)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
                return (false, "Файл образа не найден на диске.");

            byte[] rom = File.ReadAllBytes(romPath);
            if (rom.Length < 0x200)
                return (false, "Файл слишком мал для заголовка NDS/DSi.");

            if (createBackup)
            {
                File.Copy(romPath, romPath + ".bak", overwrite: true);
            }

            // 1. Update 12-byte ASCII Internal Header Title (0x00..0x0B)
            if (!string.IsNullOrWhiteSpace(newInternalTitle))
            {
                Array.Clear(rom, 0x00, 12);
                byte[] asciiTitle = Encoding.ASCII.GetBytes(newInternalTitle.Trim());
                Array.Copy(asciiTitle, 0, rom, 0x00, Math.Min(12, asciiTitle.Length));
            }

            // 2. Update 4-byte ASCII GameCode (0x0C..0x0F)
            if (!string.IsNullOrWhiteSpace(newGameCode) && newGameCode.Trim().Length == 4)
            {
                byte[] asciiCode = Encoding.ASCII.GetBytes(newGameCode.Trim().ToUpperInvariant());
                Array.Copy(asciiCode, 0, rom, 0x0C, 4);
            }

            // 3. Recalculate Header CRC16 at 0x015E (over 0x0000..0x015D)
            ushort hdrCrc = ComputeCrc16(rom.AsSpan(0, 0x015E));
            BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan(0x015E, 2), hdrCrc);

            // 4. Update Banner Titles & Optional Icon at BannerOffset (0x68)
            uint bannerOffset = BitConverter.ToUInt32(rom, 0x68);
            if (bannerOffset > 0 && bannerOffset + 0x840 <= rom.Length)
            {
                ushort bVer = BitConverter.ToUInt16(rom, (int)bannerOffset);

                void WriteTitleSlot(int relOffset, string langKey)
                {
                    if ((int)bannerOffset + relOffset + 256 > rom.Length) return;
                    if (!editedTitles.TryGetValue(langKey, out string? val) || val == null) return;

                    Array.Clear(rom, (int)bannerOffset + relOffset, 256);
                    string normalized = val.Replace("\r\n", "\n").Replace("\r", "\n");
                    byte[] utf16 = Encoding.Unicode.GetBytes(normalized);
                    Array.Copy(utf16, 0, rom, (int)bannerOffset + relOffset, Math.Min(254, utf16.Length));
                }

                WriteTitleSlot(0x240, "JP");
                WriteTitleSlot(0x340, "EN");
                WriteTitleSlot(0x440, "FR");
                WriteTitleSlot(0x540, "DE");
                WriteTitleSlot(0x640, "IT");
                WriteTitleSlot(0x740, "ES");
                if (bVer >= 0x0002) WriteTitleSlot(0x840, "ZH");
                if (bVer >= 0x0003) WriteTitleSlot(0x940, "KO");

                // 5. If replacement PNG icon was supplied, quantize to 32x32 4bpp + 16-color BGR555 palette
                if (replacementPngBytes != null && replacementPngBytes.Length > 32)
                {
                    if (TryQuantizePngToNdsBanner(replacementPngBytes, out byte[] tiles512, out byte[] pal32))
                    {
                        Array.Copy(tiles512, 0, rom, (int)bannerOffset + 0x0020, 512);
                        Array.Copy(pal32, 0, rom, (int)bannerOffset + 0x0220, 32);

                        // Also update DSi Animated Frame 0 if Version >= 0x0103
                        if (bVer >= 0x0103 && bannerOffset + 0x23C0 <= rom.Length)
                        {
                            Array.Copy(tiles512, 0, rom, (int)bannerOffset + 0x1240, 512);
                            Array.Copy(pal32, 0, rom, (int)bannerOffset + 0x2240, 32);
                        }
                    }
                }

                // 6. Recalculate all Banner CRC16 checksums (0x0002, 0x0004, 0x0006, 0x0008)
                ushort crcV1 = ComputeCrc16(rom.AsSpan((int)bannerOffset + 0x0020, 0x0820));
                BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan((int)bannerOffset + 0x0002, 2), crcV1);

                if (bVer >= 0x0002 && bannerOffset + 0x0940 <= rom.Length)
                {
                    ushort crcV2 = ComputeCrc16(rom.AsSpan((int)bannerOffset + 0x0020, 0x0920));
                    BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan((int)bannerOffset + 0x0004, 2), crcV2);
                }

                if (bVer >= 0x0003 && bannerOffset + 0x0A40 <= rom.Length)
                {
                    ushort crcV3 = ComputeCrc16(rom.AsSpan((int)bannerOffset + 0x0020, 0x0A20));
                    BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan((int)bannerOffset + 0x0006, 2), crcV3);
                }

                if (bVer >= 0x0103 && bannerOffset + 0x23C0 <= rom.Length)
                {
                    ushort crcTwl = ComputeCrc16(rom.AsSpan((int)bannerOffset + 0x1240, 0x1180));
                    BinaryPrimitives.WriteUInt16LittleEndian(rom.AsSpan((int)bannerOffset + 0x0008, 2), crcTwl);
                }
            }

            string tmpPath = romPath + ".bnr_tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(rom, 0, rom.Length);
                fs.Flush(true);
            }
            File.Copy(tmpPath, romPath, overwrite: true);
            File.Delete(tmpPath);

            return (true, "Баннер, заголовки и контрольные суммы CRC16 успешно обновлены!");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public static bool TryQuantizePngToNdsBanner(byte[] pngBytes, out byte[] tiles512, out byte[] pal32)
    {
        tiles512 = new byte[512];
        pal32 = new byte[32];

        try
        {
            if (!TryDecodePngToRgba(pngBytes, out byte[] rgba, out int w, out int h) || w <= 0 || h <= 0)
                return false;

            // Resize Nearest-Neighbor to 32x32
            uint[] px32 = new uint[32 * 32];
            var colorHistogram = new Dictionary<ushort, int>();

            for (int y = 0; y < 32; y++)
            {
                int sy = Math.Clamp(y * h / 32, 0, h - 1);
                for (int x = 0; x < 32; x++)
                {
                    int sx = Math.Clamp(x * w / 32, 0, w - 1);
                    int idx = (sy * w + sx) * 4;
                    byte r = rgba[idx + 0];
                    byte g = rgba[idx + 1];
                    byte b = rgba[idx + 2];
                    byte a = rgba[idx + 3];

                    if (a < 64)
                    {
                        px32[y * 32 + x] = 0;
                    }
                    else
                    {
                        ushort bgr555 = (ushort)(((r >> 3) & 0x1F) | (((g >> 3) & 0x1F) << 5) | (((b >> 3) & 0x1F) << 10));
                        px32[y * 32 + x] = (uint)(0x80000000u | bgr555);
                        colorHistogram[bgr555] = colorHistogram.GetValueOrDefault(bgr555) + 1;
                    }
                }
            }

            // Pick up to 15 most frequent distinct BGR555 colors (index 0 = transparent)
            var topColors = colorHistogram.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Take(15).ToList();
            while (topColors.Count < 15)
                topColors.Add(0);

            ushort[] pal16 = new ushort[16];
            pal16[0] = 0;
            for (int i = 0; i < 15; i++)
            {
                pal16[i + 1] = topColors[i];
                BinaryPrimitives.WriteUInt16LittleEndian(pal32.AsSpan((i + 1) * 2, 2), topColors[i]);
            }

            int FindClosestPaletteIndex(ushort targetBgr)
            {
                int tr = targetBgr & 0x1F;
                int tg = (targetBgr >> 5) & 0x1F;
                int tb = (targetBgr >> 10) & 0x1F;

                int bestIdx = 1;
                int bestDist = int.MaxValue;
                for (int i = 1; i < 16; i++)
                {
                    ushort c = pal16[i];
                    int dr = tr - (c & 0x1F);
                    int dg = tg - ((c >> 5) & 0x1F);
                    int db = tb - ((c >> 10) & 0x1F);
                    int dist = dr * dr + dg * dg + db * db;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = i;
                        if (dist == 0) break;
                    }
                }
                return bestIdx;
            }

            // Encode 32x32 into 16 8x8 4bpp tiles (512 bytes)
            for (int tile = 0; tile < 16; tile++)
            {
                int tileX = (tile % 4) * 8;
                int tileY = (tile / 4) * 8;
                int tileOffset = tile * 32;

                for (int row = 0; row < 8; row++)
                {
                    int rowOffset = tileOffset + row * 4;
                    int py = tileY + row;
                    for (int colPair = 0; colPair < 4; colPair++)
                    {
                        int px0 = tileX + colPair * 2;
                        int px1 = px0 + 1;

                        uint v0 = px32[py * 32 + px0];
                        uint v1 = px32[py * 32 + px1];

                        int c0 = v0 == 0 ? 0 : FindClosestPaletteIndex((ushort)(v0 & 0x7FFF));
                        int c1 = v1 == 0 ? 0 : FindClosestPaletteIndex((ushort)(v1 & 0x7FFF));

                        tiles512[rowOffset + colPair] = (byte)((c0 & 0x0F) | ((c1 & 0x0F) << 4));
                    }
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string CleanTitle(string raw)
    {
        int nullIdx = raw.IndexOf('\0');
        if (nullIdx >= 0) raw = raw.Substring(0, nullIdx);
        var lines = raw.Replace("\r\n", "\n").Replace('\r', '\n')
                       .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(" — ", lines).Trim();
    }

    public static byte[] EncodeRgbaToPng(byte[] rgba, int width, int height)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });

        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(0, 4), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        WritePngChunk(ms, "IHDR", ihdr);

        using (var idatRaw = new MemoryStream())
        {
            int stride = width * 4;
            for (int y = 0; y < height; y++)
            {
                idatRaw.WriteByte(0);
                idatRaw.Write(rgba, y * stride, stride);
            }
            byte[] rawBytes = idatRaw.ToArray();

            using var compressed = new MemoryStream();
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(rawBytes, 0, rawBytes.Length);
            }
            WritePngChunk(ms, "IDAT", compressed.ToArray());
        }

        WritePngChunk(ms, "IEND", ReadOnlySpan<byte>.Empty);
        return ms.ToArray();
    }

    private static void WritePngChunk(Stream s, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        s.Write(lenBytes);

        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes);
        if (data.Length > 0)
            s.Write(data);

        uint crc = 0xFFFFFFFFu;
        foreach (byte b in typeBytes)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (byte b in data)
            crc = Crc32Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc = ~crc;

        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        s.Write(crcBytes);
    }

    public static byte[] DefaultCartridgePng => _defaultCartridgePng ??= BuildDefaultCartridgePng();

    private static byte[] BuildDefaultCartridgePng()
    {
        const int w = 128;
        const int h = 128;
        byte[] rgba = new byte[w * h * 4];

        for (int y = 8; y < 120; y++)
        {
            for (int x = 14; x < 114; x++)
            {
                if (x + y < 32) continue;
                int idx = (y * w + x) * 4;
                bool isBorder = (x <= 17 || x >= 110 || y <= 11 || y >= 116);
                bool isLabel = (x >= 26 && x <= 102 && y >= 26 && y <= 84);
                bool isPins = (x >= 28 && x <= 100 && y >= 96 && y <= 112 && ((x / 6) % 2 == 0));

                if (isBorder)
                {
                    rgba[idx + 0] = 139; rgba[idx + 1] = 92; rgba[idx + 2] = 246; rgba[idx + 3] = 255;
                }
                else if (isLabel)
                {
                    rgba[idx + 0] = 0; rgba[idx + 1] = 229; rgba[idx + 2] = 255; rgba[idx + 3] = 255;
                }
                else if (isPins)
                {
                    rgba[idx + 0] = 245; rgba[idx + 1] = 158; rgba[idx + 2] = 11; rgba[idx + 3] = 255;
                }
                else
                {
                    rgba[idx + 0] = 30; rgba[idx + 1] = 24; rgba[idx + 2] = 54; rgba[idx + 3] = 255;
                }
            }
        }
        return EncodeRgbaToPng(rgba, w, h);
    }

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

    private static bool TryDecodePngToRgba(byte[] png, out byte[] rgbaOut, out int width, out int height)
    {
        rgbaOut = Array.Empty<byte>();
        width = 0;
        height = 0;

        try
        {
            if (png.Length < 45 || png[0] != 137 || png[1] != 80 || png[2] != 78 || png[3] != 71)
                return false;

            width = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4));
            height = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4));
            byte bitDepth = png[24];
            byte colorType = png[25];

            if (width <= 0 || height <= 0 || width > 1024 || height > 1024 || bitDepth != 8 || (colorType != 6 && colorType != 2))
                return false;

            int pos = 8;
            using var idatCombined = new MemoryStream();
            while (pos + 8 <= png.Length)
            {
                int chunkLen = (int)BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(pos, 4));
                string type = Encoding.ASCII.GetString(png, pos + 4, 4);
                pos += 8;
                if (pos + chunkLen > png.Length) break;
                if (type == "IDAT")
                    idatCombined.Write(png, pos, chunkLen);
                else if (type == "IEND")
                    break;
                pos += chunkLen + 4;
            }

            byte[] compressed = idatCombined.ToArray();
            if (compressed.Length == 0) return false;

            int bpp = colorType == 6 ? 4 : 3;
            int stride = width * bpp;
            byte[] rawScanlines = new byte[height * (stride + 1)];
            using (var inMs = new MemoryStream(compressed))
            using (var zlib = new ZLibStream(inMs, CompressionMode.Decompress))
            {
                int got = ReadUpTo(zlib, rawScanlines, 0, rawScanlines.Length);
                if (got < rawScanlines.Length) return false;
            }

            rgbaOut = new byte[width * height * 4];
            byte[] prevLine = new byte[stride];
            byte[] curLine = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int srcRow = y * (stride + 1);
                byte filter = rawScanlines[srcRow];
                for (int i = 0; i < stride; i++)
                {
                    byte raw = rawScanlines[srcRow + 1 + i];
                    byte a = i >= bpp ? curLine[i - bpp] : (byte)0;
                    byte b = prevLine[i];
                    byte c = i >= bpp ? prevLine[i - bpp] : (byte)0;

                    curLine[i] = filter switch
                    {
                        1 => (byte)(raw + a),
                        2 => (byte)(raw + b),
                        3 => (byte)(raw + ((a + b) >> 1)),
                        4 => (byte)(raw + PaethPredictor(a, b, c)),
                        _ => raw
                    };
                }

                for (int x = 0; x < width; x++)
                {
                    int sIdx = x * bpp;
                    int dIdx = (y * width + x) * 4;
                    rgbaOut[dIdx + 0] = curLine[sIdx + 0];
                    rgbaOut[dIdx + 1] = curLine[sIdx + 1];
                    rgbaOut[dIdx + 2] = curLine[sIdx + 2];
                    rgbaOut[dIdx + 3] = bpp == 4 ? curLine[sIdx + 3] : (byte)255;
                }

                System.Buffer.BlockCopy(curLine, 0, prevLine, 0, stride);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int PaethPredictor(int a, int b, int c)
    {
        int p = a + b - c;
        int pa = Math.Abs(p - a);
        int pb = Math.Abs(p - b);
        int pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static WriteableBitmap? TryDecodeOurPngToWriteableBitmap(byte[] png)
    {
        try
        {
            if (!TryDecodePngToRgba(png, out byte[] rgba, out int width, out int height))
                return null;

            byte[] bgra = new byte[width * height * 4];
            for (int i = 0; i < width * height; i++)
            {
                int idx = i * 4;
                byte r = rgba[idx + 0];
                byte g = rgba[idx + 1];
                byte b = rgba[idx + 2];
                byte a = rgba[idx + 3];

                bgra[idx + 0] = (byte)(b * a / 255);
                bgra[idx + 1] = (byte)(g * a / 255);
                bgra[idx + 2] = (byte)(r * a / 255);
                bgra[idx + 3] = a;
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

    public static ImageSource GetOrCreateBitmapImage(byte[]? pngBytes) => FromPngBytes(pngBytes);
}
