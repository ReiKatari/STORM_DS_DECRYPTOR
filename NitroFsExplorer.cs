using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace StormDsDecryptor;

public class NitroFsEntry
{
    public int FileId { get; set; }
    public string VirtualPath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public uint Offset { get; set; }
    public uint Size { get; set; }
    public string FileTypeBadge { get; set; } = "BIN";
    public bool IsSystemEntry { get; set; }

    public string OffsetHex => $"0x{Offset:X8}";
    public string SizeFormatted =>
        Size < 1024 ? $"{Size} B" :
        Size < 1024 * 1024 ? $"{Size / 1024.0:F1} KB" :
        $"{Size / (1024.0 * 1024.0):F2} MB";
}

public static class NitroFsExplorer
{
    public static List<NitroFsEntry> ParseRomFileSystem(string romPath)
    {
        var list = new List<NitroFsEntry>();
        if (string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath))
            return list;

        try
        {
            using var fs = new FileStream(romPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x200) return list;

            byte[] hdr = new byte[0x1000];
            int hdrRead = NdsBannerParser.ReadUpTo(fs, hdr, 0, (int)Math.Min(hdr.Length, fs.Length));
            if (hdrRead < 0x160) return list;

            uint arm9Off  = BitConverter.ToUInt32(hdr, 0x20);
            uint arm9Size = BitConverter.ToUInt32(hdr, 0x2C);
            uint arm7Off  = BitConverter.ToUInt32(hdr, 0x30);
            uint arm7Size = BitConverter.ToUInt32(hdr, 0x3C);
            uint fntOff   = BitConverter.ToUInt32(hdr, 0x40);
            uint fntSize  = BitConverter.ToUInt32(hdr, 0x44);
            uint fatOff   = BitConverter.ToUInt32(hdr, 0x48);
            uint fatSize  = BitConverter.ToUInt32(hdr, 0x4C);
            uint ov9Off   = BitConverter.ToUInt32(hdr, 0x50);
            uint ov9Size  = BitConverter.ToUInt32(hdr, 0x54);
            uint ov7Off   = BitConverter.ToUInt32(hdr, 0x58);
            uint ov7Size  = BitConverter.ToUInt32(hdr, 0x5C);
            uint bannerOff = BitConverter.ToUInt32(hdr, 0x68);

            // 1. Add virtual system files (/sys/*)
            list.Add(new NitroFsEntry
            {
                FileId = -1,
                VirtualPath = "/sys/header.bin",
                FileName = "header.bin",
                Offset = 0,
                Size = (uint)Math.Min(0x1000, fs.Length),
                FileTypeBadge = "HDR",
                IsSystemEntry = true
            });

            if (bannerOff > 0 && bannerOff + 0x240 <= fs.Length)
            {
                fs.Seek(bannerOff, SeekOrigin.Begin);
                byte[] bver = new byte[2];
                NdsBannerParser.ReadUpTo(fs, bver, 0, 2);
                ushort ver = BitConverter.ToUInt16(bver, 0);
                uint bSize = ver >= 0x0103 ? 0x23C0u : (ver >= 0x0003 ? 0x0A40u : (ver >= 0x0002 ? 0x0940u : 0x0840u));
                bSize = (uint)Math.Min(bSize, fs.Length - bannerOff);

                list.Add(new NitroFsEntry
                {
                    FileId = -2,
                    VirtualPath = "/sys/banner.bin",
                    FileName = "banner.bin",
                    Offset = bannerOff,
                    Size = bSize,
                    FileTypeBadge = ver >= 0x0103 ? "BNR-DSi" : "BNR",
                    IsSystemEntry = true
                });
            }

            if (arm9Off > 0 && arm9Size > 0 && arm9Off + arm9Size <= fs.Length)
            {
                list.Add(new NitroFsEntry
                {
                    FileId = -3,
                    VirtualPath = "/sys/arm9.bin",
                    FileName = "arm9.bin",
                    Offset = arm9Off,
                    Size = arm9Size,
                    FileTypeBadge = "ARM9",
                    IsSystemEntry = true
                });
            }

            if (arm7Off > 0 && arm7Size > 0 && arm7Off + arm7Size <= fs.Length)
            {
                list.Add(new NitroFsEntry
                {
                    FileId = -4,
                    VirtualPath = "/sys/arm7.bin",
                    FileName = "arm7.bin",
                    Offset = arm7Off,
                    Size = arm7Size,
                    FileTypeBadge = "ARM7",
                    IsSystemEntry = true
                });
            }

            // Read FAT table if valid
            uint[] fatStarts = Array.Empty<uint>();
            uint[] fatEnds = Array.Empty<uint>();
            int fatCount = 0;

            if (fatOff > 0 && fatSize >= 8 && fatOff + fatSize <= fs.Length && fatSize <= 64 * 1024 * 1024)
            {
                fatCount = (int)(fatSize / 8);
                fatStarts = new uint[fatCount];
                fatEnds = new uint[fatCount];
                byte[] fatBuf = new byte[fatCount * 8];
                fs.Seek(fatOff, SeekOrigin.Begin);
                NdsBannerParser.ReadUpTo(fs, fatBuf, 0, fatBuf.Length);

                for (int i = 0; i < fatCount; i++)
                {
                    fatStarts[i] = BitConverter.ToUInt32(fatBuf, i * 8);
                    fatEnds[i] = BitConverter.ToUInt32(fatBuf, i * 8 + 4);
                }
            }

            // Read ARM9 Overlays (each entry in OVT is 32 bytes; offset +0x18 = FileId)
            if (ov9Off > 0 && ov9Size >= 32 && ov9Off + ov9Size <= fs.Length && ov9Size <= 4 * 1024 * 1024)
            {
                int ovCount = (int)(ov9Size / 32);
                byte[] ovBuf = new byte[ovCount * 32];
                fs.Seek(ov9Off, SeekOrigin.Begin);
                NdsBannerParser.ReadUpTo(fs, ovBuf, 0, ovBuf.Length);

                for (int i = 0; i < ovCount; i++)
                {
                    uint ovId = BitConverter.ToUInt32(ovBuf, i * 32);
                    int fileId = (int)BitConverter.ToUInt32(ovBuf, i * 32 + 0x18);
                    if (fileId >= 0 && fileId < fatCount)
                    {
                        uint st = fatStarts[fileId];
                        uint en = fatEnds[fileId];
                        if (en >= st && en <= fs.Length)
                        {
                            list.Add(new NitroFsEntry
                            {
                                FileId = fileId,
                                VirtualPath = $"/sys/overlay9_{ovId:D4}.bin",
                                FileName = $"overlay9_{ovId:D4}.bin",
                                Offset = st,
                                Size = en - st,
                                FileTypeBadge = "OVL9",
                                IsSystemEntry = true
                            });
                        }
                    }
                }
            }

            // Read FNT table if valid
            if (fntOff > 0 && fntSize >= 8 && fntOff + fntSize <= fs.Length && fntSize <= 32 * 1024 * 1024 && fatCount > 0)
            {
                byte[] fntBuf = new byte[fntSize];
                fs.Seek(fntOff, SeekOrigin.Begin);
                NdsBannerParser.ReadUpTo(fs, fntBuf, 0, fntBuf.Length);

                var visitedDirs = new HashSet<ushort>();
                TraverseFntDirectory(fntBuf, 0xF000, "/data", fatStarts, fatEnds, fs.Length, list, visitedDirs);
            }
        }
        catch { }

        return list;
    }

    private static void TraverseFntDirectory(
        byte[] fnt,
        ushort dirId,
        string currentPath,
        uint[] fatStarts,
        uint[] fatEnds,
        long romLength,
        List<NitroFsEntry> outList,
        HashSet<ushort> visited)
    {
        if (!visited.Add(dirId) || visited.Count > 16384)
            return;

        int dirIndex = dirId & 0x0FFF;
        int dirHeaderOffset = dirIndex * 8;
        if (dirHeaderOffset + 8 > fnt.Length)
            return;

        uint subTableOffset = BitConverter.ToUInt32(fnt, dirHeaderOffset);
        ushort firstFileId = BitConverter.ToUInt16(fnt, dirHeaderOffset + 4);

        if (subTableOffset >= fnt.Length)
            return;

        int pos = (int)subTableOffset;
        ushort curFileId = firstFileId;

        while (pos < fnt.Length)
        {
            byte typeLen = fnt[pos++];
            if (typeLen == 0)
                break;

            bool isSubDir = (typeLen & 0x80) != 0;
            int nameLen = typeLen & 0x7F;
            if (pos + nameLen > fnt.Length)
                break;

            string name = Encoding.UTF8.GetString(fnt, pos, nameLen);
            pos += nameLen;

            if (isSubDir)
            {
                if (pos + 2 > fnt.Length) break;
                ushort subDirId = BitConverter.ToUInt16(fnt, pos);
                pos += 2;
                TraverseFntDirectory(fnt, subDirId, $"{currentPath}/{name}", fatStarts, fatEnds, romLength, outList, visited);
            }
            else
            {
                int fid = curFileId++;
                if (fid >= 0 && fid < fatStarts.Length)
                {
                    uint st = fatStarts[fid];
                    uint en = fatEnds[fid];
                    uint sz = en >= st ? (en - st) : 0;
                    outList.Add(new NitroFsEntry
                    {
                        FileId = fid,
                        VirtualPath = $"{currentPath}/{name}",
                        FileName = name,
                        Offset = st,
                        Size = sz,
                        FileTypeBadge = GuessFileBadge(name),
                        IsSystemEntry = false
                    });
                }
            }
        }
    }

    private static string GuessFileBadge(string fileName)
    {
        string ext = Path.GetExtension(fileName).ToUpperInvariant().TrimStart('.');
        if (string.IsNullOrEmpty(ext)) return "DATA";
        return ext.Length > 6 ? ext.Substring(0, 6) : ext;
    }

    public static byte[] ExtractEntryBytes(string romPath, NitroFsEntry entry, bool tryDecompressBlz, out bool wasDecompressed)
    {
        wasDecompressed = false;
        using var fs = new FileStream(romPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (entry.Offset + entry.Size > fs.Length || entry.Size == 0)
            return Array.Empty<byte>();

        byte[] raw = new byte[entry.Size];
        fs.Seek(entry.Offset, SeekOrigin.Begin);
        NdsBannerParser.ReadUpTo(fs, raw, 0, raw.Length);

        if (tryDecompressBlz && TryDecompressBlz(raw, out byte[]? decomp) && decomp != null)
        {
            wasDecompressed = true;
            return decomp;
        }

        return raw;
    }

    public static int ExtractAllToDirectory(string romPath, IEnumerable<NitroFsEntry> entries, string outputRootDir)
    {
        int count = 0;
        using var fs = new FileStream(romPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        foreach (var e in entries)
        {
            if (e.Offset + e.Size > fs.Length) continue;
            string rel = e.VirtualPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            string fullOut = Path.Combine(outputRootDir, rel);
            string? dir = Path.GetDirectoryName(fullOut);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            byte[] buf = new byte[e.Size];
            fs.Seek(e.Offset, SeekOrigin.Begin);
            NdsBannerParser.ReadUpTo(fs, buf, 0, buf.Length);
            File.WriteAllBytes(fullOut, buf);
            count++;
        }
        return count;
    }

    // ==================== NINTENDO BACKWARD LZ77 (BLZ) DECOMPRESSOR (ARM9 / OVERLAYS) ====================

    public static bool TryDecompressBlz(byte[] data, out byte[]? result)
    {
        result = null;
        try
        {
            if (data == null || data.Length < 12)
                return false;

            // For ARM9, sometimes there is a 12-byte _start_ModuleParams footer or the BLZ header is at the very end
            int endPos = data.Length;
            uint addLen = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(endPos - 4, 4));
            if (addLen == 0 || addLen > 32 * 1024 * 1024)
                return false;

            uint headerAndBottom = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(endPos - 8, 4));
            int headerLen = (int)((headerAndBottom >> 24) & 0xFF);
            int compressedLen = (int)(headerAndBottom & 0x00FFFFFF);

            if (headerLen < 8 || headerLen > 16 || compressedLen <= headerLen || compressedLen > endPos)
                return false;

            int outLen = (int)(endPos + addLen);
            if (outLen <= endPos || outLen > 64 * 1024 * 1024)
                return false;

            byte[] dst = new byte[outLen];
            Buffer.BlockCopy(data, 0, dst, 0, endPos - compressedLen);

            int srcPos = endPos - headerLen;
            int srcLimit = endPos - compressedLen;
            int dstPos = outLen;

            while (srcPos > srcLimit && dstPos > endPos - compressedLen)
            {
                byte flags = data[--srcPos];
                for (int bit = 0; bit < 8; bit++)
                {
                    if (srcPos <= srcLimit || dstPos <= endPos - compressedLen)
                        break;

                    if ((flags & 0x80) == 0)
                    {
                        dst[--dstPos] = data[--srcPos];
                    }
                    else
                    {
                        if (srcPos - 2 < srcLimit)
                            break;
                        byte b1 = data[--srcPos];
                        byte b2 = data[--srcPos];
                        int length = (b1 >> 4) + 3;
                        int disp = (((b1 & 0x0F) << 8) | b2) + 3;

                        if (dstPos + disp > outLen)
                            return false;

                        for (int j = 0; j < length && dstPos > 0; j++)
                        {
                            byte copyByte = dst[dstPos + disp - 1];
                            dst[--dstPos] = copyByte;
                        }
                    }
                    flags <<= 1;
                }
            }

            result = dst;
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    // ==================== SAFE SMART ROM TRIMMER (NDS / DSi) ====================

    public static (bool Success, long SavedBytes, string Message) TrimRomFile(string romPath, bool createBackup)
    {
        try
        {
            if (!File.Exists(romPath))
                return (false, 0, "File not found");

            byte[] rom = File.ReadAllBytes(romPath);
            if (rom.Length < 0x1000)
                return (false, 0, "ROM too small");

            byte padByte = rom[rom.Length - 1];
            if (padByte != 0xFF && padByte != 0x00)
                return (false, 0, "Уже обрезан (нет хвостового паддинга 0xFF/0x00)");

            uint headerUsedSize = BitConverter.ToUInt32(rom, 0x80);
            uint unitCode = rom[0x12];
            uint dsiTotalSize = (unitCode & 0x02) != 0 && rom.Length >= 0x214 ? BitConverter.ToUInt32(rom, 0x210) : 0;

            int scanPos = rom.Length - 1;
            while (scanPos > 0x4000 && rom[scanPos] == padByte)
                scanPos--;

            long minSafeSize = Math.Max(headerUsedSize, dsiTotalSize);
            // Keep at least 0x88 bytes after last non-padding byte for RSA Download Play signature + 512-byte alignment
            long trimmedLen = Math.Max(minSafeSize, scanPos + 1 + 0x88);
            trimmedLen = (trimmedLen + 511) & ~511L;

            if (trimmedLen >= rom.Length - 4096)
                return (false, 0, "Образ уже оптимального размера");

            if (createBackup)
            {
                File.Copy(romPath, romPath + ".bak", overwrite: true);
            }

            string tmpPath = romPath + ".trim_tmp";
            using (var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(rom, 0, (int)trimmedLen);
                fs.Flush(true);
            }

            File.Copy(tmpPath, romPath, overwrite: true);
            File.Delete(tmpPath);

            long saved = rom.Length - trimmedLen;
            return (true, saved, $"Обрезано {saved / 1024.0:F1} КБ пустого хвоста (новый размер: {trimmedLen / (1024.0 * 1024.0):F2} МБ)");
        }
        catch (Exception ex)
        {
            return (false, 0, ex.Message);
        }
    }
}
