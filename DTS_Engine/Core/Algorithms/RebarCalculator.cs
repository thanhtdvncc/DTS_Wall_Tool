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

            // 2. Đường kính đai: Dùng EstimatedStirrupDiameter từ Settings (không parse nữa)
            double stirrupDia = settings.Beam?.EstimatedStirrupDiameter ?? 10;

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
        /// Tính thép cho cả BeamGroup theo tiêu chuẩn đa quốc gia
        /// Top và Bot tính riêng biệt, đường kính đồng nhất cho dải dầm
        /// </summary>
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
    }
}
