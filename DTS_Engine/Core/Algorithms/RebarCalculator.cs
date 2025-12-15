using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms
{
    public class RebarCalculator
    {
        /// <summary>
        /// Tính toán chọn thép cho 1 tiết diện.
        /// </summary>
        public static string Calculate(double areaReq, double b, double h, RebarSettings settings)
        {
            if (areaReq <= 0.01) return "-"; // Không cần thép

            // 1. Xác định list đường kính
            var diameters = settings.PreferredDiameters;
            if (diameters == null || diameters.Count == 0) diameters = new List<int> { 16, 18, 20, 22, 25 };

            // 2. Loop qua các đường kính để tìm phương án tối ưu
            // Tiêu chí tối ưu:
            // - Thỏa mãn As >= AreaReq
            // - Thỏa mãn khoảng hở (min spacing)
            // - Ít thanh nhất hoặc dư ít nhất? (Thường ưu tiên số thanh chẵn/hợp lý và dư ít nhất)
            
            // Chiến thuật "Vét cạn" đơn giản hóa (Greedy per diameter):
            // Thử từng D, xem cần bao nhiêu thanh. Check spacing. Nếu 1 lớp không đủ -> 2 lớp.
            
            string bestSol = "";
            double minAreaExcess = double.MaxValue;

            foreach (int d in diameters)
            {
                double as1 = Math.PI * d * d / 400.0; // cm2 per bar
                
                // Số thanh lý thuyết
                int nTotal = (int)Math.Ceiling(areaReq / as1);
                
                // Check Max thanh 1 lớp
                int nMaxOneLayer = GetMaxBarsPerLayer(b, settings.CoverTop, d, settings.MinSpacing);
                
                // Xử lý bố trí
                string currentSol = "";
                
                if (nTotal <= nMaxOneLayer)
                {
                    // 1 Lớp đủ
                    // Quy tắc VBA cũ: Nếu chỉ 1 cây -> ép lên 2 cây (để tạo khung)
                    if (nTotal < 2) nTotal = 2;
                    currentSol = $"{nTotal}d{d}";
                }
                else
                {
                    // Phải 2 lớp
                    // Lớp 1: nMax (hoặc n_chaysuot)
                    // Lớp 2: nTotal - nMax
                    // Ràng buộc số lớp tối đa? User settings usually imply limit.
                    // Let's assume max 2 layers for simplicity first version.
                    
                    int nL1 = nMaxOneLayer;
                    int nL2 = nTotal - nL1;
                    
                    // Logic VBA: n_chaysuot (Run-through).
                    // Thường lớp 1 là chạy suốt, lớp 2 gia cường.
                    // Nếu nL2 quá ít (1 cây), có thể tăng nL2 lên 2.
                    if (nL2 < 2) nL2 = 2;
                    
                    // Re-check total area with adjusted counts
                    nTotal = nL1 + nL2;
                    
                    currentSol = $"{nL1}d{d} + {nL2}d{d}";
                }

                double areaProv = nTotal * as1;
                double excess = areaProv - areaReq;
                
                // Chọn phương án dư ít nhất (Economy)
                if (excess >= 0 && excess < minAreaExcess)
                {
                    minAreaExcess = excess;
                    bestSol = currentSol;
                }
                else if (string.IsNullOrEmpty(bestSol) && excess >= 0)
                {
                    // Fallback: nếu chưa có sol nào và excess >= 0, lấy luôn
                    bestSol = currentSol;
                    minAreaExcess = excess;
                }
            }

            // Nếu vẫn không tìm được (tất cả diameter đều không đủ chỗ), 
            // dùng đường kính lớn nhất và bố trí dư
            if (string.IsNullOrEmpty(bestSol))
            {
                int dMax = diameters.Max();
                double as1 = Math.PI * dMax * dMax / 400.0;
                int n = (int)Math.Ceiling(areaReq / as1);
                if (n < 2) n = 2;
                bestSol = $"{n}d{dMax}*"; // Asterisk indicates forced arrangement
            }

            return bestSol;
        }

        private static int GetMaxBarsPerLayer(double b, double cover, int d, double minSpacing)
        {
            // b: width (mm)
            // cover: (mm)
            // d: (mm)
            // space: (mm)
            
            // Valid width = b - 2*cover - 2*stirrup (assume 10mm stirrup)
            double workingWidth = b - 2 * cover - 2 * 10; 
            
            // n * d + (n-1)*s <= workingWidth
            // n(d+s) - s <= workingWidth
            // n(d+s) <= workingWidth + s
            // n <= (workingWidth + s) / (d + s)
            
            double val = (workingWidth + minSpacing) / (d + minSpacing);
            int n = (int)Math.Floor(val);
            return n < 2 ? 2 : n; // Min 2 bars usually
        }

        /// <summary>
        /// Tính toán bước đai từ diện tích cắt yêu cầu.
        /// Input: shearArea (cm2/cm) - Diện tích thép đai trên 1 đơn vị dài
        /// Output: String dạng "2-d8a150" (số nhánh - phi - bước)
        /// Logic: Tự động tăng số nhánh (2→3→4) nếu bước đai quá nhỏ
        /// </summary>
        public static string CalculateStirrup(double shearArea, RebarSettings settings)
        {
            if (shearArea <= 0.01) return "-"; // Không cần đai

            int d = settings.StirrupDiameter;
            var spacings = settings.StirrupSpacings;
            int minSpacing = 100; // Bước đai tối thiểu thi công được (mm)

            if (spacings == null || spacings.Count == 0)
                spacings = new List<int> { 100, 150, 200, 250 };

            // Diện tích 1 nhánh đai (cm2)
            double as1 = Math.PI * d * d / 400.0;

            // Thử từ 2 nhánh đến 4 nhánh
            for (int nLegs = 2; nLegs <= 4; nLegs++)
            {
                double asTotal = as1 * nLegs;

                // Tính bước đai tối đa cho phép
                // Công thức: Asw/s >= shearArea => s <= Asw / shearArea
                double maxSpacing = (asTotal / shearArea) * 10; // cm → mm

                // Chọn bước đai chuẩn lớn nhất thỏa mãn
                int selectedSpacing = -1;
                foreach (var s in spacings.OrderByDescending(x => x))
                {
                    if (s <= maxSpacing)
                    {
                        selectedSpacing = s;
                        break;
                    }
                }

                // Nếu tìm được bước đai >= minSpacing
                if (selectedSpacing >= minSpacing)
                {
                    return $"{nLegs}-d{d}a{selectedSpacing}";
                }
                
                // Nếu bước đai nhỏ nhất trong spacings vẫn <= maxSpacing nhưng < minSpacing
                // thì tăng nhánh và thử lại
            }

            // Fallback: Dùng 4 nhánh với bước nhỏ nhất
            return $"4-d{d}a{spacings.Min()}*";
        }
        /// <summary>
        /// Tính toán cốt giá/sườn (Web bars).
        /// Logic: Max(Diện tích chịu xoắn phân bổ, Diện tích cấu tạo theo chiều cao).
        /// Input:
        /// - torsionTotal: Tổng diện tích xoắn Al (cm2) lấy từ SAP.
        /// - torsionRatioSide: Tỷ lệ phân bổ vào sườn (VD: 0.5).
        /// - heightMm: Chiều cao dầm (mm).
        /// </summary>
        public static string CalculateWebBars(double torsionTotal, double torsionRatioSide, double heightMm, RebarSettings settings)
        {
            int d = settings.WebBarDiameter;
            double as1 = Math.PI * d * d / 400.0; // cm2 per bar

            // 1. Tính toán theo Chịu lực (Torsion)
            // Diện tích cần cho 2 mặt bên = Al * RatioSide
            double reqAreaSide = torsionTotal * torsionRatioSide;
            int nTorsion = 0;
            if (reqAreaSide > 0.01)
            {
                nTorsion = (int)Math.Ceiling(reqAreaSide / as1);
                if (nTorsion % 2 != 0) nTorsion++; // Luôn chẵn (đối xứng)
            }

            // 2. Tính toán theo Cấu tạo (Constructive)
            int nConstructive = 0;
            if (heightMm >= settings.WebBarMinHeight)
            {
                // Rule: H>=700 -> 2 cây; H>=1000 -> 4 cây
                nConstructive = 2;
                if (heightMm >= 1000) nConstructive = 4;
            }

            // 3. Lấy Max (Envelope)
            int nFinal = Math.Max(nTorsion, nConstructive);

            if (nFinal == 0) return "-";

            return $"{nFinal}d{d}";
        }
    }
}
