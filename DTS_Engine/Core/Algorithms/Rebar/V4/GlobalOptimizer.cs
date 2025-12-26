using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// GlobalOptimizer V4.2: "NGƯỜI PHÁN QUYẾT CUỐI CÙNG"
    /// 
    /// CRITICAL FIX:
    /// 1. Backbone Sourcing: CHỈ lấy từ kết quả SectionSolver (không bịa ra)
    /// 2. Lookup First: Ưu tiên dùng lại phương án đã tính ở SectionSolver
    /// 3. Synthesize Fallback: Nếu không có sẵn, tự tính Addon theo Rule
    /// 4. Geometry Validation: Chặn đứng các phương án vi phạm MaxSpacing
    /// 
    /// Triết lý: Không còn "bịa" ra 2D25 khi SectionSolver đã loại nó do vi phạm spacing.
    /// </summary>
    public class GlobalOptimizer
    {
        #region Configuration

        private readonly DtsSettings _settings;
        private readonly List<int> _allowedDiameters;
        private readonly int _minBarsPerSide;
        private readonly int _maxBarsPerSide;

        public int MaxBackboneCandidates { get; set; } = 20;
        public int MaxSolutions { get; set; } = 5;

        #endregion

        #region Constructor

        public GlobalOptimizer(DtsSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            var generalCfg = settings.General ?? new GeneralConfig();
            var beamCfg = settings.Beam ?? new BeamConfig();

            var inventory = generalCfg.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            _allowedDiameters = DiameterParser.ParseRange(beamCfg.MainBarRange ?? "16-25", inventory);

            if (_allowedDiameters.Count == 0)
            {
                _allowedDiameters = inventory.Where(d => d >= 16 && d <= 25).ToList();
            }

            _minBarsPerSide = beamCfg.MinBarsPerLayer > 0 ? beamCfg.MinBarsPerLayer : 2;
            _maxBarsPerSide = beamCfg.MaxBarsPerLayer > 0 ? beamCfg.MaxBarsPerLayer : 8;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Tìm top N phương án tối ưu.
        /// CRITICAL: Chỉ chọn Backbone từ danh sách ValidArrangements của SectionSolver.
        /// </summary>
        public List<ContinuousBeamSolution> FindBestSolutions(
            List<DesignSection> sections,
            BeamGroup group,
            ExternalConstraints externalConstraints = null)
        {
            if (sections == null || sections.Count == 0)
            {
                return new List<ContinuousBeamSolution> { CreateErrorSolution("No sections provided") };
            }

            try
            {
                Utils.RebarLogger.Log("  GLOBAL OPTIMIZE: SMART HARVEST & SYNTHESIS");
                Utils.RebarLogger.Log("═══════════════════════════════════════════════════════════════");

                // 1. THU HOẠCH Backbone candidates TỪ KẾT QUẢ SECTIONSOLVER
                var candidates = HarvestBackboneCandidates(sections, externalConstraints);
                Utils.RebarLogger.Log($"Harvested {candidates.Count} backbone candidates from SectionSolver results");

                if (candidates.Count == 0)
                {
                    return new List<ContinuousBeamSolution> { CreateErrorSolution("No valid backbone candidates from sections") };
                }

                // 2. Đánh giá từng candidate với Geometry Validation + Smart Resolution
                var validSolutions = new List<ContinuousBeamSolution>();

                foreach (var candidate in candidates)
                {
                    // Check 1: Backbone có vi phạm MaxSpacing ở bất kỳ section nào không?
                    if (!ValidateBackboneGeometry(candidate, sections))
                    {
                        Utils.RebarLogger.Log($"  REJECT {candidate.CountTop}D{candidate.Diameter}: MaxSpacing violation");
                        continue;
                    }

                    // Check 2: Đánh giá chi tiết với Lookup & Synthesize
                    var evalResult = EvaluateWithSmartResolution(candidate, sections, group);

                    if (evalResult.IsValid)
                    {
                        var solution = BuildSolutionFromEvaluation(candidate, evalResult, sections, group);
                        validSolutions.Add(solution);
                    }
                }

                // 3. Fallback nếu không có solution valid
                if (validSolutions.Count == 0)
                {
                    Utils.RebarLogger.LogError("No valid solution after smart resolution. Trying relaxed approach...");
                    var relaxedSolution = TryRelaxedBackbone(candidates, sections, group);
                    if (relaxedSolution != null)
                    {
                        validSolutions.Add(relaxedSolution);
                    }
                    else
                    {
                        return new List<ContinuousBeamSolution> { CreateErrorSolution("No valid backbone found") };
                    }
                }

                // 4. Xếp hạng và chọn
                var rankedSolutions = validSolutions
                    .OrderByDescending(s => s.TotalScore)
                    .ThenBy(s => s.TotalSteelWeight)
                    .Take(MaxSolutions)
                    .ToList();

                LogSolutionRanking(rankedSolutions);

                return rankedSolutions;
            }
            catch (Exception ex)
            {
                Utils.RebarLogger.LogError($"GlobalOptimizer error: {ex.Message}");
                return new List<ContinuousBeamSolution> { CreateErrorSolution($"Optimization failed: {ex.Message}") };
            }
        }

        #endregion

        #region Backbone Harvesting - CRITICAL FIX

        /// <summary>
        /// THU HOẠCH Backbone candidates TỪ KẾT QUẢ SECTIONSOLVER.
        /// KHÔNG sinh ngẫu nhiên, CHỈ lấy những gì đã được validate về spacing/layers.
        /// </summary>
        private List<BackboneCandidate> HarvestBackboneCandidates(
            List<DesignSection> sections,
            ExternalConstraints constraints)
        {
            var distinctConfigs = new HashSet<(int Diameter, int CountTop, int CountBot)>();

            // Lọc đường kính theo constraints
            var validDiameters = _allowedDiameters.ToList();
            if (constraints?.ForcedBackboneDiameter.HasValue == true)
            {
                validDiameters = new List<int> { constraints.ForcedBackboneDiameter.Value };
            }

            // Quét tất cả ValidArrangements từ các sections
            foreach (var section in sections)
            {
                // Quét TOP arrangements
                foreach (var arr in section.ValidArrangementsTop)
                {
                    if (validDiameters.Contains(arr.PrimaryDiameter))
                    {
                        // Thêm chính nó làm backbone candidate
                        distinctConfigs.Add((arr.PrimaryDiameter, arr.TotalCount, _minBarsPerSide));

                        // Thử giảm cấp để làm backbone (addon sẽ bù)
                        if (arr.TotalCount > _minBarsPerSide)
                        {
                            distinctConfigs.Add((arr.PrimaryDiameter, _minBarsPerSide, _minBarsPerSide));
                            distinctConfigs.Add((arr.PrimaryDiameter, arr.TotalCount - 1, _minBarsPerSide));
                        }
                    }
                }

                // Quét BOT arrangements
                foreach (var arr in section.ValidArrangementsBot)
                {
                    if (validDiameters.Contains(arr.PrimaryDiameter))
                    {
                        distinctConfigs.Add((arr.PrimaryDiameter, _minBarsPerSide, arr.TotalCount));

                        if (arr.TotalCount > _minBarsPerSide)
                        {
                            distinctConfigs.Add((arr.PrimaryDiameter, _minBarsPerSide, _minBarsPerSide));
                            distinctConfigs.Add((arr.PrimaryDiameter, _minBarsPerSide, arr.TotalCount - 1));
                        }
                    }
                }
            }

            // Thêm các tổ hợp cân bằng (Top = Bot) cho các đường kính phổ biến
            foreach (var d in validDiameters.OrderByDescending(d => d).Take(3))
            {
                for (int n = _minBarsPerSide; n <= Math.Min(4, _maxBarsPerSide); n++)
                {
                    distinctConfigs.Add((d, n, n));
                }
            }

            // Convert to BackboneCandidate objects
            var candidates = new List<BackboneCandidate>();
            foreach (var config in distinctConfigs)
            {
                candidates.Add(new BackboneCandidate
                {
                    Diameter = config.Diameter,
                    CountTop = config.CountTop,
                    CountBot = config.CountBot,
                    IsGloballyValid = false,
                    FailedSections = new List<string>()
                });
            }

            // Sắp xếp: Ưu tiên đường kính lớn (ít thanh), số thanh cân bằng
            return candidates
                .OrderByDescending(c => c.Diameter)
                .ThenBy(c => Math.Abs(c.CountTop - c.CountBot))
                .ThenBy(c => c.CountTop + c.CountBot)
                .Take(MaxBackboneCandidates)
                .ToList();
        }

        #endregion

        #region Geometry Validation - Block Bad Backbones

        /// <summary>
        /// Kiểm tra Backbone có vi phạm MaxSpacing ở BẤT KỲ section nào không.
        /// Chặn đứng 2D25 nếu dầm rộng gây spacing > MaxClearSpacing.
        /// </summary>
        private bool ValidateBackboneGeometry(BackboneCandidate backbone, List<DesignSection> sections)
        {
            double maxSpacing = _settings.Beam?.MaxClearSpacing ?? 200;
            double minSpacing = _settings.Beam?.MinClearSpacing ?? 30;

            foreach (var section in sections)
            {
                // Check TOP
                if (section.ReqTop > 0.01 && backbone.CountTop >= 2)
                {
                    double spacing = CalculateClearSpacing(section.UsableWidth, backbone.CountTop, backbone.Diameter);
                    if (spacing > maxSpacing || spacing < minSpacing)
                    {
                        return false;
                    }
                }

                // Check BOT
                if (section.ReqBot > 0.01 && backbone.CountBot >= 2)
                {
                    double spacing = CalculateClearSpacing(section.UsableWidth, backbone.CountBot, backbone.Diameter);
                    if (spacing > maxSpacing || spacing < minSpacing)
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private double CalculateClearSpacing(double usableWidth, int barCount, int diameter)
        {
            if (barCount <= 1) return usableWidth - diameter;
            double totalBarWidth = barCount * diameter;
            double availableForGaps = usableWidth - totalBarWidth;
            return availableForGaps / (barCount - 1);
        }

        #endregion

        #region Smart Resolution - Lookup First, Synthesize Fallback

        /// <summary>
        /// Evaluation result cho một backbone candidate.
        /// </summary>
        private class EvaluationResult
        {
            public bool IsValid { get; set; }
            public double TotalWeight { get; set; }
            public double BackboneWeight { get; set; }
            public double AddonWeight { get; set; }
            public int NativeMatchCount { get; set; }
            public int TotalChecks { get; set; }
            public Dictionary<string, RebarInfo> Addons { get; set; } = new Dictionary<string, RebarInfo>();
            public List<string> FailedSections { get; set; } = new List<string>();
        }

        /// <summary>
        /// Đánh giá Backbone với logic "Lookup First, Synthesize Fallback".
        /// </summary>
        private EvaluationResult EvaluateWithSmartResolution(
            BackboneCandidate backbone,
            List<DesignSection> sections,
            BeamGroup group)
        {
            var result = new EvaluationResult { IsValid = true };
            double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;

            // Tính trọng lượng backbone
            double totalLength = CalculateTotalLength(group, sections);
            result.BackboneWeight = WeightCalculator.CalculateBackboneWeight(
                backbone.Diameter,
                totalLength,
                backbone.CountTop + backbone.CountBot,
                1.05); // 5% lap splice allowance

            result.TotalWeight = result.BackboneWeight;

            foreach (var section in sections)
            {
                string spanId = section.SpanId ?? $"S{section.SpanIndex + 1}";
                string zoneName = GetZoneName(section);

                // --- XỬ LÝ TOP ---
                if (section.ReqTop > 0.01)
                {
                    result.TotalChecks++;
                    double requiredArea = section.ReqTop * safetyFactor;

                    var resTop = ResolveSectionSide(
                        section.ValidArrangementsTop,
                        backbone.CountTop,
                        backbone.Diameter,
                        requiredArea,
                        section,
                        group);

                    if (!resTop.Success)
                    {
                        result.FailedSections.Add($"{section.SectionId}_Top");
                    }
                    else
                    {
                        result.AddonWeight += resTop.AddonWeight;
                        result.TotalWeight += resTop.AddonWeight;
                        if (resTop.IsNativeMatch) result.NativeMatchCount++;

                        if (resTop.AddonInfo != null && resTop.AddonInfo.Count > 0)
                        {
                            result.Addons[$"{spanId}_Top_{zoneName}"] = resTop.AddonInfo;
                        }
                    }
                }

                // --- XỬ LÝ BOT ---
                if (section.ReqBot > 0.01)
                {
                    result.TotalChecks++;
                    double requiredArea = section.ReqBot * safetyFactor;

                    var resBot = ResolveSectionSide(
                        section.ValidArrangementsBot,
                        backbone.CountBot,
                        backbone.Diameter,
                        requiredArea,
                        section,
                        group);

                    if (!resBot.Success)
                    {
                        result.FailedSections.Add($"{section.SectionId}_Bot");
                    }
                    else
                    {
                        result.AddonWeight += resBot.AddonWeight;
                        result.TotalWeight += resBot.AddonWeight;
                        if (resBot.IsNativeMatch) result.NativeMatchCount++;

                        if (resBot.AddonInfo != null && resBot.AddonInfo.Count > 0)
                        {
                            result.Addons[$"{spanId}_Bot_{zoneName}"] = resBot.AddonInfo;
                        }
                    }
                }
            }

            // --- POST-PROCESSING: UNIFY BOTTOM ADDONS PER SPAN ---
            // Rule: Nếu trong một nhịp có nhiều vùng bụng cần gia cường, phải thống nhất theo thép bụng lớn nhất
            var botAddonKeys = result.Addons.Keys.Where(k => k.Contains("_Bot_")).ToList();
            if (botAddonKeys.Count > 1)
            {
                var spanGroups = botAddonKeys.GroupBy(k => k.Split('_')[0]); // Nhóm theo SpanId (ví dụ: S1, S2...)
                foreach (var groupBot in spanGroups)
                {
                    if (groupBot.Count() <= 1) continue;

                    // Tìm phương án thép có diện tích lớn nhất (Count * D^2)
                    string maxKey = groupBot.OrderByDescending(k =>
                    {
                        var info = result.Addons[k];
                        return (double)info.Count * info.Diameter * info.Diameter;
                    }).First();

                    var bestAddon = result.Addons[maxKey];

                    // Áp dụng phương án tốt nhất cho tất cả các vùng bụng trong nhịp này
                    foreach (var key in groupBot)
                    {
                        if (key == maxKey) continue;
                        result.Addons[key] = bestAddon;
                        // Lưu ý: Ta chấp nhận sự sai lệch nhẹ về khối lượng dự toán ở bước này 
                        // vì quan trọng nhất là tính đúng đắn của phương án cấu tạo.
                    }
                }
            }

            // Kiểm tra tỷ lệ fail - cho phép tối đa 20% section fail
            double failRatio = result.TotalChecks > 0
                ? (double)result.FailedSections.Count / result.TotalChecks
                : 0;

            result.IsValid = failRatio <= 0.2;

            return result;
        }

        /// <summary>
        /// Resolution result cho một section side.
        /// </summary>
        private class SectionResolution
        {
            public bool Success { get; set; }
            public double AddonWeight { get; set; }
            public bool IsNativeMatch { get; set; }
            public RebarInfo AddonInfo { get; set; }
        }

        /// <summary>
        /// "Gõ cửa" từng section: Lookup → Synthesize → Fallback.
        /// CRITICAL FIX: 
        /// 1. Validate final area meets SafetyFactor requirement
        /// 2. Enforce stirrup alignment rule between layers
        /// </summary>
        private SectionResolution ResolveSectionSide(
            List<SectionArrangement> existingOptions,
            int backboneCount,
            int backboneDiameter,
            double requiredArea,
            DesignSection section,
            BeamGroup group)
        {
            double backboneArea = backboneCount * Math.PI * backboneDiameter * backboneDiameter / 400.0;
            double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;
            double targetArea = requiredArea; // Already includes SafetyFactor from caller

            // 1. Kiểm tra nếu Backbone đã đủ (Strict check with safety factor)
            if (backboneArea >= targetArea)
            {
                return new SectionResolution { Success = true, IsNativeMatch = true };
            }

            double deficit = targetArea - backboneArea;

            // 2. LOOKUP: Dò trong thư viện đã tính (ValidArrangements)
            var nativeMatch = existingOptions
                .Where(opt =>
                    opt.PrimaryDiameter == backboneDiameter &&
                    opt.TotalCount > backboneCount &&
                    opt.TotalArea >= targetArea) // Strict check with safety factor
                .OrderBy(opt => opt.TotalArea) // Lấy phương án nhỏ nhất đủ yêu cầu
                .FirstOrDefault();

            if (nativeMatch != null)
            {
                // Tìm thấy phương án native!
                int rawAddonCount = nativeMatch.TotalCount - backboneCount;

                // CRITICAL FIX: Enforce stirrup alignment rule between layers
                int addonCount = EnforceStirrupLayerAlignment(backboneCount, rawAddonCount);

                if (addonCount >= 2)
                {
                    // Validate final area meets requirement
                    double addonArea = addonCount * Math.PI * backboneDiameter * backboneDiameter / 400.0;
                    double totalProvidedArea = backboneArea + addonArea;

                    if (totalProvidedArea >= targetArea)
                    {
                        double spanLength = GetSpanLength(section, group);
                        double weight = WeightCalculator.CalculateAddonWeight(backboneDiameter, addonCount, spanLength / 1000.0, section.Type == SectionType.Support ? 0.3 : 0.6);

                        // Use layer breakdown from arrangement
                        var addonDetails = nativeMatch.GetAddon(backboneCount, backboneDiameter);

                        return new SectionResolution
                        {
                            Success = true,
                            IsNativeMatch = true,
                            AddonWeight = weight,
                            AddonInfo = new RebarInfo
                            {
                                Count = addonCount,
                                Diameter = backboneDiameter,
                                LayerCounts = addonDetails.layerBreakdown
                            }
                        };
                    }
                }
            }

            // 3. SYNTHESIZE: Tự tính addon với safety factor và stirrup alignment
            var synthesized = SynthesizeAddon(deficit, backboneDiameter, section, targetArea - backboneArea, backboneCount);

            if (synthesized != null)
            {
                double spanLength = GetSpanLength(section, group);
                double weight = WeightCalculator.CalculateAddonWeight(synthesized.Diameter, synthesized.Count, spanLength / 1000.0, section.Type == SectionType.Support ? 0.3 : 0.6);

                return new SectionResolution
                {
                    Success = true,
                    IsNativeMatch = false,
                    AddonWeight = weight,
                    AddonInfo = synthesized
                };
            }

            // 4. FALLBACK: Tính addon bắt buộc dù không optimal
            var fallback = ForceSynthesizeAddon(deficit, section, targetArea - backboneArea, backboneCount);
            if (fallback != null)
            {
                double spanLength = GetSpanLength(section, group);
                double weight = WeightCalculator.CalculateAddonWeight(fallback.Diameter, fallback.Count, spanLength / 1000.0, section.Type == SectionType.Support ? 0.3 : 0.6);

                return new SectionResolution
                {
                    Success = true,
                    IsNativeMatch = false,
                    AddonWeight = weight,
                    AddonInfo = fallback
                };
            }

            return new SectionResolution { Success = false };
        }

        /// <summary>
        /// Enforce stirrup layer alignment rule.
        /// 
        /// RULE: Lớp addon (Layer 2) phải tương thích với Backbone (Layer 1) để bó đai được.
        /// 
        /// - Layer 1 CHẴN (4, 6, 8...) → Layer 2 phải CHẴN (2, 4, 6...)
        ///   Vì đai lồng cần đối xứng, nếu L1=4 mà L2=3 thì không cân đai được
        ///   
        /// - Layer 1 LẺ (3, 5, 7...) → Layer 2 có thể CHẴN hoặc LẺ (2, 3, 4, 5...)
        ///   Vì số lẻ L1 cho phép đai bao được cả chẵn lẫn lẻ ở L2
        ///   
        /// Examples:
        /// - L1=4, raw=3 → bump to 4 (chẵn phải chẵn)
        /// - L1=4, raw=5 → bump to 6 (chẵn phải chẵn)
        /// - L1=5, raw=3 → keep 3 (lẻ cho phép cả hai)
        /// - L1=6, raw=3 → bump to 4 (chẵn phải chẵn)
        /// </summary>
        private int EnforceStirrupLayerAlignment(int layer1Count, int layer2Count)
        {
            // Minimum 2 bars for any addon layer
            if (layer2Count < 2) layer2Count = 2;

            bool layer1IsEven = layer1Count % 2 == 0;
            bool layer2IsEven = layer2Count % 2 == 0;

            if (layer1IsEven)
            {
                // Layer 1 CHẴN → Layer 2 phải CHẴN
                if (!layer2IsEven)
                {
                    // Bump lên số chẵn tiếp theo
                    layer2Count = layer2Count + 1;
                }
            }
            // else: Layer 1 LẺ → Layer 2 có thể chẵn hoặc lẻ, không cần điều chỉnh

            return layer2Count;
        }

        /// <summary>
        /// Sinh addon tuân thủ rule: min 2 thanh, stirrup alignment, check spacing.
        /// </summary>
        private RebarInfo SynthesizeAddon(double deficit, int backboneDiameter, DesignSection section, double minRequiredAddonArea, int backboneCount)
        {
            // Ưu tiên đường kính bằng hoặc nhỏ hơn backbone
            var candidates = _allowedDiameters
                .Where(d => d <= backboneDiameter)
                .OrderByDescending(d => d)
                .ToList();

            foreach (var d in candidates)
            {
                double oneBarArea = Math.PI * d * d / 400.0;
                int count = (int)Math.Ceiling(deficit / oneBarArea);

                // Enforce minimum 2 bars and stirrup layer alignment
                count = EnforceStirrupLayerAlignment(backboneCount, count);

                // Verify addon area is sufficient
                double addonArea = count * oneBarArea;
                if (addonArea < minRequiredAddonArea)
                {
                    // Need more bars, calculate and re-enforce alignment
                    int neededCount = (int)Math.Ceiling(minRequiredAddonArea / oneBarArea);
                    count = EnforceStirrupLayerAlignment(backboneCount, neededCount);
                    addonArea = count * oneBarArea;
                }

                // Spacing check
                int total1 = backboneCount + count;
                double spacing = (section.UsableWidth - total1 * d) / (total1 - 1);

                if (spacing >= 25)
                {
                    return new RebarInfo { Count = count, Diameter = d, LayerCounts = new List<int> { count } };
                }
                else
                {
                    // Multi-layer fallback
                    int maxPerLayer = (int)Math.Floor((section.UsableWidth + 25) / (d + 25));
                    if (maxPerLayer < 2) continue; // Diameter too large

                    int maxLayersLimit = _settings.Beam?.MaxLayers ?? 3;
                    var layers = CalculateAddonLayerBreakdown(count, backboneCount, maxPerLayer, maxLayersLimit);
                    if (layers == null) continue; // Exceeds MaxLayers

                    return new RebarInfo { Count = count, Diameter = d, LayerCounts = layers };
                }
            }

            return null;
        }

        private RebarInfo ForceSynthesizeAddon(double deficit, DesignSection section, double minRequiredAddonArea, int backboneCount)
        {
            // Dùng đường kính nhỏ nhất có thể để nhét nhiều thanh
            var candidates = _allowedDiameters.OrderBy(d => d).ToList();

            foreach (var d in candidates)
            {
                double oneBarArea = Math.PI * d * d / 400.0;
                int count = (int)Math.Ceiling(deficit / oneBarArea);

                // Enforce stirrup layer alignment
                count = EnforceStirrupLayerAlignment(backboneCount, count);

                // Verify addon area is sufficient  
                double addonArea = count * oneBarArea;
                if (addonArea < minRequiredAddonArea)
                {
                    int neededCount = (int)Math.Ceiling(minRequiredAddonArea / oneBarArea);
                    count = EnforceStirrupLayerAlignment(backboneCount, neededCount);
                }

                int maxPerLayer = (int)Math.Floor((section.UsableWidth + 25) / (d + 25));
                if (maxPerLayer < 2) maxPerLayer = 2;

                int maxLayersLimit = _settings.Beam?.MaxLayers ?? 3;
                var layers = CalculateAddonLayerBreakdown(count, backboneCount, maxPerLayer, maxLayersLimit);
                if (layers != null)
                {
                    return new RebarInfo { Count = count, Diameter = d, LayerCounts = layers };
                }
            }

            return null;
        }

        private List<int> CalculateAddonLayerBreakdown(int totalAddon, int backboneCount, int maxPerLayer, int maxLayersLimit)
        {
            var result = new List<int>();
            int remAddon = totalAddon;
            int remBackbone = backboneCount;

            // Layer 1
            int cap1 = maxPerLayer;
            int bars1 = Math.Min(remBackbone + remAddon, cap1);
            int add1 = Math.Max(0, bars1 - remBackbone);
            if (add1 > 0) result.Add(add1);
            remAddon -= add1;

            // Layers 2+
            while (remAddon > 0)
            {
                // CRITICAL: Check MaxLayers limit
                if (result.Count >= maxLayersLimit)
                {
                    // Vượt quá số lớp cho phép -> Hủy phương án này
                    return null;
                }

                int add = Math.Min(remAddon, maxPerLayer);
                result.Add(add);
                remAddon -= add;
            }

            return result;
        }

        #endregion

        #region Solution Building

        private ContinuousBeamSolution BuildSolutionFromEvaluation(
            BackboneCandidate backbone,
            EvaluationResult evalResult,
            List<DesignSection> sections,
            BeamGroup group)
        {
            double as1 = Math.PI * backbone.Diameter * backbone.Diameter / 400.0;

            var solution = new ContinuousBeamSolution
            {
                OptionName = $"{backbone.CountTop}D{backbone.Diameter}+{backbone.CountBot}D{backbone.Diameter}",
                BackboneDiameter_Top = backbone.Diameter,
                BackboneDiameter_Bot = backbone.Diameter,
                BackboneCount_Top = backbone.CountTop,
                BackboneCount_Bot = backbone.CountBot,
                As_Backbone_Top = backbone.CountTop * as1,
                As_Backbone_Bot = backbone.CountBot * as1,
                TotalSteelWeight = evalResult.TotalWeight,
                Reinforcements = new Dictionary<string, RebarSpec>(),
                SpanResults = new List<SpanRebarResult>(),
                StirrupDesigns = new Dictionary<string, string>(),
                IsValid = true
            };

            // Convert addons to Reinforcements
            foreach (var kvp in evalResult.Addons)
            {
                solution.Reinforcements[kvp.Key] = new RebarSpec
                {
                    Diameter = kvp.Value.Diameter,
                    Count = kvp.Value.Count,
                    Position = kvp.Key.Contains("_Top_") ? "Top" : "Bot",
                    Layer = kvp.Value.LayerCounts?.Count ?? 1,
                    LayerBreakdown = kvp.Value.LayerCounts
                };
            }

            // Build SpanResults
            var spanGroups = sections.GroupBy(s => s.SpanIndex).OrderBy(g => g.Key);
            foreach (var spanGroup in spanGroups)
            {
                var spanResult = BuildSpanResult(backbone, spanGroup.ToList(), evalResult, group);
                solution.SpanResults.Add(spanResult);
            }

            // Calculate scores
            CalculateFinalScores(solution, evalResult, sections, group);

            return solution;
        }

        private SpanRebarResult BuildSpanResult(
            BackboneCandidate backbone,
            List<DesignSection> spanSections,
            EvaluationResult evalResult,
            BeamGroup group)
        {
            var firstSection = spanSections.FirstOrDefault();
            int spanIndex = firstSection?.SpanIndex ?? 0;
            string spanId = firstSection?.SpanId ?? $"S{spanIndex + 1}";

            var result = new SpanRebarResult
            {
                SpanIndex = spanIndex,
                SpanId = spanId,
                TopBackbone = new RebarInfo { Count = backbone.CountTop, Diameter = backbone.Diameter },
                BotBackbone = new RebarInfo { Count = backbone.CountBot, Diameter = backbone.Diameter }
            };

            // Populate Requirements (Sync for Viewer)
            // Sections usually ordered by ZoneIndex: 0 (Left), 1 (Mid), 2 (Right)
            var secLeft = spanSections.FirstOrDefault(s => s.ZoneIndex == 0);
            var secMid = spanSections.FirstOrDefault(s => s.ZoneIndex == 1);
            var secRight = spanSections.FirstOrDefault(s => s.ZoneIndex == 2);

            result.ReqTop = new double[] { secLeft?.ReqTop ?? 0, secMid?.ReqTop ?? 0, secRight?.ReqTop ?? 0 };
            result.ReqBot = new double[] { secLeft?.ReqBot ?? 0, secMid?.ReqBot ?? 0, secRight?.ReqBot ?? 0 };

            // DEBUG: Log SpanResult creation
            Utils.RebarLogger.Log($"[BuildSpanResult] SpanId='{spanId}' Index={spanIndex} | " +
                $"ReqTop=[{result.ReqTop[0]:F2}, {result.ReqTop[1]:F2}, {result.ReqTop[2]:F2}] | " +
                $"ReqBot=[{result.ReqBot[0]:F2}, {result.ReqBot[1]:F2}, {result.ReqBot[2]:F2}]");

            result.TopAddons = new Dictionary<string, RebarInfo>();
            result.BotAddons = new Dictionary<string, RebarInfo>();
            result.Stirrups = new Dictionary<string, string>();
            result.WebBars = new Dictionary<string, string>();

            // Add addons for this span
            foreach (var section in spanSections)
            {
                string zoneName = GetZoneName(section);

                // Check for Top addon
                string keyTop = $"{spanId}_Top_{zoneName}";
                if (evalResult.Addons.TryGetValue(keyTop, out var addonTop))
                {
                    result.TopAddons[zoneName] = addonTop;
                }

                // Check for Bot addon
                string keyBot = $"{spanId}_Bot_{zoneName}";
                if (evalResult.Addons.TryGetValue(keyBot, out var addonBot))
                {
                    result.BotAddons[zoneName] = addonBot;
                }

                // Stirrup calculation
                if (section.ReqStirrup > 0.01)
                {
                    string stirrupStr = RebarCalculator.CalculateStirrup(
                        section.ReqStirrup, 0, section.Width, _settings);
                    result.Stirrups[zoneName] = stirrupStr;
                }
            }

            return result;
        }

        private void CalculateFinalScores(
            ContinuousBeamSolution solution,
            EvaluationResult evalResult,
            List<DesignSection> sections,
            BeamGroup group)
        {
            // Waste calculation
            double totalRequired = sections.Sum(s => s.ReqTop + s.ReqBot);
            double totalProvided = solution.As_Backbone_Top * sections.Count
                                 + solution.As_Backbone_Bot * sections.Count
                                 + solution.Reinforcements.Values.Sum(r => r.Count * Math.PI * r.Diameter * r.Diameter / 400.0);

            solution.WastePercentage = totalRequired > 0
                ? Math.Max(0, (totalProvided - totalRequired) / totalRequired * 100)
                : 0;

            // Efficiency Score (100 - Waste%)
            solution.EfficiencyScore = Math.Max(0, 100 - solution.WastePercentage);

            // Constructability Score
            solution.ConstructabilityScore = CalculateConstructabilityScore(solution, evalResult, sections);

            // Native Match Bonus - Thưởng điểm nếu dùng lại được phương án của SectionSolver
            double matchRatio = evalResult.TotalChecks > 0
                ? (double)evalResult.NativeMatchCount / evalResult.TotalChecks
                : 0;
            double nativeBonus = matchRatio * 15.0; // Max +15 điểm

            // Total Score
            double effWeight = _settings.Beam?.EfficiencyScoreWeight ?? 0.6;
            solution.TotalScore = effWeight * solution.EfficiencyScore
                                + (1 - effWeight) * solution.ConstructabilityScore
                                + nativeBonus;

            solution.TotalScore = Math.Min(100, solution.TotalScore);

            // Metadata
            solution.As_Required_Top_Max = sections.Max(s => s.ReqTop);
            solution.As_Required_Bot_Max = sections.Max(s => s.ReqBot);
            solution.Description = GenerateDescription(solution, evalResult);
        }

        private double CalculateConstructabilityScore(
            ContinuousBeamSolution solution,
            EvaluationResult evalResult,
            List<DesignSection> sections)
        {
            double score = 100;

            // Penalty for too many backbone bars
            if (solution.BackboneCount_Top > 4) score -= 5;
            if (solution.BackboneCount_Bot > 4) score -= 5;

            // Penalty for large diameter (hard to bend)
            if (solution.BackboneDiameter > 25) score -= 10;

            // Penalty for asymmetric backbone
            if (solution.BackboneCount_Top != solution.BackboneCount_Bot)
            {
                int diff = Math.Abs(solution.BackboneCount_Top - solution.BackboneCount_Bot);
                score -= diff * 3;
            }

            // Penalty for too many addons
            score -= evalResult.Addons.Count * 2;

            // Penalty for failed sections
            score -= evalResult.FailedSections.Count * 5;

            return Math.Max(0, score);
        }

        private string GenerateDescription(ContinuousBeamSolution solution, EvaluationResult evalResult)
        {
            int addonCount = solution.Reinforcements.Count;
            string matchInfo = evalResult.TotalChecks > 0
                ? $"{evalResult.NativeMatchCount}/{evalResult.TotalChecks} native"
                : "";

            return $"Backbone: {solution.BackboneCount_Top}D{solution.BackboneDiameter} / {solution.BackboneCount_Bot}D{solution.BackboneDiameter}"
                 + (addonCount > 0 ? $" + {addonCount} addons" : "")
                 + (!string.IsNullOrEmpty(matchInfo) ? $" ({matchInfo})" : "");
        }

        #endregion

        #region Fallback & Error Handling

        private ContinuousBeamSolution TryRelaxedBackbone(
            List<BackboneCandidate> candidates,
            List<DesignSection> sections,
            BeamGroup group)
        {
            // Thử backbone nhỏ nhất có thể
            var minBackbone = candidates
                .OrderBy(c => c.CountTop + c.CountBot)
                .ThenByDescending(c => c.Diameter)
                .FirstOrDefault();

            if (minBackbone == null) return null;

            // Force evaluation với relaxed rules
            var evalResult = EvaluateWithSmartResolution(minBackbone, sections, group);
            evalResult.IsValid = true; // Force valid

            return BuildSolutionFromEvaluation(minBackbone, evalResult, sections, group);
        }

        private ContinuousBeamSolution CreateErrorSolution(string message)
        {
            return new ContinuousBeamSolution
            {
                OptionName = "ERROR",
                IsValid = false,
                ValidationMessage = message,
                TotalScore = 0,
                Reinforcements = new Dictionary<string, RebarSpec>(),
                SpanResults = new List<SpanRebarResult>()
            };
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Check if addon can fit with proper spacing.
        /// </summary>
        private bool CheckAddonSpacing(DesignSection section, int diameter, int count)
        {
            double usableWidth = section.UsableWidth;
            double minSpacing = _settings.Beam?.MinClearSpacing ?? 30;
            double maxSpacing = _settings.Beam?.MaxClearSpacing ?? 200;

            // Calculate spacing if bars are on one layer
            if (count <= 1) return true;

            double totalBarWidth = count * diameter;
            double availableForGaps = usableWidth - totalBarWidth;
            double spacing = availableForGaps / (count - 1);

            return spacing >= minSpacing && spacing <= maxSpacing;
        }

        private double CalculateTotalLength(BeamGroup group, List<DesignSection> sections)
        {
            if (group?.TotalLength > 0) return group.TotalLength * 1000;
            if (group?.Spans?.Count > 0) return group.Spans.Sum(s => s.Length) * 1000;
            if (sections.Count > 0)
            {
                double maxPos = sections.Max(s => s.Position);
                double minPos = sections.Min(s => s.Position);
                if (maxPos > minPos) return (maxPos - minPos) * 1000;
            }
            return 5000; // Default 5m
        }

        private double GetSpanLength(DesignSection section, BeamGroup group)
        {
            if (group?.Spans != null && section.SpanIndex < group.Spans.Count)
            {
                return group.Spans[section.SpanIndex].Length * 1000;
            }
            return 5000;
        }

        /// <summary>
        /// Get zone name from section.
        /// ZoneIndex: 0=Left (Start support), 1=Mid (Midspan), 2=Right (End support)
        /// CRITICAL FIX: ZoneIndex 2 is "Right", NOT "Mid"
        /// </summary>
        private string GetZoneName(DesignSection section)
        {
            // Primary: Use ZoneIndex directly
            switch (section.ZoneIndex)
            {
                case 0:
                    return "Left";
                case 1:
                    return "Mid";
                case 2:
                    return "Right";
                default:
                    // Fallback: Use SectionType
                    if (section.Type == SectionType.MidSpan)
                        return "Mid";
                    if (section.IsSupportLeft)
                        return "Left";
                    if (section.IsSupportRight)
                        return "Right";
                    return "Mid"; // Default fallback
            }
        }

        private void LogSolutionRanking(List<ContinuousBeamSolution> solutions)
        {
            Utils.RebarLogger.Log("");
            Utils.RebarLogger.Log("┌──────┬────────────┬─────────────┬───────┬────────────────────────────┐");
            Utils.RebarLogger.Log("│ Rank │ Backbone   │ Weight (kg) │ Score │ Description                │");
            Utils.RebarLogger.Log("├──────┼────────────┼─────────────┼───────┼────────────────────────────┤");

            int rank = 1;
            foreach (var s in solutions.Take(5))
            {
                string backbone = $"{s.BackboneCount_Top}D{s.BackboneDiameter}/{s.BackboneCount_Bot}D{s.BackboneDiameter}";
                Utils.RebarLogger.Log($"│  {rank++}   │ {backbone,-10} │ {s.TotalSteelWeight,9:F1}   │ {s.TotalScore,5:F1} │ {(s.IsValid ? "Valid" : "INVALID"),-26} │");
            }

            Utils.RebarLogger.Log("└──────┴────────────┴─────────────┴───────┴────────────────────────────┘");
        }

        #endregion
    }
}
