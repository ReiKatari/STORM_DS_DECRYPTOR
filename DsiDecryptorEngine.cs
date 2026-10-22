using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace StormDsDecryptor;

public class RomInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameCode { get; set; } = string.Empty;
    public string MakerCode { get; set; } = string.Empty;
    public string Region { get; set; } = "FREE";
    public byte UnitCode { get; set; }
    public long FileSize { get; set; }
    public bool IsDsiRom { get; set; }
    public bool HasModcrypt { get; set; }
    public bool IsEncrypted { get; set; }
    public bool NeedsCompatibilityPatch { get; set; }
    public uint Modcrypt1Offset { get; set; }
    public uint Modcrypt1Size { get; set; }
    public uint Modcrypt2Offset { get; set; }
    public uint Modcrypt2Size { get; set; }
    public BannerInfo? Banner { get; set; }
}

public class RomDetailedInfo
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string InternalHeaderTitle { get; set; } = string.Empty;
    public string GameCode { get; set; } = string.Empty;
    public string MakerCode { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public byte UnitCode { get; set; }
    public string UnitCodeDescription { get; set; } = string.Empty;
    public byte RomVersion { get; set; }
    public uint Arm9Offset { get; set; }
    public uint Arm9EntryAddress { get; set; }
    public uint Arm9RamAddress { get; set; }
    public uint Arm9Size { get; set; }
    public uint Arm7Offset { get; set; }
    public uint Arm7EntryAddress { get; set; }
    public uint Arm7RamAddress { get; set; }
    public uint Arm7Size { get; set; }
    public uint FntOffset { get; set; }
    public uint FntSize { get; set; }
    public uint FatOffset { get; set; }
    public uint FatSize { get; set; }
    public uint BannerOffset { get; set; }
    public uint TotalUsedRomSize { get; set; }
    public uint Modcrypt1Offset { get; set; }
    public uint Modcrypt1Size { get; set; }
    public uint Modcrypt2Offset { get; set; }
    public uint Modcrypt2Size { get; set; }
    public string TwlTitleId { get; set; } = string.Empty;
    public ushort HeaderCrc16 { get; set; }
    public ushort CalculatedHeaderCrc16 { get; set; }
    public ushort SecureAreaCrc16 { get; set; }
    public string Crc32Hex { get; set; } = string.Empty;
    public string Md5Hex { get; set; } = string.Empty;
    public string Sha1Hex { get; set; } = string.Empty;
    public string Sha256Hex { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
    public BannerInfo? Banner { get; set; }
    public string FullPath => FilePath;
    public long FileSizeBytes => FileSize;
    public string FormatType => IsEncrypted ? "DSi Modcrypt" : UnitCodeDescription;
    public string InternalTitle => InternalHeaderTitle;
    public bool HasAnimatedBanner => Banner?.IsAnimated == true;
    public string Crc32 => $"0x{Crc32Hex}";
    public string Md5 => Md5Hex;
    public string Sha1 => Sha1Hex;
    public string Sha256 => Sha256Hex;
    public System.Collections.Generic.Dictionary<string, string> BannerTitles => Banner?.AllTitles ?? new();
}

public static class DsiDecryptorEngine
{
    private static readonly ushort[] Crc16Table = InitCrc16Table();

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

    public static string GetRegionFromCode(string gameCode)
    {
        if (string.IsNullOrEmpty(gameCode) || gameCode.Length < 4) return "FREE";
        return char.ToUpperInvariant(gameCode[3]) switch
        {
            'J' => "JPN",
            'E' or 'T' => "USA",
            'P' or 'D' or 'F' or 'I' or 'S' or 'H' or 'X' or 'Y' or 'Z' => "EUR",
            'V' => "EUR/AUS",
            'U' => "AUS",
            'K' => "KOR",
            'C' => "CHN",
            'A' or 'W' => "FREE",
            _ => "NDS"
        };
    }

    private static void ROL16(Span<byte> val, int n)
    {
        int n_coarse = n >> 3;
        int n_fine = n & 7;
        Span<byte> tmp = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
            tmp[i] = val[(i - n_coarse) & 0xF];
        for (int i = 0; i < 16; i++)
            val[i] = (byte)((tmp[i] << n_fine) | (tmp[(i - 1) & 0xF] >> (8 - n_fine)));
    }

    public static byte[] DeriveNormalKey(ReadOnlySpan<byte> keyX, ReadOnlySpan<byte> keyY)
    {
        ReadOnlySpan<byte> keyConst = new byte[] {
            0xFF, 0xFE, 0xFB, 0x4E, 0x29, 0x59, 0x02, 0x58,
            0x2A, 0x68, 0x0F, 0x5F, 0x1A, 0x4F, 0x3E, 0x79
        };
        byte[] tmp = new byte[16];
        for (int i = 0; i < 16; i++)
            tmp[i] = (byte)(keyX[i] ^ keyY[i]);

        uint carry = 0;
        for (int i = 0; i < 16; i++)
        {
            uint res = (uint)(tmp[i] + keyConst[15 - i] + carry);
            tmp[i] = (byte)(res & 0xFF);
            carry = res >> 8;
        }

        ROL16(tmp, 42);
        return tmp;
    }

    private static void AddCtr(Span<byte> ctr, uint carry)
    {
        Span<uint> counter = stackalloc uint[4];
        for (int i = 0; i < 4; i++)
            counter[i] = ((uint)ctr[i * 4 + 0] << 24) | ((uint)ctr[i * 4 + 1] << 16) |
                         ((uint)ctr[i * 4 + 2] << 8) | (uint)ctr[i * 4 + 3];

        for (int i = 3; i >= 0; i--)
        {
            ulong sum = (ulong)counter[i] + carry;
            carry = (uint)(sum >> 32);
            counter[i] = (uint)sum;
        }

        for (int i = 0; i < 4; i++)
        {
            ctr[i * 4 + 0] = (byte)(counter[i] >> 24);
            ctr[i * 4 + 1] = (byte)(counter[i] >> 16);
            ctr[i * 4 + 2] = (byte)(counter[i] >> 8);
            ctr[i * 4 + 3] = (byte)(counter[i] >> 0);
        }
    }

    // Batch AES-ECB Counter Mode (256 blocks = 4096 bytes per TransformBlock call for 5x-7x AES-NI throughput)
    public static void CryptArea(Aes aes, byte[] ctrInit, byte[] rom, uint offset, uint size)
    {
        byte[] ctr = new byte[16];
        for (int i = 0; i < 16; i++)
            ctr[i] = ctrInit[15 - i];

        const int batchBlocks = 256;
        const int batchBytes = batchBlocks * 16;
        byte[] ctrBatch = new byte[batchBytes];
        byte[] streamBatch = new byte[batchBytes];

        using var encryptor = aes.CreateEncryptor();

        uint pos = 0;
        while (pos < size)
        {
            uint remaining = size - pos;
            int blocksThisTurn = (int)Math.Min(batchBlocks, (remaining + 15) / 16);
            int bytesToTransform = blocksThisTurn * 16;

            for (int b = 0; b < blocksThisTurn; b++)
            {
                Buffer.BlockCopy(ctr, 0, ctrBatch, b * 16, 16);
                AddCtr(ctr, 1);
            }

            encryptor.TransformBlock(ctrBatch, 0, bytesToTransform, streamBatch, 0);

            for (int b = 0; b < blocksThisTurn; b++)
            {
                uint blockOffset = pos + (uint)(b * 16);
                uint blockLen = Math.Min(16u, size - blockOffset);
                int streamBase = b * 16;
                for (uint k = 0; k < blockLen; k++)
                {
                    rom[offset + blockOffset + k] ^= streamBatch[streamBase + 15 - (int)k];
                }
            }

            pos += (uint)bytesToTransform;
        }
    }

    public static byte[] BuildKeyX(byte[] header)
    {
        byte[] keyX = new byte[16];
        byte[] nintendo = Encoding.ASCII.GetBytes("Nintendo");
        Array.Copy(nintendo, 0, keyX, 0, 8);
        Array.Copy(header, 0x0C, keyX, 8, 4);
        keyX[12] = header[0x0C + 3];
        keyX[13] = header[0x0C + 2];
        keyX[14] = header[0x0C + 1];
        keyX[15] = header[0x0C + 0];
        return keyX;
    }

    public static ushort CalcHeaderCRC16(byte[] data, int offset, int len)
    {
        ushort crc = 0xFFFF;
        int end = offset + len;
        for (int i = offset; i < end; i++)
        {
            crc = (ushort)((crc >> 8) ^ Crc16Table[(crc ^ data[i]) & 0xFF]);
        }
        return crc;
    }

    public static ushort CalcHeaderCRC16(byte[] data, int len) => CalcHeaderCRC16(data, 0, len);

    public static bool IsRomHeader(byte[] header)
    {
        if (header == null || header.Length < 0x160) return false;
        ushort headerCrc = BitConverter.ToUInt16(header, 0x15E);
        if (headerCrc == 0 || headerCrc == 0xFFFF) return false;
        return headerCrc == CalcHeaderCRC16(header, 0x15E);
    }

    public static byte[]? FindWorkingNormalKey(byte[] rom, uint offset, uint size, int ivOffset)
    {
        if (offset == 0 || size == 0 || offset + size > rom.Length) return null;

        byte[] keyX = BuildKeyX(rom);
        byte[] iv = new byte[16];
        Array.Copy(rom, ivOffset, iv, 0, 16);

        int trialLen = (int)Math.Min(size, 512);
        ReadOnlySpan<int> hashOffsets = stackalloc int[] { 0x350, 0x328, 0x364 };

        foreach (int hashOff in hashOffsets)
        {
            if (hashOff + 16 > rom.Length) continue;

            bool allZero = true;
            for (int k = 0; k < 16; k++)
            {
                if (rom[hashOff + k] != 0) { allZero = false; break; }
            }
            if (allZero) continue;

            byte[] keyY = new byte[16];
            Array.Copy(rom, hashOff, keyY, 0, 16);
            byte[] normalKey = DeriveNormalKey(keyX, keyY);
            byte[] keySwap = new byte[16];
            for (int i = 0; i < 16; i++) keySwap[i] = normalKey[15 - i];

            byte[] trialBuf = new byte[trialLen];
            Array.Copy(rom, offset, trialBuf, 0, trialLen);

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = keySwap;
                CryptArea(aes, iv, trialBuf, 0, (uint)trialLen);
            }

            int trialZeros = 0;
            for (int i = 0; i < trialLen; i++)
                if (trialBuf[i] == 0) trialZeros++;

            if (trialZeros >= 15)
                return normalKey;
        }

        return null;
    }

    public static bool IsAreaEncrypted(Stream fs, uint offset, uint size, int ivOffset, byte[] header, long fileLength)
    {
        if (offset == 0 || size == 0 || offset + size > fileLength) return false;

        int sampleLen = (int)Math.Min(size, 512);
        byte[] sample = new byte[sampleLen];
        fs.Seek(offset, SeekOrigin.Begin);
        fs.ReadExactly(sample, 0, sampleLen);

        int origZeros = 0;
        for (int i = 0; i < sampleLen; i++)
            if (sample[i] == 0) origZeros++;

        if (origZeros >= 25)
            return false;

        byte[] keyX = BuildKeyX(header);
        byte[] iv = new byte[16];
        Array.Copy(header, ivOffset, iv, 0, 16);

        ReadOnlySpan<int> hashOffsets = stackalloc int[] { 0x350, 0x328, 0x364 };
        foreach (int hashOff in hashOffsets)
        {
            if (hashOff + 16 > header.Length) continue;

            bool allZero = true;
            for (int k = 0; k < 16; k++)
            {
                if (header[hashOff + k] != 0) { allZero = false; break; }
            }
            if (allZero) continue;

            byte[] keyY = new byte[16];
            Array.Copy(header, hashOff, keyY, 0, 16);
            byte[] normalKey = DeriveNormalKey(keyX, keyY);
            byte[] keySwap = new byte[16];
            for (int i = 0; i < 16; i++) keySwap[i] = normalKey[15 - i];

            byte[] trialBuf = new byte[sampleLen];
            Array.Copy(sample, 0, trialBuf, 0, sampleLen);

            using (var aes = Aes.Create())
            {
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                aes.Key = keySwap;
                CryptArea(aes, iv, trialBuf, 0, (uint)sampleLen);
            }

            int trialZeros = 0;
            for (int i = 0; i < sampleLen; i++)
                if (trialBuf[i] == 0) trialZeros++;

            if (trialZeros >= 15)
                return true;
        }

        return false;
    }

    public static bool IsAreaEncryptedBuffer(byte[] rom, uint offset, uint size, int ivOffset)
    {
        if (offset == 0 || size == 0 || offset + size > rom.Length) return false;

        int scanLen = (int)Math.Min(size, 512);
        int origZeros = 0;
        for (int i = 0; i < scanLen; i++)
            if (rom[offset + i] == 0) origZeros++;

        if (origZeros >= 25)
            return false;

        return FindWorkingNormalKey(rom, offset, size, ivOffset) != null;
    }

    public static RomInfo InspectRom(string filePath)
    {
        var fi = new FileInfo(filePath);
        var info = new RomInfo
        {
            FilePath = filePath,
            FileName = fi.Name,
            FileSize = fi.Length
        };

        if (fi.Length < 0x400) return info;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] header = new byte[0x400];
        fs.ReadExactly(header, 0, 0x400);

        info.GameTitle = Encoding.ASCII.GetString(header, 0, 12).Trim((char)0);
        info.GameCode = Encoding.ASCII.GetString(header, 0x0C, 4).Trim((char)0);
        info.MakerCode = Encoding.ASCII.GetString(header, 0x10, 2).Trim((char)0);
        info.Region = GetRegionFromCode(info.GameCode);

        uint bannerOffset = BitConverter.ToUInt32(header, 0x68);
        if (bannerOffset != 0 && bannerOffset + 0x240 <= fi.Length)
        {
            info.Banner = NdsBannerParser.Parse(fs, bannerOffset);
            if (info.Banner != null && !string.IsNullOrWhiteSpace(info.Banner.BestTitle))
            {
                info.GameTitle = info.Banner.BestTitle;
            }
        }

        byte unitCode = header[0x12];
        info.UnitCode = unitCode;
        info.IsDsiRom = (unitCode & 0x02) != 0 || unitCode == 0x03;

        info.Modcrypt1Offset = BitConverter.ToUInt32(header, 0x220);
        info.Modcrypt1Size   = BitConverter.ToUInt32(header, 0x224);
        info.Modcrypt2Offset = BitConverter.ToUInt32(header, 0x228);
        info.Modcrypt2Size   = BitConverter.ToUInt32(header, 0x22C);

        info.HasModcrypt = (info.Modcrypt1Offset != 0 && info.Modcrypt1Size != 0 && info.Modcrypt1Size != 0xFFFFFFFF) ||
                           (info.Modcrypt2Offset != 0 && info.Modcrypt2Size != 0 && info.Modcrypt2Size != 0xFFFFFFFF);

        if (info.HasModcrypt)
        {
            bool mod1Enc = false;
            if (info.Modcrypt1Offset != 0 && info.Modcrypt1Size > 0 && info.Modcrypt1Offset + info.Modcrypt1Size <= fi.Length)
                mod1Enc = IsAreaEncrypted(fs, info.Modcrypt1Offset, info.Modcrypt1Size, 0x300, header, fi.Length);

            bool mod2Enc = false;
            if (info.Modcrypt2Offset != 0 && info.Modcrypt2Size > 0 && info.Modcrypt2Offset + info.Modcrypt2Size <= fi.Length)
                mod2Enc = IsAreaEncrypted(fs, info.Modcrypt2Offset, info.Modcrypt2Size, 0x314, header, fi.Length);

            info.IsEncrypted = mod1Enc || mod2Enc;
        }

        if (info.GameCode.StartsWith("KAL", StringComparison.OrdinalIgnoreCase))
        {
            if (info.IsEncrypted)
            {
                info.NeedsCompatibilityPatch = true;
            }
            else if (fi.Length > 0x50d7)
            {
                fs.Seek(0x50d4, SeekOrigin.Begin);
                Span<byte> checkBytes = stackalloc byte[4];
                fs.ReadExactly(checkBytes);
                uint val = BitConverter.ToUInt32(checkBytes);
                if (val == 0xeb0085a3)
                    info.NeedsCompatibilityPatch = true;
            }
        }

        return info;
    }

    public static RomDetailedInfo InspectRomDetailed(RomItem item)
    {
        var det = new RomDetailedInfo
        {
            FilePath = item.FilePath,
            FileName = item.FileName,
            FileSize = item.FileSize,
            GameCode = item.GameCode,
            Region = GetRegionFromCode(item.GameCode),
            IsEncrypted = item.IsEncrypted
        };

        try
        {
            byte[] data;
            if (item.IsFromArchive && File.Exists(item.ArchivePath))
            {
                data = ArchiveManager.ExtractRomBuffer(item.ArchivePath, item.ArchiveEntryName);
            }
            else if (item.IsSpecialFormat && File.Exists(item.FilePath))
            {
                using var ms = new MemoryStream();
                SpecialFormatManager.ExtractToStream(item.FilePath, item.SpecialContentOffset, item.SpecialContentSize, ms);
                data = ms.ToArray();
            }
            else if (File.Exists(item.FilePath))
            {
                data = File.ReadAllBytes(item.FilePath);
            }
            else
            {
                return det;
            }

            if (data.Length >= 0x200)
            {
                det.InternalHeaderTitle = Encoding.ASCII.GetString(data, 0, 12).Trim((char)0);
                det.GameCode = Encoding.ASCII.GetString(data, 0x0C, 4).Trim((char)0);
                det.MakerCode = Encoding.ASCII.GetString(data, 0x10, 2).Trim((char)0);
                det.Region = GetRegionFromCode(det.GameCode);
                det.UnitCode = data[0x12];
                det.UnitCodeDescription = det.UnitCode switch
                {
                    0x00 => "NTR (Nintendo DS Classic)",
                    0x02 => "TWL Hybrid (Nintendo DS + DSi Enhanced)",
                    0x03 => "TWL Exclusive (Nintendo DSi / DSiWare)",
                    _ => $"0x{det.UnitCode:X2}"
                };
                det.RomVersion = data[0x1E];
                det.Arm9Offset = BitConverter.ToUInt32(data, 0x20);
                det.Arm9EntryAddress = BitConverter.ToUInt32(data, 0x24);
                det.Arm9RamAddress = BitConverter.ToUInt32(data, 0x28);
                det.Arm9Size = BitConverter.ToUInt32(data, 0x2C);
                det.Arm7Offset = BitConverter.ToUInt32(data, 0x30);
                det.Arm7EntryAddress = BitConverter.ToUInt32(data, 0x34);
                det.Arm7RamAddress = BitConverter.ToUInt32(data, 0x38);
                det.Arm7Size = BitConverter.ToUInt32(data, 0x3C);
                det.FntOffset = BitConverter.ToUInt32(data, 0x40);
                det.FntSize = BitConverter.ToUInt32(data, 0x44);
                det.FatOffset = BitConverter.ToUInt32(data, 0x48);
                det.FatSize = BitConverter.ToUInt32(data, 0x4C);
                det.BannerOffset = BitConverter.ToUInt32(data, 0x68);
                det.SecureAreaCrc16 = BitConverter.ToUInt16(data, 0x6C);
                det.TotalUsedRomSize = BitConverter.ToUInt32(data, 0x80);
                det.HeaderCrc16 = BitConverter.ToUInt16(data, 0x15E);
                det.CalculatedHeaderCrc16 = CalcHeaderCRC16(data, 0, 0x15E);

                if (data.Length >= 0x300)
                {
                    det.Modcrypt1Offset = BitConverter.ToUInt32(data, 0x220);
                    det.Modcrypt1Size = BitConverter.ToUInt32(data, 0x224);
                    det.Modcrypt2Offset = BitConverter.ToUInt32(data, 0x228);
                    det.Modcrypt2Size = BitConverter.ToUInt32(data, 0x22C);

                    byte[] tid = new byte[8];
                    Array.Copy(data, 0x230, tid, 0, 8);
                    Array.Reverse(tid);
                    det.TwlTitleId = Convert.ToHexString(tid);
                }

                det.Banner = NdsBannerParser.Parse(data);
            }

            det.Crc32Hex = NdsBannerParser.ComputeCrc32(data).ToString("X8");
            det.Md5Hex = Convert.ToHexString(MD5.HashData(data));
            det.Sha1Hex = Convert.ToHexString(SHA1.HashData(data));
            det.Sha256Hex = Convert.ToHexString(SHA256.HashData(data));
        }
        catch { }

        return det;
    }

    public static bool ApplyCompatibilityPatches(byte[] rom, string gameCode)
    {
        bool patched = false;

        if (gameCode.StartsWith("KAL", StringComparison.OrdinalIgnoreCase))
        {
            if (rom.Length > 0x50d7 && BitConverter.ToUInt32(rom, 0x50d4) == 0xeb0085a3)
            {
                BitConverter.GetBytes(0xe1a00000u).CopyTo(rom, 0x50d4);
                patched = true;
            }
            if (rom.Length > 0xba017 && BitConverter.ToUInt32(rom, 0xba014) == 0xebfdb1d3)
            {
                BitConverter.GetBytes(0xea00000fu).CopyTo(rom, 0xba014);
                patched = true;
            }
            if (rom.Length > 0xe454b && BitConverter.ToUInt32(rom, 0xe4548) == 0xebfd0886)
            {
                BitConverter.GetBytes(0xe1a00000u).CopyTo(rom, 0xe4548);
                patched = true;
            }
            if (rom.Length > 0xf5c53 && BitConverter.ToUInt32(rom, 0xf5c50) == 0xebfcc2c4)
            {
                BitConverter.GetBytes(0xe1a00000u).CopyTo(rom, 0xf5c50);
                patched = true;
            }
            if (rom.Length > 0xf5f0f && BitConverter.ToUInt32(rom, 0xf5f0c) == 0xebfcc215)
            {
                BitConverter.GetBytes(0xe1a00000u).CopyTo(rom, 0xf5f0c);
                patched = true;
            }

            ReadOnlySpan<byte> targetPath = "nand:/sys/TWLFontTable.dat"u8;
            int pathIdx = rom.AsSpan().IndexOf(targetPath);
            if (pathIdx != -1)
            {
                byte[] replacement = new byte[targetPath.Length];
                byte[] fontName = Encoding.ASCII.GetBytes("rom:/Arial.NFTR");
                Array.Copy(fontName, 0, replacement, 0, fontName.Length);
                Array.Copy(replacement, 0, rom, pathIdx, targetPath.Length);
                patched = true;
            }

            if (rom.Length >= 0x8000)
            {
                ushort secCrc = CalcHeaderCRC16(rom, 0x4000, 0x4000);
                rom[0x6C] = (byte)(secCrc & 0xFF);
                rom[0x6D] = (byte)(secCrc >> 8);
            }

            ushort headerCrc = CalcHeaderCRC16(rom, 0, 0x15E);
            rom[0x15E] = (byte)(headerCrc & 0xFF);
            rom[0x15F] = (byte)(headerCrc >> 8);
        }

        return patched;
    }

    public static bool DecryptRomBuffer(byte[] rom)
    {
        if (rom.Length < 0x400) return false;

        uint mod1Off = BitConverter.ToUInt32(rom, 0x220);
        uint mod1Sz  = BitConverter.ToUInt32(rom, 0x224);
        uint mod2Off = BitConverter.ToUInt32(rom, 0x228);
        uint mod2Sz  = BitConverter.ToUInt32(rom, 0x22C);

        bool mod1Encrypted = mod1Off != 0 && mod1Sz != 0 && mod1Sz != 0xFFFFFFFF && mod1Off + mod1Sz <= rom.Length && IsAreaEncryptedBuffer(rom, mod1Off, mod1Sz, 0x300);
        bool mod2Encrypted = mod2Off != 0 && mod2Sz != 0 && mod2Sz != 0xFFFFFFFF && mod2Off + mod2Sz <= rom.Length && IsAreaEncryptedBuffer(rom, mod2Off, mod2Sz, 0x314);

        if (!mod1Encrypted && !mod2Encrypted)
        {
            string code = Encoding.ASCII.GetString(rom, 0x0C, 4);
            bool patched = ApplyCompatibilityPatches(rom, code);

            if ((rom[0x1C] & 0x03) != 0x03)
            {
                rom[0x1C] |= 0x03;
                patched = true;
            }

            if (patched)
            {
                ushort crc = CalcHeaderCRC16(rom, 0x15E);
                rom[0x15E] = (byte)(crc & 0xFF);
                rom[0x15F] = (byte)(crc >> 8);
            }
            return true;
        }

        if (mod1Encrypted)
        {
            byte[]? normalKey1 = FindWorkingNormalKey(rom, mod1Off, mod1Sz, 0x300);
            if (normalKey1 != null)
            {
                byte[] keySwap1 = new byte[16];
                for (int i = 0; i < 16; i++) keySwap1[i] = normalKey1[15 - i];

                using var aes1 = Aes.Create();
                aes1.Mode = CipherMode.ECB;
                aes1.Padding = PaddingMode.None;
                aes1.Key = keySwap1;

                byte[] iv1 = new byte[16];
                Array.Copy(rom, 0x300, iv1, 0, 16);
                CryptArea(aes1, iv1, rom, mod1Off, mod1Sz);
            }
        }

        if (mod2Encrypted)
        {
            byte[]? normalKey2 = FindWorkingNormalKey(rom, mod2Off, mod2Sz, 0x314);
            if (normalKey2 != null)
            {
                byte[] keySwap2 = new byte[16];
                for (int i = 0; i < 16; i++) keySwap2[i] = normalKey2[15 - i];

                using var aes2 = Aes.Create();
                aes2.Mode = CipherMode.ECB;
                aes2.Padding = PaddingMode.None;
                aes2.Key = keySwap2;

                byte[] iv2 = new byte[16];
                Array.Copy(rom, 0x314, iv2, 0, 16);
                CryptArea(aes2, iv2, rom, mod2Off, mod2Sz);
            }
        }

        string gameCode = Encoding.ASCII.GetString(rom, 0x0C, 4);
        ApplyCompatibilityPatches(rom, gameCode);

        rom[0x1C] |= 0x03;

        ushort headerCrc = CalcHeaderCRC16(rom, 0x15E);
        rom[0x15E] = (byte)(headerCrc & 0xFF);
        rom[0x15F] = (byte)(headerCrc >> 8);

        return true;
    }

    public static bool ValidateDecryptedRom(byte[] rom)
    {
        if (rom == null || rom.Length < 0x200) return false;
        if (!IsRomHeader(rom)) return false;

        uint arm9Off = BitConverter.ToUInt32(rom, 0x20);
        uint arm9Size = BitConverter.ToUInt32(rom, 0x2C);
        uint arm7Off = BitConverter.ToUInt32(rom, 0x30);
        uint arm7Size = BitConverter.ToUInt32(rom, 0x3C);

        if (arm9Off > 0 && arm9Size > 0 && arm9Off + arm9Size > rom.Length) return false;
        if (arm7Off > 0 && arm7Size > 0 && arm7Off + arm7Size > rom.Length) return false;

        return true;
    }

    public static bool DecryptFile(string inputPath, string outputPath, bool createBackup = false)
    {
        var info = InspectRom(inputPath);
        if (!info.IsEncrypted && !info.NeedsCompatibilityPatch)
        {
            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outDir) && !Directory.Exists(outDir))
                Directory.CreateDirectory(outDir);

            if (!string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(inputPath, outputPath, true);
            }
            return true;
        }

        byte[] rom = File.ReadAllBytes(inputPath);
        if (!DecryptRomBuffer(rom)) return false;
        if (!ValidateDecryptedRom(rom)) return false;

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        bool isInPlace = string.Equals(Path.GetFullPath(inputPath), Path.GetFullPath(outputPath), StringComparison.OrdinalIgnoreCase);
        string tempFile = Path.Combine(dir ?? "", $"{Path.GetFileName(outputPath)}.tmp_storm_{Guid.NewGuid():N}");

        try
        {
            File.WriteAllBytes(tempFile, rom);

            var fi = new FileInfo(tempFile);
            if (fi.Length != rom.Length)
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
                return false;
            }

            string backupFile = outputPath + ".bak";

            if (isInPlace && File.Exists(outputPath))
            {
                try
                {
                    if (File.Exists(backupFile)) File.Delete(backupFile);
                    File.Replace(tempFile, outputPath, backupFile, ignoreMetadataErrors: true);
                    if (!createBackup && File.Exists(backupFile))
                        File.Delete(backupFile);
                }
                catch
                {
                    if (File.Exists(backupFile)) File.Delete(backupFile);
                    File.Move(outputPath, backupFile);
                    File.Move(tempFile, outputPath);
                    if (!createBackup && File.Exists(backupFile))
                        File.Delete(backupFile);
                }
            }
            else
            {
                if (File.Exists(outputPath)) File.Delete(outputPath);
                File.Move(tempFile, outputPath);
            }

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[DecryptFile Atomic Write Error]: {ex.Message}");
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            return false;
        }
    }

    public static RomItem InspectFile(string path)
    {
        if (ArchiveManager.IsArchiveFile(path))
        {
            var entries = ArchiveManager.FindRomsInArchive(path);
            if (entries.Count > 0)
            {
                var e = entries[0];
                return new RomItem
                {
                    FilePath = path,
                    FileName = e.SuggestedFileName,
                    SuggestedFileName = e.SuggestedFileName,
                    GameTitle = !string.IsNullOrWhiteSpace(e.RomInfo.Banner?.BestTitle) ? e.RomInfo.Banner.BestTitle : e.RomInfo.GameTitle,
                    GameCode = e.RomInfo.GameCode,
                    FileSize = e.UncompressedSize,
                    IsDsiRom = e.RomInfo.IsDsiRom,
                    IsEncrypted = e.RomInfo.IsEncrypted,
                    NeedsCompatibilityPatch = e.RomInfo.NeedsCompatibilityPatch,
                    IsFromArchive = true,
                    ArchivePath = path,
                    ArchiveEntryName = e.EntryName,
                    BannerIconPng = e.RomInfo.Banner?.IconPng,
                    Status = e.RomInfo.IsEncrypted ? LocalizationManager.Get("StatusEncrypted") : LocalizationManager.Get("StatusDecrypted")
                };
            }
        }
        if (ArchiveManager.IsArchiveFile(path))
        {
            var entries = ArchiveManager.FindRomsInArchive(path);
            if (entries.Count > 0)
            {
                var e = entries[0];
                return new RomItem
                {
                    FilePath = path,
                    FileName = e.SuggestedFileName,
                    SuggestedFileName = e.SuggestedFileName,
                    GameTitle = !string.IsNullOrWhiteSpace(e.RomInfo.Banner?.BestTitle) ? e.RomInfo.Banner.BestTitle : e.RomInfo.GameTitle,
                    GameCode = e.RomInfo.GameCode,
                    FileSize = e.UncompressedSize,
                    IsDsiRom = e.RomInfo.IsDsiRom,
                    IsEncrypted = e.RomInfo.IsEncrypted,
                    NeedsCompatibilityPatch = e.RomInfo.NeedsCompatibilityPatch,
                    IsFromArchive = true,
                    ArchivePath = path,
                    ArchiveEntryName = e.EntryName,
                    BannerIconPng = e.RomInfo.Banner?.IconPng,
                    Status = e.RomInfo.IsEncrypted ? LocalizationManager.Get("StatusEncrypted") : LocalizationManager.Get("StatusDecrypted")
                };
            }
        }
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".cia" or ".tad" or ".tmd" || Path.GetFileName(path).Equals("title.tmd", StringComparison.OrdinalIgnoreCase))
        {
            SpecialFormatItem? sf = null;
            if (ext == ".cia") sf = SpecialFormatManager.InspectCia(path);
            else if (ext == ".tad") sf = SpecialFormatManager.InspectTad(path);
            else sf = SpecialFormatManager.InspectNusFolder(Path.GetDirectoryName(path) ?? path);

            if (sf != null)
            {
                return new RomItem
                {
                    FilePath = sf.ContainerPath,
                    FileName = Path.GetFileName(path),
                    GameTitle = sf.GameTitle,
                    GameCode = sf.GameCode,
                    FileSize = sf.ContentSize,
                    IsDsiRom = true,
                    IsEncrypted = sf.IsEncrypted,
                    NeedsCompatibilityPatch = sf.NeedsCompatibilityPatch,
                    IsSpecialFormat = true,
                    SpecialFormatType = sf.FormatType,
                    SpecialContentOffset = sf.ContentOffset,
                    SpecialContentSize = sf.ContentSize,
                    TitleId = sf.TitleId,
                    BannerIconPng = sf.Banner?.IconPng,
                    Status = sf.IsEncrypted ? LocalizationManager.Get("StatusEncrypted") : LocalizationManager.Get("StatusDecrypted")
                };
            }
        }

        var info = InspectRom(path);
        return new RomItem
        {
            FilePath = path,
            FileName = Path.GetFileName(path),
            GameTitle = !string.IsNullOrWhiteSpace(info.Banner?.Title) ? info.Banner.Title : info.GameTitle,
            GameCode = info.GameCode,
            FileSize = info.FileSize,
            IsDsiRom = info.IsDsiRom,
            IsEncrypted = info.IsEncrypted,
            NeedsCompatibilityPatch = info.NeedsCompatibilityPatch,
            BannerIconPng = info.Banner?.PngBytes,
            Status = info.IsEncrypted
                ? LocalizationManager.Get("StatusEncrypted")
                : (info.IsDsiRom ? LocalizationManager.Get("StatusDecrypted") : LocalizationManager.Get("StatusNotDsi"))
        };
    }

    public static (bool Success, string Message, string OutputPath) ProcessRom(
        RomItem item,
        string outputPath,
        bool verifyCrc,
        bool compat,
        bool createBackup,
        IProgress<double>? progress = null)
    {
        try
        {
            progress?.Report(25);
            if (item.IsFromArchive)
            {
                bool okArch = ArchiveManager.ExtractAndDecrypt(item.ArchivePath, item.ArchiveEntryName, outputPath);
                progress?.Report(100);
                return (okArch, okArch ? LocalizationManager.Get("StatusSuccess") : LocalizationManager.Get("StatusFailed"), outputPath);
            }
            if (item.IsFromArchive)
            {
                bool okArch = ArchiveManager.ExtractAndDecrypt(item.ArchivePath, item.ArchiveEntryName, outputPath);
                progress?.Report(100);
                return (okArch, okArch ? LocalizationManager.Get("StatusSuccess") : LocalizationManager.Get("StatusFailed"), outputPath);
            }
            if (item.IsSpecialFormat)
            {
                bool okSpec = SpecialFormatManager.ExtractAndDecrypt(item.FilePath, item.SpecialContentOffset, item.SpecialContentSize, outputPath, createBackup);
                progress?.Report(100);
                return (okSpec, okSpec ? LocalizationManager.Get("StatusSuccess") : LocalizationManager.Get("StatusFailed"), outputPath);
            }

            bool ok = DecryptFile(item.FilePath, outputPath, createBackup);
            progress?.Report(100);
            return (ok, ok ? LocalizationManager.Get("StatusSuccess") : LocalizationManager.Get("StatusFailed"), outputPath);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, outputPath);
        }
    }
}
