using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// SectionSolver: Giải bài toán tìm phương án bố trí thép hợp lệ cho mỗi mặt cắt.
    /// CRITICAL FIX: Sử dụng đầy đủ các settings từ DtsSettings.
    /// </summary>
    public class SectionSolver
    {
        #region Configuration

        private readonly DtsSettings _settings;
        private readonly List<int> _allowedDiameters;
        private readonly int _maxLayers;
        private readonly double _minSpacing;
        private readonly double _maxSpacing;
        private readonly double _minLayerSpacing;
        private readonly int _aggregateSize;
        private readonly int _minBarsPerLayer;

        public int MaxArrangementsPerSection { get; set; } = 20;
        public bool AllowMixedDiameters { get; set; } = false;

        #endregion

        #region Constructor

        public SectionSolver(DtsSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            // Load settings with fallbacks
            var beamCfg = settings.Beam ?? new BeamConfig();
            var generalCfg = settings.General ?? new GeneralConfig();

            // Parse allowed diameters from MainBarRange
            var inventory = generalCfg.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            _allowedDiameters = DiameterParser.ParseRange(beamCfg.MainBarRange ?? "16-25", inventory);

            if (_allowedDiameters.Count == 0)
            {
                _allowedDiameters = inventory.Where(d => d >= 16 && d <= 25).ToList();
            }

            // NOTE: Không lọc thép chẵn ở đây - User muốn sử dụng CHÍNH XÁC các đường kính từ Settings.

            _maxLayers = beamCfg.MaxLayers > 0 ? beamCfg.MaxLayers : 2;
            _minSpacing = beamCfg.MinClearSpacing > 0 ? beamCfg.MinClearSpacing : 30;
            _maxSpacing = beamCfg.MaxClearSpacing > 0 ? beamCfg.MaxClearSpacing : 200;
            _minLayerSpacing = beamCfg.MinLayerSpacing > 0 ? beamCfg.MinLayerSpacing : 25;
            _aggregateSize = beamCfg.AggregateSize > 0 ? beamCfg.AggregateSize : 20;
            _minBarsPerLayer = beamCfg.MinBarsPerLayer > 0 ? beamCfg.MinBarsPerLayer : 2;

            AllowMixedDiameters = beamCfg.AllowDiameterMixing;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Solve một section và một position (Top/Bot).
        /// </summary>
        public List<SectionArrangement> Solve(DesignSection section, RebarPosition position)
        {
            double safetyFactor = _settings?.Rules?.SafetyFactor ?? 1.0;
            double reqArea = (position == RebarPosition.Top ? section.ReqTop : section.ReqBot) * safetyFactor;

            // No requirement = return empty arrangement
            if (reqArea <= 0.01)
            {
                return new List<SectionArrangement> { SectionArrangement.Empty };
            }

            double usableWidth = section.UsableWidth;
            double usableHeight = section.UsableHeight;

            if (usableWidth <= 0 || usableHeight <= 0)
            {
                Utils.RebarLogger.LogError($"Invalid dimensions for {section.SectionId}: W={usableWidth}, H={usableHeight}");
                return new List<SectionArrangement>();
            }

            var results = new List<SectionArrangement>();

            // Generate single-diameter arrangements
            var singleDiaResults = GenerateSingleDiameterArrangements(reqArea, usableWidth, usableHeight, section);
            results.AddRange(singleDiaResults);

            // Generate mixed-diameter arrangements if allowed
            if (AllowMixedDiameters && _allowedDiameters.Count >= 2)
            {
                var mixedResults = GenerateMixedDiameterArrangements(reqArea, usableWidth, section);
                results.AddRange(mixedResults);
            }

            // CRITICAL: Lấy SafetyFactor từ Settings thay vì hardcode tolerance
            // safetyFactor already defined at start of Solve
            double tolerance = 1.0 - safetyFactor;
            if (tolerance < 0) tolerance = 0;
            // NOTE: Relax tolerance slightly for strict cases
            if (tolerance > 0.05) tolerance = 0.05;

            // Filter valid and score
            var validResults = results
                .Where(a => a.TotalArea >= reqArea * (1 - tolerance))
                .OrderByDescending(a => a.Score)
                .Take(MaxArrangementsPerSection)
                .ToList();

            // Fallback if no valid results
            if (validResults.Count == 0)
            {
                Utils.RebarLogger.Log($"No valid arrangements for {section.SectionId} ({position}), creating fallback");
                var fallback = CreateFallbackArrangement(reqArea, usableWidth, section);
                if (fallback != null)
                {
                    validResults.Add(fallback);
                }
            }

            return validResults;
        }

        /// <summary>
        /// Solve tất cả sections.
        /// </summary>
        public void SolveAll(List<DesignSection> sections)
        {
            Utils.RebarLogger.LogPhase("STEP 2: SECTION SOLVER");
            Utils.RebarLogger.Log($"Solving {sections.Count} sections");

            foreach (var section in sections)
            {
                section.ValidArrangementsTop = Solve(section, RebarPosition.Top);
                section.ValidArrangementsBot = Solve(section, RebarPosition.Bot);

                // Log arrangements for this section (with dimensions)
                Utils.RebarLogger.LogArrangements(section.SectionId, section.ValidArrangementsTop, "TOP", section.Width, section.Height);
                Utils.RebarLogger.LogArrangements(section.SectionId, section.ValidArrangementsBot, "BOT", section.Width, section.Height);
            }
        }

        #endregion

        #region Single Diameter Generation

        private List<SectionArrangement> GenerateSingleDiameterArrangements(
            double reqArea,
            double usableWidth,
            double usableHeight,
            DesignSection section)
        {
            var results = new List<SectionArrangement>();

            // CRITICAL FIX: Luôn sắp xếp từ lớn đến nhỏ (OrderByDescending) để nhất quán
            var sortedDiameters = _allowedDiameters.OrderByDescending(d => d).ToList();

            // Calculate strict area limit to avoid over-provisioning
            // Example: Req=9cm2. Max allow = 9 * 1.5 = 13.5cm2. 
            // Avoids picking 28cm2 options.
            double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;
            double maxAreaRatio = 1.65; // Allow 65% surplus max
            double maxAllowedArea = reqArea * maxAreaRatio;

            // For very small required areas, absolute margin is needed
            if (reqArea < 5.0) maxAllowedArea = Math.Max(maxAllowedArea, reqArea + 4.0);

            foreach (int dia in sortedDiameters)
            {
                double as1 = Math.PI * dia * dia / 400.0;

                // 1. Calculate min bars needed (Physical requirement)
                int minBars = (int)Math.Ceiling(reqArea / as1);
                if (minBars < _minBarsPerLayer) minBars = _minBarsPerLayer;

                // CRITICAL FIX: REMOVED forced symmetry check here. 
                // Odd numbers (e.g. 3 bars) are valid in 1 layer and can be symmetric.
                // Was: if (PreferSymmetric && minBars % 2 != 0) minBars++; -> REMOVED

                // 2. Calculate max bars (Economical constraint)
                int maxBarsByArea = (int)Math.Floor(maxAllowedArea / as1);

                // 3. Calculate max bars (Geometric constraint)
                int maxPerLayer = CalculateMaxBarsPerLayer(usableWidth, dia);
                int maxLayersForHeight = CalculateMaxLayers(usableHeight, dia);
                int actualMaxLayers = Math.Min(_maxLayers, maxLayersForHeight);
                int maxTotalBarsPossible = maxPerLayer * actualMaxLayers;

                // Determine search range
                int searchStart = minBars;
                int searchEnd = Math.Min(maxTotalBarsPossible, maxBarsByArea);

                // If search range is invalid (min > max due to strict area), force at least one try at minBars
                if (searchEnd < searchStart) searchEnd = searchStart;

                // Heuristic: Don't search too far beyond minBars if area is satisfied
                if (searchEnd > searchStart + 4) searchEnd = searchStart + 4;

                for (int totalBars = searchStart; totalBars <= searchEnd; totalBars++)
                {
                    // Generate layer configurations
                    var configs = GenerateLayerConfigurations(totalBars, maxPerLayer, actualMaxLayers);

                    foreach (var config in configs)
                    {
                        // Validate all layers fit
                        bool allFit = config.All(n => CheckSpacing(usableWidth, n, dia));
                        if (!allFit) continue;

                        var arr = CreateArrangement(config, dia, usableWidth, reqArea);
                        if (arr != null)
                        {
                            // Validate Area Constraint again (Safety check)
                            // Allow slight breach if it's the only option close to minBars
                            if (arr.TotalArea > maxAllowedArea && totalBars > minBars)
                            {
                                continue;
                            }

                            arr.Score = CalculateScore(arr, reqArea, section);
                            results.Add(arr);
                        }
                    }
                }

                // Limit per diameter to avoid explosion
                if (results.Count > 50) break;
            }

            return results;
        }

        private List<List<int>> GenerateLayerConfigurations(int totalBars, int maxPerLayer, int maxLayers)
        {
            var results = new List<List<int>>();
            GenerateLayerConfigsRecursive(totalBars, maxPerLayer, maxLayers, new List<int>(), results);
            return results;
        }

        private void GenerateLayerConfigsRecursive(
            int remaining,
            int maxPerLayer,
            int maxLayers,
            List<int> current,
            List<List<int>> results)
        {
            if (remaining == 0)
            {
                if (current.Count > 0)
                {
                    results.Add(new List<int>(current));
                }
                return;
            }

            if (current.Count >= maxLayers) return;
            if (results.Count > 100) return; // Limit combinations

            // First layer (or subsequent): try different bar counts
            int minInLayer = current.Count == 0 ? _minBarsPerLayer : 2;
            int maxInLayer = Math.Min(remaining, maxPerLayer);

            // V4.2: Nếu còn quá nhiều thanh mà không còn nhiều lớp, phải lấy tối đa cho lớp này
            int remainingLayers = maxLayers - current.Count;
            if (remaining > maxPerLayer * (remainingLayers - 1))
            {
                minInLayer = Math.Max(minInLayer, remaining - maxPerLayer * (remainingLayers - 1));
            }

            for (int n = maxInLayer; n >= minInLayer; n--)
            {
                // Prefer pyramidal: each subsequent layer should have <= bars of previous
                if (current.Count > 0 && n > current.Last()) continue;

                current.Add(n);
                GenerateLayerConfigsRecursive(remaining - n, maxPerLayer, maxLayers, current, results);
                current.RemoveAt(current.Count - 1);
            }
        }

        #endregion

        #region Mixed Diameter Generation

        private List<SectionArrangement> GenerateMixedDiameterArrangements(
            double reqArea,
            double usableWidth,
            DesignSection section)
        {
            var results = new List<SectionArrangement>();

            if (_allowedDiameters.Count < 2) return results;

            var sorted = _allowedDiameters.OrderByDescending(d => d).ToList();

            // Area Constraint for mixed bars
            double maxAllowedArea = reqArea * 1.5;
            if (reqArea < 5.0) maxAllowedArea = reqArea + 4.0;

            // Try combinations of 2 adjacent diameters
            for (int i = 0; i < sorted.Count - 1 && i < 2; i++)
            {
                int d1 = sorted[i];     // Larger
                int d2 = sorted[i + 1]; // Smaller

                double as1 = Math.PI * d1 * d1 / 400.0;
                double as2 = Math.PI * d2 * d2 / 400.0;

                // Try different combinations
                for (int n1 = 2; n1 <= 6; n1++)
                {
                    double remainingArea = reqArea - n1 * as1;
                    if (remainingArea <= 0)
                    {
                        // n1 of d1 alone is enough
                        break;
                    }

                    int n2 = (int)Math.Ceiling(remainingArea / as2);
                    if (n2 < 2) n2 = 2;

                    // Check if mixed combination fits
                    if (!CanFitMixedBars(usableWidth, n1, d1, n2, d2)) continue;

                    double totalArea = n1 * as1 + n2 * as2;

                    // Check over-design
                    if (totalArea > maxAllowedArea) continue;

                    // CRITICAL: Lấy SafetyFactor từ Settings
                    double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;
                    if (totalArea < reqArea * (1.0 - (1.0 - safetyFactor))) continue;

                    var barDiameters = Enumerable.Repeat(d1, n1).Concat(Enumerable.Repeat(d2, n2)).ToList();

                    var arr = new SectionArrangement
                    {
                        TotalCount = n1 + n2,
                        TotalArea = totalArea,
                        LayerCount = 1,
                        BarsPerLayer = new List<int> { n1 + n2 },
                        DiametersPerLayer = new List<int> { d1 }, // Primary = larger
                        PrimaryDiameter = d1,
                        BarDiameters = barDiameters,
                        ClearSpacing = CalculateMixedSpacing(usableWidth, barDiameters),
                        Efficiency = totalArea / reqArea
                    };

                    arr.Score = CalculateScore(arr, reqArea, section) - 5; // Slight penalty for mixed
                    results.Add(arr);
                }
            }

            return results;
        }

        #endregion

        #region Spacing Calculations

        private int CalculateMaxBarsPerLayer(double usableWidth, int diameter)
        {
            double minClear = GetMinClearSpacing(diameter);

            // V4.2 STRICT FORMULA: n*d + (n-1)*s <= UsableWidth
            // => n*(d+s) - s <= UsableWidth
            // => n <= (UsableWidth + s) / (d + s)
            double n = (usableWidth + minClear) / (diameter + minClear);
            int maxBars = (int)Math.Floor(n + 0.001); // Thêm sai số làm tròn floating point

            // CRITICAL: Giới hạn bởi MaxBarsPerLayer từ settings
            int configMax = _settings.Beam?.MaxBarsPerLayer ?? 8;
            return Math.Min(maxBars, configMax);
        }

        private int CalculateMaxLayers(double usableHeight, int diameter)
        {
            // Each layer needs: diameter + layer spacing
            double perLayer = diameter + _minLayerSpacing;
            int maxLayers = (int)Math.Floor((usableHeight + _minLayerSpacing) / perLayer);
            return Math.Max(1, Math.Min(maxLayers, _maxLayers));
        }

        private double GetMinClearSpacing(int diameter)
        {
            // Clear spacing = max(d, aggregateSize * 1.33, minSpacing)
            double byDiameter = diameter;
            double byAggregate = _aggregateSize * 1.33;
            double byConfig = _minSpacing;

            // Apply UseBarDiameterForSpacing if enabled
            if (_settings.Beam?.UseBarDiameterForSpacing == true)
            {
                double mult = _settings.Beam.BarDiameterSpacingMultiplier > 0
                    ? _settings.Beam.BarDiameterSpacingMultiplier
                    : 1.0;
                byDiameter = diameter * mult;
            }

            return Math.Max(Math.Max(byDiameter, byAggregate), byConfig);
        }

        private double CalculateClearSpacing(double usableWidth, int barsInLayer, int diameter)
        {
            if (barsInLayer <= 1) return usableWidth - diameter;

            double totalBarWidth = barsInLayer * diameter;
            double availableForGaps = usableWidth - totalBarWidth;
            return availableForGaps / (barsInLayer - 1);
        }

        /// <summary>
        /// CRITICAL FIX: Kiểm tra chặt chẽ cả Min và Max Spacing.
        /// </summary>
        private bool CheckSpacing(double usableWidth, int barsInLayer, int diameter)
        {
            if (barsInLayer <= 0) return true;

            // Trường hợp 1 thanh: Kiểm tra xem dầm có đủ rộng để đặt thanh đó không
            if (barsInLayer == 1) return usableWidth >= (diameter + 20); // +20mm margin an toàn

            double clearSpacing = CalculateClearSpacing(usableWidth, barsInLayer, diameter);
            double minRequired = GetMinClearSpacing(diameter);

            // 1. Kiểm tra MinSpacing (Bắt buộc - để đổ bê tông)
            if (clearSpacing < minRequired) return false;

            // 2. Kiểm tra MaxSpacing (HARD CONSTRAINT - loại phương án spacing quá rộng)
            // Cho phép sai số nhỏ (Tolerance) 5mm để tránh loại oan các ca sát nút
            double maxAllowed = _maxSpacing + 5.0;
            if (clearSpacing > maxAllowed)
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// CRITICAL FIX: Kiểm tra mixed bars với max diameter (không phải avg) và aggregate constraint.
        /// </summary>
        private bool CanFitMixedBars(double usableWidth, int n1, int dia1, int n2, int dia2)
        {
            double totalBarWidth = n1 * dia1 + n2 * dia2;
            int totalBars = n1 + n2;

            if (totalBars <= 1) return true;

            // CRITICAL FIX: Dùng MAX diameter thay vì average cho khoảng hở tối thiểu
            int maxDia = Math.Max(dia1, dia2);
            double minClear = Math.Max(maxDia, _settings.Beam?.MinClearSpacing ?? 30);

            // CRITICAL FIX: Áp dụng aggregate constraint từ settings
            double aggregateClear = (_settings.Beam?.AggregateSize ?? 20) * 1.33;
            double requiredClearMin = Math.Max(minClear, aggregateClear);

            // Tính khoảng hở thực tế
            double availableGapTotal = usableWidth - totalBarWidth;
            double actualSpacing = availableGapTotal / (totalBars - 1);

            // 1. Check Min
            if (actualSpacing < requiredClearMin) return false;

            // 2. Check Max (HARD CONSTRAINT cho Mixed Bars)
            double maxAllowed = (_settings.Beam?.MaxClearSpacing ?? 200) + 5.0;
            if (actualSpacing > maxAllowed) return false;

            return true;
        }

        private double CalculateMixedSpacing(double usableWidth, List<int> diameters)
        {
            if (diameters.Count <= 1) return 0;

            double totalBarWidth = diameters.Sum();
            double availableForGaps = usableWidth - totalBarWidth;
            return availableForGaps / (diameters.Count - 1);
        }

        #endregion

        #region Arrangement Creation & Scoring

        private SectionArrangement CreateArrangement(List<int> layers, int diameter, double usableWidth, double reqArea)
        {
            int totalBars = layers.Sum();
            double as1 = Math.PI * diameter * diameter / 400.0;
            double totalArea = totalBars * as1;

            var arr = new SectionArrangement
            {
                TotalCount = totalBars,
                TotalArea = totalArea,
                LayerCount = layers.Count,
                BarsPerLayer = new List<int>(layers),
                DiametersPerLayer = layers.Select(_ => diameter).ToList(),
                PrimaryDiameter = diameter,
                BarDiameters = new List<int>(),
                ClearSpacing = layers.Count > 0 ? CalculateClearSpacing(usableWidth, layers[0], diameter) : 0,
                VerticalSpacing = _minLayerSpacing,
                Efficiency = reqArea > 0.01 ? totalArea / reqArea : 1.0,
                IsSymmetric = IsLayerConfigurationSymmetric(layers),
                FitsStirrupLayout = true
            };

            // Calculate waste count (bars beyond requirement)
            int minBars = (int)Math.Ceiling(reqArea / as1);
            arr.WasteCount = Math.Max(0, totalBars - minBars);

            return arr;
        }

        /// <summary>
        /// Determine if a layer configuration is considered symmetric.
        /// Generally, equal bars in layers is usually symmetric.
        /// Odd bars in a layer (e.g., 3) is symmetric if placed centrally.
        /// DTS Engine assumes odd bars in 1 layer are symmetric (center bar).
        /// </summary>
        private bool IsLayerConfigurationSymmetric(List<int> layers)
        {
            // If any layer has different parity than others, it might be weird, 
            // but physically:
            // [3] -> Symmetric
            // [2] -> Symmetric
            // [3, 2] -> Symmetric (3 top, 2 bot aligned)
            // [2, 1] -> Symmetric
            // Simple heuristic: standard bar placement is usually symmetric.
            // But strict "Count" symmetry (Total % 2 == 0) was the old logic.
            // New logic: assume valid standard placements are symmetric.
            return true;
        }

        private SectionArrangement CreateFallbackArrangement(double reqArea, double usableWidth, DesignSection section)
        {
            // Use largest diameter and calculate minimum bars
            int maxDia = _allowedDiameters.Max();
            double as1 = Math.PI * maxDia * maxDia / 400.0;
            int nBars = (int)Math.Ceiling(reqArea / as1);
            if (nBars < _minBarsPerLayer) nBars = _minBarsPerLayer;

            // Check if fits in one layer
            int maxPerLayer = CalculateMaxBarsPerLayer(usableWidth, maxDia);

            var layers = new List<int>();
            int remaining = nBars;

            while (remaining > 0 && layers.Count < _maxLayers)
            {
                int inThisLayer = Math.Min(remaining, maxPerLayer);
                layers.Add(inThisLayer);
                remaining -= inThisLayer;
            }

            if (remaining > 0)
            {
                // Can't fit all bars
                Utils.RebarLogger.LogError($"Fallback failed for {section.SectionId}: need {nBars} D{maxDia}, only fit {layers.Sum()}");
                return null;
            }

            var arr = CreateArrangement(layers, maxDia, usableWidth, reqArea);
            arr.Score = 50; // Low score for fallback
            return arr;
        }

        /// <summary>
        /// CRITICAL FIX: Tính điểm sử dụng Settings thay vì hardcode.
        /// </summary>
        private double CalculateScore(SectionArrangement arr, double reqArea, DesignSection section)
        {
            double score = 100.0;

            // CRITICAL: Lấy WastePenaltyScore từ Settings (default 20)
            double wastePenalty = _settings.Rules?.WastePenaltyScore ?? 20.0;

            // 1. Efficiency penalty (waste) - DÙNG SETTING
            double wasteRatio = arr.Efficiency > 1 ? (arr.Efficiency - 1.0) : 0;
            score -= wasteRatio * wastePenalty;

            // 2. Layer penalty - 10 điểm mỗi lớp thêm
            score -= (arr.LayerCount - 1) * 10;

            // 3. Bar count penalty - nếu quá nhiều thanh
            int configMaxBars = _settings.Beam?.MaxBarsPerLayer ?? 8;
            if (arr.TotalCount > configMaxBars)
            {
                score -= (arr.TotalCount - configMaxBars) * 3;
            }

            // Penalize very high bar counts generally (complex)
            if (arr.TotalCount >= 6) score -= 2;
            if (arr.TotalCount >= 8) score -= 5;

            // 4. CRITICAL FIX: Optimal spacing check - DÙNG SETTING
            double optMin = _settings.Beam?.MinClearSpacing ?? 30;
            double optMax = _settings.Beam?.MaxClearSpacing ?? 200;
            double midOptimal = (optMin + optMax) / 2; // ~115mm với defaults

            if (arr.ClearSpacing >= optMin && arr.ClearSpacing <= optMax)
            {
                // Spacing trong dải cho phép
                if (arr.ClearSpacing <= midOptimal)
                {
                    // Prefer denser layouts (gần midOptimal)
                    double densityBonus = 10 * (1 - (arr.ClearSpacing - optMin) / (midOptimal - optMin));
                    score += Math.Max(0, densityBonus);
                }
            }
            else if (arr.ClearSpacing > optMax)
            {
                // Penalty cho spacing quá rộng (risk of cracking)
                score -= 15;
            }

            // 5. Preference bonuses từ settings - DÙNG SETTING
            if (_settings.Beam?.PreferSingleDiameter == true && arr.IsSingleDiameter)
            {
                score += 3;
            }

            if (_settings.Beam?.PreferSymmetric == true && arr.TotalCount % 2 != 0)
            {
                // Penalty odd bars ONLY if PreferSymmetric is STRICT.
                // But typically 3 bars is fine. 
                // score -= 2; // Disabled to allow 3 bars
            }

            if (_settings.Beam?.PreferFewerBars == true)
            {
                // Bonus cho ít thanh hơn (max 6 điểm)
                score += Math.Max(0, 6 - arr.TotalCount);
            }

            // 6. Preferred diameter bonus - DÙNG SETTING
            if (_settings.Beam?.PreferredDiameter > 0 && arr.PrimaryDiameter == _settings.Beam.PreferredDiameter)
            {
                score += 5;
            }

            // 7. Waste count penalty - DÙNG SETTING
            score -= arr.WasteCount * (wastePenalty / 10.0); // Scale down for count vs ratio

            return Math.Max(0, Math.Min(100, score));
        }

        #endregion
    }

    /// <summary>
    /// Extension methods for LINQ compatibility.
    /// </summary>
    public static class EnumerableExtensions
    {
        /// <summary>
        /// Distinct by key selector.
        /// </summary>
        public static IEnumerable<T> DistinctBy<T, TKey>(this IEnumerable<T> source, Func<T, TKey> keySelector)
        {
            var seen = new HashSet<TKey>();
            foreach (var item in source)
            {
                var key = keySelector(item);
                if (seen.Add(key))
                {
                    yield return item;
                }
            }
        }
    }
}
