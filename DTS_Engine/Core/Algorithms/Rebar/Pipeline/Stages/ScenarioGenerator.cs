using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms.Rebar.Pipeline.Stages
{
    /// <summary>
    /// Stage 1: Sinh ra tất cả các kịch bản backbone.
    /// 1 context vào → N contexts ra (mỗi context = 1 kịch bản)
    /// 
    /// PERFORMANCE NOTE:
    /// - CalculateMinBarsForSpacing() filters out too-sparse scenarios
    /// - GetMaxBarsPerLayer() filters out too-dense scenarios
    /// - |nTop - nBot| > 2 check filters asymmetric backbone
    /// These filters run BEFORE yield return to minimize Pipeline load.
    /// </summary>
    public class ScenarioGenerator : IRebarPipelineStage
    {
        public string StageName { get { return "ScenarioGenerator"; } }
        public int Order { get { return 1; } }

        public IEnumerable<SolutionContext> Execute(
            IEnumerable<SolutionContext> inputs,
            ProjectConstraints globalConstraints)
        {
            // Lấy seed context đầu tiên
            var seed = inputs.FirstOrDefault();
            if (seed == null) yield break;

            var settings = seed.Settings;
            var external = seed.ExternalConstraints;

            // ═══════════════════════════════════════════════════════════════
            // STEP 1: GLOBAL GEOMETRY ANALYSIS (V3.5 Engineer Thinking)
            // Quét TOÀN BỘ các nhịp để tìm ràng buộc khắc nghiệt nhất:
            // - MinWidth: Quyết định maxBars (nhịp hẹp nhất = sức chứa giới hạn)
            // - MaxWidth: Quyết định minBars (nhịp rộng nhất = khoảng hở tối đa)
            // ═══════════════════════════════════════════════════════════════

            double minBeamWidth = double.MaxValue;
            double maxBeamWidth = double.MinValue;
            double beamHeight = seed.Group.Height;

            // Scan all spans from SpanResults
            var spansToCheck = seed.SpanResults?.Where(s => s != null).ToList() ?? new List<BeamResultData>();

            if (spansToCheck.Any())
            {
                foreach (var span in spansToCheck)
                {
                    double w = NormalizeWidth(span.Width);
                    if (w > 0)
                    {
                        if (w < minBeamWidth) minBeamWidth = w;
                        if (w > maxBeamWidth) maxBeamWidth = w;
                    }

                    // Also check height if available
                    if (beamHeight <= 0 && span.SectionHeight > 0)
                    {
                        beamHeight = NormalizeWidth(span.SectionHeight);
                    }
                }
            }

            // Fallback to Group dimensions if no valid spans
            if (minBeamWidth == double.MaxValue || maxBeamWidth == double.MinValue)
            {
                double groupWidth = NormalizeWidth(seed.Group.Width);
                if (groupWidth > 0)
                {
                    minBeamWidth = groupWidth;
                    maxBeamWidth = groupWidth;
                }
            }

            // Normalize height from Group if still missing
            if (beamHeight <= 0)
            {
                beamHeight = NormalizeWidth(seed.Group.Height);
            }

            // HARD FAIL: No valid dimensions
            if (minBeamWidth == double.MaxValue || minBeamWidth <= 0 || beamHeight <= 0)
            {
                var failContext = seed.Clone();
                failContext.IsValid = false;
                failContext.FailStage = StageName;
                yield return failContext;
                yield break;
            }

            // Store for downstream use (use minWidth for safety in capacity calculations)
            double beamWidth = minBeamWidth;

            // ═══════════════════════════════════════════════════════════════
            // STEP 2: PARSE ALLOWED DIAMETERS
            // ═══════════════════════════════════════════════════════════════

            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var allowedDias = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            if (settings.Beam?.PreferEvenDiameter == true)
                allowedDias = DiameterParser.FilterEvenDiameters(allowedDias);

            // Apply global override if any
            if (globalConstraints?.AllowedDiametersOverride != null && globalConstraints.AllowedDiametersOverride.Any())
                allowedDias = allowedDias.Intersect(globalConstraints.AllowedDiametersOverride).ToList();

            // Apply external force if any
            if (external?.ForcedBackboneDiameter.HasValue == true)
                allowedDias = new List<int> { external.ForcedBackboneDiameter.Value };

            allowedDias.Sort();

            if (!allowedDias.Any())
            {

                var failContext = seed.Clone();
                failContext.IsValid = false;
                failContext.FailStage = StageName;
                yield return failContext;
                yield break;
            }

            double maxSpacing = settings.Beam?.MaxClearSpacing ?? 300;


            int scenariosGenerated = 0;

            // ═══════════════════════════════════════════════════════════════
            // STEP 3: 4 NESTED LOOPS - GENERATE ALL SCENARIOS
            // ═══════════════════════════════════════════════════════════════

            foreach (int topDia in allowedDias)
            {
                // V3.5 CRITICAL FIX:
                // - minBars: Use WIDEST span (maxBeamWidth) to prevent too-sparse layout
                // - maxBars: Use NARROWEST span (minBeamWidth) to ensure bars fit
                int topMinBars = CalculateMinBarsForSpacing(maxBeamWidth, topDia, maxSpacing, settings);
                int topMaxBars = GetMaxBarsPerLayer(minBeamWidth, topDia, settings);

                // Early exit: this diameter cannot work across variable widths
                if (topMaxBars < topMinBars) continue;

                // Apply external force for Top count
                if (external?.ForcedBackboneCountTop.HasValue == true)
                {
                    topMinBars = topMaxBars = external.ForcedBackboneCountTop.Value;
                }

                foreach (int botDia in allowedDias)
                {
                    // Same global constraint logic for Bot
                    int botMinBars = CalculateMinBarsForSpacing(maxBeamWidth, botDia, maxSpacing, settings);
                    int botMaxBars = GetMaxBarsPerLayer(minBeamWidth, botDia, settings);

                    // Early exit: invalid diameter for this width
                    if (botMaxBars < botMinBars) continue;

                    if (external?.ForcedBackboneCountBot.HasValue == true)
                    {
                        botMinBars = botMaxBars = external.ForcedBackboneCountBot.Value;
                    }

                    int topStart = Math.Max(2, topMinBars);
                    int topEnd = Math.Min(topStart + 2, topMaxBars);

                    for (int nTop = topStart; nTop <= topEnd; nTop++)
                    {
                        int botStart = Math.Max(2, botMinBars);
                        int botEnd = Math.Min(botStart + 2, botMaxBars);

                        for (int nBot = botStart; nBot <= botEnd; nBot++)
                        {
                            // Early exit: too asymmetric
                            if (Math.Abs(nTop - nBot) > 2) continue;

                            // Clone seed và set scenario params
                            var scenario = seed.Clone();
                            scenario.ScenarioId = nTop == nBot && topDia == botDia
                                ? string.Format("{0}D{1}", nTop, topDia)
                                : string.Format("T:{0}D{1}/B:{2}D{3}", nTop, topDia, nBot, botDia);
                            scenario.TopBackboneDiameter = topDia;
                            scenario.BotBackboneDiameter = botDia;
                            scenario.TopBackboneCount = nTop;
                            scenario.BotBackboneCount = nBot;
                            scenario.BeamWidth = beamWidth;
                            scenario.BeamHeight = beamHeight;
                            scenario.AllowedDiameters = allowedDias;

                            // Apply PreferredDiameter bonus
                            if (globalConstraints != null && globalConstraints.PreferredMainDiameter.HasValue)
                            {
                                double bonus = globalConstraints.NeighborMatchBonus;
                                if (topDia == globalConstraints.PreferredMainDiameter.Value)
                                    scenario.PreferredDiameterBonus += bonus / 2;
                                if (botDia == globalConstraints.PreferredMainDiameter.Value)
                                    scenario.PreferredDiameterBonus += bonus / 2;
                            }

                            scenariosGenerated++;
                            yield return scenario;
                        }
                    }
                }
            }


        }

        // ═══════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ═══════════════════════════════════════════════════════════════

        /// <summary>
        /// Normalize dimension value to mm.
        /// Handles values in m, cm, or mm using heuristics.
        /// </summary>
        private static double NormalizeWidth(double val)
        {
            if (val <= 0) return 0;
            if (val < 5) return val * 1000; // m -> mm (e.g. 0.4m = 400mm)
            if (val < 100) return val * 10;  // cm -> mm (e.g. 40cm = 400mm)
            return val; // Already mm
        }

        /// <summary>
        /// Calculate minimum bars to prevent spacing > MaxSpacing (crack control).
        /// 
        /// HOTFIX: Thêm quy tắc Heuristic để dầm 400mm có tối thiểu 3 thanh:
        /// - Rule 1: Formula chuẩn theo MaxSpacing
        /// - Rule 2: Heuristic thực tế: mỗi 180mm bề rộng cần 1 thanh
        /// - Kết quả: MAX(2, max(rule1, rule2))
        /// </summary>
        private static int CalculateMinBarsForSpacing(double width, int dia, double maxSpacing, DtsSettings settings)
        {
            double cover = settings.Beam?.CoverSide ?? 25;
            double stirrup = settings.Beam?.EstimatedStirrupDiameter ?? 10;
            double usable = width - 2 * cover - 2 * stirrup;

            if (usable <= 0 || maxSpacing <= 0) return 2;

            // Rule 1: Tính theo công thức khoảng hở tiêu chuẩn
            int minByCode = (int)Math.Ceiling(usable / (maxSpacing + dia));

            // Rule 2: Heuristic thực tế - mỗi 180mm bề rộng dầm cần 1 thanh
            // VD: Dầm 400 -> 400/180 = 2.2 -> ceil = 3 thanh
            // VD: Dầm 300 -> 300/180 = 1.67 -> ceil = 2 thanh
            // HEURISTIC: Estimate min/max bars to reduce search space
            double densityDivisor = settings?.Beam?.DensityHeuristic ?? 180.0;
            int minByHeuristic = (int)Math.Ceiling(width / densityDivisor);

            // Lấy giá trị lớn hơn để đảm bảo an toàn & thẩm mỹ
            return Math.Max(2, Math.Max(minByCode, minByHeuristic));
        }

        /// <summary>
        /// Calculate max bars per layer based on spacing constraints.
        /// </summary>
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
    }
}
