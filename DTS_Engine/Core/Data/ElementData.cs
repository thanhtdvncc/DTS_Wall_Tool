using System;
using System.Collections.Generic;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Lớp cơ sở trừu tượng cho tất cả phần tử xây dựng trong hệ thống DTS. 
    /// Tuân thủ ISO/IEC 25010: Maintainability, Modularity, Reusability. 
    /// 
    /// Mọi phần tử (Wall, Column, Beam, Slab...) đều kế thừa từ lớp này.
    /// Chứa các thuộc tính chung: Link, Origin, BaseZ, Metadata. 
    /// </summary>
    public abstract class ElementData
    {
        #region Identity

        /// <summary>
        /// Loại phần tử - Bắt buộc override bởi lớp con
        /// </summary>
        public abstract ElementType ElementType { get; }

        /// <summary>
        /// Mã định danh xType trong XData (dùng cho serialization)
        /// </summary>
        public virtual string XType => ElementType.ToString().ToUpperInvariant();

        #endregion

        #region Relationship Links (Smart Link System)

        /// <summary>
        /// Handle của đối tượng cha (Origin/Story)
        /// Khi có giá trị = đã được liên kết với Origin Point của tầng
        /// </summary>
        public string OriginHandle { get; set; } = null;

        /// <summary>
        /// [V7.0] Handle của Mother beam trong Group (dầm đầu tiên trong nhóm)
        /// Khác với OriginHandle (Origin Point tầng)
        /// </summary>
        public string MotherHandle { get; set; } = null;

        /// <summary>
        /// Danh sách Handle của các đối tượng con
        /// </summary>
        public List<string> ChildHandles { get; set; } = new List<string>();

        /// <summary>
        /// [MỚI] Danh sách các liên kết tham chiếu phụ (Reference/Secondary Parents).
        /// Dùng cho: Gối đỡ phụ, Dim/Tag, hoặc quan hệ logic không phải hình học chính.
        /// </summary>
        public List<string> ReferenceHandles { get; set; } = new List<string>();

        /// <summary>
        /// Kiểm tra đã được liên kết với Origin chưa
        /// </summary>
        public bool IsLinked => !string.IsNullOrEmpty(OriginHandle);

        /// <summary>
        /// [V7.0] Kiểm tra có thuộc Group (có Mother) không
        /// </summary>
        public bool HasMother => !string.IsNullOrEmpty(MotherHandle);

        #endregion

        #region Geometry & Position

        /// <summary>
        /// Cao độ đáy phần tử (mm) - lấy từ Origin khi Link
        /// </summary>
        public double? BaseZ { get; set; } = null;

        /// <summary>
        /// Chiều cao phần tử (mm) - dùng để tính tải
        /// </summary>
        public double? Height { get; set; } = null;

        #endregion

        #region SAP2000 Mapping

        /// <summary>
        /// Danh sách mapping đến frame trong SAP2000
        /// </summary>
        public List<MappingRecord> Mappings { get; set; } = new List<MappingRecord>();

        /// <summary>
        /// Tên phần tử SAP2000 (Label) - được ghi khi vẽ từ DTS_PLOT_FROM_SAP
        /// VD: "580", "B12", "C5"
        /// </summary>
        public string SapFrameName { get; set; }

        /// <summary>
        /// Kiểm tra đã được mapping với SAP2000 chưa
        /// </summary>
        public bool HasMapping => Mappings != null && Mappings.Count > 0 &&
                                   Mappings.Exists(m => m.TargetFrame != "New");

        /// <summary>
        /// Kiểm tra có SapFrameName trực tiếp không
        /// </summary>
        public bool HasSapFrame => !string.IsNullOrEmpty(SapFrameName);

        #endregion

        #region Metadata

        /// <summary>
        /// Ghi chú tùy chọn
        /// </summary>
        public string Note { get; set; } = null;

        /// <summary>
        /// Thời điểm cập nhật cuối (ISO 8601)
        /// </summary>
        public string LastModified { get; set; } = null;

        /// <summary>
        /// Phiên bản dữ liệu (để tương thích khi nâng cấp)
        /// </summary>
        public int DataVersion { get; set; } = 1;

        #endregion

        #region Abstract Methods (Bắt buộc implement)

        /// <summary>
        /// Kiểm tra dữ liệu có hợp lệ không
        /// </summary>
        public abstract bool HasValidData();

        /// <summary>
        /// Clone đối tượng (Deep Copy)
        /// </summary>
        public abstract ElementData Clone();

        /// <summary>
        /// Chuyển đổi sang Dictionary để serialize
        /// </summary>
        public abstract Dictionary<string, object> ToDictionary();

        /// <summary>
        /// Đọc dữ liệu từ Dictionary
        /// </summary>
        public abstract void FromDictionary(Dictionary<string, object> dict);

        #endregion

        #region Common Methods

        /// <summary>
        /// Đọc các thuộc tính chung từ Dictionary (gọi từ lớp con)
        /// </summary>
        protected void ReadBaseProperties(Dictionary<string, object> dict)
        {
            if (dict.TryGetValue("xOriginHandle", out var origin))
                OriginHandle = origin?.ToString();

            // V7.0: Read MotherHandle (Group mother beam)
            if (dict.TryGetValue("xMotherHandle", out var mother))
                MotherHandle = mother?.ToString();

            if (dict.TryGetValue("xBaseZ", out var baseZ))
                BaseZ = ConvertToDouble(baseZ);

            if (dict.TryGetValue("xHeight", out var height))
                Height = ConvertToDouble(height);

            if (dict.TryGetValue("xNote", out var note))
                Note = note?.ToString();

            // NOTE: xLastModified không còn được đọc/ghi vào XData
            // NOTE: xDataVersion không còn được đọc/ghi vào XData

            if (dict.TryGetValue("xChildHandles", out var children))
                ChildHandles = ConvertToStringList(children);

            // [MỚI] Đọc an toàn ReferenceHandles (backward compatible)
            if (dict.TryGetValue("xReferenceHandles", out var refs))
            {
                ReferenceHandles = ConvertToStringList(refs);
            }
            else
            {
                ReferenceHandles = new List<string>();
            }

            if (dict.TryGetValue("xMappings", out var mappings))
                Mappings = ConvertToMappingList(mappings);

            // SAP Frame Name (direct label from DTS_PLOT_FROM_SAP)
            if (dict.TryGetValue("xSapFrameName", out var sapFrame))
                SapFrameName = sapFrame?.ToString();
        }

        /// <summary>
        /// Ghi các thuộc tính chung vào Dictionary (gọi từ lớp con)
        /// </summary>
        protected void WriteBaseProperties(Dictionary<string, object> dict)
        {
            dict["xType"] = XType;
            // NOTE: xDataVersion không còn được ghi vào XData

            if (!string.IsNullOrEmpty(OriginHandle))
                dict["xOriginHandle"] = OriginHandle;

            // V7.0: Write MotherHandle (Group mother beam)
            if (!string.IsNullOrEmpty(MotherHandle))
                dict["xMotherHandle"] = MotherHandle;

            if (BaseZ.HasValue)
                dict["xBaseZ"] = BaseZ.Value;

            if (Height.HasValue)
                dict["xHeight"] = Height.Value;

            if (!string.IsNullOrEmpty(Note))
                dict["xNote"] = Note;

            // NOTE: xLastModified không còn được ghi vào XData

            if (ChildHandles != null && ChildHandles.Count > 0)
                dict["xChildHandles"] = ChildHandles;
            // [MỚI] Chỉ ghi ReferenceHandles nếu có để giữ nhỏ gọn dữ liệu
            if (ReferenceHandles != null && ReferenceHandles.Count > 0)
                dict["xReferenceHandles"] = ReferenceHandles;

            if (Mappings != null && Mappings.Count > 0)
                dict["xMappings"] = ConvertMappingsToSerializable(Mappings);

            // SAP Frame Name (direct label from DTS_PLOT_FROM_SAP)
            if (!string.IsNullOrEmpty(SapFrameName))
                dict["xSapFrameName"] = SapFrameName;
        }

        /// <summary>
        /// Copy các thuộc tính base sang đối tượng khác
        /// </summary>
        protected void CopyBaseTo(ElementData target)
        {
            target.OriginHandle = OriginHandle;
            target.BaseZ = BaseZ;
            target.Height = Height;
            target.Note = Note;
            target.LastModified = LastModified;
            target.DataVersion = DataVersion;

            target.ChildHandles = new List<string>(ChildHandles);
            target.Mappings = new List<MappingRecord>();
            foreach (var m in Mappings)
            {
                target.Mappings.Add(m.Clone());
            }
            // [MỚI] Clone References
            target.ReferenceHandles = new List<string>(ReferenceHandles ?? new List<string>());
            // SAP Frame Name
            target.SapFrameName = SapFrameName;
        }

        /// <summary>
        /// Cập nhật timestamp
        /// </summary>
        public void UpdateTimestamp()
        {
            LastModified = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }

        #endregion

        #region Static Helpers

        protected static double? ConvertToDouble(object val)
        {
            if (val == null) return null;
            if (val is double d) return d;
            if (double.TryParse(val.ToString(), out double result)) return result;
            return null;
        }

        protected static int? ConvertToInt(object val)
        {
            if (val == null) return null;
            if (val is int i) return i;
            if (int.TryParse(val.ToString(), out int result)) return result;
            return null;
        }

        protected static List<string> ConvertToStringList(object val)
        {
            var list = new List<string>();
            if (val is System.Collections.IEnumerable enumerable && !(val is string))
            {
                foreach (var item in enumerable)
                {
                    if (item != null) list.Add(item.ToString());
                }
            }
            return list;
        }

        protected static List<MappingRecord> ConvertToMappingList(object val)
        {
            var list = new List<MappingRecord>();
            if (val is System.Collections.IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    if (item is Dictionary<string, object> dict)
                    {
                        var rec = new MappingRecord();
                        if (dict.TryGetValue("TargetFrame", out var tf)) rec.TargetFrame = tf?.ToString();
                        if (dict.TryGetValue("TargetType", out var tt)) rec.TargetType = tt?.ToString() ?? "Frame";
                        if (dict.TryGetValue("MatchType", out var mt)) rec.MatchType = mt?.ToString();
                        if (dict.TryGetValue("DistI", out var di)) rec.DistI = Convert.ToDouble(di);
                        if (dict.TryGetValue("DistJ", out var dj)) rec.DistJ = Convert.ToDouble(dj);
                        if (dict.TryGetValue("CoveredLength", out var cl)) rec.CoveredLength = Convert.ToDouble(cl);
                        if (dict.TryGetValue("FrameLength", out var fl)) rec.FrameLength = Convert.ToDouble(fl);
                        list.Add(rec);
                    }
                }
            }
            return list;
        }

        protected static List<Dictionary<string, object>> ConvertMappingsToSerializable(List<MappingRecord> mappings)
        {
            var list = new List<Dictionary<string, object>>();
            foreach (var m in mappings)
            {
                list.Add(new Dictionary<string, object>
                {
                    ["TargetFrame"] = m.TargetFrame,
                    ["TargetType"] = m.TargetType,
                    ["MatchType"] = m.MatchType,
                    ["DistI"] = m.DistI,
                    ["DistJ"] = m.DistJ,
                    ["CoveredLength"] = m.CoveredLength,
                    ["FrameLength"] = m.FrameLength
                });
            }
            return list;
        }

        #endregion
    }
}