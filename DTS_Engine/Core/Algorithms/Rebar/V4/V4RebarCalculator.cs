using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// V4 Rebar Calculator - Bottom-Up Architecture (Sole Engine).
    /// Entry point cho toàn bộ hệ thống tính toán cốt thép dầm.
    /// Không có fallback - V4 là engine duy nhất.
    /// 
    /// Quy trình:
    /// 1. Discretization: SpanResults → DesignSections (N spans × Z zones)
    /// 2. Local Solve: Mỗi section → Danh sách phương án hợp lệ (N layers)
    /// 3. Topology Merge: Đồng bộ gối (Type 3 Constraint)
    /// 4. Global Optimize: Tìm Backbone + Tổng hợp Solution
    /// 
    /// ISO 25010: Performance Efficiency - O(N × M) thay vì O(M^N)
    /// ISO 12207: Design Phase - Clean modular architecture
    /// </summary>
    public class V4RebarCalculator
    {
        #region Configuration

        /// <summary>Cấu hình discretization (số zones per span)</summary>
        public DiscretizationConfig DiscretizationConfig { get; set; } = DiscretizationConfig.Default;

        #endregion

        #region Dependencies

        private readonly DtsSettings _settings;
        private readonly SectionSolver _sectionSolver;
        private readonly TopologyMerger _topologyMerger;
        private readonly GlobalOptimizer _globalOptimizer;

        #endregion

        #region Constructor

        /// <summary>
        /// Tạo V4 Calculator với settings.
        /// </summary>
        public V4RebarCalculator(DtsSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _sectionSolver = new SectionSolver(settings);
            _topologyMerger = new TopologyMerger(settings);
            _globalOptimizer = new GlobalOptimizer(settings);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Tính toán và trả về Top N phương án.
        /// </summary>
        /// <param name="group">Nhóm dầm</param>
        /// <param name="spanResults">Kết quả SAP cho mỗi nhịp</param>
        /// <param name="externalConstraints">Ràng buộc bên ngoài (nếu có)</param>
        /// <returns>Danh sách ContinuousBeamSolution sắp theo TotalScore</returns>
        public List<ContinuousBeamSolution> Calculate(
            BeamGroup group,
            List<BeamResultData> spanResults,
            ExternalConstraints externalConstraints = null)
        {
            // === LOGGING ===
            Utils.RebarLogger.IsEnabled = _settings?.EnablePipelineLogging ?? false;
            if (Utils.RebarLogger.IsEnabled)
            {
                Utils.RebarLogger.Initialize();
                Utils.RebarLogger.LogPhase($"V4 CALCULATE: {group?.GroupName ?? "?"}");
            }

            try
            {
                // === STEP 0: FAIL-FAST VALIDATION ===
                Utils.RebarLogger.LogPhase("STEP 0: VALIDATION");
                var validationResult = FailFastValidator.ValidateCalculatorInput(group, spanResults, _settings);
                
                if (!validationResult.IsValid)
                {
                    Utils.RebarLogger.LogError($"Validation failed: {validationResult.Message}");
                    return CreateSingleErrorSolution(validationResult.Message);
                }
                
                // Log warnings if any
                if (validationResult.Warnings.Count > 0)
                {
                    foreach (var warning in validationResult.Warnings)
                    {
                        Utils.RebarLogger.Log($"  WARNING: {warning}");
                    }
                }

                // Legacy quick check (kept for backward compatibility)
                if (spanResults == null || spanResults.Count == 0)
                {
                    Utils.RebarLogger.LogError("No span results provided");
                    return CreateSingleErrorSolution("Không có dữ liệu nội lực nhịp");
                }

                // === STEP 1: DISCRETIZATION ===
                Utils.RebarLogger.LogPhase("STEP 1: DISCRETIZATION");
                var sections = Discretize(group, spanResults);

                if (sections.Count == 0)
                {
                    Utils.RebarLogger.LogError("Discretization failed: No sections created");
                    return CreateSingleErrorSolution("Không thể phân tích dữ liệu nhịp");
                }

                Utils.RebarLogger.Log($"Created {sections.Count} design sections from {spanResults.Count} spans");

                // === STEP 2: LOCAL SOLVE ===
                Utils.RebarLogger.LogPhase("STEP 2: LOCAL SOLVE");
                _sectionSolver.SolveAll(sections);

                // Log section results
                foreach (var section in sections)
                {
                    Utils.RebarLogger.Log($"  {section.SectionId}: " +
                        $"TopOptions={section.ValidArrangementsTop.Count}, " +
                        $"BotOptions={section.ValidArrangementsBot.Count}");
                }

                // Check if any section has no solutions
                var failedSections = sections
                    .Where(s => (s.ReqTop > 0.01 && s.ValidArrangementsTop.Count == 0) ||
                               (s.ReqBot > 0.01 && s.ValidArrangementsBot.Count == 0))
                    .ToList();

                if (failedSections.Count > 0)
                {
                    var failedIds = string.Join(", ", failedSections.Select(s => s.SectionId));
                    Utils.RebarLogger.LogError($"Local solve failed for sections: {failedIds}");

                    return CreateSingleErrorSolution($"Không tìm được phương án cho: {failedIds}");
                }

                // === STEP 3: TOPOLOGY MERGE ===
                Utils.RebarLogger.LogPhase("STEP 3: TOPOLOGY MERGE");
                if (!_topologyMerger.ApplyConstraints(sections))
                {
                    Utils.RebarLogger.LogError("Topology merge failed: No compatible arrangements at supports");

                    return CreateSingleErrorSolution("Không tìm được phương án thống nhất tại gối");
                }

                // Log after merge
                var supportSections = sections.Where(s => s.Type == SectionType.Support).ToList();
                foreach (var section in supportSections)
                {
                    Utils.RebarLogger.Log($"  {section.SectionId} (merged): " +
                        $"TopOptions={section.ValidArrangementsTop.Count}, " +
                        $"BotOptions={section.ValidArrangementsBot.Count}");
                }

                // === STEP 4: GLOBAL OPTIMIZE ===
                Utils.RebarLogger.LogPhase("STEP 4: GLOBAL OPTIMIZE");
                var solutions = _globalOptimizer.FindBestSolutions(sections, group, externalConstraints);

                // Log solutions
                foreach (var sol in solutions)
                {
                    Utils.RebarLogger.Log($"  Solution: {sol.OptionName} | " +
                        $"Score={sol.TotalScore:F1} | Weight={sol.TotalSteelWeight:F1}kg | " +
                        $"Valid={sol.IsValid}");
                }

                Utils.RebarLogger.LogPhase("V4 COMPLETE");
                return solutions;
            }
            catch (Exception ex)
            {
                Utils.RebarLogger.LogError($"V4 Exception: {ex.Message}\n{ex.StackTrace}");
                return CreateSingleErrorSolution($"Lỗi hệ thống: {ex.Message}");
            }
            finally
            {
                Utils.RebarLogger.OpenLogFile();
            }
        }

        /// <summary>
        /// Static entry point - V4 is the sole calculator.
        /// </summary>
        public static List<ContinuousBeamSolution> CalculateProposals(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ExternalConstraints externalConstraints = null)
        {
            var calculator = new V4RebarCalculator(settings);
            return calculator.Calculate(group, spanResults, externalConstraints);
        }

        /// <summary>
        /// CRITICAL: Apply solution kết quả về SpanData của BeamGroup.
        /// Đây là single source of truth để sync giữa calculator -> viewer -> XData.
        /// </summary>
        public static void ApplySolutionToGroup(BeamGroup group, ContinuousBeamSolution solution)
        {
            if (group == null || solution == null) return;
            if (group.Spans == null || group.Spans.Count == 0) return;

            // 1. Apply backbone info
            int backboneDiaTop = solution.BackboneDiameter_Top > 0 ? solution.BackboneDiameter_Top : solution.BackboneDiameter;
            int backboneDiaBot = solution.BackboneDiameter_Bot > 0 ? solution.BackboneDiameter_Bot : solution.BackboneDiameter;

            // 2. Iterate through SpanResults if available
            if (solution.SpanResults != null && solution.SpanResults.Count > 0)
            {
                foreach (var spanResult in solution.SpanResults)
                {
                    var span = group.Spans.FirstOrDefault(s => s.SpanId == spanResult.SpanId);
                    if (span == null && spanResult.SpanIndex >= 0 && spanResult.SpanIndex < group.Spans.Count)
                    {
                        span = group.Spans[spanResult.SpanIndex];
                    }
                    if (span == null) continue;

                    // Apply backbone
                    span.TopBackbone = spanResult.TopBackbone ?? new RebarInfo
                    {
                        Count = solution.BackboneCount_Top,
                        Diameter = backboneDiaTop
                    };
                    span.BotBackbone = spanResult.BotBackbone ?? new RebarInfo
                    {
                        Count = solution.BackboneCount_Bot,
                        Diameter = backboneDiaBot
                    };

                    // Apply addons from SpanResult
                    if (spanResult.TopAddons != null)
                    {
                        span.TopAddLeft = spanResult.TopAddons.TryGetValue("Left", out var tl) ? tl : null;
                        span.TopAddMid = spanResult.TopAddons.TryGetValue("Mid", out var tm) ? tm : null;
                        span.TopAddRight = spanResult.TopAddons.TryGetValue("Right", out var tr) ? tr : null;
                    }

                    if (spanResult.BotAddons != null)
                    {
                        span.BotAddLeft = spanResult.BotAddons.TryGetValue("Left", out var bl) ? bl : null;
                        span.BotAddMid = spanResult.BotAddons.TryGetValue("Mid", out var bm) ? bm : null;
                        span.BotAddRight = spanResult.BotAddons.TryGetValue("Right", out var br) ? br : null;
                    }

                    // Build legacy TopRebar/BotRebar arrays for viewer compatibility
                    BuildLegacyRebarArrays(span, solution);

                    // Apply stirrups if available
                    if (spanResult.Stirrups != null && spanResult.Stirrups.Count > 0)
                    {
                        if (span.Stirrup == null) span.Stirrup = new string[3];
                        if (spanResult.Stirrups.TryGetValue("Left", out var sl)) span.Stirrup[0] = sl;
                        if (spanResult.Stirrups.TryGetValue("Mid", out var sm)) span.Stirrup[1] = sm;
                        if (spanResult.Stirrups.TryGetValue("Right", out var sr)) span.Stirrup[2] = sr;
                    }
                }
            }
            else
            {
                // Fallback: Apply backbone uniformly if no SpanResults
                foreach (var span in group.Spans)
                {
                    span.TopBackbone = new RebarInfo
                    {
                        Count = solution.BackboneCount_Top,
                        Diameter = backboneDiaTop
                    };
                    span.BotBackbone = new RebarInfo
                    {
                        Count = solution.BackboneCount_Bot,
                        Diameter = backboneDiaBot
                    };

                    // Apply reinforcements from dictionary
                    ApplyReinforcementsToSpan(span, solution);
                    BuildLegacyRebarArrays(span, solution);
                }
            }
        }

        /// <summary>
        /// Apply Reinforcements dictionary to a specific span.
        /// </summary>
        private static void ApplyReinforcementsToSpan(SpanData span, ContinuousBeamSolution solution)
        {
            if (solution.Reinforcements == null) return;

            // Parse reinforcements for this span
            foreach (var kvp in solution.Reinforcements)
            {
                if (!kvp.Key.StartsWith(span.SpanId + "_")) continue;

                var parts = kvp.Key.Split('_');
                if (parts.Length < 3) continue;

                string position = parts[1]; // "Top" or "Bot"
                string zone = parts[2];     // "Left", "Mid", "Right" or index

                var spec = kvp.Value;
                var info = new RebarInfo
                {
                    Count = spec.Count,
                    Diameter = spec.Diameter,
                    LayerCounts = spec.LayerBreakdown
                };

                if (position == "Top")
                {
                    if (zone == "Left" || zone == "0") span.TopAddLeft = info;
                    else if (zone == "Mid" || zone == "1" || zone == "2") span.TopAddMid = info;
                    else if (zone == "Right" || zone == "4") span.TopAddRight = info;
                }
                else if (position == "Bot")
                {
                    if (zone == "Left" || zone == "0") span.BotAddLeft = info;
                    else if (zone == "Mid" || zone == "1" || zone == "2") span.BotAddMid = info;
                    else if (zone == "Right" || zone == "4") span.BotAddRight = info;
                }
            }
        }

        /// <summary>
        /// Build legacy TopRebar/BotRebar string arrays for viewer compatibility.
        /// Format: TopRebar[layer][zone] where zone: 0=Left, 1=Mid, 2=Right
        /// </summary>
        private static void BuildLegacyRebarArrays(SpanData span, ContinuousBeamSolution solution)
        {
            // Initialize arrays if null
            if (span.TopRebarInternal == null) span.TopRebarInternal = new string[3, 6];
            if (span.BotRebarInternal == null) span.BotRebarInternal = new string[3, 6];

            // Layer 0: Backbone + Addon (combined)
            string topBackboneStr = span.TopBackbone?.DisplayString ?? $"{solution.BackboneCount_Top}D{solution.BackboneDiameter}";
            string botBackboneStr = span.BotBackbone?.DisplayString ?? $"{solution.BackboneCount_Bot}D{solution.BackboneDiameter}";

            // Zone 0 (Left Support)
            string topL0 = CombineRebarStrings(topBackboneStr, span.TopAddLeft?.DisplayString);
            string botL0 = CombineRebarStrings(botBackboneStr, span.BotAddLeft?.DisplayString);

            // Zone 2 (Mid)
            string topL2 = CombineRebarStrings(topBackboneStr, span.TopAddMid?.DisplayString);
            string botL2 = CombineRebarStrings(botBackboneStr, span.BotAddMid?.DisplayString);

            // Zone 4 (Right Support)
            string topL4 = CombineRebarStrings(topBackboneStr, span.TopAddRight?.DisplayString);
            string botL4 = CombineRebarStrings(botBackboneStr, span.BotAddRight?.DisplayString);

            // Apply to internal arrays (6 zones, but typically only 0, 2, 4 are used)
            for (int layer = 0; layer < 3; layer++)
            {
                if (layer == 0)
                {
                    span.TopRebarInternal[layer, 0] = topL0;
                    span.TopRebarInternal[layer, 2] = topL2;
                    span.TopRebarInternal[layer, 4] = topL4;

                    span.BotRebarInternal[layer, 0] = botL0;
                    span.BotRebarInternal[layer, 2] = botL2;
                    span.BotRebarInternal[layer, 4] = botL4;
                }
            }
        }

        /// <summary>
        /// Combine backbone + addon strings (e.g., "2D20" + "2D18" => "2D20+2D18")
        /// </summary>
        private static string CombineRebarStrings(string backbone, string addon)
        {
            if (string.IsNullOrEmpty(addon) || addon == "-") return backbone;
            if (string.IsNullOrEmpty(backbone) || backbone == "-") return addon;
            return $"{backbone}+{addon}";
        }

        #endregion

        #region Discretization

        /// <summary>
        /// Chuyển đổi SpanResults thành danh sách DesignSections.
        /// Hỗ trợ N nhịp linh hoạt với cấu hình zones tùy chỉnh.
        /// </summary>
        private List<DesignSection> Discretize(BeamGroup group, List<BeamResultData> spanResults)
        {
            var sections = new List<DesignSection>();

            // Lấy số nhịp
            int numSpans = spanResults.Count;

            // Lấy thông tin nhịp từ group nếu có, nếu không tạo từ spanResults
            var spanInfos = ExtractSpanInfos(group, spanResults);

            if (spanInfos.Count == 0)
            {
                Utils.RebarLogger.LogError("No span info available");
                return sections;
            }

            // Cấu hình zones
            var config = DiscretizationConfig;
            int zonesPerSpan = config.ZonesPerSpan;

            double torsionTop = _settings.Beam?.TorsionDist_TopBar ?? 0.25;
            double torsionBot = _settings.Beam?.TorsionDist_BotBar ?? 0.25;

            double cumPosition = 0;
            int globalIndex = 0;

            for (int spanIdx = 0; spanIdx < spanInfos.Count; spanIdx++)
            {
                var spanInfo = spanInfos[spanIdx];
                var result = spanIdx < spanResults.Count ? spanResults[spanIdx] : null;

                double spanLength = spanInfo.Length;
                double width = spanInfo.Width;
                double height = spanInfo.Height;

                // Tạo sections cho mỗi zone
                for (int zoneIdx = 0; zoneIdx < zonesPerSpan; zoneIdx++)
                {
                    double relativePos = config.ZonePositions[zoneIdx];
                    SectionType zoneType = config.ZoneTypes[zoneIdx];

                    // Xác định loại section thực tế (đầu/cuối dầm có thể là FreeEnd)
                    SectionType actualType = DetermineSectionType(
                        spanIdx, numSpans, zoneIdx, zonesPerSpan, zoneType, group);

                    // Xác định position flags
                    bool isSupportLeft = (zoneIdx == 0);
                    bool isSupportRight = (zoneIdx == zonesPerSpan - 1);

                    // Lấy diện tích yêu cầu từ result (Smart Scanning với Zone Ratio)
                    double reqTop = GetReqAreaSmartScan(result, true, zoneIdx, zonesPerSpan, torsionTop);
                    double reqBot = GetReqAreaSmartScan(result, false, zoneIdx, zonesPerSpan, torsionBot);
                    int resultZoneIndex = MapZoneToResultIndex(zoneIdx, zonesPerSpan);
                    double reqStirrup = result?.ShearArea?.ElementAtOrDefault(resultZoneIndex) ?? 0;

                    string sectionId = $"{spanInfo.SpanId}_{GetZoneName(zoneIdx, zonesPerSpan)}";

                    sections.Add(new DesignSection
                    {
                        GlobalIndex = globalIndex++,
                        SectionId = sectionId,
                        SpanIndex = spanIdx,
                        ZoneIndex = zoneIdx,
                        SpanId = spanInfo.SpanId,
                        Type = actualType,
                        Position = cumPosition + spanLength * relativePos,
                        RelativePosition = relativePos,
                        Width = width,
                        Height = height,
                        CoverTop = _settings.Beam?.CoverTop ?? 35,
                        CoverBot = _settings.Beam?.CoverBot ?? 35,
                        CoverSide = _settings.Beam?.CoverSide ?? 25,
                        StirrupDiameter = _settings.Beam?.EstimatedStirrupDiameter ?? 10,
                        ReqTop = reqTop,
                        ReqBot = reqBot,
                        ReqStirrup = reqStirrup,
                        IsSupportLeft = isSupportLeft && spanIdx > 0, // Not for first span start
                        IsSupportRight = isSupportRight && spanIdx < numSpans - 1 // Not for last span end
                    });
                }

                cumPosition += spanLength;
            }

            return sections;
        }

        /// <summary>
        /// Trích xuất thông tin nhịp từ group hoặc tạo từ spanResults.
        /// </summary>
        private List<SpanInfo> ExtractSpanInfos(BeamGroup group, List<BeamResultData> spanResults)
        {
            var infos = new List<SpanInfo>();

            if (group?.Spans != null && group.Spans.Count > 0)
            {
                // Lấy từ group
                for (int i = 0; i < group.Spans.Count && i < spanResults.Count; i++)
                {
                    var span = group.Spans[i];
                    var result = spanResults[i];

                    infos.Add(new SpanInfo
                    {
                        SpanId = span.SpanId ?? $"S{i + 1}",
                        Length = span.Length > 0 ? span.Length : 5.0,
                        Width = NormalizeToMm(span.Width > 0 ? span.Width : result?.Width ?? group.Width),
                        Height = NormalizeToMm(span.Height > 0 ? span.Height : result?.SectionHeight ?? group.Height)
                    });
                }
            }
            else
            {
                // Tạo từ spanResults
                for (int i = 0; i < spanResults.Count; i++)
                {
                    var result = spanResults[i];

                    infos.Add(new SpanInfo
                    {
                        SpanId = $"S{i + 1}",
                        Length = 5.0, // Default 5m
                        Width = NormalizeToMm(result?.Width ?? group?.Width ?? 300),
                        Height = NormalizeToMm(result?.SectionHeight ?? group?.Height ?? 500)
                    });
                }
            }

            return infos;
        }

        /// <summary>
        /// Xác định loại section thực tế.
        /// </summary>
        private SectionType DetermineSectionType(
            int spanIdx, int numSpans,
            int zoneIdx, int zonesPerSpan,
            SectionType zoneType,
            BeamGroup group)
        {
            // Đầu dầm (first zone of first span)
            if (spanIdx == 0 && zoneIdx == 0)
            {
                var firstSupport = group?.Supports?.FirstOrDefault();
                if (firstSupport?.Type == SupportType.FreeEnd)
                    return SectionType.FreeEnd;
            }

            // Cuối dầm (last zone of last span)
            if (spanIdx == numSpans - 1 && zoneIdx == zonesPerSpan - 1)
            {
                var lastSupport = group?.Supports?.LastOrDefault();
                if (lastSupport?.Type == SupportType.FreeEnd)
                    return SectionType.FreeEnd;
            }

            return zoneType;
        }

        /// <summary>
        /// Map zone index sang result index (result thường có 3 vị trí: 0=Left, 1=Mid, 2=Right).
        /// </summary>
        private int MapZoneToResultIndex(int zoneIdx, int zonesPerSpan)
        {
            if (zonesPerSpan == 3)
            {
                return zoneIdx; // Direct mapping
            }

            if (zonesPerSpan == 5)
            {
                // [0, 1, 2, 3, 4] -> [0, 0, 1, 2, 2]
                switch (zoneIdx)
                {
                    case 0: return 0;
                    case 1: return 0;
                    case 2: return 1;
                    case 3: return 2;
                    case 4: return 2;
                    default: return 1;
                }
            }

            // Default: map proportionally
            double ratio = (double)zoneIdx / (zonesPerSpan - 1);
            if (ratio <= 0.33) return 0;
            if (ratio >= 0.67) return 2;
            return 1;
        }

        /// <summary>
        /// Lấy tên zone từ index.
        /// </summary>
        private string GetZoneName(int zoneIdx, int zonesPerSpan)
        {
            if (zonesPerSpan == 3)
            {
                switch (zoneIdx)
                {
                    case 0: return "Support_Left";
                    case 1: return "MidSpan";
                    case 2: return "Support_Right";
                    default: return $"Zone{zoneIdx}";
                }
            }

            if (zonesPerSpan == 5)
            {
                switch (zoneIdx)
                {
                    case 0: return "Support_Left";
                    case 1: return "Quarter_Left";
                    case 2: return "MidSpan";
                    case 3: return "Quarter_Right";
                    case 4: return "Support_Right";
                    default: return $"Zone{zoneIdx}";
                }
            }

            return $"Zone{zoneIdx}";
        }

        /// <summary>
        /// Thông tin nhịp tạm thời.
        /// </summary>
        private class SpanInfo
        {
            public string SpanId { get; set; }
            public double Length { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
        }

        #endregion

        #region Helpers

        /// <summary>
        /// Lấy diện tích thép yêu cầu lớn nhất trong vùng được cấu hình (Smart Scanning).
        /// Sử dụng Settings.General.ZoneL1_Ratio và ZoneL2_Ratio.
        /// </summary>
        /// <param name="result">Kết quả SAP</param>
        /// <param name="isTop">True = thép trên, False = thép dưới</param>
        /// <param name="zoneIdx">Index của zone (0=Left, 1=Mid, 2=Right cho 3-zone)</param>
        /// <param name="zonesPerSpan">Số zones per span (VD: 3)</param>
        /// <param name="torsionFactor">Hệ số phân bổ xoắn (0.25 cho Top/Bot)</param>
        private double GetReqAreaSmartScan(BeamResultData result, bool isTop, int zoneIdx, int zonesPerSpan, double torsionFactor)
        {
            if (result == null) return 0;

            var areaList = isTop ? result.TopArea : result.BotArea;
            var torsionList = result.TorsionArea;

            if (areaList == null || areaList.Length == 0) return 0;

            // 1. Lấy Safety Factor từ Settings
            double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;

            // 2. Xác định phạm vi index cần quét dựa trên Settings
            int count = areaList.Length;
            int startIdx, endIdx;

            if (count <= 3) // Nếu dữ liệu nội lực quá ít (chỉ có L/M/R), fallback về logic cũ
            {
                int mapIdx = MapZoneToResultIndex(zoneIdx, zonesPerSpan);
                startIdx = endIdx = Math.Min(mapIdx, count - 1);
            }
            else
            {
                // Lấy tỷ lệ vùng từ General Config (VD: 0.25 = 1/4 nhịp)
                double ratioL1 = _settings.General?.ZoneL1_Ratio ?? 0.25;
                double ratioL2 = _settings.General?.ZoneL2_Ratio ?? 0.25;

                // Tính index biên
                int idxL1 = (int)(count * ratioL1);
                int idxL2 = count - (int)(count * ratioL2);

                // Map Zone Index sang phạm vi quét
                if (zonesPerSpan == 3) // Cấu hình 3 Zone (Support-Mid-Support)
                {
                    if (zoneIdx == 0) { startIdx = 0; endIdx = idxL1; } // Left Support
                    else if (zoneIdx == 2) { startIdx = idxL2; endIdx = count - 1; } // Right Support
                    else { startIdx = idxL1; endIdx = idxL2; } // Mid Span
                }
                else // Fallback cho các trường hợp khác (5 zones, etc.)
                {
                    int centerIdx = (int)(count * (double)zoneIdx / (zonesPerSpan - 1));
                    int scanRadius = Math.Max(1, count / 10); // Quét lân cận 10%
                    startIdx = Math.Max(0, centerIdx - scanRadius);
                    endIdx = Math.Min(count - 1, centerIdx + scanRadius);
                }
            }

            // 3. Quét Max trong vùng
            double maxArea = 0;
            for (int i = startIdx; i <= endIdx; i++)
            {
                if (i >= areaList.Length) break;

                double flex = areaList[i];
                double tor = (torsionList != null && i < torsionList.Length) ? torsionList[i] : 0;

                // Công thức: As_req = (As_flex + As_torsion * Factor) * SafetyFactor
                double total = (flex + tor * torsionFactor) * safetyFactor;

                if (total > maxArea) maxArea = total;
            }

            return maxArea;
        }

        /// <summary>
        /// Lấy diện tích yêu cầu (bao gồm torsion distribution) - Legacy fallback.
        /// </summary>
        private double GetReqArea(BeamResultData result, bool isTop, int position, double torsionFactor)
        {
            if (result == null) return 0;

            double flexArea = isTop
                ? (result.TopArea?.ElementAtOrDefault(position) ?? 0)
                : (result.BotArea?.ElementAtOrDefault(position) ?? 0);

            double torsionArea = (result.TorsionArea?.ElementAtOrDefault(position) ?? 0) * torsionFactor;

            // Áp dụng SafetyFactor từ Settings
            double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;

            return (flexArea + torsionArea) * safetyFactor;
        }

        /// <summary>
        /// Normalize kích thước về mm.
        /// </summary>
        private double NormalizeToMm(double? val)
        {
            if (!val.HasValue || val.Value <= 0) return 0;
            double v = val.Value;

            if (v < 5) return v * 1000;  // m → mm
            if (v < 100) return v * 10;  // cm → mm
            return v; // Already mm
        }

        /// <summary>
        /// Tạo danh sách chứa 1 solution lỗi.
        /// </summary>
        private List<ContinuousBeamSolution> CreateSingleErrorSolution(string message)
        {
            return new List<ContinuousBeamSolution>
            {
                new ContinuousBeamSolution
                {
                    OptionName = "ERROR",
                    IsValid = false,
                    ValidationMessage = message,
                    TotalScore = 0,
                    Reinforcements = new Dictionary<string, RebarSpec>(),
                    SpanResults = new List<SpanRebarResult>()
                }
            };
        }

        #endregion
    }
}
