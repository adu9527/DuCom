using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SharpCompress.Compressors.LZMA;

namespace DuCom.Plugins.Checksum;

public sealed record FirmwareResult
{
    public required string Path { get; init; }
    public required string FileName { get; init; }
    public required long FileSize { get; init; }
    public required string FileSha256 { get; init; }
    public string Manufacturer { get; init; } = "未知厂商";
    public string FileType { get; init; } = "未识别";
    public string Chip { get; init; } = "未识别";
    public string BluetoothName { get; init; } = "未识别";
    public string BuildDate { get; init; } = "未嵌入";
    public string FirmwareCode { get; init; } = "";
    public bool? VendorContentVerified { get; init; }
    public bool? CrcValid { get; init; }
    public bool? ImageValid { get; init; }
    public bool? PackageCrcMatches { get; init; }
    public string Note { get; init; } = "";
    public bool IsCorrupt => CrcValid is false || ImageValid is false || PackageCrcMatches is false;
    public string Status => IsCorrupt ? "校验不一致，请核对固件文件。"
        : VendorContentVerified is true || (ImageValid is true && CrcValid is true) ? "完整性校验通过"
        : string.IsNullOrEmpty(FirmwareCode) ? "未识别到校验码；恒玄固件请先按接入文档配置。"
        : "已读取校验码；未完成整文件完整性验证";
}

public static class FirmwareAnalyzer
{
    public const int MaxBytes = 64 * 1024 * 1024;
    private static readonly Regex Metadata = new(@"\nCHIP=[^\x00]{1,8192}\x00", RegexOptions.CultureInvariant);
    private static readonly Regex CrcField = new(@"(?<=\n)CRC32_OF_IMAGE=0x([0-9A-Fa-f]{8})(?=\r?\n)", RegexOptions.CultureInvariant);
    private static readonly Regex ImageField = new(@"(?<=\n)IMAGE_SHA256=([0-9A-Fa-f]{64})(?=\r?\n)", RegexOptions.CultureInvariant);
    private static readonly byte[] FooterPrefix = "CRC32_OF_IMAGE=0x"u8.ToArray();
    private static readonly uint[] CrcTable = MakeTable();

    public static FirmwareResult Analyze(byte[] file, string path, DateTimeOffset? lastWriteTimeUtc = null)
    {
        ArgumentNullException.ThrowIfNull(file);
        if (file.Length == 0 || file.Length > MaxBytes) throw new InvalidDataException("文件为空或超过 64 MiB，请选择完整固件。");
        var result = new FirmwareResult { Path = path, FileName = System.IO.Path.GetFileName(path), FileSize = file.Length, FileSha256 = Hash(file) };
        var vendor = VendorFormats.Read(file);
        if (vendor != null)
            return result with
            {
                Manufacturer = vendor.Manufacturer, FileType = vendor.FileType, FirmwareCode = vendor.Code,
                Chip = vendor.Chip, BluetoothName = vendor.BluetoothName, VendorContentVerified = vendor.ContentVerified, Note = vendor.Note,
                BuildDate = lastWriteTimeUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + (lastWriteTimeUtc.HasValue ? "（文件修改时间）" : "未识别"),
            };
        bool compressed = file.Length >= 43 && file.AsSpan(0, 4).SequenceEqual(new byte[] { 255, 255, 255, 255 })
            && file.AsSpan(file.Length - 26, FooterPrefix.Length).SequenceEqual(FooterPrefix);
        byte[] image = file;
        string? footerCrc = null;
        if (compressed) (image, footerCrc) = DecodePackage(file);
        // Only decode bounded metadata; a 64 MiB input must not create a 128 MiB string.
        List<int> starts = [];
        int search = 0;
        while (search < image.Length)
        {
            int match = image.AsSpan(search).IndexOf("\nCHIP="u8);
            if (match < 0) break;
            int start = search + match;
            int available = Math.Min(8199, image.Length - start);
            int terminator = image.AsSpan(start, available).IndexOf((byte)0);
            if (terminator > 6) starts.Add(start);
            search = start + 6;
        }
        if (starts.Count > 1) throw new InvalidDataException("固件存在多个 CHIP 信息段，无法唯一定位，已停止解析。");
        int metadataStart = starts.Count == 1 ? starts[0] : 0;
        string text = starts.Count == 1 ? Encoding.Latin1.GetString(image, metadataStart, image.AsSpan(metadataStart).IndexOf((byte)0) + 1) : "";
        var blocks = Metadata.Matches(text);
        if (blocks.Count == 0) return result with { Note = "未识别到受支持的固件结构。恒玄需要 build_info 元信息；不根据文件名或扩展名猜测厂商。" };
        if (blocks.Count != 1) throw new InvalidDataException("固件存在多个 CHIP 信息段，无法唯一定位，已停止解析。");
        var block = blocks[0];
        var fields = Regex.Matches(block.Value, @"(?:^|\n)([A-Z_0-9]+)=([^\r\n\x00]*)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
        string Field(string key, string fallback = "") => fields.GetValueOrDefault(key, fallback);
        bool bes = Regex.IsMatch(Field("CHIP"), @"\Abest[0-9A-Za-z_]+\z", RegexOptions.IgnoreCase)
            && fields.ContainsKey("BUILD_DATE") && fields.ContainsKey("KERNEL");
        if (!bes) return result with { Note = "信息块未能确认恒玄厂商结构。" };
        var crcs = CrcField.Matches(block.Value);
        var shas = ImageField.Matches(block.Value);
        if (crcs.Count > 1 || shas.Count > 1) throw new InvalidDataException("信息段包含重复的校验字段。");
        foreach (var key in new[] { "SOURCE_SHA256", "BUILD_SHA256", "IMAGE_SHA256" })
            if (fields.ContainsKey(key) && !Regex.IsMatch(Field(key), @"\A[0-9A-Fa-f]{64}\z")) throw new InvalidDataException($"{key} 字段格式损坏。");
        if (fields.ContainsKey("FW_ID_SCHEMA") && Field("FW_ID_SCHEMA") != "1") throw new InvalidDataException("不支持此固件身份格式版本，请升级工具。");
        if (fields.ContainsKey("FW_ID_SCHEMA") && new[] { "SOURCE_SHA256", "BUILD_SHA256", "IMAGE_SHA256", "IDENTITY_TARGET" }.Any(key => !fields.ContainsKey(key)))
            throw new InvalidDataException("新格式固件缺少必要身份字段。");
        if (fields.ContainsKey("FIRMWARE_CODE") && (!Regex.IsMatch(Field("FIRMWARE_CODE"), @"\A[0-9A-Fa-f]{8}\z")
            || !fields.ContainsKey("BUILD_SHA256") || !string.Equals(Crc32(Convert.FromHexString(Field("BUILD_SHA256"))).ToString("X8", CultureInfo.InvariantCulture), Field("FIRMWARE_CODE"), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("固件版本校验码与构建标识不匹配。");
        bool? crcValid = null, imageValid = null;
        string stored = "", calculated = "";
        if (crcs.Count == 1)
        {
            var crc = crcs[0];
            int offset = metadataStart + block.Index + crc.Groups[1].Index;
            stored = crc.Groups[1].Value.ToUpperInvariant();
            calculated = Crc32(image.AsSpan(0, offset)).ToString("X8", CultureInfo.InvariantCulture);
            crcValid = stored == "00000000" ? null : stored == calculated;
            if (shas.Count == 1)
            {
                var sha = shas[0];
                string imageSha = sha.Groups[1].Value.ToUpperInvariant();
                int shaOffset = metadataStart + block.Index + sha.Groups[1].Index;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                int cursor = 0;
                foreach (var field in new[] { (Offset: offset, Length: 8), (Offset: shaOffset, Length: 64) }.OrderBy(field => field.Offset))
                {
                    hash.AppendData(image.AsSpan(cursor, field.Offset - cursor));
                    hash.AppendData(Encoding.ASCII.GetBytes(new string('0', field.Length)));
                    cursor = field.Offset + field.Length;
                }
                hash.AppendData(image.AsSpan(cursor));
                imageValid = imageSha != new string('0', 64) && Convert.ToHexString(hash.GetHashAndReset()) == imageSha;
                crcValid = stored == calculated;
            }
        }
        else if (fields.ContainsKey("FW_ID_SCHEMA")) throw new InvalidDataException("新格式固件缺少 CRC32 字段。");
        return result with
        {
            Manufacturer = "恒玄", FileType = compressed ? "OTA升级固件" : "烧录固件", Chip = Field("CHIP", "未识别"),
            BuildDate = NormalizeDate(Field("BUILD_DATE", "未嵌入")),
            BluetoothName = fields.ContainsKey("BT_NAME") ? Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(Field("BT_NAME"))).Trim().Trim('"') : LegacyName(image),
            FirmwareCode = Field("FIRMWARE_CODE", stored == "00000000" ? "" : stored).ToUpperInvariant(),
            CrcValid = crcValid, ImageValid = imageValid, PackageCrcMatches = compressed && stored.Length > 0 ? footerCrc == stored : null,
            Note = imageValid.HasValue ? "整镜像 SHA-256 覆盖所有镜像字节（计算时自身与 CRC 字段置零）；版本短码与 OTA CRC 含义不同。"
                : "已读取固件版本标识。若存在原厂 CRC，仅按 BES OTA 前缀约定验证，不覆盖该字段后的内容；未宣称整文件校验通过。",
        };
    }

    private static string LegacyName(byte[] image) => image.AsSpan().IndexOf("[USER_BT_NAME] Classic repaired and runtime updated"u8) >= 0
        && image.AsSpan().IndexOf("\0Sudio SoundBase\0"u8) >= 0 ? "Sudio SoundBase（旧版候选）" : "未识别（旧固件无名称字段）";

    private static string NormalizeDate(string value)
    {
        string[] formats = ["MMM d yyyy HH:mm:ss", "yyyy-MM-dd HH:mm:ss", "yyyy/MM/dd HH:mm:ss", "yyyy年MM月dd日HH:mm:ss"];
        return DateTime.TryParseExact(Regex.Replace(value.Trim(), @"\s+", " "), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) : value;
    }

    private static (byte[], string) DecodePackage(byte[] data)
    {
        int end = data.Length - 26;
        string footer = Encoding.ASCII.GetString(data, end, 26);
        if (!Regex.IsMatch(footer, @"\ACRC32_OF_IMAGE=0x[0-9A-Fa-f]{8}\n\z")) throw new InvalidDataException("OTA 尾部 CRC 格式损坏。");
        using var output = new MemoryStream();
        int offset = 4;
        while (offset < end)
        {
            if (end - offset < 4) throw new InvalidDataException("OTA 块长度缺失。");
            uint size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            offset += 4;
            if (size < 13 || size > end - offset) throw new InvalidDataException("OTA 块长度越界或文件被截断。");
            long unpacked = BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset + 5, 8));
            if (unpacked != -1 && (unpacked <= 0 || unpacked > 131072)) throw new InvalidDataException("OTA 解压长度无效或超限。");
            byte[] properties = data.AsSpan(offset, 5).ToArray();
            uint dictionary = BinaryPrimitives.ReadUInt32LittleEndian(properties.AsSpan(1));
            if (dictionary > 128 * 1024 * 1024) throw new InvalidDataException("OTA LZMA 字典超限。");
            using var compressed = new MemoryStream(data, offset + 13, (int)size - 13, false);
            using var decoder = LzmaStream.Create(properties, compressed, (long)size - 13, unpacked, false);
            long before = output.Length;
            var buffer = new byte[8192];
            int read;
            while ((read = decoder.Read(buffer, 0, buffer.Length)) > 0)
            {
                if (output.Length - before + read > 131072 || output.Length + read > MaxBytes || (unpacked != -1 && output.Length - before + read > unpacked)) throw new InvalidDataException("OTA 解压数据超过声明长度。");
                output.Write(buffer, 0, read);
            }
            if (output.Length == before || (unpacked != -1 && output.Length - before != unpacked)) throw new InvalidDataException("OTA 解压数据长度不匹配。");
            offset += (int)size;
        }
        if (offset != end || output.Length == 0) throw new InvalidDataException("OTA 包结构无效。");
        return (output.ToArray(), footer.Substring(FooterPrefix.Length, 8).ToUpperInvariant());
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));
    public static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte b in bytes) crc = CrcTable[(crc ^ b) & 255] ^ (crc >> 8);
        return crc ^ uint.MaxValue;
    }
    private static uint[] MakeTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i;
            for (int j = 0; j < 8; j++) value = (value & 1) != 0 ? 0xEDB88320 ^ (value >> 1) : value >> 1;
            table[i] = value;
        }
        return table;
    }
}
