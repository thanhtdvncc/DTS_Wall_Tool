using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Module tính điểm thi công (Constructability Score).
    /// Score về thang 0-100 (càng cao càng dễ thi công).
    /// </summary>
    public static class ConstructabilityScoring
    {
        public sealed class ScoreWeights
        {
            public double W_Cuts { get; set; } = 0.35;
            public double W_Diversity { get; set; } = 0.30;
            public double W_Spacing { get; set; } = 0.20;
            public double W_Layering { get; set; } = 0.15;

            public static ScoreWeights Default => new ScoreWeights();

            public static ScoreWeights Economical => new ScoreWeights
            {
                W_Cuts = 0.2,
                W_Diversity = 0.2,
                W_Spacing = 0.1,
                W_Layering = 0.5
            };

            public static ScoreWeights FastConstruction => new ScoreWeights
            {
                W_Cuts = 0.5,
                W_Diversity = 0.3,
                W_Spacing = 0.15,
                W_Layering = 0.05
            };
        }

        /// <summary>
        /// Tính Constructability Score có ngữ cảnh BeamGroup (khuyến nghị).
        /// - Splice/Cuts: ưu tiên đọc splice thật từ group.TopBarSegments/BotBarSegments.
        /// - Spacing: dùng bề rộng thật từ group/spans.
        /// </summary>
        public static double CalculateScore(
            ContinuousBeamSolution solution,
            BeamGroup group,
            DtsSettings settings,
            ScoreWeights weights = null)
        {
            if (solution == null || !solution.IsValid) return 0;
            if (settings?.Beam == null) return 0;

            weights = weights ?? ScoreWeights.Default;

            double groupTotalLengthMm = ResolveGroupTotalLengthMm(group);
            double beamWidthMm = ResolveBeamWidthMm(group);

            double cutsScore = CalculateCutsScore(solution, group, settings, groupTotalLengthMm);
            double diversityScore = CalculateDiversityScore(solution);
            double spacingScore = CalculateSpacingScore(solution, settings, beamWidthMm);
            double layeringScore = CalculateLayeringScore(solution);

            double total =
                weights.W_Cuts * cutsScore +
                weights.W_Diversity * diversityScore +
                weights.W_Spacing * spacingScore +
                weights.W_Layering * layeringScore;

            return Math.Max(0, Math.Min(100, total * 100));
        }

        /// <summary>
        /// Overload cho các callsite không có BeamGroup.
        /// </summary>
        public static double CalculateScore(
            ContinuousBeamSolution solution,
            DtsSettings settings,
            double beamWidthMm,
            double groupTotalLengthMm,
            ScoreWeights weights = null)
        {
            if (solution == null || !solution.IsValid) return 0;
            if (settings?.Beam == null) return 0;

            weights = weights ?? ScoreWeights.Default;

            double cutsScore = CalculateCutsScore(solution, group: null, settings: settings, groupTotalLengthMm: groupTotalLengthMm);
            double diversityScore = CalculateDiversityScore(solution);
            double spacingScore = CalculateSpacingScore(solution, settings, beamWidthMm);
            double layeringScore = CalculateLayeringScore(solution);

            double total =
                weights.W_Cuts * cutsScore +
                weights.W_Diversity * diversityScore +
                weights.W_Spacing * spacingScore +
                weights.W_Layering * layeringScore;

            return Math.Max(0, Math.Min(100, total * 100));
        }

        private static double CalculateCutsScore(ContinuousBeamSolution sol, BeamGroup group, DtsSettings settings, double groupTotalLengthMm)
        {
            int totalBars = GetTotalBars(sol);
            if (totalBars <= 0) return 0;

            // 1) Prefer real splice flags from populated segments (only when they belong to this solution)
            int spliceCount = TryCountSplicesFromSegments(group, sol);
            if (spliceCount >= 0)
            {
                double ratio = Math.Min(1.0, spliceCount / (double)totalBars);
                return Math.Max(0.0, 1.0 - ratio);
            }

            // 2) If we have BeamGroup context, generate segments in-memory for this solution
            spliceCount = TryComputeSpliceCountFromAlgorithm(group, sol, settings, groupTotalLengthMm);
            if (spliceCount >= 0)
            {
                double ratio = Math.Min(1.0, spliceCount / (double)totalBars);
                return Math.Max(0.0, 1.0 - ratio);
            }

            // 3) Fallback: estimate by standard bar length from settings
            double standardLenMm = ResolveStandardBarLengthMm(settings);
            if (standardLenMm <= 0) return 0;
            if (groupTotalLengthMm <= 0) return 0;

            int splicesPerContinuousBar = (int)Math.Ceiling(groupTotalLengthMm / standardLenMm) - 1;
            splicesPerContinuousBar = Math.Max(0, splicesPerContinuousBar);

            // Approx: each continuous backbone bar has the same splice count
            int estimatedSplices = splicesPerContinuousBar * (Math.Max(0, sol.BackboneCount_Top) + Math.Max(0, sol.BackboneCount_Bot));
            double cutsRatio = Math.Min(1.0, estimatedSplices / (double)totalBars);
            return Math.Max(0.0, 1.0 - cutsRatio);
        }

        private static double CalculateDiversityScore(ContinuousBeamSolution sol)
        {
            var uniqueDiameters = new HashSet<int>();
            if (sol.BackboneDiameter > 0) uniqueDiameters.Add(sol.BackboneDiameter);

            if (sol.Reinforcements != null)
            {
                foreach (var spec in sol.Reinforcements.Values)
                {
                    if (spec?.Diameter > 0) uniqueDiameters.Add(spec.Diameter);
                }
            }

            int n = Math.Max(1, uniqueDiameters.Count);
            return 1.0 / n;
        }

        private static double CalculateSpacingScore(ContinuousBeamSolution sol, DtsSettings settings, double beamWidthMm)
        {
            if (beamWidthMm <= 0) return 0;

            double cover = settings.Beam.CoverSide;
            double stirrupDia = settings.Beam.EstimatedStirrupDiameter;

            double usableWidth = beamWidthMm - 2 * cover - 2 * stirrupDia;
            if (usableWidth <= 0) return 0;

            double scoreTop = ComputeLayerSpacingScore(usableWidth, sol.BackboneCount_Top, sol.BackboneDiameter, settings);
            double scoreBot = ComputeLayerSpacingScore(usableWidth, sol.BackboneCount_Bot, sol.BackboneDiameter, settings);

            return Math.Max(0.0, Math.Min(scoreTop, scoreBot));
        }

        private static double ComputeLayerSpacingScore(double usableWidthMm, int nBars, int diaMm, DtsSettings settings)
        {
            if (nBars <= 1 || diaMm <= 0) return 1.0;

            double totalDia = nBars * diaMm;
            double remaining = usableWidthMm - totalDia;
            if (remaining <= 0) return 0;

            double actualSpacing = remaining / (nBars - 1);
            double requiredClear = ResolveRequiredClearSpacingMm(diaMm, settings);
            if (requiredClear <= 0) return 0;

            double ratio = actualSpacing / requiredClear;
            if (ratio <= 0) return 0;

            // Strong penalty when spacing is below required
            if (ratio < 1.0)
                return Math.Max(0, ratio * ratio);

            // Above required: full score
            return 1.0;
        }

        private static double CalculateLayeringScore(ContinuousBeamSolution sol)
        {
            int maxLayers = 1;
            if (sol.Reinforcements != null && sol.Reinforcements.Values.Any())
            {
                maxLayers = sol.Reinforcements.Values.Max(r => r?.Layer ?? 1);
                if (maxLayers < 1) maxLayers = 1;
            }

            switch (maxLayers)
            {
                case 1: return 1.0;
                case 2: return 0.8;
                case 3: return 0.5;
                default: return 0.3;
            }
        }

        public static int EstimateTotalSplices(double groupTotalLengthMm, double standardBarLengthMm)
        {
            if (groupTotalLengthMm <= 0 || standardBarLengthMm <= 0) return 0;

            int splicesPerBar = (int)Math.Ceiling(groupTotalLengthMm / standardBarLengthMm) - 1;
            return Math.Max(0, splicesPerBar);
        }

        public static string GenerateReport(
            ContinuousBeamSolution sol,
            DtsSettings settings,
            double beamWidthMm,
            double groupTotalLengthMm,
            ScoreWeights weights = null)
        {
            weights = weights ?? ScoreWeights.Default;

            double cutsScore = CalculateCutsScore(sol, group: null, settings: settings, groupTotalLengthMm: groupTotalLengthMm);
            double diversityScore = CalculateDiversityScore(sol);
            double spacingScore = CalculateSpacingScore(sol, settings, beamWidthMm);
            double layeringScore = CalculateLayeringScore(sol);
            double totalScore = CalculateScore(sol, settings, beamWidthMm, groupTotalLengthMm, weights);

            return $@"
=== CONSTRUCTABILITY REPORT ===
Total Score: {totalScore:F1}/100

Component Scores:
  1. Cuts/Splices   ({weights.W_Cuts * 100:F0}%): {cutsScore * 100:F1}/100
  2. Diversity      ({weights.W_Diversity * 100:F0}%): {diversityScore * 100:F1}/100
  3. Spacing        ({weights.W_Spacing * 100:F0}%): {spacingScore * 100:F1}/100
  4. Layering       ({weights.W_Layering * 100:F0}%): {layeringScore * 100:F1}/100
";
        }

        private static int GetTotalBars(ContinuousBeamSolution sol)
        {
            int totalBars = Math.Max(0, sol.BackboneCount_Top) + Math.Max(0, sol.BackboneCount_Bot);

            if (sol.Reinforcements != null)
            {
                totalBars += sol.Reinforcements.Values.Where(r => r != null).Sum(r => Math.Max(0, r.Count));
            }

            return totalBars;
        }

        private static double ResolveStandardBarLengthMm(DtsSettings settings)
        {
            // Prefer detailing (more general), fallback to beam standard bar length
            double len = settings?.Detailing?.MaxBarLength ?? 0;
            if (len > 0) return len;
            return settings?.Beam?.StandardBarLength ?? 0;
        }

        private static double ResolveRequiredClearSpacingMm(int diaMm, DtsSettings settings)
        {
            if (settings?.Beam == null) return 0;

            double required = settings.Beam.MinClearSpacing;

            if (settings.Beam.UseBarDiameterForSpacing)
            {
                required = Math.Max(required, settings.Beam.BarDiameterSpacingMultiplier * diaMm);
            }

            required = Math.Max(required, 1.33 * settings.Beam.AggregateSize);
            return required;
        }

        private static double ResolveBeamWidthMm(BeamGroup group)
        {
            if (group == null) return 0;
            if (group.Width > 0) return group.Width;

            if (group.Spans != null)
            {
                var widths = group.Spans.Select(s => s.Width).Where(w => w > 0).ToList();
                if (widths.Count > 0) return widths.Average();
            }

            return 0;
        }

        private static double ResolveGroupTotalLengthMm(BeamGroup group)
        {
            if (group == null) return 0;

            if (group.TotalLength > 0)
            {
                return group.TotalLength * 1000.0;
            }

            if (group.Spans != null && group.Spans.Count > 0)
            {
                // SpanData.Length in this repo is typically mm for some algorithms; if it's already meters, this will be wrong,
                // but we prefer using group.TotalLength when available.
                double sum = group.Spans.Sum(s => s.Length);
                return sum;
            }

            return 0;
        }

        private static int TryCountSplicesFromSegments(BeamGroup group, ContinuousBeamSolution sol)
        {
            if (group == null || sol == null) return -1;

            // Only trust segments if they belong to this exact selected design (avoid scoring proposals by stale segments)
            if (group.SelectedDesign == null) return -1;
            if (!ReferenceEquals(group.SelectedDesign, sol) && !string.Equals(group.SelectedDesign.OptionName, sol.OptionName, StringComparison.OrdinalIgnoreCase))
                return -1;

            bool hasAny =
                (group.TopBarSegments != null && group.TopBarSegments.Count > 0) ||
                (group.BotBarSegments != null && group.BotBarSegments.Count > 0);

            if (!hasAny) return -1;

            int count = 0;

            if (group.TopBarSegments != null)
            {
                foreach (var seg in group.TopBarSegments)
                {
                    if (seg.SpliceAtEnd) count++;
                }
            }

            if (group.BotBarSegments != null)
            {
                foreach (var seg in group.BotBarSegments)
                {
                    if (seg.SpliceAtEnd) count++;
                }
            }

            return Math.Max(0, count);
        }

        private static int TryComputeSpliceCountFromAlgorithm(BeamGroup group, ContinuousBeamSolution sol, DtsSettings settings, double groupTotalLengthMm)
        {
            try
            {
                if (group == null || sol == null || settings == null) return -1;
                if (group.Spans == null || group.Spans.Count == 0) return -1;
            // Prefer span-derived total length; groupTotalLengthMm can be stale.

                int barDiameter = sol.BackboneDiameter;
                if (barDiameter <= 0) return -1;

                var spanInfos = new List<SpanInfo>();
                double cumPosMm = 0;
                foreach (var span in group.Spans)
                {
                    // In this repo, span.Length is stored in meters in BeamGroupDetector pipeline.
                    double spanLenMm = span.Length * 1000.0;
                    if (spanLenMm <= 0) continue;

                    spanInfos.Add(new SpanInfo
                    {
                        SpanId = span.SpanId,
                        Length = spanLenMm,
                        StartPos = cumPosMm
                    });
                    cumPosMm += spanLenMm;
                }
                if (spanInfos.Count == 0) return -1;

                double totalLengthMm = cumPosMm;
                if (totalLengthMm <= 0) return -1;

                string groupType = group.GroupType?.ToUpperInvariant() ?? "BEAM";
                string startSupportType = SupportTypeToString(group.Supports?.FirstOrDefault()?.Type ?? SupportType.FreeEnd);
                string endSupportType = SupportTypeToString(group.Supports?.LastOrDefault()?.Type ?? SupportType.FreeEnd);

                string concreteGrade = !string.IsNullOrWhiteSpace(group.ConcreteGrade)
                    ? group.ConcreteGrade
                    : (settings.Anchorage?.ConcreteGrades?.FirstOrDefault() ?? settings.General?.ConcreteGradeName);
                string steelGrade = !string.IsNullOrWhiteSpace(group.SteelGrade)
                    ? group.SteelGrade
                    : (settings.Anchorage?.SteelGrades?.FirstOrDefault() ?? settings.General?.SteelGradeName);

                int barsPerLayerTop = sol.BackboneCount_Top > 0 ? sol.BackboneCount_Top : 2;
                int barsPerLayerBot = sol.BackboneCount_Bot > 0 ? sol.BackboneCount_Bot : 2;

                var algorithm = new RebarCuttingAlgorithm(settings);

                var top = algorithm.ProcessComplete(
                    totalLengthMm,
                    spanInfos,
                    isTopBar: true,
                    groupType: groupType,
                    startSupportType: startSupportType,
                    endSupportType: endSupportType,
                    barDiameter: barDiameter,
                    barsPerLayer: barsPerLayerTop,
                    concreteGrade: concreteGrade,
                    steelGrade: steelGrade);

                var bot = algorithm.ProcessComplete(
                    totalLengthMm,
                    spanInfos,
                    isTopBar: false,
                    groupType: groupType,
                    startSupportType: startSupportType,
                    endSupportType: endSupportType,
                    barDiameter: barDiameter,
                    barsPerLayer: barsPerLayerBot,
                    concreteGrade: concreteGrade,
                    steelGrade: steelGrade);

                int spliceCount = (top?.SpliceCount ?? 0) + (bot?.SpliceCount ?? 0);
                return Math.Max(0, spliceCount);
            }
            catch
            {
                return -1;
            }
        }

        private static string SupportTypeToString(SupportType type)
        {
            switch (type)
            {
                case SupportType.Column: return "COLUMN";
                case SupportType.Wall: return "WALL";
                case SupportType.Beam: return "BEAM";
                default: return "FREEEND";
            }
        }
    }
}
