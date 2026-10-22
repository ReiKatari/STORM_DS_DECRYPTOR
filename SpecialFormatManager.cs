using System;
using System.IO;
using System.Text;

namespace StormDsDecryptor;

public class SpecialFormatItem
{
    public string ContainerPath { get; set; } = string.Empty;
    public string FormatType { get; set; } = string.Empty; // "CIA", "TAD", "NUS"
    public string InternalName { get; set; } = string.Empty;
    public string GameTitle { get; set; } = string.Empty;
    public string GameCode { get; set; } = string.Empty;
    public long ContentOffset { get; set; }
    public long ContentSize { get; set; }
    public string TitleId { get; set; } = string.Empty;
    public bool IsEncrypted { get; set; }
    public bool NeedsCompatibilityPatch { get; set; }
    public byte[]? ExtractedHeader { get; set; }
    public BannerInfo? Banner { get; set; }
}

public static class SpecialFormatManager
{
    private static long Align64(long size) => (size + 63) & ~63L;

    public static bool IsSpecialFormat(string filePath)
    {
        string ext = Path.GetExtension(filePath);
        return ext.Equals(".cia", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".tad", StringComparison.OrdinalIgnoreCase);
    }

    public static SpecialFormatItem? InspectCia(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length < 0x100) return null;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            uint headerSize = br.ReadUInt32();
            ushort type = br.ReadUInt16();
            ushort version = br.ReadUInt16();
            uint certSize = br.ReadUInt32();
            uint ticketSize = br.ReadUInt32();
            uint tmdSize = br.ReadUInt32();
            uint metaSize = br.ReadUInt32();
            ulong contentSize = br.ReadUInt64();

            if (headerSize < 0x20 || headerSize > 0x10000) return null;

            long certOffset = Align64(headerSize);
            long ticketOffset = certOffset + Align64(certSize);
            long tmdOffset = ticketOffset + Align64(ticketSize);
            long contentOffset = tmdOffset + Align64(tmdSize);

            if (contentOffset + 0x200 > fi.Length) return null;

            string titleId = string.Empty;
            if (tmdSize >= 0x194 && tmdOffset + 0x194 <= fi.Length)
            {
                fs.Seek(tmdOffset + 0x18C, SeekOrigin.Begin);
                byte[] tidBytes = br.ReadBytes(8);
                titleId = BitConverter.ToString(tidBytes).Replace("-", "");
            }

            fs.Seek(contentOffset, SeekOrigin.Begin);
            byte[] header = br.ReadBytes(0x400);

            if (DsiDecryptorEngine.IsRomHeader(header) || (header.Length >= 0x160 && header[0x12] != 0))
            {
                var item = new SpecialFormatItem
                {
                    ContainerPath = filePath,
                    FormatType = "CIA",
                    InternalName = $"{Path.GetFileNameWithoutExtension(filePath)}.nds",
                    GameTitle = Encoding.ASCII.GetString(header, 0, 12).Trim((char)0),
                    GameCode = Encoding.ASCII.GetString(header, 0x0C, 4),
                    ContentOffset = contentOffset,
                    ContentSize = (long)contentSize > 0 && contentOffset + (long)contentSize <= fi.Length
                        ? (long)contentSize
                        : fi.Length - contentOffset,
                    TitleId = titleId,
                    ExtractedHeader = header,
                    IsEncrypted = (header[0x1C] & 0x03) != 0x03
                };

                // Check banner
                uint bannerOffset = BitConverter.ToUInt32(header, 0x68);
                if (bannerOffset != 0 && contentOffset + bannerOffset + 0x840 <= fi.Length)
                {
                    fs.Seek(contentOffset + bannerOffset, SeekOrigin.Begin);
                    byte[] bannerData = new byte[0x840];
                    int bRead = fs.Read(bannerData, 0, 0x840);
                    if (bRead >= 0x240)
                    {
                        item.Banner = NdsBannerParser.ParseFromBuffer(bannerData);
                        if (item.Banner != null && !string.IsNullOrWhiteSpace(item.Banner.BestTitle))
                        {
                            item.GameTitle = item.Banner.BestTitle;
                        }
                    }
                }

                return item;
            }
        }
        catch { }

        return null;
    }

    public static SpecialFormatItem? InspectTad(string filePath)
    {
        try
        {
            var fi = new FileInfo(filePath);
            if (fi.Length < 0x100) return null;

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var br = new BinaryReader(fs);

            // 1. Check if the TAD is already a raw NDS/DSi ROM
            byte[] rawHeader = new byte[0x400];
            fs.ReadExactly(rawHeader, 0, 0x400);
            if (DsiDecryptorEngine.IsRomHeader(rawHeader))
            {
                var item = new SpecialFormatItem
                {
                    ContainerPath = filePath,
                    FormatType = "TAD",
                    InternalName = $"{Path.GetFileNameWithoutExtension(filePath)}.nds",
                    GameTitle = Encoding.ASCII.GetString(rawHeader, 0, 12).Trim((char)0),
                    GameCode = Encoding.ASCII.GetString(rawHeader, 0x0C, 4),
                    ContentOffset = 0,
                    ContentSize = fi.Length,
                    ExtractedHeader = rawHeader,
                    IsEncrypted = (rawHeader[0x1C] & 0x03) != 0x03
                };

                uint bannerOffset = BitConverter.ToUInt32(rawHeader, 0x68);
                if (bannerOffset != 0 && bannerOffset + 0x840 <= fi.Length)
                {
                    item.Banner = NdsBannerParser.Parse(fs, bannerOffset);
                    if (item.Banner != null && !string.IsNullOrWhiteSpace(item.Banner.BestTitle))
                    {
                        item.GameTitle = item.Banner.BestTitle;
                    }
                }

                return item;
            }

            // 2. Parse WAD/TAD header
            fs.Seek(0, SeekOrigin.Begin);
            uint headerSize = br.ReadUInt32();
            uint tadType = br.ReadUInt32();
            uint certSize = br.ReadUInt32();
            uint crlSize = br.ReadUInt32();
            uint ticketSize = br.ReadUInt32();
            uint tmdSize = br.ReadUInt32();
            uint dataSize = br.ReadUInt32();

            if (headerSize >= 0x20 && headerSize <= 0x1000)
            {
                long certOffset = Align64(headerSize);
                long crlOffset = certOffset + Align64(certSize);
                long ticketOffset = crlOffset + Align64(crlSize);
                long tmdOffset = ticketOffset + Align64(ticketSize);
                long dataOffset = tmdOffset + Align64(tmdSize);

                if (dataOffset + 0x200 <= fi.Length)
                {
                    fs.Seek(dataOffset, SeekOrigin.Begin);
                    byte[] dataHeader = br.ReadBytes(0x400);
                    if (DsiDecryptorEngine.IsRomHeader(dataHeader) || (dataHeader.Length >= 0x160 && dataHeader[0x12] != 0))
                    {
                        var item = new SpecialFormatItem
                        {
                            ContainerPath = filePath,
                            FormatType = "TAD",
                            InternalName = $"{Path.GetFileNameWithoutExtension(filePath)}.nds",
                            GameTitle = Encoding.ASCII.GetString(dataHeader, 0, 12).Trim((char)0),
                            GameCode = Encoding.ASCII.GetString(dataHeader, 0x0C, 4),
                            ContentOffset = dataOffset,
                            ContentSize = dataSize > 0 && dataOffset + dataSize <= fi.Length ? dataSize : fi.Length - dataOffset,
                            ExtractedHeader = dataHeader,
                            IsEncrypted = (dataHeader[0x1C] & 0x03) != 0x03
                        };

                        uint bannerOffset = BitConverter.ToUInt32(dataHeader, 0x68);
                        if (bannerOffset != 0 && dataOffset + bannerOffset + 0x840 <= fi.Length)
                        {
                            fs.Seek(dataOffset + bannerOffset, SeekOrigin.Begin);
                            byte[] bannerData = new byte[0x840];
                            int bRead = fs.Read(bannerData, 0, 0x840);
                            if (bRead >= 0x240)
                            {
                                item.Banner = NdsBannerParser.ParseFromBuffer(bannerData);
                                if (item.Banner != null && !string.IsNullOrWhiteSpace(item.Banner.BestTitle))
                                {
                                    item.GameTitle = item.Banner.BestTitle;
                                }
                            }
                        }

                        return item;
                    }
                }
            }
        }
        catch { }

        return null;
    }

    public static SpecialFormatItem? InspectNusFolder(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return null;

            string? tmdPath = null;
            foreach (var name in new[] { "title.tmd", "tmd", "tmd.0" })
            {
                string p = Path.Combine(dirPath, name);
                if (File.Exists(p)) { tmdPath = p; break; }
            }

            string? contentPath = null;
            foreach (var f in Directory.GetFiles(dirPath))
            {
                string fn = Path.GetFileName(f);
                if (fn.Equals("00000000", StringComparison.OrdinalIgnoreCase) ||
                    fn.Equals("00000000.app", StringComparison.OrdinalIgnoreCase) ||
                    fn.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    contentPath = f;
                    break;
                }
            }

            if (contentPath == null) return null;

            string titleId = string.Empty;
            if (tmdPath != null && File.Exists(tmdPath))
            {
                byte[] tmdBytes = File.ReadAllBytes(tmdPath);
                if (tmdBytes.Length >= 0x194)
                {
                    byte[] tidBytes = new byte[8];
                    Array.Copy(tmdBytes, 0x18C, tidBytes, 0, 8);
                    titleId = BitConverter.ToString(tidBytes).Replace("-", "");
                }
            }

            byte[] header = new byte[0x400];
            using (var fs = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                fs.ReadExactly(header, 0, 0x400);
            }

            string dirName = Path.GetFileName(dirPath);
            var item = new SpecialFormatItem
            {
                ContainerPath = contentPath,
                FormatType = "NUS",
                InternalName = $"{dirName}.nds",
                GameTitle = Encoding.ASCII.GetString(header, 0, 12).Trim((char)0),
                GameCode = Encoding.ASCII.GetString(header, 0x0C, 4),
                ContentOffset = 0,
                ContentSize = new FileInfo(contentPath).Length,
                TitleId = titleId,
                ExtractedHeader = header,
                IsEncrypted = (header[0x1C] & 0x03) != 0x03
            };

            using (var fs = new FileStream(contentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                uint bannerOffset = BitConverter.ToUInt32(header, 0x68);
                if (bannerOffset != 0 && bannerOffset + 0x840 <= fs.Length)
                {
                    item.Banner = NdsBannerParser.Parse(fs, bannerOffset);
                    if (item.Banner != null && !string.IsNullOrWhiteSpace(item.Banner.BestTitle))
                    {
                        item.GameTitle = item.Banner.BestTitle;
                    }
                }
            }

            return item;
        }
        catch { }

        return null;
    }

    public static void ExtractToStream(string containerPath, long offset, long size, Stream outputStream)
    {
        using var fs = new FileStream(containerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(offset, SeekOrigin.Begin);

        byte[] buffer = new byte[64 * 1024];
        long remaining = size;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(buffer.Length, remaining);
            int read = fs.Read(buffer, 0, toRead);
            if (read <= 0) break;
            outputStream.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    public static bool ExtractToFile(string containerPath, long offset, long size, string outputPath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            using var fsOut = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
            ExtractToStream(containerPath, offset, size, fsOut);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ExtractAndDecrypt(string containerPath, long offset, long size, string outputPath, bool createBackup = false)
    {
        try
        {
            string? dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            if (offset == 0 && size == new FileInfo(containerPath).Length)
            {
                return DsiDecryptorEngine.DecryptFile(containerPath, outputPath, createBackup);
            }

            // Extract to temporary staging file
            string tempStaging = Path.Combine(dir ?? "", $"{Path.GetFileName(outputPath)}.tmp_storm_stage_{Guid.NewGuid():N}");
            try
            {
                if (!ExtractToFile(containerPath, offset, size, tempStaging))
                    return false;

                bool success = DsiDecryptorEngine.DecryptFile(tempStaging, outputPath, false);
                return success;
            }
            finally
            {
                if (File.Exists(tempStaging))
                {
                    try { File.Delete(tempStaging); } catch { }
                }
            }
        }
        catch
        {
            return false;
        }
    }
}
