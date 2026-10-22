using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using SharpCompress.Archives;

namespace StormDsDecryptor;

public class ArchiveRomEntry
{
    public string ArchivePath { get; set; } = string.Empty;
    public string EntryName { get; set; } = string.Empty;
    public string SuggestedFileName { get; set; } = string.Empty;
    public long UncompressedSize { get; set; }
    public RomInfo RomInfo { get; set; } = new();
}

public static class ArchiveManager
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz"
    };

    public static bool IsArchiveFile(string path)
    {
        string ext = Path.GetExtension(path);
        return !string.IsNullOrEmpty(ext) && ArchiveExtensions.Contains(ext);
    }

    public static List<ArchiveRomEntry> FindRomsInArchive(string archivePath)
    {
        var results = new List<ArchiveRomEntry>();
        string ext = Path.GetExtension(archivePath).ToLowerInvariant();

        if (ext == ".zip")
        {
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                foreach (var entry in zip.Entries)
                {
                    if (entry.Length < 0x200) continue;
                    using var stream = entry.Open();
                    byte[] header = new byte[0x400];
                    int read = NdsBannerParser.ReadUpTo(stream, header, 0, 0x400);
                    if (read >= 0x160 && DsiDecryptorEngine.IsRomHeader(header))
                    {
                        var entryInfo = InspectHeader(header, read, entry.Length, archivePath, stream);
                        results.Add(new ArchiveRomEntry
                        {
                            ArchivePath = archivePath,
                            EntryName = entry.FullName,
                            SuggestedFileName = DetermineSuggestedName(archivePath, entry.FullName, entryInfo),
                            UncompressedSize = entry.Length,
                            RomInfo = entryInfo
                        });
                    }
                }
                if (results.Count > 0)
                {
                    return DeduplicateEntries(results);
                }
            }
            catch
            {
                // Fall back to SharpCompress below if native zip fails
            }
        }

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath);
            foreach (var entry in archive.Entries)
            {
                if (entry.IsDirectory || entry.Size < 0x200) continue;
                using var stream = entry.OpenEntryStream();
                byte[] header = new byte[0x400];
                int read = NdsBannerParser.ReadUpTo(stream, header, 0, 0x400);
                if (read >= 0x160 && DsiDecryptorEngine.IsRomHeader(header))
                {
                    var entryInfo = InspectHeader(header, read, entry.Size, archivePath, stream);
                    results.Add(new ArchiveRomEntry
                    {
                        ArchivePath = archivePath,
                        EntryName = entry.Key ?? "",
                        SuggestedFileName = DetermineSuggestedName(archivePath, entry.Key ?? "", entryInfo),
                        UncompressedSize = entry.Size,
                        RomInfo = entryInfo
                    });
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Archive Read Error {archivePath}]: {ex.Message}");
        }

        return DeduplicateEntries(results);
    }

    private static List<ArchiveRomEntry> DeduplicateEntries(List<ArchiveRomEntry> list)
    {
        if (list.Count <= 1) return list;

        return list
            .GroupBy(r => r.RomInfo.GameCode)
            .Select(g => g.OrderByDescending(r => r.EntryName).First())
            .ToList();
    }

    public static string DetermineSuggestedName(string archivePath, string entryName, RomInfo info)
    {
        string archiveBase = Path.GetFileNameWithoutExtension(archivePath);
        string entryExt = Path.GetExtension(entryName).ToLowerInvariant();

        if (string.IsNullOrEmpty(entryExt) || entryName.All(char.IsDigit) || entryExt is not (".nds" or ".dsi" or ".app"))
        {
            return $"{archiveBase}.nds";
        }

        return Path.GetFileName(entryName);
    }

    private static RomInfo InspectHeader(byte[] header, int bytesAlreadyRead, long fileSize, string archivePath, Stream? stream = null)
    {
        var info = new RomInfo
        {
            FilePath = archivePath,
            FileName = Path.GetFileName(archivePath),
            FileSize = fileSize
        };

        info.GameTitle = System.Text.Encoding.ASCII.GetString(header, 0, 12).Trim((char)0);
        info.GameCode = System.Text.Encoding.ASCII.GetString(header, 0x0C, 4);

        if (stream != null)
        {
            try
            {
                uint bannerOffset = BitConverter.ToUInt32(header, 0x68);
                if (bannerOffset >= (uint)bytesAlreadyRead && bannerOffset + 0x240 <= fileSize)
                {
                    if (stream.CanSeek)
                    {
                        info.Banner = NdsBannerParser.Parse(stream, bannerOffset);
                    }
                    else if (bannerOffset <= 128 * 1024 * 1024)
                    {
                        long skip = bannerOffset - bytesAlreadyRead;
                        if (skip > 0)
                        {
                            byte[] discard = new byte[32768];
                            long rem = skip;
                            while (rem > 0)
                            {
                                int toRead = (int)Math.Min(discard.Length, rem);
                                int r = stream.Read(discard, 0, toRead);
                                if (r <= 0) break;
                                rem -= r;
                            }
                        }
                        int maxBannerLen = (int)Math.Min(0x23C0, fileSize - bannerOffset);
                        byte[] bannerData = new byte[maxBannerLen];
                        int bRead = NdsBannerParser.ReadUpTo(stream, bannerData, 0, maxBannerLen);
                        if (bRead >= 0x240)
                        {
                            if (bRead < maxBannerLen)
                            {
                                Array.Resize(ref bannerData, bRead);
                            }
                            info.Banner = NdsBannerParser.ParseFromBuffer(bannerData);
                        }
                    }
                    if (info.Banner != null && !string.IsNullOrWhiteSpace(info.Banner.BestTitle))
                    {
                        info.GameTitle = info.Banner.BestTitle;
                    }
                }
            }
            catch { }
        }

        byte unitCode = header[0x12];
        info.IsDsiRom = (unitCode & 0x02) != 0 || unitCode == 0x03;

        info.Modcrypt1Offset = BitConverter.ToUInt32(header, 0x220);
        info.Modcrypt1Size   = BitConverter.ToUInt32(header, 0x224);
        info.Modcrypt2Offset = BitConverter.ToUInt32(header, 0x228);
        info.Modcrypt2Size   = BitConverter.ToUInt32(header, 0x22C);

        info.HasModcrypt = (info.Modcrypt1Offset != 0 && info.Modcrypt1Size != 0 && info.Modcrypt1Size != 0xFFFFFFFF) ||
                           (info.Modcrypt2Offset != 0 && info.Modcrypt2Size != 0 && info.Modcrypt2Size != 0xFFFFFFFF);

        if (info.HasModcrypt)
        {
            info.IsEncrypted = true;
        }

        if (info.GameCode.StartsWith("KAL", StringComparison.OrdinalIgnoreCase))
        {
            info.NeedsCompatibilityPatch = true;
        }

        return info;
    }

    public static byte[] ExtractRomBuffer(string archivePath, string entryName)
    {
        string ext = Path.GetExtension(archivePath).ToLowerInvariant();
        if (ext == ".zip")
        {
            try
            {
                using var zip = ZipFile.OpenRead(archivePath);
                var entry = zip.GetEntry(entryName) ?? zip.Entries.FirstOrDefault(e => e.FullName.Equals(entryName, StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var ms = new MemoryStream((int)entry.Length);
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                // Fall back to SharpCompress below
            }
        }

        using var archive = ArchiveFactory.OpenArchive(archivePath);
        var arcEntry = archive.Entries.FirstOrDefault(e => !e.IsDirectory && (e.Key?.Equals(entryName, StringComparison.OrdinalIgnoreCase) ?? false));
        if (arcEntry != null)
        {
            using var stream = arcEntry.OpenEntryStream();
            using var ms = new MemoryStream((int)arcEntry.Size);
            stream.CopyTo(ms);
            return ms.ToArray();
        }

        throw new FileNotFoundException($"Entry '{entryName}' not found in archive '{archivePath}'");
    }

    public static bool ExtractAndDecrypt(string archivePath, string entryName, string outputPath)
    {
        byte[] rom = ExtractRomBuffer(archivePath, entryName);
        bool res = DsiDecryptorEngine.DecryptRomBuffer(rom);
        if (!res) return false;
        if (!DsiDecryptorEngine.ValidateDecryptedRom(rom)) return false;

        string? dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

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
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(tempFile, outputPath);
            return true;
        }
        catch
        {
            if (File.Exists(tempFile))
            {
                try { File.Delete(tempFile); } catch { }
            }
            return false;
        }
    }
}
