using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Algorithms.Rebar.Strategies;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.Pipeline.Stages
{
    /// <summary>
    /// Stage 2: Tính toán thép gia cường cho mỗi nhịp.
    /// V3.1: SUPPORT-CENTRIC DESIGN - Engineer Thinking
    /// - Phase 1: Unified Support Design (Gối là thực thể duy nhất)
    /// - Phase 2: Span Bridging (Gán thép gối cho cả 2 nhịp liền kề)
    /// - Phase 3: Mid-span Design
    /// </summary>
    public class ReinforcementFiller : IRebarPipelineStage
    {
        public string StageName { get { return "ReinforcementFiller"; } }
        public int Order { get { return 2; } }

        private readonly IFillingStrategy _greedyStrategy;
        private readonly IFillingStrategy _balancedStrategy;

        public ReinforcementFiller()
        {
            _greedyStrategy = new GreedyFillingStrategy();
            _balancedStrategy = new BalancedFillingStrategy();
        }

        public ReinforcementFiller(IFillingStrategy greedyStrategy, IFillingStrategy balancedStrategy)
        {
            _greedyStrategy = greedyStrategy;
            _balancedStrategy = balancedStrategy;
        }

        public IEnumerable<SolutionContext> Execute(
            IEnumerable<SolutionContext> inputs,
            ProjectConstraints globalConstraints)
        {
            foreach (var ctx in inputs)
            {
                if (!ctx.IsValid)
                {
                    yield return ctx;
                    continue;
                }
                // Use the new Engineer-Thinking logic
                yield return SolveScenarioEngineered(ctx);
            }
        }

        /// <summary>
        /// NEW LOGIC: Support-Centric Design instead of Span-Centric.
        /// Kỹ sư nhìn vào Biểu đồ bao vật liệu (Envelope) của toàn dầm.
        /// </summary>
        private SolutionContext SolveScenarioEngineered(SolutionContext ctx)
        {
            var group = ctx.Group;
            var results = ctx.SpanResults;
            var settings = ctx.Settings;

            // Get Safety Factor from settings (default 1.0, user can increase to 1.05)
            double safetyFactor = settings?.Rules?.SafetyFactor ?? 1.0;

            // 1. Setup Backbone (Base Solution)
            var sol = new ContinuousBeamSolution
            {
                OptionName = ctx.ScenarioId,
                BackboneDiameter_Top = ctx.TopBackboneDiameter,
                BackboneDiameter_Bot = ctx.BotBackboneDiameter,
                BackboneCount_Top = ctx.TopBackboneCount,
                BackboneCount_Bot = ctx.BotBackboneCount,
                IsValid = true,
                Reinforcements = new Dictionary<string, RebarSpec>()
            };

            // Calculate Backbone Area
            double backboneAreaTop = ctx.TopBackboneCount * GetBarArea(ctx.TopBackboneDiameter);
            double backboneAreaBot = ctx.BotBackboneCount * GetBarArea(ctx.BotBackboneDiameter);
            sol.As_Backbone_Top = backboneAreaTop;
            sol.As_Backbone_Bot = backboneAreaBot;

            ctx.CurrentSolution = sol;
            // V3.3: Initial stirrup leg estimate uses width-based fallback
            // Will be refined after reinforcement design when bar count is known
            ctx.StirrupLegCount = GetStirrupLegCountByWidth(ctx.BeamWidth, settings);

            // ═══════════════════════════════════════════════════════════════
            // V3.5: EXPLICIT SPAN VALIDATION (Fail-Fast for Missing Data)
            // ═══════════════════════════════════════════════════════════════
            int numSpansGeometry = group.Spans?.Count ?? 0;
            int numSpansResults = results?.Count ?? 0;

            if (numSpansGeometry == 0)
            {
                ctx.IsValid = false;
                sol.IsValid = false;
                sol.ValidationMessage = "FATAL: Không có dữ liệu hình học nhịp (Spans is empty)";
                return ctx;
            }

            if (numSpansResults == 0)
            {
                ctx.IsValid = false;
                sol.IsValid = false;
                sol.ValidationMessage = "FATAL: Không có dữ liệu nội lực (SpanResults is empty)";
                return ctx;
            }

            // Check for missing span data - each span must have corresponding result
            for (int i = 0; i < Math.Min(numSpansGeometry, numSpansResults); i++)
            {
                if (results[i] == null)
                {
                    ctx.IsValid = false;
                    sol.IsValid = false;
                    sol.ValidationMessage = $"FATAL: Dữ liệu nội lực nhịp {i + 1} bị thiếu (null)";
                    return ctx;
                }
            }

            int numSpans = Math.Min(numSpansGeometry, numSpansResults);

            // ==================================================================================
            // PHASE 1: UNIFY SUPPORTS (Đồng bộ hóa Gối)
            // Support i is between Span i-1 and Span i.
            // Tại một cột (Gối), thép lớp trên là sự liên tục từ nhịp trái sang nhịp phải.
            // ==================================================================================

            // Dictionary to store designed reinforcement at each support index
            var supportDesignsTop = new Dictionary<int, RebarSpec>();
            var supportDesignsBot = new Dictionary<int, RebarSpec>();

            for (int i = 0; i <= numSpans; i++)
            {
                // === TOP REINFORCEMENT AT SUPPORT ===
                double reqTopLeft = 0;  // From span i-1 (Right end)
                double reqTopRight = 0; // From span i (Left end)

                if (i > 0 && results[i - 1] != null)
                    reqTopLeft = GetReqArea(results[i - 1], true, 2, settings);

                if (i < numSpans && results[i] != null)
                    reqTopRight = GetReqArea(results[i], true, 0, settings);

                // The Envelope Requirement at this Support (apply safety factor)
                double maxReqTop = Math.Max(reqTopLeft, reqTopRight) * safetyFactor;

                // Design the TOP reinforcement for this specific support
                var topSpec = DesignLocation(
                    ctx, maxReqTop, ctx.TopBackboneDiameter, ctx.TopBackboneCount,
                    backboneAreaTop, safetyFactor, true
                );

                if (topSpec == null)
                {
                    ctx.IsValid = false;
                    ctx.FailStage = StageName;
                    sol.IsValid = false;
                    sol.ValidationMessage = string.Format(
                        "CRITICAL: Không đủ chỗ bố trí thép Top tại Gối {0} (Req: {1:F1} cm²)",
                        i, maxReqTop);
                    return ctx;
                }
                supportDesignsTop[i] = topSpec;

                // === BOTTOM REINFORCEMENT AT SUPPORT (if needed) ===
                double reqBotLeft = 0;
                double reqBotRight = 0;

                if (i > 0 && results[i - 1] != null)
                    reqBotLeft = GetReqArea(results[i - 1], false, 2, settings);

                if (i < numSpans && results[i] != null)
                    reqBotRight = GetReqArea(results[i], false, 0, settings);

                double maxReqBot = Math.Max(reqBotLeft, reqBotRight) * safetyFactor;

                // Only design if required > backbone (thường thép dưới gối không cần gia cường)
                if (maxReqBot > backboneAreaBot)
                {
                    var botSpec = DesignLocation(
                        ctx, maxReqBot, ctx.BotBackboneDiameter, ctx.BotBackboneCount,
                        backboneAreaBot, safetyFactor, false
                    );

                    if (botSpec == null)
                    {
                        ctx.IsValid = false;
                        ctx.FailStage = StageName;
                        sol.IsValid = false;
                        sol.ValidationMessage = string.Format(
                            "CRITICAL: Không đủ chỗ bố trí thép Bot tại Gối {0}", i);
                        return ctx;
                    }
                    supportDesignsBot[i] = botSpec;
                }
            }

            // ==================================================================================
            // PHASE 2: FILL SPANS & BRIDGE SUPPORTS (Điền vào nhịp & Gán thép gối)
            // Đảm bảo 100% đồng bộ: cả 2 nhịp liền kề dùng chung 1 RebarSpec tại gối
            // ==================================================================================

            for (int i = 0; i < numSpans; i++)
            {
                var span = group.Spans[i];
                var res = results[i];

                // A. ASSIGN SUPPORT REINFORCEMENT (TOP)
                // Left Support of this span = Support i
                if (supportDesignsTop.ContainsKey(i))
                    AssignSpecToSolution(sol, supportDesignsTop[i], string.Format("{0}_Top_Left", span.SpanId));

                // Right Support of this span = Support i+1
                if (supportDesignsTop.ContainsKey(i + 1))
                    AssignSpecToSolution(sol, supportDesignsTop[i + 1], string.Format("{0}_Top_Right", span.SpanId));

                // B. ASSIGN SUPPORT REINFORCEMENT (BOT - if exists)
                if (supportDesignsBot.ContainsKey(i))
                    AssignSpecToSolution(sol, supportDesignsBot[i], string.Format("{0}_Bot_Left", span.SpanId));

                if (supportDesignsBot.ContainsKey(i + 1))
                    AssignSpecToSolution(sol, supportDesignsBot[i + 1], string.Format("{0}_Bot_Right", span.SpanId));

                // C. DESIGN MID-SPAN (BOTTOM - Primary)
                double reqBotMid = GetReqArea(res, false, 1, settings) * safetyFactor;
                var midBotSpec = DesignLocation(
                    ctx, reqBotMid, ctx.BotBackboneDiameter, ctx.BotBackboneCount,
                    backboneAreaBot, safetyFactor, false
                );

                if (midBotSpec == null)
                {
                    ctx.IsValid = false;
                    ctx.FailStage = StageName;
                    sol.IsValid = false;
                    sol.ValidationMessage = string.Format(
                        "CRITICAL: Không đủ chỗ bố trí thép Bot Mid tại {0} (Req: {1:F1} cm²)",
                        span.SpanId, reqBotMid);
                    return ctx;
                }
                AssignSpecToSolution(sol, midBotSpec, string.Format("{0}_Bot_Mid", span.SpanId));

                // D. DESIGN MID-SPAN (TOP - if needed for compression/hanging)
                double reqTopMid = GetReqArea(res, true, 1, settings) * safetyFactor;
                if (reqTopMid > backboneAreaTop)
                {
                    var midTopSpec = DesignLocation(
                        ctx, reqTopMid, ctx.TopBackboneDiameter, ctx.TopBackboneCount,
                        backboneAreaTop, safetyFactor, true
                    );
                    if (midTopSpec != null)
                        AssignSpecToSolution(sol, midTopSpec, string.Format("{0}_Top_Mid", span.SpanId));
                }
            }

            // ==================================================================================
            // PHASE 2.5: INTELLIGENT BRIDGING (Nối thông nhịp ngắn)
            // Nếu khe hở giữa 2 đoạn thép gia cường < ngưỡng → Merge thành thanh chạy suốt
            // Uses Curtailment settings from DtsSettings (BeamCurtailment/GirderCurtailment)
            // ==================================================================================
            ApplyBridgingLogic(sol, group, settings);

            // ==================================================================================
            // PHASE 3: METRICS CALCULATION
            // ==================================================================================
            CalculateSolutionMetrics(sol, group, settings);

            return ctx;
        }

        /// <summary>
        /// Helper to design a single location (Support or Midspan)
        /// trying both Greedy and Balanced, returning the best valid Spec.
        /// Returns null if CANNOT fit (dầm quá hẹp).
        /// V3.3: Correct mixed-diameter capacity check - tries ALL diameters with proper spacing validation.
        /// </summary>
        private RebarSpec DesignLocation(
            SolutionContext ctx,
            double reqArea,
            int backboneDia,
            int backboneCount,
            double backboneArea,
            double safetyFactor,
            bool isTop)
        {
            // If backbone covers requirement
            if (backboneArea >= reqArea)
            {
                return new RebarSpec { Count = 0, Diameter = backboneDia, Layer = 1 }; // No add bars
            }

            var settings = ctx.Settings;
            bool preferSingleDiameter = settings?.Beam?.PreferSingleDiameter ?? true;

            // V3.3: Build list of ALL diameters to try (not just larger ones)
            var availableDiameters = settings?.General?.AvailableDiameters ?? new List<int> { 12, 14, 16, 18, 20, 22, 25, 28, 32 };
            var diametersToTry = new List<int>();

            if (preferSingleDiameter)
            {
                // Only try backbone diameter
                diametersToTry.Add(backboneDia);
            }
            else
            {
                // Try ALL available diameters, ordered by preference:
                // 1. Same diameter as backbone (easiest construction)
                // 2. Smaller diameters (descending - to minimize bar count while staying smaller)
                // 3. Larger diameters (ascending - only if smaller don't fit)
                diametersToTry.Add(backboneDia);

                // Add smaller diameters (descending order - prefer larger of the smaller ones)
                var smaller = availableDiameters.Where(d => d < backboneDia).OrderByDescending(d => d);
                diametersToTry.AddRange(smaller);

                // Add larger diameters (ascending order - prefer smaller of the larger ones)
                var larger = availableDiameters.Where(d => d > backboneDia).OrderBy(d => d);
                diametersToTry.AddRange(larger);

                // Remove duplicates
                diametersToTry = diametersToTry.Distinct().ToList();
            }

            RebarSpec bestSpec = null;
            int bestScore = int.MinValue;

            foreach (int tryDia in diametersToTry)
            {
                // V3.3: Calculate how many addon bars of this diameter we need
                double areaPerBar = GetBarArea(tryDia);
                double areaNeeded = reqArea - backboneArea;
                int minAddonBars = (int)Math.Ceiling(areaNeeded / areaPerBar);
                if (minAddonBars <= 0) continue;

                // V3.3: Check if mixed bars actually fit using CanFitMixedBars
                // Start from minimum addon bars and try to fit
                for (int addonBars = minAddonBars; addonBars <= minAddonBars + 4; addonBars++)
                {
                    // Check Layer 1 first
                    if (!CanFitMixedBars(ctx.BeamWidth, backboneCount, backboneDia, addonBars, tryDia, settings))
                    {
                        // Need to push to multiple layers - use strategy
                        break; // Let strategy handle multi-layer
                    }

                    // Single layer fit! Verify area is sufficient
                    double providedArea = backboneArea + addonBars * areaPerBar;
                    if (providedArea >= reqArea)
                    {
                        // Calculate score
                        int score = 1000;
                        score -= addonBars * 10;              // Fewer bars is better
                        score -= 0;                            // Single layer = 0 penalty
                        if (tryDia == backboneDia) score += 50;// Prefer same diameter
                        if (tryDia < backboneDia) score += 20; // Slightly prefer smaller addon (engineer rule)

                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestSpec = new RebarSpec
                            {
                                Diameter = tryDia,
                                Count = addonBars,
                                Layer = 1,
                                Position = isTop ? "Top" : "Bot",
                                LayerBreakdown = new List<int> { backboneCount + addonBars }
                            };
                        }
                        break; // Found fit for this diameter
                    }
                }
            }

            // If single-layer fit not found, fall back to strategy-based multi-layer
            if (bestSpec == null)
            {
                foreach (int tryDia in diametersToTry)
                {
                    // Use simpler capacity for strategy (worst case)
                    int capacity = GetMaxBarsPerLayer(ctx.BeamWidth, Math.Max(backboneDia, tryDia), settings);
                    if (backboneCount > capacity) continue;

                    var fillCtx = new FillingContext
                    {
                        RequiredArea = reqArea,
                        BackboneArea = backboneArea,
                        BackboneCount = backboneCount,
                        BackboneDiameter = tryDia,
                        LayerCapacity = capacity,
                        StirrupLegCount = ctx.StirrupLegCount,
                        MaxLayers = settings?.Beam?.MaxLayers ?? 2,
                        Settings = settings,
                        Constraints = ctx.ExternalConstraints
                    };

                    var resGreedy = _greedyStrategy.Calculate(fillCtx);
                    var resBalanced = _balancedStrategy.Calculate(fillCtx);

                    FillingResult best = null;

                    if (resGreedy.IsValid && !resBalanced.IsValid) best = resGreedy;
                    else if (!resGreedy.IsValid && resBalanced.IsValid) best = resBalanced;
                    else if (resGreedy.IsValid && resBalanced.IsValid)
                    {
                        if (resGreedy.LayerCounts.Count < resBalanced.LayerCounts.Count)
                            best = resGreedy;
                        else if (resBalanced.LayerCounts.Count < resGreedy.LayerCounts.Count)
                            best = resBalanced;
                        else
                            best = (resGreedy.TotalBars <= resBalanced.TotalBars) ? resGreedy : resBalanced;
                    }

                    if (best == null) continue;

                    int totalBars = best.LayerCounts.Sum();
                    int addBars = totalBars - backboneCount;
                    if (addBars <= 0) continue;

                    // V3.3: Verify mixed bars fit in Layer 1 with actual diameters
                    int layer1Addon = best.LayerCounts.Count > 0 ? Math.Max(0, best.LayerCounts[0] - backboneCount) : 0;
                    if (layer1Addon > 0 && !CanFitMixedBars(ctx.BeamWidth, backboneCount, backboneDia, layer1Addon, tryDia, settings))
                    {
                        continue; // This diameter combo doesn't actually fit
                    }

                    int score = 1000;
                    score -= addBars * 10;
                    score -= best.LayerCounts.Count * 50;
                    score -= best.WasteCount * 5;
                    if (tryDia == backboneDia) score += 30;
                    if (tryDia < backboneDia) score += 15; // Prefer smaller addon

                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestSpec = new RebarSpec
                        {
                            Diameter = tryDia,
                            Count = addBars,
                            Layer = best.LayerCounts.Count,
                            Position = isTop ? "Top" : "Bot",
                            LayerBreakdown = best.LayerCounts
                        };

                        ctx.AccumulatedWasteCount += best.WasteCount;
                    }
                }
            }

            return bestSpec;
        }

        private void AssignSpecToSolution(ContinuousBeamSolution sol, RebarSpec spec, string key)
        {
            if (spec != null && spec.Count > 0)
            {
                sol.Reinforcements[key] = spec;
            }
        }

        /// <summary>
        /// V3.3: Intelligent Bridging Logic.
        /// Nếu khe hở giữa 2 đoạn thép gia cường (Left và Right) < ngưỡng → Merge thành thanh chạy suốt.
        /// Uses TopSupportExtRatio from Curtailment settings (BeamCurtailment/GirderCurtailment).
        /// </summary>
        private static void ApplyBridgingLogic(ContinuousBeamSolution sol, BeamGroup group, DtsSettings settings)
        {
            if (sol?.Reinforcements == null || group?.Spans == null) return;

            // Detect Girder vs Beam
            bool isGirder = group.GroupName?.StartsWith("G") == true ||
                           group.GroupName?.Contains("Girder") == true;

            // Get correct curtailment settings
            var curtailment = isGirder
                ? settings?.Beam?.GirderCurtailment
                : settings?.Beam?.BeamCurtailment;

            // Get extension ratio from settings (không hardcode!)
            double extRatio = curtailment?.TopSupportExtRatio ?? 0.25;

            foreach (var span in group.Spans)
            {
                string leftKey = string.Format("{0}_Top_Left", span.SpanId);
                string rightKey = string.Format("{0}_Top_Right", span.SpanId);

                // Check if both left and right specs exist
                if (!sol.Reinforcements.ContainsKey(leftKey) ||
                    !sol.Reinforcements.ContainsKey(rightKey))
                    continue;

                var specLeft = sol.Reinforcements[leftKey];
                var specRight = sol.Reinforcements[rightKey];

                // Only merge if specs are similar
                if (!specLeft.IsSimilar(specRight)) continue;

                // V3.4: Layer-aware bridging - prevent merging bars from different layers
                if (specLeft.Layer != specRight.Layer) continue;

                // Calculate gap using curtailment ratios from settings
                double cutLenLeft = span.Length * extRatio;
                double cutLenRight = span.Length * extRatio;
                double gap = span.Length - cutLenLeft - cutLenRight;

                // Threshold: 1m or 40d (whichever is larger)
                double limit = Math.Max(1000, 40 * specLeft.Diameter);

                if (gap < limit)
                {
                    // MERGE: Remove Left/Right, create Full span spec
                    sol.Reinforcements.Remove(leftKey);
                    sol.Reinforcements.Remove(rightKey);

                    // Create merged spec with IsRunningThrough flag
                    string fullKey = string.Format("{0}_Top_Full", span.SpanId);
                    sol.Reinforcements[fullKey] = new RebarSpec
                    {
                        Diameter = specLeft.Diameter,
                        Count = specLeft.Count,
                        Position = "Top",
                        Layer = specLeft.Layer,
                        LayerBreakdown = specLeft.LayerBreakdown,
                        IsRunningThrough = true  // Mark as running through
                    };

                    // Log decision
                    if (string.IsNullOrEmpty(sol.Description))
                        sol.Description = "";
                    sol.Description += string.Format(" [Nối thông Top {0}]", span.SpanId);
                }
            }

            // Also check Bot bars for short spans
            foreach (var span in group.Spans)
            {
                string leftKey = string.Format("{0}_Bot_Left", span.SpanId);
                string rightKey = string.Format("{0}_Bot_Right", span.SpanId);

                if (!sol.Reinforcements.ContainsKey(leftKey) ||
                    !sol.Reinforcements.ContainsKey(rightKey))
                    continue;

                var specLeft = sol.Reinforcements[leftKey];
                var specRight = sol.Reinforcements[rightKey];

                if (!specLeft.IsSimilar(specRight)) continue;

                double cutLen = span.Length * extRatio;
                double gap = span.Length - 2 * cutLen;
                double limit = Math.Max(1000, 40 * specLeft.Diameter);

                if (gap < limit)
                {
                    sol.Reinforcements.Remove(leftKey);
                    sol.Reinforcements.Remove(rightKey);

                    string fullKey = string.Format("{0}_Bot_Full", span.SpanId);
                    sol.Reinforcements[fullKey] = new RebarSpec
                    {
                        Diameter = specLeft.Diameter,
                        Count = specLeft.Count,
                        Position = "Bot",
                        Layer = specLeft.Layer,
                        LayerBreakdown = specLeft.LayerBreakdown,
                        IsRunningThrough = true
                    };

                    sol.Description += string.Format(" [Nối thông Bot {0}]", span.SpanId);
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ═══════════════════════════════════════════════════════════════

        private static double GetBarArea(int diameter)
        {
            return Math.PI * diameter * diameter / 400.0; // cm²
        }

        private static double GetReqArea(BeamResultData res, bool isTop, int position, DtsSettings settings)
        {
            double[] arr = isTop ? res.TopArea : res.BotArea;
            if (arr == null || position >= arr.Length) return 0;
            double val = arr[position];
            return val > 0 ? val : 0;
        }

        /// <summary>
        /// V3.3: Check if mixed-diameter bars actually fit in a single layer.
        /// This accounts for different backbone and addon diameters.
        /// Returns true if the bars can physically fit with proper clear spacing.
        /// </summary>
        private static bool CanFitMixedBars(
            double width,
            int backboneCount, int backboneDia,
            int addonCount, int addonDia,
            DtsSettings settings)
        {
            if (backboneCount + addonCount <= 0) return true;

            double cover = settings?.Beam?.CoverSide ?? 25;
            double stirrup = settings?.Beam?.EstimatedStirrupDiameter ?? 10;
            int aggregateSize = settings?.Beam?.AggregateSize ?? 20;

            double usable = width - 2 * cover - 2 * stirrup;
            if (usable <= 0) return false;

            // Total bar width (backbone + addon)
            double totalBarWidth = backboneCount * backboneDia + addonCount * addonDia;

            // Minimum clear spacing - must accommodate both bar diameters
            // and aggregate (1.33 × aggregate size)
            int maxDia = Math.Max(backboneDia, addonDia);
            double minSpacingAggregate = 1.33 * aggregateSize;
            double minSpacingSettings = settings?.Beam?.MinClearSpacing ?? 25;
            double minSpacing = Math.Max(maxDia, Math.Max(minSpacingAggregate, minSpacingSettings));

            // UseBarDiameterForSpacing check
            if (settings?.Beam?.UseBarDiameterForSpacing == true)
            {
                double multiplier = settings?.Beam?.BarDiameterSpacingMultiplier ?? 1.0;
                minSpacing = Math.Max(minSpacing, maxDia * multiplier);
            }

            // Number of gaps = total bars - 1
            int totalBars = backboneCount + addonCount;
            double totalSpacingRequired = (totalBars - 1) * minSpacing;

            // Check: totalBarWidth + totalSpacing <= usable
            return (totalBarWidth + totalSpacingRequired) <= usable;
        }

        /// <summary>
        /// ENGINEER UPGRADE: Stricter capacity check based on real clear spacing.
        /// Considers: bar diameter, aggregate size, and TCVN/ACI standards.
        /// Formula: n*d + (n-1)*s <= usable width
        /// </summary>
        private static int GetMaxBarsPerLayer(double width, int dia, DtsSettings settings)
        {
            double cover = settings?.Beam?.CoverSide ?? 25;
            double stirrup = settings?.Beam?.EstimatedStirrupDiameter ?? 10;
            int aggregateSize = settings?.Beam?.AggregateSize ?? 20;

            // Minimum clear spacing theo tiêu chuẩn:
            // 1. >= bar diameter
            // 2. >= 1.33 × aggregate size (để cốt liệu lọt qua)
            // 3. >= MinClearSpacing from settings
            double minSpacingAggregate = 1.33 * aggregateSize;
            double minSpacingSettings = settings?.Beam?.MinClearSpacing ?? 25;
            double minSpacing = Math.Max(dia, Math.Max(minSpacingAggregate, minSpacingSettings));

            // Check if UseBarDiameterForSpacing is enabled
            if (settings?.Beam?.UseBarDiameterForSpacing == true)
            {
                double multiplier = settings?.Beam?.BarDiameterSpacingMultiplier ?? 1.0;
                minSpacing = Math.Max(minSpacing, dia * multiplier);
            }

            double usable = width - 2 * cover - 2 * stirrup;
            if (usable <= 0) return 0;

            // n(d+s) - s <= usable => n <= (usable + s) / (d + s)
            int maxBars = (int)Math.Floor((usable + minSpacing) / (dia + minSpacing));
            return Math.Max(0, maxBars);
        }

        /// <summary>
        /// V3.3: Get stirrup leg count using lookup table based on bar count.
        /// Falls back to width-based rules if EnableAdvancedRules = false.
        /// </summary>
        /// <param name="barCount">Total bars in Layer 1 (backbone + addon)</param>
        /// <param name="hasAddon">True if Layer 1 has addon bars (dense), False if backbone only (sparse)</param>
        private static int GetStirrupLegCount(int barCount, bool hasAddon, DtsSettings settings)
        {
            // Use new table lookup if enabled
            if (settings?.Stirrup?.EnableAdvancedRules == true)
            {
                return settings.Stirrup.GetLegCount(barCount, hasAddon);
            }

            // Fallback: Legacy width-based rules (kept for backward compatibility)
            return 2; // Default if no rules
        }

        /// <summary>
        /// V3.3: Legacy width-based leg count (fallback only).
        /// Kept for backward compatibility when EnableAdvancedRules = false.
        /// </summary>
        private static int GetStirrupLegCountByWidth(double width, DtsSettings settings)
        {
            string rules = settings?.Beam?.AutoLegsRules ?? "250-2 400-4 600-6";

            try
            {
                var parsedRules = rules.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(r =>
                    {
                        var parts = r.Split('-');
                        if (parts.Length == 2 && int.TryParse(parts[0], out int w) && int.TryParse(parts[1], out int l))
                            return new { Width = w, Legs = l };
                        return new { Width = 0, Legs = 2 };
                    })
                    .Where(r => r.Width > 0)
                    .OrderBy(r => r.Width)
                    .ToList();

                foreach (var rule in parsedRules)
                {
                    if (width <= rule.Width) return rule.Legs;
                }

                return parsedRules.LastOrDefault()?.Legs ?? 4;
            }
            catch
            {
                if (width < 300) return 2;
                if (width < 500) return 4;
                return 6;
            }
        }

        private static void CalculateSolutionMetrics(ContinuousBeamSolution sol, BeamGroup group, DtsSettings settings)
        {
            double totalLengthMM = group.Spans?.Sum(s => s.Length) ?? 6000;

            // --- 1. BACKBONE WEIGHT ---
            double wBackboneTop = Core.Utils.WeightCalculator.CalculateBackboneWeight(
                sol.BackboneDiameter, totalLengthMM, sol.BackboneCount_Top, 1.02);
            double wBackboneBot = Core.Utils.WeightCalculator.CalculateBackboneWeight(
                sol.BackboneDiameter, totalLengthMM, sol.BackboneCount_Bot, 1.02);
            double wBackbone = wBackboneTop + wBackboneBot;

            // --- 2. REINFORCEMENT WEIGHT ---
            // Detect Beam vs Girder based on GroupName convention (G = Girder, B = Beam)
            bool isGirder = group?.GroupName?.StartsWith("G") == true ||
                           group?.GroupName?.Contains("Girder") == true;

            // Get correct curtailment settings based on beam type
            CurtailmentConfig curtailment;
            if (isGirder)
                curtailment = settings?.Beam?.GirderCurtailment ?? new CurtailmentConfig { SupportReinfRatio = 0.33, MidSpanReinfRatio = 0.8 };
            else
                curtailment = settings?.Beam?.BeamCurtailment ?? new CurtailmentConfig { SupportReinfRatio = 0.33, MidSpanReinfRatio = 0.8 };

            // Fallback to global Curtailment if type-specific is null
            double supportRatio = curtailment?.SupportReinfRatio ?? settings?.Curtailment?.SupportReinfRatio ?? 0.33;
            double midSpanRatio = curtailment?.MidSpanReinfRatio ?? settings?.Curtailment?.MidSpanReinfRatio ?? 0.8;

            double wReinf = 0;
            foreach (var kvp in sol.Reinforcements)
            {
                var spec = kvp.Value;
                if (spec.Count <= 0) continue;

                var span = group.Spans?.FirstOrDefault(s => kvp.Key.StartsWith(s.SpanId));
                double spanLenMM = span?.Length ?? 5000;

                double barLenMM;
                if (kvp.Key.Contains("Left") || kvp.Key.Contains("Right"))
                    barLenMM = spanLenMM * supportRatio;
                else
                    barLenMM = spanLenMM * midSpanRatio;

                wReinf += Core.Utils.WeightCalculator.CalculateWeight(spec.Diameter, barLenMM, spec.Count);
            }

            // --- 3. TOTAL WEIGHT ---
            sol.TotalSteelWeight = wBackbone + wReinf;

            // --- 4. EFFICIENCY SCORE ---
            double effScore = 10000.0 / (sol.TotalSteelWeight + 1);
            if (sol.Reinforcements.Any(r => r.Value.Layer >= 2)) effScore *= 0.95;
            if (sol.BackboneCount_Top != sol.BackboneCount_Bot) effScore *= 0.98;
            sol.EfficiencyScore = effScore;

            // --- 5. DESCRIPTION ---
            sol.Description = sol.BackboneCount_Top == 2 ? "Tiết kiệm" :
                              sol.BackboneCount_Top == 3 ? "Cân bằng" :
                              sol.BackboneCount_Top == 4 ? "An toàn" : "";
        }
    }
}
