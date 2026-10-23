namespace Models.V3
{
    /// <summary>
    /// Form v3 đã nạp và biên dịch từ App_Data/Form/{verb}{Resource}.xml (KE_HOACH_V3 mục 2). Bất biến: request không sửa được
    /// form đang cache (sửa lỗi 1.4 #4)
    /// </summary>
    public sealed class ImportForm
    {
        /// <summary>Tên file không đuôi, vd setSaleInvoice. Cũng là tên quyền trong restApiAllowForm</summary>
        public required string Name { get; init; }
        /// <summary>Động từ: đoạn chữ thường đầu tên file (set, get, cancel…)</summary>
        public required string Verb { get; init; }
        /// <summary>Resource kebab-case, vd sale-invoice</summary>
        public required string Resource { get; init; }
        /// <summary>Đường dẫn route, vd /api/v3/sale-invoice hoặc /api/v3/sale-invoice/cancel</summary>
        public required string Route { get; init; }

        public required ImportFormType Type { get; init; }
        public string VoucherCode { get; init; } = string.Empty;
        public bool UnitRequired { get; init; }
        /// <summary>Quyền đơn vị cần kiểm (Acc/New/Edit/Del), đã suy từ động từ nếu XML không khai</summary>
        public required string UnitAction { get; init; }
        public bool Enable { get; init; } = true;
        public string Description { get; init; } = string.Empty;

        /// <summary>Khối đầu tiên là khối gốc</summary>
        public required IReadOnlyList<ImportBlock> Blocks { get; init; }
        public ImportBlock Root => Blocks[0];

        /// <summary>Query checking đã thay @@checking / @@checking(n); rỗng nếu không khai</summary>
        public string CheckingQuery { get; init; } = string.Empty;
        public string ProcessingQuery { get; init; } = string.Empty;

        public ImportPaging? Paging { get; init; }

        /// <summary>Cảnh báo lúc nạp (không chặn nạp form)</summary>
        public IReadOnlyList<string> Warnings { get; init; } = [];
    }

    public enum ImportFormType
    {
        List,
        Voucher,
        Report,
        Transfer
    }

    public sealed class ImportBlock
    {
        /// <summary>Tên khối = thuộc tính JSON = bảng tạm #name</summary>
        public required string Name { get; init; }
        /// <summary>Bảng FBO thật, kiểu và độ rộng cột lấy theo bảng này (mục 2.3); rỗng nếu không khai</summary>
        public string Table { get; init; } = string.Empty;
        public bool IsRoot { get; init; }
        /// <summary>Chỉ khối gốc: tên field (JSON) làm khóa nối với khối con</summary>
        public string Key { get; init; } = string.Empty;
        public required IReadOnlyList<ImportField> Fields { get; init; }
    }

    public sealed class ImportField
    {
        /// <summary>Tên thuộc tính JSON</summary>
        public required string Name { get; init; }
        /// <summary>Tên cột bảng tạm / bảng FBO</summary>
        public required string FastName { get; init; }
        public ImportFieldType Type { get; init; } = ImportFieldType.String;
        public int? MaxLength { get; init; }
        public bool AllowNull { get; init; } = true;
        public bool IsPrimaryKey { get; init; }
        public bool UpperCase { get; init; }
        public bool Hidden { get; init; }
        public string? DefaultValue { get; init; }
        public bool JustContent { get; init; }
        public bool CheckIrregular { get; init; }
        /// <summary>Định dạng ngày nhận vào; luôn nhận thêm ISO 8601</summary>
        public string Format { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Example { get; init; } = string.Empty;
        /// <summary>@@checking(N) chỉ chạy field có category = N</summary>
        public int? Category { get; init; }
        /// <summary>Field khóa tự thêm vào khối con từ khóa khối gốc</summary>
        public bool IsInheritedKey { get; init; }
        public IReadOnlyList<ImportCheck> Checks { get; init; } = [];
    }

    public enum ImportFieldType
    {
        String,
        Numeric,
        DateTime,
        Bool,
        Int,
        UnixTime
    }

    /// <summary>
    /// Một phần tử của 4 danh sách song song check/condition/tag/errorCode (như Upload FBO). check đúng = lỗi
    /// </summary>
    public sealed record ImportCheck(string Check, string Condition, string Tag, string ErrorCode);

    public sealed record ImportPaging(int PageSize, string Key);
}
