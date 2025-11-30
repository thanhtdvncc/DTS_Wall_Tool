using System;

namespace DTS_Wall_Tool.Core
{
    // Class lưu thông tin kết quả mapping để vẽ Label
    public class MappingRecord
    {
        public string TargetFrame { get; set; } = "";
        public string MatchType { get; set; } = "";
        public double DistI { get; set; } = 0;
        public double DistJ { get; set; } = 0;

        public override string ToString()
        {
            if (MatchType == "NEW") return "to New";
            // Đổi mm sang m cho gọn khi hiển thị
            return $"to {TargetFrame} I={DistI / 1000:0.0}to{DistJ / 1000:0.0}";
        }
    }
}