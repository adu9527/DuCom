namespace DuCom.Core.Protocols.Custom;

public static class CustomBinaryProfileValidator
{
    public static CustomBinaryProfileValidationResult Validate(
        CustomBinaryProfile profile,
        CustomBinaryProfileLimits? limits = null,
        string path = "$")
    {
        ArgumentNullException.ThrowIfNull(profile);
        limits ??= new CustomBinaryProfileLimits();
        List<ProtocolDiagnostic> diagnostics = [];
        void Error(string suffix, string message) => diagnostics.Add(new ProtocolDiagnostic(
            "custom.profile.invalid", message, ProtocolDiagnosticSeverity.Error, path + suffix));

        if (string.IsNullOrWhiteSpace(profile.Id)) Error(".id", "Profile ID is required.");
        if (string.IsNullOrWhiteSpace(profile.Name)) Error(".name", "Profile name is required.");
        if (profile.MaximumFrameLength is < 1 || profile.MaximumFrameLength > limits.MaximumFrameLength)
            Error(".maximumFrameLength", $"Maximum frame length must be between 1 and {limits.MaximumFrameLength}.");

        int framingCount = (profile.FixedLength.HasValue ? 1 : 0) + (profile.Length is null ? 0 : 1) + (profile.Terminator is null ? 0 : 1);
        if (framingCount != 1) Error("", "Exactly one of fixedLength, length, or terminator must be configured.");
        if (profile.FixedLength is <= 0) Error(".fixedLength", "Fixed length must be positive.");
        if (profile.FixedLength > profile.MaximumFrameLength) Error(".fixedLength", "Fixed length exceeds maximumFrameLength.");
        if (profile.Sync is { Bytes.Length: 0 }) Error(".sync.bytes", "Sync bytes cannot be empty.");
        if (profile.Sync is { Bytes.Length: > 0 } sync && sync.Bytes.Length > limits.MaximumSyncLength)
            Error(".sync.bytes", $"Sync cannot exceed {limits.MaximumSyncLength} bytes.");
        if (profile.Terminator is { Bytes.Length: 0 }) Error(".terminator.bytes", "Terminator bytes cannot be empty.");
        if (profile.Terminator is { Bytes.Length: > 0 } terminator && terminator.Bytes.Length > limits.MaximumTerminatorLength)
            Error(".terminator.bytes", $"Terminator cannot exceed {limits.MaximumTerminatorLength} bytes.");
        if (profile.Length is { } length)
        {
            if (length.Offset < 0) Error(".length.offset", "Length offset cannot be negative.");
            if (length.Size is < 1 or > 4) Error(".length.size", "Length size must be between 1 and 4 bytes.");
            if ((long)length.Offset + length.Size > profile.MaximumFrameLength) Error(".length", "Length field lies outside maximumFrameLength.");
        }

        if (profile.Escape is { StartOffset: < 0 }) Error(".escape.startOffset", "Escape startOffset cannot be negative.");

        int checksumSize = GetChecksumSize(profile.Checksum?.Algorithm);
        if (profile.Checksum is { } checksum)
        {
            if (checksum.FieldOffset.HasValue == checksum.FieldOffsetFromEnd.HasValue)
                Error(".checksum", "Set exactly one of fieldOffset or fieldOffsetFromEnd.");
            if (checksum.FieldOffset < 0) Error(".checksum.fieldOffset", "Checksum fieldOffset cannot be negative.");
            if (checksum.FieldOffsetFromEnd < checksumSize) Error(".checksum.fieldOffsetFromEnd", "Checksum offset from end must include the checksum field size.");
            if (checksum.CoveredFrom < 0) Error(".checksum.coveredFrom", "Checksum coverage start cannot be negative.");
            if (checksum.CoveredLength < 0) Error(".checksum.coveredLength", "Checksum coverage length cannot be negative.");
        }

        int fieldCount = 0;
        ValidateFields(profile.Fields, ".fields", 1);
        if (fieldCount > limits.MaximumFields) Error(".fields", $"Profile cannot contain more than {limits.MaximumFields} fields.");
        return new CustomBinaryProfileValidationResult(diagnostics.AsReadOnly());

        void ValidateFields(IReadOnlyList<BinaryFieldDefinition>? fields, string fieldsPath, int depth)
        {
            if (fields is null)
            {
                Error(fieldsPath, "Fields cannot be null.");
                return;
            }

            if (depth > limits.MaximumNestingDepth)
            {
                Error(fieldsPath, $"Field nesting cannot exceed {limits.MaximumNestingDepth} levels.");
                return;
            }

            for (int index = 0; index < fields.Count; index++)
            {
                fieldCount++;
                BinaryFieldDefinition field = fields[index];
                string fieldPath = $"{fieldsPath}[{index}]";
                if (string.IsNullOrWhiteSpace(field.Name)) Error(fieldPath + ".name", "Field name is required.");
                if (field.Offset < 0) Error(fieldPath + ".offset", "Field offset cannot be negative.");
                int size = GetFieldSize(field);
                if (size <= 0) Error(fieldPath + ".length", "Text and bit fields require a positive length.");
                if ((long)field.Offset + Math.Max(size, 0) > profile.MaximumFrameLength)
                    Error(fieldPath, "Field lies outside maximumFrameLength.");
                if (field.Type == BinaryFieldType.Bits && (field.BitOffset < 0 || field.BitLength is < 1 or > 64 || field.BitOffset + field.BitLength > size * 8))
                    Error(fieldPath, "Bit range is outside the source bytes or exceeds 64 bits.");
                if (!double.IsFinite(field.Scale) || !double.IsFinite(field.ValueOffset)) Error(fieldPath, "Scale and valueOffset must be finite.");
                ValidateFields(field.Children, fieldPath + ".children", depth + 1);
            }
        }
    }

    internal static int GetChecksumSize(BinaryChecksumAlgorithm? algorithm) => algorithm switch
    {
        BinaryChecksumAlgorithm.Crc16Modbus or BinaryChecksumAlgorithm.Crc16Ccitt or BinaryChecksumAlgorithm.Sum16 => 2,
        BinaryChecksumAlgorithm.Crc32 => 4,
        BinaryChecksumAlgorithm.Xor or BinaryChecksumAlgorithm.Sum8 => 1,
        _ => 0,
    };

    internal static int GetFieldSize(BinaryFieldDefinition field) => field.Type switch
    {
        BinaryFieldType.UInt8 or BinaryFieldType.Int8 => 1,
        BinaryFieldType.UInt16 or BinaryFieldType.Int16 => 2,
        BinaryFieldType.UInt32 or BinaryFieldType.Int32 or BinaryFieldType.Float32 => 4,
        BinaryFieldType.UInt64 or BinaryFieldType.Int64 or BinaryFieldType.Float64 => 8,
        BinaryFieldType.Ascii or BinaryFieldType.Utf8 or BinaryFieldType.Bits => field.Length ?? 0,
        _ => 0,
    };
}
