using System;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Core
{
    // Class chứa dữ liệu tường - Mọi thứ mặc định là null
    public class WallData
    {
        // Dữ liệu cơ bản (Null able)
        public double? Thickness { get; set; } = null;
        public string WallType { get; set; } = null;
        public string LoadPattern { get; set; } = null;
        public double? LoadValue { get; set; } = null;

        // Danh sách các dầm trong SAP được map tới
        public System.Collections.Generic.List<MappingRecord> Mappings { get; set; } = new System.Collections.Generic.List<MappingRecord>();


        // Link Handle cha (1-1): "Tôi thuộc về ai?" (Ví dụ: Tường thuộc về Tầng nào)
        public string OriginHandle { get; set; } = null; // Link tới Cha (Gốc/Cột)

        // Link con: Danh sách các Con: "Ai thuộc về tôi?" (Dùng cho mạng lưới sau này)
        // Dùng cho tuong lai khi cần liên kết nhiều đối tượng với nhau
        // Ví dụ: Cột có thể chứa danh sách các Tường con đang bám vào nó
        // Khởi tạo sẵn danh sách rỗng để tránh lỗi null reference
        public List<string> ChildHandles { get; set; } = new List<string>();

        // Cao độ (nếu gán cứng)
        public double? BaseZ { get; set; } = null;

        public override string ToString()
        {
            // Kiểm tra null khi in ra
            string thkStr = Thickness.HasValue ? Thickness.Value.ToString() : "[Trống]";
            string loadStr = LoadValue.HasValue ? LoadValue.Value.ToString() : "[Trống]";
            
            string parentInfo = string.IsNullOrEmpty(OriginHandle)? "":$"|Cha: {OriginHandle}";
            string childInfo = (ChildHandles.Count > 0) ? $" | Con: {ChildHandles.Count} bé" : "";

            return $"Type={WallType ?? "[Trống]"}, Thick={thkStr}, Load={loadStr}{parentInfo}{childInfo}";
        }
    }
}