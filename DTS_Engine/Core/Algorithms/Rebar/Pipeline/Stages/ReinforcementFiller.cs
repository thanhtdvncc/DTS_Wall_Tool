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
                BackboneDiameter = ctx.TopBackboneDiameter,
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
            ctx.StirrupLegCount = GetStirrupLegCount(ctx.BeamWidth, settings);

            int numSpans = Math.Min(group.Spans?.Count ?? 0, results?.Count ?? 0);
            if (numSpans == 0) return ctx;

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
            // PHASE 3: METRICS CALCULATION
            // ==================================================================================
            CalculateSolutionMetrics(sol, group, settings);

            return ctx;
        }

        /// <summary>
        /// Helper to design a single location (Support or Midspan)
        /// trying both Greedy and Balanced, returning the best valid Spec.
        /// Returns null if CANNOT fit (dầm quá hẹp).
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

            // Prepare context for strategies
            int capacity = GetMaxBarsPerLayer(ctx.BeamWidth, backboneDia, ctx.Settings);
            if (backboneCount > capacity) return null; // Already exceeds capacity

            var fillCtx = new FillingContext
            {
                RequiredArea = reqArea,
                BackboneArea = backboneArea,
                BackboneCount = backboneCount,
                BackboneDiameter = backboneDia,
                LayerCapacity = capacity,
                StirrupLegCount = ctx.StirrupLegCount,
                MaxLayers = ctx.Settings?.Beam?.MaxLayers ?? 2,
                Settings = ctx.Settings,
                Constraints = ctx.ExternalConstraints
            };

            // Try Strategy 1: Greedy (ưu tiên ít lớp)
            var resGreedy = _greedyStrategy.Calculate(fillCtx);

            // Try Strategy 2: Balanced (ưu tiên đối xứng)
            var resBalanced = _balancedStrategy.Calculate(fillCtx);

            FillingResult best = null;

            // Decision Logic (Engineer Mindset):
            // 1. Validity is King.
            // 2. Prefer fewer layers (Greedy) unless congestion is high.
            // 3. Prefer Balanced for symmetry if layers are equal.

            if (resGreedy.IsValid && !resBalanced.IsValid) best = resGreedy;
            else if (!resGreedy.IsValid && resBalanced.IsValid) best = resBalanced;
            else if (resGreedy.IsValid && resBalanced.IsValid)
            {
                // Compare logic
                if (resGreedy.LayerCounts.Count < resBalanced.LayerCounts.Count)
                    best = resGreedy;
                else if (resBalanced.LayerCounts.Count < resGreedy.LayerCounts.Count)
                    best = resBalanced;
                else
                {
                    // Same layers, prefer fewer bars (less waste)
                    best = (resGreedy.TotalBars <= resBalanced.TotalBars) ? resGreedy : resBalanced;
                }
            }

            if (best == null) return null; // FAILED - Cannot fit

            // Accumulate waste for penalty scoring
            ctx.AccumulatedWasteCount += best.WasteCount;

            // Convert Result to Spec
            // LayerCounts includes Backbone. Extract ONLY additional bars.
            int totalBars = best.LayerCounts.Sum();
            int addBars = totalBars - backboneCount;

            if (addBars <= 0)
                return new RebarSpec { Count = 0, Diameter = backboneDia, Layer = 1 };

            return new RebarSpec
            {
                Diameter = backboneDia, // Keeping same diameter for standardization
                Count = addBars,
                Layer = best.LayerCounts.Count,
                Position = isTop ? "Top" : "Bot",
                LayerBreakdown = best.LayerCounts
            };
        }

        private void AssignSpecToSolution(ContinuousBeamSolution sol, RebarSpec spec, string key)
        {
            if (spec != null && spec.Count > 0)
            {
                sol.Reinforcements[key] = spec;
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
        /// ENGINEER UPGRADE: Stricter capacity check based on real clear spacing.
        /// Formula: n*d + (n-1)*s <= usable width
        /// </summary>
        private static int GetMaxBarsPerLayer(double width, int dia, DtsSettings settings)
        {
            double cover = settings?.Beam?.CoverSide ?? 25;
            double stirrup = settings?.Beam?.EstimatedStirrupDiameter ?? 10;
            double minSpacing = Math.Max(dia, settings?.Beam?.MinClearSpacing ?? 25);

            double usable = width - 2 * cover - 2 * stirrup;
            if (usable <= 0) return 0;

            // n(d+s) - s <= usable => n <= (usable + s) / (d + s)
            int maxBars = (int)Math.Floor((usable + minSpacing) / (dia + minSpacing));
            return Math.Max(0, maxBars);
        }

        private static int GetStirrupLegCount(double width, DtsSettings settings)
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
            double wBackboneTop = Utils.WeightCalculator.CalculateBackboneWeight(
                sol.BackboneDiameter, totalLengthMM, sol.BackboneCount_Top, 1.02);
            double wBackboneBot = Utils.WeightCalculator.CalculateBackboneWeight(
                sol.BackboneDiameter, totalLengthMM, sol.BackboneCount_Bot, 1.02);
            double wBackbone = wBackboneTop + wBackboneBot;

            // --- 2. REINFORCEMENT WEIGHT ---
            double supportRatio = settings?.Curtailment?.SupportReinfRatio ?? 0.33;
            double midSpanRatio = settings?.Curtailment?.MidSpanReinfRatio ?? 0.8;

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

                wReinf += Utils.WeightCalculator.CalculateWeight(spec.Diameter, barLenMM, spec.Count);
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
