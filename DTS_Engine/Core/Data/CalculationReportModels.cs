using System.Collections.Generic;

namespace DTS_Engine.Core.Data
{
    // Cấu trúc tương ứng với JSON cho Javascript
    public class ReportGroupData
    {
        public string GroupName { get; set; }   // VD: "Dầm Tầng 2 - Trục A"
        public string ProjectName { get; set; } // VD: "Chung cư ABC"
        public string SectionName { get; set; } // VD: "GX1 (300x500)"
        public List<ReportSpanData> Spans { get; set; } = new List<ReportSpanData>();
    }

    public class ReportSpanData
    {
        public string SpanId { get; set; }      // VD: "B1-1"
        public string Section { get; set; }     // VD: "300x500"
        public string Length { get; set; }      // VD: "4500"
        public string Material { get; set; }    // VD: "B25 / CB400"

        // 3 Vùng dữ liệu
        public ReportStationData Left { get; set; }
        public ReportStationData Mid { get; set; }
        public ReportStationData Right { get; set; }
    }

    public class ReportStationData
    {
        public string ElementId { get; set; }
        public string Station { get; set; }
        public string LoadCase { get; set; } // Tên tổ hợp bao
        public int Legs { get; set; }        // Số nhánh đai (No. Leg)

        // Cần tách biệt Top/Bot/Stirrup/Web cho report
        public ReportForceResult TopResult { get; set; }
        public ReportForceResult BotResult { get; set; }
        public ReportForceResult StirrupResult { get; set; } // Tổng Av/s + 2At/s
        public ReportForceResult StirrupOnlyResult { get; set; } // Chỉ Av/s (dòng 4.6 trong spec)
        public ReportForceResult WebResult { get; set; }
        public ReportForceResult AlResult { get; set; } // Thép dọc xoắn (Al - dòng 4.7)
    }

    public class ReportForceResult
    {
        public string ElementId { get; set; }   // Số hiệu phần tử (Frame ID)
        public string Station { get; set; }     // Nhãn vị trí (L1, Center, L2)
        public string LocationMm { get; set; }  // Tọa độ mm (Traceability)
        public double? Moment { get; set; }
        public double? Shear { get; set; }
        public double? AsCalc { get; set; }
        public double? AsProv { get; set; }
        public string RebarStr { get; set; }
        public double? Ratio { get; set; }
        public string LoadCase { get; set; }
        public string Conclusion { get; set; }  // OK / NG
    }
}
