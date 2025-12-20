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
    /// Sử dụng Greedy vs Balanced dual strategy.
    /// Context vào → Context với CurrentSolution đã có Reinforcements.
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

                var result = SolveDeterministicScenario(ctx);
                yield return result;
            }
        }

        /// <summary>
        /// Solve a specific backbone scenario deterministically.
        /// Visits every span and calculates local reinforcement.
        /// </summary>
        private SolutionContext SolveDeterministicScenario(SolutionContext ctx)
        {
            var group = ctx.Group;
            var results = ctx.SpanResults;
            var settings = ctx.Settings;

            int topDia = ctx.TopBackboneDiameter;
            int botDia = ctx.BotBackboneDiameter;
            int nTop = ctx.TopBackboneCount;
            int nBot = ctx.BotBackboneCount;

            // Initialize solution
            var sol = new ContinuousBeamSolution
            {
                OptionName = ctx.ScenarioId,
                BackboneDiameter = topDia,
                BackboneCount_Top = nTop,
                BackboneCount_Bot = nBot,
                As_Backbone_Top = nTop * GetBarArea(topDia),
                As_Backbone_Bot = nBot * GetBarArea(botDia),
                IsValid = true,
                Reinforcements = new Dictionary<string, RebarSpec>()
            };

            ctx.CurrentSolution = sol;

            int numSpans = Math.Min(group.Spans?.Count ?? 0, results?.Count ?? 0);
            int legCount = GetStirrupLegCount(ctx.BeamWidth, settings);
            ctx.StirrupLegCount = legCount;

            // ═══════════════════════════════════════════════════════════════
            // ITERATE EACH SPAN - JOINT-AWARE FILLING
            // HOTFIX: Sử dụng MAX envelope tại cột để thép gối liên tục
            // ═══════════════════════════════════════════════════════════════

            for (int i = 0; i < numSpans; i++)
            {
                var span = group.Spans[i];
                var res = results[i];
                if (res == null) continue;

                // ═══════════════════════════════════════════════════════════
                // A. TOP REINFORCEMENT - GỐI TRÁI (LEFT SUPPORT)
                // Logic: Tại cột, lấy MAX(Gối phải nhịp trước, Gối trái nhịp này)
                // ═══════════════════════════════════════════════════════════
                double reqTopL = GetReqArea(res, true, 0, settings);
                if (i > 0)
                {
                    // Envelope với gối phải nhịp trước (chung cột)
                    var prevRes = results[i - 1];
                    if (prevRes != null)
                    {
                        double reqPrevRight = GetReqArea(prevRes, true, 2, settings);
                        reqTopL = Math.Max(reqTopL, reqPrevRight);
                    }
                }

                if (!TryAutoFill(ctx, sol, reqTopL, topDia, nTop, legCount, string.Format("{0}_Top_Left", span.SpanId)))
                {
                    ctx.IsValid = false;
                    ctx.FailStage = StageName;
                    sol.IsValid = false;
                    sol.ValidationMessage = string.Format("Không đủ chỗ bố trí thép tại {0} Top Left (Req={1:F2} cm²)", span.SpanId, reqTopL);
                    return ctx;
                }

                // ═══════════════════════════════════════════════════════════
                // B. TOP REINFORCEMENT - GỐI PHẢI (RIGHT SUPPORT)
                // Tương tự, envelope với gối trái nhịp sau (nếu có)
                // ═══════════════════════════════════════════════════════════
                double reqTopR = GetReqArea(res, true, 2, settings);
                if (i < numSpans - 1)
                {
                    var nextRes = results[i + 1];
                    if (nextRes != null)
                    {
                        double reqNextLeft = GetReqArea(nextRes, true, 0, settings);
                        reqTopR = Math.Max(reqTopR, reqNextLeft);
                    }
                }

                if (!TryAutoFill(ctx, sol, reqTopR, topDia, nTop, legCount, string.Format("{0}_Top_Right", span.SpanId)))
                {
                    ctx.IsValid = false;
                    ctx.FailStage = StageName;
                    sol.IsValid = false;
                    sol.ValidationMessage = string.Format("Không đủ chỗ bố trí thép tại {0} Top Right", span.SpanId);
                    return ctx;
                }

                // ═══════════════════════════════════════════════════════════
                // C. TOP REINFORCEMENT - GIỮA NHỊP (nếu cần)
                // ═══════════════════════════════════════════════════════════
                double reqTopM = GetReqArea(res, true, 1, settings);
                if (reqTopM > sol.As_Backbone_Top * 1.05)
                {
                    if (!TryAutoFill(ctx, sol, reqTopM, topDia, nTop, legCount, string.Format("{0}_Top_Mid", span.SpanId)))
                    {
                        ctx.IsValid = false;
                        ctx.FailStage = StageName;
                        sol.IsValid = false;
                        sol.ValidationMessage = string.Format("Không đủ chỗ bố trí thép tại {0} Top Mid", span.SpanId);
                        return ctx;
                    }
                }

                // ═══════════════════════════════════════════════════════════
                // D. BOTTOM REINFORCEMENT - GIỮA NHỊP (chính yếu)
                // ═══════════════════════════════════════════════════════════
                double reqBotM = GetReqArea(res, false, 1, settings);
                if (!TryAutoFill(ctx, sol, reqBotM, botDia, nBot, legCount, string.Format("{0}_Bot_Mid", span.SpanId)))
                {
                    ctx.IsValid = false;
                    ctx.FailStage = StageName;
                    sol.IsValid = false;
                    sol.ValidationMessage = string.Format("Không đủ chỗ bố trí thép tại {0} Bot Mid (Req={1:F2} cm²)", span.SpanId, reqBotM);
                    return ctx;
                }

                // ═══════════════════════════════════════════════════════════
                // E. BOTTOM REINFORCEMENT - GỐI (nếu cần, với envelope)
                // ═══════════════════════════════════════════════════════════
                double reqBotL = GetReqArea(res, false, 0, settings);
                if (i > 0)
                {
                    var prevRes = results[i - 1];
                    if (prevRes != null)
                    {
                        double reqPrevRight = GetReqArea(prevRes, false, 2, settings);
                        reqBotL = Math.Max(reqBotL, reqPrevRight);
                    }
                }
                if (reqBotL > sol.As_Backbone_Bot * 1.05)
                {
                    if (!TryAutoFill(ctx, sol, reqBotL, botDia, nBot, legCount, string.Format("{0}_Bot_Left", span.SpanId)))
                    {
                        ctx.IsValid = false;
                        ctx.FailStage = StageName;
                        sol.IsValid = false;
                        return ctx;
                    }
                }

                double reqBotR = GetReqArea(res, false, 2, settings);
                if (i < numSpans - 1)
                {
                    var nextRes = results[i + 1];
                    if (nextRes != null)
                    {
                        double reqNextLeft = GetReqArea(nextRes, false, 0, settings);
                        reqBotR = Math.Max(reqBotR, reqNextLeft);
                    }
                }
                if (reqBotR > sol.As_Backbone_Bot * 1.05)
                {
                    if (!TryAutoFill(ctx, sol, reqBotR, botDia, nBot, legCount, string.Format("{0}_Bot_Right", span.SpanId)))
                    {
                        ctx.IsValid = false;
                        ctx.FailStage = StageName;
                        sol.IsValid = false;
                        return ctx;
                    }
                }
            }

            // CALCULATE WEIGHT & METRICS
            CalculateSolutionMetrics(sol, group, settings);

            return ctx;
        }

        /// <summary>
        /// Try to auto-fill reinforcement using dual strategy.
        /// </summary>
        private bool TryAutoFill(
            SolutionContext ctx,
            ContinuousBeamSolution sol,
            double reqArea, int backboneDia, int backboneCount,
            int legCount, string locationKey)
        {
            double backboneArea = backboneCount * GetBarArea(backboneDia);

            // Backbone đủ rồi
            if (backboneArea >= reqArea * 0.99) return true;

            int addDia = backboneDia;
            double addBarArea = GetBarArea(addDia);
            int totalBarsNeeded = (int)Math.Ceiling(reqArea / addBarArea);
            int capacity = GetMaxBarsPerLayer(ctx.BeamWidth, addDia, ctx.Settings);

            if (backboneCount > capacity) return false;

            // Create filling context
            var fillContext = new FillingContext
            {
                RequiredArea = reqArea,
                BackboneArea = backboneArea,
                BackboneCount = backboneCount,
                BackboneDiameter = backboneDia,
                LayerCapacity = capacity,
                StirrupLegCount = legCount,
                Settings = ctx.Settings,
                Constraints = ctx.ExternalConstraints
            };

            // DUAL STRATEGY: GREEDY vs BALANCED
            var planA = _greedyStrategy.Calculate(fillContext);
            var planB = _balancedStrategy.Calculate(fillContext);

            FillingResult bestPlan = null;

            if (planA.IsValid && !planB.IsValid) bestPlan = planA;
            else if (!planA.IsValid && planB.IsValid) bestPlan = planB;
            else if (planA.IsValid && planB.IsValid)
            {
                // Prefer fewer bars
                if (planB.TotalBars < planA.TotalBars) bestPlan = planB;
                else bestPlan = planA;
            }
            else return false;

            // ACCUMULATE WASTE COUNT for penalty scoring
            ctx.AccumulatedWasteCount += bestPlan.WasteCount;

            int addL1 = bestPlan.CountLayer1 - backboneCount;
            int addL2 = bestPlan.CountLayer2;

            // ═══════════════════════════════════════════════════════════════
            // HOTFIX: Clamp để đảm bảo không bao giờ ra số âm
            // Ngăn ngừa trường hợp strategy trả về sai giá trị
            // ═══════════════════════════════════════════════════════════════
            addL1 = Math.Max(0, addL1);
            addL2 = Math.Max(0, addL2);


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

        private static int GetMaxBarsPerLayer(double width, int dia, DtsSettings settings)
        {
            double cover = settings.Beam?.CoverSide ?? 25;
            double stirrup = settings.Beam?.EstimatedStirrupDiameter ?? 10;
            double minSpacing = settings.Beam?.MinClearSpacing ?? 25;

            double usable = width - 2 * cover - 2 * stirrup;
            double spacing = Math.Max(dia, minSpacing);

            if (usable <= 0) return 0;

            int maxBars = (int)Math.Floor((usable + spacing) / (dia + spacing));
            return Math.Max(0, maxBars);
        }

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
            double totalLengthMm = group.Spans?.Sum(s => s.Length) ?? 6000;
            double totalLengthM = totalLengthMm / 1000.0;

            // V2 Formula: As (cm²) * 0.785 (kg/m per cm²) * Length (m)
            // Note: 0.785 = π/4 * 1 cm² * 1 m = 78.5 cm³ = 0.785 kg (steel density 7850 kg/m³)
            double wBackbone = (sol.As_Backbone_Top + sol.As_Backbone_Bot) * 0.785 * totalLengthM;

            double wReinf = 0;
            int numSpans = group.Spans?.Count ?? 1;
            double avgSpanM = totalLengthM / numSpans;

            foreach (var kvp in sol.Reinforcements)
            {
                var spec = kvp.Value;
                if (spec.Count <= 0) continue;

                double barArea = System.Math.PI * spec.Diameter * spec.Diameter / 400.0; // cm²
                // Factor: Left/Right = 30%, Mid = 50%
                double factor = kvp.Key.Contains("Mid") ? 0.5 : 0.3;
                wReinf += spec.Count * barArea * 0.785 * (avgSpanM * factor);
            }

            sol.TotalSteelWeight = wBackbone + wReinf;

            // Efficiency score (V2 formula)
            double effScore = 10000.0 / (sol.TotalSteelWeight + 1);
            if (sol.Reinforcements.Any(r => r.Value.Layer >= 2)) effScore *= 0.95;
            if (sol.BackboneCount_Top != sol.BackboneCount_Bot) effScore *= 0.98;

            sol.EfficiencyScore = effScore;

            // Description
            sol.Description = sol.BackboneCount_Top == 2 ? "Tiết kiệm" :
                              sol.BackboneCount_Top == 3 ? "Cân bằng" :
                              sol.BackboneCount_Top == 4 ? "An toàn" : "";
        }
    }
}
