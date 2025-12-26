using System;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Utility class for calculating rebar weight with CORRECT UNITS.
    /// 
    /// CÔNG THỨC KINH ĐIỂN:
    /// Weight (kg) = (d² / 162) × L(m) × N
    /// 
    /// Trong đó:
    /// - d: đường kính thanh thép (mm)
    /// - L: chiều dài thanh thép (m)
    /// - N: số lượng thanh
    /// - 162: hệ số quy đổi từ công thức 7850 × π/4 / 10⁶
    /// 
    /// VÍ DỤ:
    /// - D10, 1m, 1 thanh → 10²/162 × 1 × 1 = 0.617 kg
    /// - D25, 6m, 4 thanh → 25²/162 × 6 × 4 = 92.6 kg
    /// </summary>
    public static class WeightCalculator
    {
        /// <summary>
        /// Hệ số d²/162 (xấp xỉ từ 7850 kg/m³ × π/4 / 1,000,000)
        /// Chính xác: 7850 × 3.14159 / 4 / 1,000,000 = 0.00617
        /// Hệ số 1/162 = 0.00617
        /// </summary>
        private const double UNIT_WEIGHT_FACTOR = 0.00617;

        /// <summary>
        /// Tính trọng lượng thép (kg)
        /// </summary>
        /// <param name="diameterMM">Đường kính thanh thép (mm)</param>
        /// <param name="lengthMM">Tổng chiều dài (mm) - sẽ được đổi sang mét</param>
        /// <param name="count">Số lượng thanh</param>
        /// <returns>Trọng lượng (kg)</returns>
        public static double CalculateWeight(int diameterMM, double lengthMM, int count)
        {
            if (count <= 0 || lengthMM <= 0 || diameterMM <= 0) return 0;

            // 1. Đổi chiều dài từ mm sang MÉT
            double lengthM = lengthMM / 1000.0;

            // 2. Tính trọng lượng đơn vị (kg/m) = d² × 0.00617
            double unitWeightPerMeter = diameterMM * diameterMM * UNIT_WEIGHT_FACTOR;

            // 3. Tổng trọng lượng (kg) = unitWeight × length(m) × count
            return unitWeightPerMeter * lengthM * count;
        }

        /// <summary>
        /// Tính trọng lượng backbone chạy suốt dầm
        /// </summary>
        /// <param name="diameterMM">Đường kính (mm)</param>
        /// <param name="totalLengthMM">Tổng chiều dài dầm (mm)</param>
        /// <param name="count">Số thanh</param>
        /// <param name="lapSpliceFactor">Hệ số nối chồng (VD: 1.02 = thêm 2% cho mối nối)</param>
        /// <returns>Trọng lượng (kg)</returns>
        public static double CalculateBackboneWeight(int diameterMM, double totalLengthMM, int count, double lapSpliceFactor = 1.02)
        {
            return CalculateWeight(diameterMM, totalLengthMM, count) * lapSpliceFactor;
        }

        /// <summary>
        /// Tính trọng lượng thép gia cường (Addon) dựa trên chiều dài nhịp
        /// </summary>
        public static double CalculateAddonWeight(int diameterMM, int count, double spanLengthM, double sideFactor = 0.4)
        {
            if (count <= 0 || spanLengthM <= 0 || diameterMM <= 0) return 0;
            double lengthMM = spanLengthM * 1000.0 * sideFactor;
            return CalculateWeight(diameterMM, lengthMM, count);
        }

        /// <summary>
        /// Tra bảng trọng lượng trên mét (kg/m) cho các đường kính thông dụng
        /// </summary>
        public static double GetUnitWeight(int diameterMM)
        {
            // d² × 0.00617
            return diameterMM * diameterMM * UNIT_WEIGHT_FACTOR;
        }

        /// <summary>
        /// Validation: Kiểm tra kết quả có hợp lý không
        /// Dầm 10m × 4 thanh D25 ≈ 154 kg, không phải 0.1 kg
        /// </summary>
        public static bool ValidateWeight(double weightKg, double lengthMM, int totalBars)
        {
            if (lengthMM <= 0 || totalBars <= 0) return true;

            // Heuristic: Mỗi thanh D16 dài 1m nặng ≈ 1.58 kg
            // Nên nếu dầm 10m × 4 thanh nhẹ hơn 5kg thì sai
            double minExpected = totalBars * (lengthMM / 1000.0) * 0.5; // D10 = 0.617 kg/m
            double maxExpected = totalBars * (lengthMM / 1000.0) * 10.0; // D40 = 9.87 kg/m

            return weightKg >= minExpected * 0.5 && weightKg <= maxExpected * 2;
        }
    }
}
