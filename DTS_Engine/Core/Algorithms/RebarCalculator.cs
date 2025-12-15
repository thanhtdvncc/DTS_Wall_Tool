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
    }
}
