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

        /// <summary>
        /// Tính toán bước đai từ diện tích cắt và xoắn yêu cầu.
        /// Công thức ACI/TCVN: Atotal/s = Av/s + 2×At/s
        /// Thuật toán vét cạn: thử từng đường kính × từng số nhánh để tìm phương án tối ưu.
        /// Output: String dạng "2-d8a150" (số nhánh - phi - bước)
        /// </summary>
        /// <param name="beamWidthMm">Bề rộng dầm (mm) để tính auto legs. Nếu 0 sẽ dùng StirrupLegs.</param>
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

        /// <summary>
        /// Tính toán cốt giá/sườn (Web bars).
        /// Logic: Envelope(Torsion, Constructive) và làm chẵn.
        /// Sử dụng danh sách đường kính để tìm phương án tối ưu.
        /// </summary>
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
        /// [OUT-PERFORM ALGORITHM]
        /// Tạo ra N phương án bố trí thép cho Group, từ Tiết kiệm đến Dễ thi công.
        /// Chiến lược: Min-First Backbone + Smart Layer Filling + Joint Synchronization.
        /// </summary>
        public static List<ContinuousBeamSolution> CalculateProposalsForGroup(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings)
        {
            var solutions = new List<ContinuousBeamSolution>();
            if (spanResults == null || spanResults.Count == 0 || group?.Spans == null) return solutions;

            // 1. CHUẨN BỊ DỮ LIỆU
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var allowedDias = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            if (settings.Beam?.PreferEvenDiameter == true)
                allowedDias = DiameterParser.FilterEvenDiameters(allowedDias);

            allowedDias.Sort(); // Ưu tiên đường kính nhỏ trước (Tiết kiệm)

            // Lấy bề rộng/cao dầm từ Group (STRICT - không fallback hardcode)
            double beamWidth = group.Width;
            double beamHeight = group.Height;

            // VALIDATION: Phải có data tiết diện từ XData hoặc SAP2000
            if (beamWidth <= 0 || beamHeight <= 0)
            {
                // Thử lấy từ spanResults[0] nếu Group không có
                if (spanResults.Count > 0 && spanResults[0] != null)
                {
                    beamWidth = spanResults[0].Width > 0 ? spanResults[0].Width : beamWidth;
                    beamHeight = spanResults[0].SectionHeight > 0 ? spanResults[0].SectionHeight : beamHeight;
                }
            }

            // Nếu vẫn không có -> Skip với warning
            if (beamWidth <= 0 || beamHeight <= 0)
            {
                // Không có data tiết diện -> Không thể tính toán
                var errorSol = new ContinuousBeamSolution
                {
                    OptionName = "ERROR",
                    IsValid = false,
                    ValidationMessage = $"Không tìm thấy tiết diện dầm (Width={beamWidth}, Height={beamHeight}). Chạy DTS_REBAR_SAP_RESULT trước."
                };
                solutions.Add(errorSol);
                return solutions;
            }

            // Lấy setting cho số thanh min/max
            int minBarsPerLayer = settings.Beam?.MinBarsPerLayer ?? 2;
            int maxLayers = settings.Beam?.MaxLayers ?? 2;

            // DEBUG: Log key values
            System.Diagnostics.Debug.WriteLine($"[REBAR DEBUG] Group: {group.GroupName}, Width={beamWidth}, Height={beamHeight}");
            System.Diagnostics.Debug.WriteLine($"[REBAR DEBUG] AllowedDias count: {allowedDias.Count}, Values: [{string.Join(",", allowedDias)}]");
            System.Diagnostics.Debug.WriteLine($"[REBAR DEBUG] Spans: Group has {group.Spans?.Count ?? 0}, spanResults has {spanResults.Count}");

            // 2. VÒNG LẶP THỬ NGHIỆM (SIMULATION LOOP)
            int scenariosTried = 0;
            int validScenarios = 0;

            foreach (int backboneDia in allowedDias)
            {
                int maxBarsL1 = GetMaxBarsPerLayer(beamWidth, backboneDia, settings);
                int startBars = minBarsPerLayer;
                int endBars = Math.Min(maxBarsL1, minBarsPerLayer + 2); // Thử backbone từ Min đến Min+2

                System.Diagnostics.Debug.WriteLine($"[REBAR DEBUG] D{backboneDia}: maxBarsL1={maxBarsL1}, startBars={startBars}, endBars={endBars}");

                for (int bbCount = startBars; bbCount <= endBars; bbCount++)
                {
                    scenariosTried++;
                    var sol = SolveScenario(group, spanResults, backboneDia, bbCount, maxBarsL1, beamWidth, settings);

                    if (sol.IsValid)
                    {
                        validScenarios++;
                        solutions.Add(sol);
                    }
                }
            }

            System.Diagnostics.Debug.WriteLine($"[REBAR DEBUG] Total scenarios tried: {scenariosTried}, Valid: {validScenarios}");

            // 3. CHẤM ĐIỂM & CHỌN LỌC (RANKING)
            var rankedSolutions = solutions.OrderByDescending(s => s.EfficiencyScore).ToList();
            var finalProposals = PruneSimilarSolutions(rankedSolutions);

            System.Diagnostics.Debug.WriteLine($"[REBAR DEBUG] Final proposals count: {finalProposals.Count}");

            return finalProposals.Take(3).ToList();
        }

        /// <summary>
        /// Giải bài toán bố trí cho một kịch bản Backbone cụ thể.
        /// </summary>
        private static ContinuousBeamSolution SolveScenario(
            BeamGroup group,
            List<BeamResultData> spanResults,
            int bbDia,
            int bbCount,
            int maxBarsL1,
            double beamWidth,
            DtsSettings settings)
        {
            double as1 = Math.PI * bbDia * bbDia / 400.0; // cm²
            double asBackbone = bbCount * as1;

            var sol = new ContinuousBeamSolution
            {
                OptionName = $"{bbCount}D{bbDia}",
                BackboneDiameter = bbDia,
                BackboneCount_Top = bbCount,
                BackboneCount_Bot = bbCount,
                As_Backbone_Top = asBackbone,
                As_Backbone_Bot = asBackbone,
                Reinforcements = new Dictionary<string, RebarSpec>(),
                IsValid = true
            };

            double totalWeight = 0;
            double totalLength = group.Spans.Sum(s => s.Length);

            // --- A. GIẢI QUYẾT CÁC GỐI (JOINTS) - ĐỒNG BỘ HÓA ---
            int numSpans = Math.Min(group.Spans.Count, spanResults.Count);
            for (int i = 0; i <= numSpans; i++)
            {
                var leftSpan = (i > 0 && i - 1 < group.Spans.Count) ? group.Spans[i - 1] : null;
                var rightSpan = (i < numSpans && i < group.Spans.Count) ? group.Spans[i] : null;

                // 1. Tính As Req Max tại gối (Max của End trái và Start phải)
                double reqTopLeft = (leftSpan != null && i - 1 < spanResults.Count)
                    ? GetReqArea(spanResults[i - 1], true, 2, settings) : 0;
                double reqTopRight = (rightSpan != null && i < spanResults.Count)
                    ? GetReqArea(spanResults[i], true, 0, settings) : 0;

                double reqTopJoint = Math.Max(reqTopLeft, reqTopRight);

                // 2. Tính thép gia cường (Additional)
                var topSpecs = CalculateReinforcementSmart(reqTopJoint, asBackbone, bbCount, maxBarsL1, bbDia, as1, "Top", settings);

                // 3. Gán thép gia cường
                foreach (var spec in topSpecs)
                {
                    if (leftSpan != null)
                        sol.Reinforcements[$"{leftSpan.SpanId}_Top_Right"] = spec;
                    if (rightSpan != null)
                        sol.Reinforcements[$"{rightSpan.SpanId}_Top_Left"] = spec;

                    // Ước tính chiều dài gối = L/4 + L/4
                    double len = (leftSpan?.Length ?? 0) * 0.25 + (rightSpan?.Length ?? 0) * 0.25;
                    totalWeight += spec.Count * as1 * 0.00785 * len / 1000.0; // mm to m
                }
            }

            // --- B. GIẢI QUYẾT GIỮA NHỊP (MID-SPAN) ---
            for (int i = 0; i < numSpans; i++)
            {
                var span = group.Spans[i];
                var data = spanResults[i];

                // 1. Thép Lớp Dưới (Bot Mid)
                double reqBotMid = GetReqArea(data, false, 1, settings);
                var botSpecs = CalculateReinforcementSmart(reqBotMid, asBackbone, bbCount, maxBarsL1, bbDia, as1, "Bot", settings);

                foreach (var spec in botSpecs)
                {
                    sol.Reinforcements[$"{span.SpanId}_Bot_Mid"] = spec;
                    totalWeight += spec.Count * as1 * 0.00785 * (span.Length * 0.8) / 1000.0;
                }

                // 2. Thép Lớp Trên giữa nhịp (Top Mid) - Thường là cấu tạo
                double reqTopMid = GetReqArea(data, true, 1, settings);
                if (reqTopMid > asBackbone * 1.05) // Chỉ thêm nếu cần
                {
                    var topMidSpecs = CalculateReinforcementSmart(reqTopMid, asBackbone, bbCount, maxBarsL1, bbDia, as1, "Top", settings);
                    foreach (var spec in topMidSpecs)
                    {
                        sol.Reinforcements[$"{span.SpanId}_Top_Mid"] = spec;
                    }
                }
            }

            // --- C. TÍNH ĐIỂM (SCORING) ---
            // Trọng lượng Backbone
            totalWeight += (sol.As_Backbone_Top + sol.As_Backbone_Bot) * 0.00785 * totalLength / 1000.0;
            sol.TotalSteelWeight = totalWeight;

            // Gọi hàm tính điểm chuyên biệt
            CalculateEfficiencyScore(sol, settings);

            // Mô tả
            sol.Description = bbCount == 2 ? "Tiết kiệm" :
                              bbCount == 3 ? "Cân bằng" : "An toàn";

            // Append Score info to description for debug/viewing
            sol.Description += $" (Score: {Math.Round(sol.EfficiencyScore, 0)})";

            return sol;
        }

        private static void CalculateEfficiencyScore(ContinuousBeamSolution sol, DtsSettings settings)
        {
            // 1. Base Score = Inverse of Weight
            // (100,000 / Weight) -> Weight 100kg = 1000 pts. Weight 200kg = 500 pts.
            double baseScore = 100000.0 / (sol.TotalSteelWeight + 1.0);

            // 2. Penalties (Phạt)
            double penaltyMultiplier = 1.0;

            // 2.1 Max Layers Penalty
            int maxLayersUsed = sol.Reinforcements.Values.Any() ? sol.Reinforcements.Values.Max(x => x.Layer) : 1;
            if (maxLayersUsed == 2) penaltyMultiplier *= 0.95; // Lớp 2: -5%
            if (maxLayersUsed >= 3) penaltyMultiplier *= 0.70; // Lớp 3: -30% (Rất tệ)

            // 2.2 Prefer Symmetric Penalty (Đối xứng)
            if (settings.Beam?.PreferSymmetric == true)
            {
                // Check tất cả các vị trí gia cường
                foreach (var spec in sol.Reinforcements.Values)
                {
                    // Nếu số lượng lẻ -> Phạt
                    // (Lưu ý: CalculateReinforcementSmart đã cố gắng làm chẵn, nhưng nếu logic khác sinh ra lẻ thì phạt)
                    if (spec.Count % 2 != 0)
                    {
                        penaltyMultiplier *= 0.95; // -5% per asymmetric spot
                    }
                }
            }

            // 2.3 Prefer Fewer Bars (Ít thanh - Đường kính lớn)
            if (settings.Beam?.PreferFewerBars == true)
            {
                // Logic: Nếu tổng số thanh tại mặt cắt quá nhiều (> MinPossible + 2) -> Phạt
                // Ở đây ta phạt dựa trên số lượng thanh Backbone
                if (sol.BackboneCount_Top > 4) penaltyMultiplier *= 0.90;
            }

            // 2.4 Prefer Single Diameter (Đồng bộ đường kính)
            // Nếu đường kính gia cường != đường kính backbone -> Phạt nhẹ
            if (settings.Beam?.PreferSingleDiameter == true)
            {
                bool mixed = sol.Reinforcements.Values.Any(r => r.Diameter != sol.BackboneDiameter);
                if (mixed) penaltyMultiplier *= 0.95;
            }

            sol.EfficiencyScore = baseScore * penaltyMultiplier;
        }

        /// <summary>
        /// Thuật toán "Rót Thép" thông minh: Ưu tiên chèn Lớp 1 -> Lớp 2...
        /// </summary>
        /// <summary>
        /// Thuật toán "Rót Thép" thông minh: Ưu tiên chèn Lớp 1 -> Lớp 2...
        /// Có xét đến tính đối xứng và settings.
        /// </summary>
        private static List<RebarSpec> CalculateReinforcementSmart(
            double reqTotal, double provBackbone, int bbCount, int maxL1, int dia, double as1, string pos, DtsSettings settings)
        {
            var result = new List<RebarSpec>();
            double missing = reqTotal - provBackbone;
            if (missing <= 0.01) return result;

            int barsNeeded = (int)Math.Ceiling(missing / as1);

            // Rule: Gia cường nên chẵn để đối xứng (nếu User yêu cầu)
            if (settings.Beam?.PreferSymmetric == true && barsNeeded % 2 != 0)
            {
                barsNeeded++;
            }

            // Check chỗ trống lớp 1
            int spaceL1 = maxL1 - bbCount;
            if (spaceL1 < 0) spaceL1 = 0;

            if (barsNeeded <= spaceL1)
            {
                // Đủ chỗ lớp 1 -> Nhét hết vào
                result.Add(new RebarSpec { Diameter = dia, Count = barsNeeded, Position = pos, Layer = 1 });
            }
            else
            {
                // Không đủ chỗ lớp 1 -> Rót đầy lớp 1 trước
                // Nếu PreferSymmetric, số lượng rót vào lớp 1 cũng nên chẵn (nếu còn dư nhiều)
                // Tuy nhiên để tối ưu diện tích (h0), ta ưu tiên max số lượng.

                int fillL1 = spaceL1;

                // Tinh chỉnh fillL1 để đẹp đội hình (nếu cần)
                // Ví dụ: spaceL1 = 3, barsNeeded = 4. 
                // Nếu fill 3 (L1) + 1 (L2) -> L2 bị lẻ 1 thanh (xấu).
                // Nếu fill 2 (L1) + 2 (L2) -> Đẹp hơn? Nhưng h0 giảm.
                // Quyết định: ƯU TIÊN SỨC CHỊU LỰC (h0) -> Fill Max L1.

                if (fillL1 > 0)
                {
                    result.Add(new RebarSpec { Diameter = dia, Count = fillL1, Position = pos, Layer = 1 });
                }

                int rem = barsNeeded - fillL1;
                if (rem > 0)
                {
                    // Lớp 2
                    // Kiểm tra maxBarsL2 (thường = maxL1)
                    int maxL2 = maxL1;
                    int fillL2 = Math.Min(rem, maxL2);

                    result.Add(new RebarSpec { Diameter = dia, Count = fillL2, Position = pos, Layer = 2 });

                    // Nếu vẫn còn dư -> Lớp 3 (sẽ bị phạt điểm rất nặng ở Scoring)
                    int remL3 = rem - fillL2;
                    if (remL3 > 0)
                    {
                        result.Add(new RebarSpec { Diameter = dia, Count = remL3, Position = pos, Layer = 3 });
                    }
                }
            }
            return result;
        }

        private static List<ContinuousBeamSolution> PruneSimilarSolutions(List<ContinuousBeamSolution> input)
        {
            return input.GroupBy(x => x.OptionName)
                        .Select(g => g.First())
                        .ToList();
        }

        private static double GetReqArea(BeamResultData data, bool isTop, int pos, DtsSettings s)
        {
            if (data == null) return 0;
            double torsionFactor = isTop ? (s?.Beam?.TorsionDist_TopBar ?? 0.5) : (s?.Beam?.TorsionDist_BotBar ?? 0.5);
            double baseArea = isTop ? (data.TopArea?.ElementAtOrDefault(pos) ?? 0) : (data.BotArea?.ElementAtOrDefault(pos) ?? 0);
            double torsion = data.TorsionArea?.ElementAtOrDefault(pos) ?? 0;
            return baseArea + torsion * torsionFactor;
        }

        #endregion
    }
}
