using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Algorithms
{
    public class RebarCalculator
    {
        /// <summary>
        /// [DEPRECATED] Tính toán chọn thép cho 1 tiết diện.
        /// ⚠️ Sử dụng Calculate(areaReq, b, h, DtsSettings) thay thế!
        /// </summary>
        [Obsolete("Use Calculate with DtsSettings parameter instead")]
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

        /// <summary>
        /// Tính toán chọn thép sử dụng DtsSettings mới (với range parsing).
        /// Ưu tiên sử dụng method này cho code mới.
        /// </summary>
        public static string Calculate(double areaReq, double b, double h, DtsSettings settings)
        {
            if (areaReq <= 0.01) return "-";

            // Parse range từ settings, lọc theo inventory
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var diameters = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            if (diameters.Count == 0) diameters = inventory;

            // Lọc đường kính chẵn nếu user yêu cầu
            if (settings.Beam?.PreferEvenDiameter == true)
                diameters = DiameterParser.FilterEvenDiameters(diameters);

            if (diameters.Count == 0) diameters = new List<int> { 16, 18, 20, 22, 25 };

            int maxLayers = settings.Beam?.MaxLayers ?? 2;
            int minBarsPerLayer = settings.Beam?.MinBarsPerLayer ?? 2;

            string bestSol = "";
            double minAreaExcess = double.MaxValue;

            foreach (int d in diameters)
            {
                double as1 = Math.PI * d * d / 400.0;
                int nTotal = (int)Math.Ceiling(areaReq / as1);
                // Sử dụng overload mới với bar diameter-based spacing
                int nMaxOneLayer = GetMaxBarsPerLayer(b, d, settings);

                string currentSol = "";

                if (nTotal <= nMaxOneLayer)
                {
                    if (nTotal < 2) nTotal = 2;
                    // Ưu tiên số chẵn nếu PreferSymmetric
                    if (settings.Beam?.PreferSymmetric == true && nTotal % 2 != 0)
                        nTotal++;
                    currentSol = $"{nTotal}d{d}";
                }
                else
                {
                    int nL1 = nMaxOneLayer;
                    int nL2 = nTotal - nL1;
                    if (nL2 < 2) nL2 = 2;
                    nTotal = nL1 + nL2;
                    currentSol = $"{nL1}d{d} + {nL2}d{d}";
                }

                double areaProv = nTotal * as1;
                double excess = areaProv - areaReq;

                if (excess >= 0 && excess < minAreaExcess)
                {
                    minAreaExcess = excess;
                    bestSol = currentSol;
                }
            }

            if (string.IsNullOrEmpty(bestSol))
            {
                int dMax = diameters.Max();
                double as1 = Math.PI * dMax * dMax / 400.0;
                int n = (int)Math.Ceiling(areaReq / as1);
                if (n < 2) n = 2;
                bestSol = $"{n}d{dMax}*";
            }

            return bestSol;
        }

        /// <summary>
        /// UNIFIED ROUNDING for rebar area values across DTS_REBAR system.
        /// Rule: &lt;1 → 4 decimal places, ≥1 → 2 decimal places
        /// </summary>
        public static string FormatRebarValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return "-";
            if (value < 1)
                return value.ToString("F4");
            return value.ToString("F2");
        }

        /// <summary>
        /// Round rebar area value for XData storage (same logic as FormatRebarValue but returns double).
        /// </summary>
        public static double RoundRebarValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return 0;
            if (value < 1)
                return Math.Round(value, 4);
            return Math.Round(value, 2);
        }

        /// <summary>
        /// [DEPRECATED] Tính số thanh tối đa trong 1 lớp (phiên bản cơ bản với spacing cố định)
        /// ⚠️ CẢNH BÁO: Phương thức này hardcode stirrupDia = 10. 
        /// Sử dụng GetMaxBarsPerLayer(beamWidth, barDiameter, DtsSettings) thay thế!
        /// </summary>
        [Obsolete("Use GetMaxBarsPerLayer with DtsSettings parameter instead")]
        private static int GetMaxBarsPerLayer(double b, double cover, int d, double minSpacing)
        {
            // b: width (mm), cover: (mm), d: bar diameter (mm), minSpacing: (mm)
            // LEGACY CODE: Hardcoded stirrup diameter - SỬ DỤNG BẢN MỚI VỚI DtsSettings!
            double stirrupDia = 10; // ⚠️ HARDCODE - Đã có phiên bản mới dùng StirrupBarRange từ settings
            double workingWidth = b - 2 * cover - 2 * stirrupDia;

            // n * d + (n-1)*s <= workingWidth
            // n(d+s) - s <= workingWidth
            // n(d+s) <= workingWidth + s
            // n <= (workingWidth + s) / (d + s)

            double val = (workingWidth + minSpacing) / (d + minSpacing);
            int n = (int)Math.Floor(val);
            return n < 2 ? 2 : n; // Min 2 bars usually
        }

        /// <summary>
        /// Tính số thanh tối đa trong 1 lớp với DtsSettings
        /// Công thức: n = (UsableWidth + spacing) / (d + spacing)
        /// UsableWidth = B - 2×Cover - 2×StirrupDia
        /// spacing = max(barDiameter, MinClearSpacing)
        /// </summary>
        private static int GetMaxBarsPerLayer(double beamWidth, int barDiameter, DtsSettings settings)
        {
            // 1. Cover từ Settings
            double cover = settings.Beam?.CoverSide ?? 25;

            // 2. Đường kính đai: Dùng EstimatedStirrupDiameter từ Settings
            // Nếu = 0 (Auto), lấy Max trong StirrupBarRange (để an toàn cho tính toán hở)
            double stirrupDia = settings.Beam?.EstimatedStirrupDiameter ?? 0;
            if (stirrupDia <= 0)
            {
                var inventory = settings.General?.AvailableDiameters ?? new List<int> { 6, 8, 10 };
                var stirrups = DiameterParser.ParseRange(settings.Beam?.StirrupBarRange ?? "8-10", inventory);
                stirrupDia = stirrups.Any() ? stirrups.Max() : 10;
            }

            // 3. UsableWidth = B - 2×Cover - 2×StirrupDia
            double usableWidth = beamWidth - (2 * cover) - (2 * stirrupDia);

            if (usableWidth < barDiameter) return 0; // Dầm quá bé

            // 4. Khoảng hở: max(barDiameter, MinClearSpacing, 1.33*AggregateSize)
            double minClearSpacing = settings.Beam?.MinClearSpacing ?? 30;
            double aggregateSpacing = (settings.Beam?.AggregateSize ?? 20) * 1.33;
            double reqClearance = Math.Max(barDiameter, Math.Max(minClearSpacing, aggregateSpacing));

            // 5. Công thức: n = (W + s) / (d + s)
            double val = (usableWidth + reqClearance) / (barDiameter + reqClearance);
            int maxBars = (int)Math.Floor(val);

            int minBars = settings.Beam?.MinBarsPerLayer ?? 2;
            return maxBars < minBars ? minBars : maxBars;
        }

        /// <summary>
        /// Parse chuỗi quy tắc auto legs (VD: "250-2 400-3 600-4 800-5")
        /// Trả về list các tuple (maxWidth, legs) đã sắp xếp tăng dần.
        /// </summary>
        public static List<(int, int)> ParseAutoLegsRules(string rules)
        {
            var result = new List<(int, int)>();
            if (string.IsNullOrWhiteSpace(rules)) return result;

            var parts = rules.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('-');
                if (kv.Length == 2 && int.TryParse(kv[0], out int w) && int.TryParse(kv[1], out int l))
                {
                    result.Add((w, l)); // Item1 = maxWidth, Item2 = legs
                }
            }
            return result.OrderBy(x => x.Item1).ToList();
        }

        /// <summary>
        /// Tính số nhánh đai tự động dựa trên bề rộng dầm và quy tắc user định nghĩa.
        /// </summary>
#pragma warning disable CS0618 // Intentional: backward compatibility with RebarSettings
        public static int GetAutoLegs(double beamWidthMm, RebarSettings settings)
        {
            if (!settings.AutoLegsFromWidth)
                return settings.StirrupLegs > 0 ? settings.StirrupLegs : 2;

            var rules = ParseAutoLegsRules(settings.AutoLegsRules);
            if (rules.Count == 0)
            {
                // Quy tắc mặc định nếu không có
                if (beamWidthMm <= 250) return 2;
                if (beamWidthMm <= 400) return 3;
                if (beamWidthMm <= 600) return 4;
                return 5;
            }

            // Tìm quy tắc phù hợp (Item1 = maxWidth, Item2 = legs)
            foreach (var rule in rules)
            {
                if (beamWidthMm <= rule.Item1)
                    return rule.Item2;
            }
            // Nếu bề rộng lớn hơn tất cả, dùng số nhánh lớn nhất
            return rules.Last().Item2;
        }
#pragma warning restore CS0618

        /// <summary>
        /// Tính toán bước đai từ diện tích cắt và xoắn yêu cầu.
        /// Công thức ACI/TCVN: Atotal/s = Av/s + 2×At/s
        /// Thuật toán vét cạn: thử từng đường kính × từng số nhánh để tìm phương án tối ưu.
        /// Output: String dạng "2-d8a150" (số nhánh - phi - bước)
        /// </summary>
        /// <param name="beamWidthMm">Bề rộng dầm (mm) để tính auto legs. Nếu 0 sẽ dùng StirrupLegs.</param>
#pragma warning disable CS0618 // Intentional: backward compatibility with RebarSettings
        public static string CalculateStirrup(double shearArea, double ttArea, double beamWidthMm, RebarSettings settings)
        {
            // ACI/TCVN: Tổng diện tích đai trên đơn vị dài = Av/s + 2 * At/s
            double totalAreaPerLen = shearArea + 2 * ttArea;

            if (totalAreaPerLen <= 0.001) return "-";

            // Lấy danh sách đường kính đai (ưu tiên nhỏ trước để tiết kiệm)
            var diameters = settings.StirrupDiameters;
            if (diameters == null || diameters.Count == 0)
                diameters = new List<int> { settings.StirrupDiameter > 0 ? settings.StirrupDiameter : 8 };

            var spacings = settings.StirrupSpacings;
            if (spacings == null || spacings.Count == 0)
                spacings = new List<int> { 100, 150, 200, 250 };

            int minSpacingAcceptable = 100;

            // Tính số nhánh cơ sở từ bề rộng dầm (hoặc dùng fixed nếu AutoLegsFromWidth = false)
            int baseLegs = GetAutoLegs(beamWidthMm, settings);

            // Tạo danh sách phương án: baseLegs ± 1, 2 để tìm tối ưu
            var legOptions = new List<int> { baseLegs };
            if (baseLegs - 1 >= 2) legOptions.Insert(0, baseLegs - 1);
            legOptions.Add(baseLegs + 1);
            legOptions.Add(baseLegs + 2);

            // Lọc bỏ nhánh lẻ nếu không cho phép
            if (!settings.AllowOddLegs)
                legOptions = legOptions.Where(l => l % 2 == 0).ToList();

            if (legOptions.Count == 0)
                legOptions = new List<int> { 2, 4 };

            // Duyệt qua từng đường kính đai (ưu tiên đai nhỏ trước để tiết kiệm)
            foreach (int d in diameters.OrderBy(x => x))
            {
                // Với mỗi đường kính, thử tăng dần số nhánh
                foreach (int legs in legOptions)
                {
                    string res = TryFindSpacing(totalAreaPerLen, d, legs, spacings, minSpacingAcceptable);
                    if (res != null) return res; // Tìm thấy phương án thỏa mãn đầu tiên
                }
            }

            // Nếu vẫn không được, trả về phương án Max (lấy số nhánh lớn nhất trong list thử)
            int maxLegs = legOptions.Last();
            int dMax = diameters.Max();
            int sMin = spacings.Min();
            return $"{maxLegs}-d{dMax}a{sMin}*";
        }

        /// <summary>
        /// Helper: Thử tìm bước đai phù hợp cho đường kính và số nhánh cho trước.
        /// Trả về null nếu không tìm được bước đai >= minSpacingAcceptable.
        /// </summary>
        private static string TryFindSpacing(double totalAreaPerLen, int d, int legs, List<int> spacings, int minSpacingAcceptable)
        {
            double as1Layer = (Math.PI * d * d / 400.0) * legs;

            // Tính bước đai max cho phép (mm) = (As_1_layer / Areq_per_cm) * 10
            double maxSpacingReq = (as1Layer / totalAreaPerLen) * 10.0;

            // Tìm bước đai lớn nhất trong list mà vẫn <= maxSpacingReq
            foreach (var s in spacings.OrderByDescending(x => x))
            {
                if (s <= maxSpacingReq && s >= minSpacingAcceptable)
                {
                    return $"{legs}-d{d}a{s}";
                }
            }

            return null; // Không tìm được bước phù hợp
        }
#pragma warning restore CS0618

        /// <summary>
        /// Tính toán cốt giá/sườn (Web bars).
        /// Logic: Envelope(Torsion, Constructive) và làm chẵn.
        /// Sử dụng danh sách đường kính để tìm phương án tối ưu.
        /// </summary>
#pragma warning disable CS0618 // Intentional: backward compatibility with RebarSettings
        public static string CalculateWebBars(double torsionTotal, double torsionRatioSide, double heightMm, RebarSettings settings)
        {
            // Lấy danh sách đường kính sườn (ưu tiên nhỏ trước)
            var diameters = settings.WebBarDiameters;
            if (diameters == null || diameters.Count == 0)
                diameters = new List<int> { settings.WebBarDiameter > 0 ? settings.WebBarDiameter : 12 };

            double minHeight = settings.WebBarMinHeight > 0 ? settings.WebBarMinHeight : 700;

            // a. Theo chịu lực xoắn
            double reqArea = torsionTotal * torsionRatioSide;

            // b. Theo cấu tạo (Dầm cao >= minHeight)
            bool needConstructive = heightMm >= minHeight;

            // Thử từng đường kính để tìm phương án tối ưu
            foreach (int d in diameters.OrderBy(x => x))
            {
                double as1 = Math.PI * d * d / 400.0;

                int nTorsion = 0;
                if (reqArea > 0.01)
                    nTorsion = (int)Math.Ceiling(reqArea / as1);

                int nConstructive = needConstructive ? 2 : 0;

                // Lấy Max và làm chẵn
                int nFinal = Math.Max(nTorsion, nConstructive);
                if (nFinal > 0 && nFinal % 2 != 0) nFinal++;

                if (nFinal > 0 && nFinal <= 6) // Giới hạn hợp lý
                    return $"{nFinal}d{d}";
            }

            // Fallback: dùng đường kính lớn nhất
            int dMax = diameters.Max();
            double asMax = Math.PI * dMax * dMax / 400.0;
            int nMax = reqArea > 0.01 ? (int)Math.Ceiling(reqArea / asMax) : (needConstructive ? 2 : 0);
            if (nMax % 2 != 0) nMax++;

            if (nMax == 0) return "-";
            return $"{nMax}d{dMax}";
        }
#pragma warning restore CS0618

        // ====================================================================
        // DtsSettings OVERLOADS - Use these instead of RebarSettings versions
        // ====================================================================

        /// <summary>
        /// [DtsSettings] Tính số nhánh đai tự động dựa trên bề rộng và settings.
        /// </summary>
        public static int GetAutoLegs(double beamWidthMm, DtsSettings settings)
        {
            var beamCfg = settings.Beam;
            if (beamCfg == null) return 2;

            var rules = ParseAutoLegsRules(beamCfg.AutoLegsRules);
            if (rules.Count == 0)
            {
                // Quy tắc mặc định
                if (beamWidthMm <= 250) return 2;
                if (beamWidthMm <= 400) return 3;
                if (beamWidthMm <= 600) return 4;
                return 5;
            }

            foreach (var rule in rules)
            {
                if (beamWidthMm <= rule.Item1)
                    return rule.Item2;
            }
            return rules.Last().Item2;
        }

        /// <summary>
        /// [DtsSettings] Tính toán bước đai từ diện tích cắt và xoắn yêu cầu.
        /// </summary>
        public static string CalculateStirrup(double shearArea, double ttArea, double beamWidthMm, DtsSettings settings)
        {
            double totalAreaPerLen = shearArea + 2 * ttArea;
            if (totalAreaPerLen <= 0.001) return "-";

            var beamCfg = settings.Beam;
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 8, 10 };

            // Parse StirrupBarRange từ settings
            var diameters = DiameterParser.ParseRange(beamCfg?.StirrupBarRange ?? "8-10", inventory);
            if (diameters.Count == 0) diameters = new List<int> { 8, 10 };

            // Bước đai từ settings (với validation)
            // Bước đai từ settings (với validation)
            var spacings = beamCfg?.StirrupSpacings;
            if (spacings == null || spacings.Count == 0)
                spacings = new List<int> { 100, 150, 200, 250 };
            int minSpacingAcceptable = 100;

            int baseLegs = GetAutoLegs(beamWidthMm, settings);

            var legOptions = new List<int> { baseLegs };
            if (baseLegs - 1 >= 2) legOptions.Insert(0, baseLegs - 1);
            legOptions.Add(baseLegs + 1);
            legOptions.Add(baseLegs + 2);

            if (beamCfg?.AllowOddLegs != true)
                legOptions = legOptions.Where(l => l % 2 == 0).ToList();

            if (legOptions.Count == 0)
                legOptions = new List<int> { 2, 4 };

            foreach (int d in diameters.OrderBy(x => x))
            {
                foreach (int legs in legOptions)
                {
                    string res = TryFindSpacing(totalAreaPerLen, d, legs, spacings, minSpacingAcceptable);
                    if (res != null) return res;
                }
            }

            int maxLegs = legOptions.Last();
            int dMax = diameters.Max();
            int sMin = spacings.Min();
            return $"{maxLegs}-d{dMax}a{sMin}*";
        }

        /// <summary>
        /// [DtsSettings] Tính toán cốt giá/sườn (Web bars).
        /// </summary>
        public static string CalculateWebBars(double torsionTotal, double torsionRatioSide, double heightMm, DtsSettings settings)
        {
            var beamCfg = settings.Beam;
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 12, 14 };

            var diameters = DiameterParser.ParseRange(beamCfg?.SideBarRange ?? "12-14", inventory);
            if (diameters.Count == 0) diameters = new List<int> { 12, 14 };

            double minHeight = beamCfg?.WebBarMinHeight ?? 700;

            double reqArea = torsionTotal * torsionRatioSide;
            bool needConstructive = heightMm >= minHeight;

            foreach (int d in diameters.OrderBy(x => x))
            {
                double as1 = Math.PI * d * d / 400.0;

                int nTorsion = 0;
                if (reqArea > 0.01)
                    nTorsion = (int)Math.Ceiling(reqArea / as1);

                int nConstructive = needConstructive ? 2 : 0;

                int nFinal = Math.Max(nTorsion, nConstructive);
                if (nFinal > 0 && nFinal % 2 != 0) nFinal++;

                if (nFinal > 0 && nFinal <= 6)
                    return $"{nFinal}d{d}";
            }

            int dMax = diameters.Max();
            double asMax = Math.PI * dMax * dMax / 400.0;
            int nMax = reqArea > 0.01 ? (int)Math.Ceiling(reqArea / asMax) : (needConstructive ? 2 : 0);
            if (nMax % 2 != 0) nMax++;

            if (nMax == 0) return "-";
            return $"{nMax}d{dMax}";
        }

        /// <summary>
        /// Parse diện tích thép từ chuỗi bố trí dọc (VD: "4d20", "2d16+3d18").
        /// Trả về tổng diện tích cm2.
        /// </summary>
        public static double ParseRebarArea(string rebarStr)
        {
            if (string.IsNullOrEmpty(rebarStr) || rebarStr == "-") return 0;

            double total = 0;
            // Split by '+' for multi-layer arrangements
            var parts = rebarStr.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var s = part.Trim();
                // Expected format: NdD (e.g., "4d20")
                var match = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)[dD](\d+)");
                if (match.Success)
                {
                    int n = int.Parse(match.Groups[1].Value);
                    int d = int.Parse(match.Groups[2].Value);
                    double as1 = Math.PI * d * d / 400.0; // cm2 per bar
                    total += n * as1;
                }
            }
            return total;
        }

        /// <summary>
        /// Parse diện tích đai trên đơn vị dài từ chuỗi bố trí (VD: "d10a150", "4-d8a100").
        /// Trả về A/s (cm2/cm). Tự động nhận diện số nhánh nếu có tiền tố "N-".
        /// </summary>
        public static double ParseStirrupAreaPerLen(string stirrupStr, int defaultLegs = 2)
        {
            if (string.IsNullOrEmpty(stirrupStr) || stirrupStr == "-") return 0;

            // Regex bắt cả số nhánh (Group 1 - Optional)
            // Format: "4-d8a100" hoặc "d8a150"
            var match = System.Text.RegularExpressions.Regex.Match(stirrupStr, @"(?:(\d+)-)?[dD](\d+)[aA](\d+)");

            if (match.Success)
            {
                // 1. Xác định số nhánh
                int nLegs = defaultLegs;
                if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out int n))
                {
                    nLegs = n;
                }

                // 2. Lấy đường kính và bước
                int d = int.Parse(match.Groups[2].Value);
                int spacing = int.Parse(match.Groups[3].Value); // mm

                if (spacing <= 0) return 0;

                double as1 = Math.PI * d * d / 400.0; // cm2 per bar

                // 3. Tính cm2/cm: (nLegs * As1) / (Spacing_cm)
                double areaPerLen = (nLegs * as1) / (spacing / 10.0);
                return areaPerLen;
            }
            return 0;
        }

        #region BeamGroup-Based Calculation (Multi-Standard)

        /// <summary>
        /// Kết quả tính thép cho 1 lớp (Top hoặc Bot) của BeamGroup
        /// </summary>
        public class LayerResult
        {
            public bool IsValid { get; set; }
            public int Diameter { get; set; }
            public int MainBars { get; set; }         // Thép chạy suốt
            public Dictionary<string, int> AddBars { get; set; } = new Dictionary<string, int>(); // Thép gia cường theo section
            public int TotalBars { get; set; }
            public int LayersNeeded { get; set; }
            public double AsProvided { get; set; }    // cm²
        }

        /// <summary>
        /// Kết quả tổng hợp cho cả BeamGroup (Top + Bot)
        /// </summary>
        public class BeamGroupSolution
        {
            public bool IsValid { get; set; }
            public int MainDiameter { get; set; }
            public LayerResult TopLayer { get; set; }
            public LayerResult BotLayer { get; set; }
            public string WarningMessage { get; set; }
        }

        /// <summary>
        /// [DEPRECATED] Tính thép cho cả BeamGroup theo tiêu chuẩn đa quốc gia
        /// ⚠️ Sử dụng CalculateProposalsForGroup thay thế! Method này dùng MAX thay vì per-span As_req.
        /// </summary>
        [System.Obsolete("Use CalculateProposalsForGroup instead. This method uses MAX instead of per-span As_req.")]
        public static BeamGroupSolution SolveBeamGroup(BeamGroup group, DtsSettings settings)
        {
            if (group?.Spans == null || group.Spans.Count == 0)
                return new BeamGroupSolution { IsValid = false, WarningMessage = "Không có nhịp trong nhóm" };

            // Lấy danh sách đường kính từ Settings
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var diameters = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);
            if (diameters.Count == 0) diameters = inventory;

            int maxLayers = settings.Beam?.MaxLayers ?? 2;

            // Duyệt từng đường kính từ nhỏ → lớn
            foreach (int d in diameters.OrderBy(x => x))
            {
                // Tính riêng TOP và BOT
                var topResult = SolveLayer(group, d, true, settings);   // isTopBar = true
                var botResult = SolveLayer(group, d, false, settings);  // isTopBar = false

                // Kiểm tra cả 2 đều valid (SolveLayer đã check maxLayers)
                if (topResult.IsValid && botResult.IsValid)
                {
                    return new BeamGroupSolution
                    {
                        IsValid = true,
                        MainDiameter = d,
                        TopLayer = topResult,
                        BotLayer = botResult
                    };
                }
            }

            // Fallback: dùng đường kính lớn nhất
            int dMax = diameters.Max();
            return new BeamGroupSolution
            {
                IsValid = false,
                MainDiameter = dMax,
                TopLayer = SolveLayer(group, dMax, true, settings),
                BotLayer = SolveLayer(group, dMax, false, settings),
                WarningMessage = $"Không tìm được phương án ≤{maxLayers} lớp, dùng D{dMax}"
            };
        }

        /// <summary>
        /// Tính thép cho 1 lớp (Top hoặc Bot) với đường kính cho trước
        /// </summary>
        private static LayerResult SolveLayer(BeamGroup group, int d, bool isTopBar, DtsSettings settings)
        {
            var result = new LayerResult { Diameter = d };

            // Bề rộng dầm (TODO: Dầm T thì dùng Bf cho Top, Bw cho Bot)
            double beamWidth = group.Width > 0 ? group.Width : (group.Spans.FirstOrDefault()?.Width ?? 300);

            // Tính số thanh tối đa 1 lớp
            int maxPerLayer = GetMaxBarsPerLayer(beamWidth, d, settings);
            if (maxPerLayer <= 0)
            {
                result.IsValid = false;
                return result;
            }

            int maxLayers = settings.Beam?.MaxLayers ?? 2;
            double as1 = Math.PI * d * d / 400.0; // cm² per bar

            // Tìm As_max từ tất cả tiết diện trong dải dầm
            // SpanData.As_Top[6]: 0=GốiT, 1=L/4T, 2=Giữa, 3=L/4P, 4=GốiP
            // SpanData.As_Bot[6]: tương tự
            double asMaxRequired = 0;
            foreach (var span in group.Spans)
            {
                if (isTopBar)
                {
                    // Thép trên: lấy max từ As_Top array (chủ yếu tại gối 0 và 4)
                    if (span.As_Top != null)
                    {
                        foreach (double asVal in span.As_Top)
                        {
                            if (asVal > asMaxRequired) asMaxRequired = asVal;
                        }
                    }
                }
                else
                {
                    // Thép dưới: lấy max từ As_Bot array (chủ yếu tại giữa nhịp 2)
                    if (span.As_Bot != null)
                    {
                        foreach (double asVal in span.As_Bot)
                        {
                            if (asVal > asMaxRequired) asMaxRequired = asVal;
                        }
                    }
                }
            }

            if (asMaxRequired <= 0.01)
            {
                // Không cần thép (hoặc theo cấu tạo min 2 thanh)
                result.IsValid = true;
                result.MainBars = 2;
                result.TotalBars = 2;
                result.LayersNeeded = 1;
                result.AsProvided = 2 * as1;
                return result;
            }

            // Tính số thanh cần thiết
            int totalBars = (int)Math.Ceiling(asMaxRequired / as1);
            if (totalBars < 2) totalBars = 2;

            // Tính số lớp cần thiết
            int layersNeeded = (int)Math.Ceiling((double)totalBars / maxPerLayer);

            // Kiểm tra constraint - DÙNG settings.MaxLayers, không hardcode
            bool isValid = layersNeeded <= maxLayers;

            result.IsValid = isValid;
            result.MainBars = Math.Min(totalBars, maxPerLayer); // Thép chạy suốt = lớp 1
            result.TotalBars = totalBars;
            result.LayersNeeded = layersNeeded;
            result.AsProvided = totalBars * as1;

            return result;
        }

        /// <summary>
        /// Tính chiều dài neo có xét TopBarFactor
        /// </summary>
        public static double GetAnchorageWithTopFactor(int diameter, bool isTopBar, DtsSettings settings)
        {
            // Lấy chiều dài neo cơ bản từ AnchorageConfig
            double baseLength = settings.Anchorage?.GetAnchorageLength(diameter, "B25", "CB400")
                ?? (40 * diameter); // Fallback: 40d

            // Áp dụng TopBarFactor nếu là thép lớp trên
            if (isTopBar && settings.Beam?.ApplyTopBarFactor == true)
            {
                double factor = settings.Beam?.TopBarFactor ?? 1.3;
                baseLength *= factor;
            }

            return baseLength;
        }

        /// <summary>
        /// Tính chiều dài nối có xét TopBarFactor
        /// </summary>
        public static double GetSpliceWithTopFactor(int diameter, bool isTopBar, DtsSettings settings)
        {
            double baseLength = settings.Anchorage?.GetSpliceLength(diameter, "B25", "CB400")
                ?? (52 * diameter); // Fallback: 52d

            if (isTopBar && settings.Beam?.ApplyTopBarFactor == true)
            {
                double factor = settings.Beam?.TopBarFactor ?? 1.3;
                baseLength *= factor;
            }

            return baseLength;
        }


        #endregion

        #region OUT-PERFORM ALGORITHM (Multi-Proposal with Scoring)

        /// <summary>
        /// [V2.0 - DETERMINISTIC ALGORITHM]
        /// Tạo ra N phương án bố trí thép cho Group, từ Tiết kiệm đến Dễ thi công.
        /// 
        /// CORE PRINCIPLES:
        /// 1. No Magic Numbers - Strict input validation, no fallback values
        /// 2. Decoupling Backbone - Top/Bot calculated independently  
        /// 3. Deterministic Filling - Calculate, don't guess (Greedy vs Balanced)
        /// 4. Strict Constructability - Stirrup leg snapping, pyramid rules
        /// </summary>
        public static List<ContinuousBeamSolution> CalculateProposalsForGroup(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings)
        {
            var solutions = new List<ContinuousBeamSolution>();

            // ═══════════════════════════════════════════════════════════════════
            // STEP 1: DATA SANITIZATION (No Magic Numbers!)
            // ═══════════════════════════════════════════════════════════════════

            double beamWidth = group.Width;
            double beamHeight = group.Height;

            // Try fallback from SAP results if group dimensions missing
            if (beamWidth <= 0 || beamHeight <= 0)
            {
                var firstValidSpan = spanResults?.FirstOrDefault(s => s != null && s.Width > 0);
                if (firstValidSpan != null)
                {
                    if (beamWidth <= 0) beamWidth = firstValidSpan.Width * 1000; // m -> mm
                    if (beamHeight <= 0) beamHeight = firstValidSpan.SectionHeight * 1000;
                }
            }

            // 🛑 HARD FAIL: No valid dimensions = No calculation
            if (beamWidth <= 0 || beamHeight <= 0)
            {
                System.Diagnostics.Debug.WriteLine($"[RebarCalc V2] FAIL: Invalid dimensions W={beamWidth}, H={beamHeight}");
                return new List<ContinuousBeamSolution>
                {
                    new ContinuousBeamSolution
                    {
                        OptionName = "ERROR",
                        IsValid = false,
                        ValidationMessage = $"Không có kích thước dầm hợp lệ (W={beamWidth:F0}, H={beamHeight:F0}). Chạy DTS_REBAR_SAP_RESULT trước."
                    }
                };
            }

            // Parse available diameters
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var allowedDias = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            if (settings.Beam?.PreferEvenDiameter == true)
                allowedDias = DiameterParser.FilterEvenDiameters(allowedDias);

            allowedDias.Sort();

            if (!allowedDias.Any())
            {
                return new List<ContinuousBeamSolution>
                {
                    new ContinuousBeamSolution { OptionName = "ERROR", IsValid = false, ValidationMessage = "Không có đường kính thép hợp lệ trong Settings." }
                };
            }

            // Get spacing constraints
            double maxSpacing = settings.Beam?.MaxClearSpacing ?? 300;

            // Get Global Max Requirements for loop bounds optimization
            double maxReqTop = spanResults.Where(s => s?.TopArea != null).SelectMany(s => s.TopArea).DefaultIfEmpty(0).Max();
            double maxReqBot = spanResults.Where(s => s?.BotArea != null).SelectMany(s => s.BotArea).DefaultIfEmpty(0).Max();

            System.Diagnostics.Debug.WriteLine($"[RebarCalc V2] W={beamWidth}, H={beamHeight}, MaxReqTop={maxReqTop:F2}, MaxReqBot={maxReqBot:F2}");

            // ═══════════════════════════════════════════════════════════════════
            // STEP 2: SMART BACKBONE SIMULATION LOOPS (Dynamic Boundaries)
            // ═══════════════════════════════════════════════════════════════════

            int scenariosTried = 0;
            int validScenarios = 0;

            // Loop 1: Top Diameter
            foreach (int topDia in allowedDias)
            {
                // Calculate dynamic bounds based on spacing constraints
                int topMinBars = CalculateMinBarsForSpacing(beamWidth, topDia, maxSpacing, settings);
                int topMaxBars = GetMaxBarsPerLayer(beamWidth, topDia, settings);

                // Loop 2: Bot Diameter (Can differ from Top)
                foreach (int botDia in allowedDias)
                {
                    int botMinBars = CalculateMinBarsForSpacing(beamWidth, botDia, maxSpacing, settings);
                    int botMaxBars = GetMaxBarsPerLayer(beamWidth, botDia, settings);

                    // Loop 3: Top Backbone Count (Min to Min+2, capped at Max)
                    int topStart = Math.Max(2, topMinBars);
                    int topEnd = Math.Min(topStart + 2, topMaxBars);

                    for (int nTop = topStart; nTop <= topEnd; nTop++)
                    {
                        // Loop 4: Bot Backbone Count
                        int botStart = Math.Max(2, botMinBars);
                        int botEnd = Math.Min(botStart + 2, botMaxBars);

                        for (int nBot = botStart; nBot <= botEnd; nBot++)
                        {
                            scenariosTried++;

                            // CONSTRUCTABILITY CONSTRAINT: Top/Bot count difference
                            if (Math.Abs(nTop - nBot) > 2) continue;

                            // ═══════════════════════════════════════════════
                            // STEP 3: DETERMINISTIC SCENARIO SOLUTION
                            // ═══════════════════════════════════════════════

                            var sol = SolveDeterministicScenario(
                                group, spanResults,
                                topDia, botDia, nTop, nBot,
                                beamWidth, beamHeight, settings);

                            if (sol.IsValid)
                            {
                                validScenarios++;
                                solutions.Add(sol);
                            }
                        }
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[RebarCalc V2] Scenarios: {scenariosTried} tried, {validScenarios} valid");

            // ═══════════════════════════════════════════════════════════════════
            // STEP 4: SCORING & RANKING
            // ═══════════════════════════════════════════════════════════════════

            if (solutions.Count > 0)
            {
                // Calculate Constructability Scores
                foreach (var s in solutions.Where(s => s.IsValid))
                {
                    s.ConstructabilityScore = ConstructabilityScoring.CalculateScore(s, group, settings);
                }

                // Normalize Weight Scores
                var weights = solutions.Where(s => s.IsValid).Select(s => s.TotalSteelWeight).Where(w => w > 0).ToList();
                if (weights.Count > 0)
                {
                    double minW = weights.Min();
                    double maxW = weights.Max();

                    foreach (var s in solutions.Where(s => s.IsValid))
                    {
                        double weightScore = (maxW - minW) < 0.001 ? 100 : (maxW - s.TotalSteelWeight) / (maxW - minW) * 100;
                        weightScore = Math.Max(0, Math.Min(100, weightScore));

                        double cs = Math.Max(0, Math.Min(100, s.ConstructabilityScore));
                        s.TotalScore = 0.6 * weightScore + 0.4 * cs;
                    }
                }

                // Remove duplicates and rank
                var ranked = solutions
                    .Where(s => s.IsValid)
                    .GroupBy(s => s.OptionName)
                    .Select(g => g.OrderByDescending(x => x.TotalScore).First())
                    .OrderByDescending(s => s.TotalScore)
                    .ThenBy(s => s.TotalSteelWeight)
                    .Take(5)
                    .ToList();

                return ranked;
            }

            return solutions;
        }

        /// <summary>
        /// Calculate minimum bars to prevent spacing > MaxSpacing (crack control).
        /// Formula: N_min = ceil((W - 2C) / (S_max + d))
        /// </summary>
        private static int CalculateMinBarsForSpacing(double width, int dia, double maxSpacing, DtsSettings settings)
        {
            double cover = settings.Beam?.CoverSide ?? 25;
            double stirrup = settings.Beam?.EstimatedStirrupDiameter ?? 10;
            double usable = width - 2 * cover - 2 * stirrup;

            if (usable <= 0 || maxSpacing <= 0) return 2;

            int n = (int)Math.Ceiling(usable / (maxSpacing + dia));
            return Math.Max(2, n);
        }

        /// <summary>
        /// Solves a specific backbone scenario deterministically.
        /// Visits every span and calculates local reinforcement using AutoFill.
        /// </summary>
        private static ContinuousBeamSolution SolveDeterministicScenario(
            BeamGroup group, List<BeamResultData> results,
            int topDia, int botDia, int nTop, int nBot,
            double beamWidth, double beamHeight, DtsSettings settings)
        {
            var sol = new ContinuousBeamSolution
            {
                OptionName = nTop == nBot && topDia == botDia
                    ? $"{nTop}D{topDia}"
                    : $"T:{nTop}D{topDia}/B:{nBot}D{botDia}",
                BackboneDiameter = topDia,
                BackboneCount_Top = nTop,
                BackboneCount_Bot = nBot,
                As_Backbone_Top = nTop * GetBarArea(topDia),
                As_Backbone_Bot = nBot * GetBarArea(botDia),
                IsValid = true,
                Reinforcements = new Dictionary<string, RebarSpec>()
            };

            double totalLength = group.Spans?.Sum(s => s.Length) ?? 0;
            if (totalLength <= 0) totalLength = 6000;

            int numSpans = Math.Min(group.Spans?.Count ?? 0, results?.Count ?? 0);
            int legCount = GetStirrupLegCount(beamWidth, settings);

            // ═══════════════════════════════════════════════════════════════════
            // ITERATE EACH SPAN - DETERMINISTIC LOCAL FILLING
            // ═══════════════════════════════════════════════════════════════════

            for (int i = 0; i < numSpans; i++)
            {
                var span = group.Spans[i];
                var res = results[i];
                if (res == null) continue;

                // A. TOP REINFORCEMENT (Support zones: Left, Right)
                double reqTopL = GetReqArea(res, true, 0, settings);
                if (!AutoFillReinforcementV2(sol, reqTopL, topDia, nTop, beamWidth, legCount, settings, $"{span.SpanId}_Top_Left"))
                {
                    sol.IsValid = false;
                    sol.ValidationMessage = $"Không đủ chỗ bố trí thép tại {span.SpanId} Top Left (Req={reqTopL:F2} cm²)";
                    return sol;
                }

                double reqTopR = GetReqArea(res, true, 2, settings);
                if (!AutoFillReinforcementV2(sol, reqTopR, topDia, nTop, beamWidth, legCount, settings, $"{span.SpanId}_Top_Right"))
                {
                    sol.IsValid = false;
                    sol.ValidationMessage = $"Không đủ chỗ bố trí thép tại {span.SpanId} Top Right (Req={reqTopR:F2} cm²)";
                    return sol;
                }

                double reqTopM = GetReqArea(res, true, 1, settings);
                if (reqTopM > sol.As_Backbone_Top * 1.05)
                {
                    if (!AutoFillReinforcementV2(sol, reqTopM, topDia, nTop, beamWidth, legCount, settings, $"{span.SpanId}_Top_Mid"))
                    {
                        sol.IsValid = false;
                        sol.ValidationMessage = $"Không đủ chỗ bố trí thép tại {span.SpanId} Top Mid";
                        return sol;
                    }
                }

                // B. BOTTOM REINFORCEMENT (Mid span zone)
                double reqBotM = GetReqArea(res, false, 1, settings);
                if (!AutoFillReinforcementV2(sol, reqBotM, botDia, nBot, beamWidth, legCount, settings, $"{span.SpanId}_Bot_Mid"))
                {
                    sol.IsValid = false;
                    sol.ValidationMessage = $"Không đủ chỗ bố trí thép tại {span.SpanId} Bot Mid (Req={reqBotM:F2} cm²)";
                    return sol;
                }

                double reqBotL = GetReqArea(res, false, 0, settings);
                if (reqBotL > sol.As_Backbone_Bot * 1.05)
                {
                    if (!AutoFillReinforcementV2(sol, reqBotL, botDia, nBot, beamWidth, legCount, settings, $"{span.SpanId}_Bot_Left"))
                    {
                        sol.IsValid = false;
                        return sol;
                    }
                }

                double reqBotR = GetReqArea(res, false, 2, settings);
                if (reqBotR > sol.As_Backbone_Bot * 1.05)
                {
                    if (!AutoFillReinforcementV2(sol, reqBotR, botDia, nBot, beamWidth, legCount, settings, $"{span.SpanId}_Bot_Right"))
                    {
                        sol.IsValid = false;
                        return sol;
                    }
                }
            }

            // CALCULATE WEIGHT & METRICS
            CalculateSolutionMetricsV2(sol, group, settings, totalLength);

            return sol;
        }

        /// <summary>
        /// Smart Auto-Fill Algorithm with Snap-to-Structure.
        /// Implements Greedy vs Balanced dual strategy with constructability constraints.
        /// </summary>
        private static bool AutoFillReinforcementV2(
            ContinuousBeamSolution sol,
            double reqArea, int backboneDia, int backboneCount,
            double beamWidth, int legCount, DtsSettings settings, string locationKey)
        {
            double backboneArea = backboneCount * GetBarArea(backboneDia);

            if (backboneArea >= reqArea * 0.99) return true;

            int addDia = backboneDia;
            double addBarArea = GetBarArea(addDia);
            int totalBarsNeeded = (int)Math.Ceiling(reqArea / addBarArea);
            int capacity = GetMaxBarsPerLayer(beamWidth, addDia, settings);

            if (backboneCount > capacity) return false;

            // DUAL STRATEGY: GREEDY vs BALANCED
            var planA = CalculateLayerPlanV2(totalBarsNeeded, capacity, backboneCount, legCount, "GREEDY", settings);
            var planB = CalculateLayerPlanV2(totalBarsNeeded, capacity, backboneCount, legCount, "BALANCED", settings);

            (int CountL1, int CountL2, int TotalBars, bool IsValid) bestPlan = (0, 0, 0, false);

            if (planA.IsValid && !planB.IsValid) bestPlan = planA;
            else if (!planA.IsValid && planB.IsValid) bestPlan = planB;
            else if (planA.IsValid && planB.IsValid)
            {
                if (planB.TotalBars < planA.TotalBars) bestPlan = planB;
                else if (planA.TotalBars < planB.TotalBars) bestPlan = planA;
                else bestPlan = planA;
            }
            else return false;

            int addL1 = bestPlan.CountL1 - backboneCount;
            int addL2 = bestPlan.CountL2;

            if (addL1 > 0 || addL2 > 0)
            {
                sol.Reinforcements[locationKey] = new RebarSpec
                {
                    Diameter = addDia,
                    Count = addL1 + addL2,
                    Layer = addL2 > 0 ? 2 : 1,
                    Position = locationKey.Contains("Top") ? "Top" : "Bot"
                };
            }

            return true;
        }

        /// <summary>
        /// Calculate layer distribution plan with all constructability constraints.
        /// </summary>
        private static (int CountL1, int CountL2, int TotalBars, bool IsValid) CalculateLayerPlanV2(
            int totalNeeded, int capacity, int backboneCount, int legCount,
            string strategy, DtsSettings settings)
        {
            int n1 = 0, n2 = 0;
            int maxLayers = settings.Beam?.MaxLayers ?? 2;
            bool preferSymmetric = settings.Beam?.PreferSymmetric ?? true;

            if (strategy == "GREEDY")
            {
                n1 = Math.Min(totalNeeded, capacity);
                n2 = Math.Max(0, totalNeeded - n1);
            }
            else // BALANCED
            {
                int half = (int)Math.Ceiling(totalNeeded / 2.0);
                n1 = Math.Max(half, backboneCount);
                n1 = Math.Min(n1, capacity);
                n2 = Math.Max(0, totalNeeded - n1);
            }

            // CONSTRAINT 1: Pyramid Rule (L2 <= L1)
            if (n2 > n1) return (0, 0, 0, false);

            // CONSTRAINT 2: Max Layers
            if (n2 > 0 && maxLayers < 2) return (0, 0, 0, false);

            // CONSTRAINT 3: Snap-to-Structure (Stirrup Legs)
            if (n2 > 0 && legCount > 2)
            {
                if (n2 >= legCount - 1 && n2 < legCount && n2 <= n1)
                    n2 = legCount;
            }

            // CONSTRAINT 4: Symmetry
            if (preferSymmetric)
            {
                if (n1 % 2 != 0 && n1 + 1 <= capacity) n1++;
                if (n2 > 0 && n2 % 2 != 0 && n2 + 1 <= n1) n2++;
            }

            // CONSTRAINT 5: Vertical Alignment
            if (n2 > 0 && n1 % 2 == 0 && n2 % 2 != 0 && n2 + 1 <= n1)
                n2++;

            // Re-check constraints
            if (n2 > n1 || n1 > capacity) return (0, 0, 0, false);

            return (n1, n2, n1 + n2, true);
        }

        /// <summary>
        /// Get stirrup leg count based on beam width and settings.
        /// </summary>
        private static int GetStirrupLegCount(double width, DtsSettings settings)
        {
            string rules = settings.Beam?.AutoLegsRules ?? "250-2 400-4 600-6";

            try
            {
                var parsedRules = rules.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(r =>
                    {
                        var parts = r.Split('-');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int l))
                            return (Width: w, Legs: l);
                        return (Width: 0, Legs: 2);
                    })
                    .Where(r => r.Width > 0)
                    .OrderBy(r => r.Width)
                    .ToList();

                foreach (var rule in parsedRules)
                {
                    if (width <= rule.Width) return rule.Legs;
                }

                return parsedRules.LastOrDefault().Legs > 0 ? parsedRules.Last().Legs : 4;
            }
            catch
            {
                if (width < 300) return 2;
                if (width < 500) return 4;
                return 6;
            }
        }

        private static double GetBarArea(int dia) => Math.PI * dia * dia / 400.0;

        private static double GetReqArea(BeamResultData data, bool isTop, int pos, DtsSettings s)
        {
            if (data == null) return 0;
            double torsionFactor = isTop ? (s?.Beam?.TorsionDist_TopBar ?? 0.25) : (s?.Beam?.TorsionDist_BotBar ?? 0.25);
            double baseArea = isTop ? (data.TopArea?.ElementAtOrDefault(pos) ?? 0) : (data.BotArea?.ElementAtOrDefault(pos) ?? 0);
            double torsion = data.TorsionArea?.ElementAtOrDefault(pos) ?? 0;
            return baseArea + torsion * torsionFactor;
        }

        /// <summary>
        /// Calculate weight and scoring metrics for a solution.
        /// </summary>
        private static void CalculateSolutionMetricsV2(ContinuousBeamSolution sol, BeamGroup group, DtsSettings settings, double totalLengthMm)
        {
            double totalLengthM = totalLengthMm / 1000.0;

            double wBackbone = (sol.As_Backbone_Top + sol.As_Backbone_Bot) * 0.785 * totalLengthM;

            double wReinf = 0;
            int numSpans = group.Spans?.Count ?? 1;
            double avgSpanM = totalLengthM / numSpans;

            foreach (var kvp in sol.Reinforcements)
            {
                var spec = kvp.Value;
                if (spec.Count <= 0) continue;

                double barArea = GetBarArea(spec.Diameter);
                double factor = kvp.Key.Contains("Mid") ? 0.8 : 0.33;
                wReinf += spec.Count * barArea * 0.785 * (avgSpanM * factor);
            }

            sol.TotalSteelWeight = wBackbone + wReinf;

            double effScore = 10000.0 / (sol.TotalSteelWeight + 1);
            if (sol.Reinforcements.Any(r => r.Value.Layer >= 2)) effScore *= 0.95;
            if (sol.BackboneCount_Top != sol.BackboneCount_Bot) effScore *= 0.98;

            sol.EfficiencyScore = effScore;

            sol.Description = sol.BackboneCount_Top == 2 ? "Tiết kiệm" :
                              sol.BackboneCount_Top == 3 ? "Cân bằng" :
                              sol.BackboneCount_Top == 4 ? "An toàn" : "";
        }

        #endregion
    }
}
