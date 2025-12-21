using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Algorithms.Rebar.Pipeline;
using DTS_Engine.Core.Algorithms.Rebar.Pipeline.Stages;
using DTS_Engine.Core.Algorithms.Rebar.Rules;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// V3.0 Rebar Calculator - Unified Class.
    /// Includes pipeline-based calculation (Continuous Beam) and static utility methods.
    /// Merged from RebarCalculatorV3 (V3.4.2)
    /// </summary>
    public class RebarCalculator
    {
        #region V3 Pipeline Logic (Instance Based)

        private readonly RebarPipeline _pipeline;

        /// <summary>
        /// Create V3 calculator with default stages and rules.
        /// </summary>
        public RebarCalculator()
        {
            // Create stages in order
            var stages = new List<IRebarPipelineStage>
            {
                new ScenarioGenerator(),      // Stage 1: Generate backbone scenarios
                new ReinforcementFiller(),    // Stage 2: Fill reinforcement per span
                new StirrupCalculator(),      // Stage 3: Calculate stirrups from SAP2000 data
                new ConflictResolver()        // Stage 4: Check and report design conflicts
            };

            // Create rule engine with default rules
            var rules = new List<IDesignRule>
            {
                new PyramidRule(),            // Priority 1: Critical - L[n] <= L[n-1]
                new SymmetryRule(),           // Priority 5: Warning - Prefer even counts
                new PreferredDiameterRule(),  // Priority 10: Info - Diameter matching
                new VerticalAlignmentRule(),  // Priority 12: Warning - Top/Bot odd/even match
                new WastePenaltyRule()        // Priority 15: Warning - Penalize waste bars
            };

            var ruleEngine = new RuleEngine(rules);

            _pipeline = new RebarPipeline(stages, ruleEngine);
        }

        /// <summary>
        /// Create V3 calculator with custom pipeline.
        /// </summary>
        public RebarCalculator(RebarPipeline customPipeline)
        {
            _pipeline = customPipeline;
        }

        /// <summary>
        /// Calculate proposals for a BeamGroup.
        /// </summary>
        /// <param name="group">Beam group to calculate</param>
        /// <param name="spanResults">SAP analysis results per span</param>
        /// <param name="settings">User settings</param>
        /// <param name="projectConstraints">Optional project-level constraints</param>
        /// <returns>Top 5 solutions ranked by TotalScore</returns>
        public List<ContinuousBeamSolution> Calculate(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ProjectConstraints projectConstraints = null)
        {
            return _pipeline.Execute(group, spanResults, settings, projectConstraints ?? new ProjectConstraints(), null);
        }

        /// <summary>
        /// Calculate with external constraints (for locked beams or multi-beam sync).
        /// </summary>
        public List<ContinuousBeamSolution> CalculateWithConstraints(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ProjectConstraints projectConstraints,
            ExternalConstraints externalConstraints)
        {
            return _pipeline.Execute(group, spanResults, settings, projectConstraints ?? new ProjectConstraints(), externalConstraints);
        }

        /// <summary>
        /// Static entry point for Continuous Beam Group.
        /// Allows migrating legacy static calls to V3 pipeline.
        /// </summary>
        public static List<ContinuousBeamSolution> CalculateProposalsForGroup(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings)
        {
            // Always use V3 pipeline
            var calculator = new RebarCalculator();
            return calculator.Calculate(group, spanResults, settings);
        }

        #endregion

        #region Static Utility Methods (Legacy & Helpers)



        /// <summary>
        /// Tính toán chọn thép sử dụng DtsSettings mới (với range parsing).
        /// Ưu tiên sử dụng method này cho code mới.
        /// </summary>
        public static string Calculate(double areaReq, double b, double h, DtsSettings settings)
        {
            if (areaReq <= 0.01) return "-";

            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var diameters = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            if (diameters.Count == 0) diameters = inventory;
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
                int nMaxOneLayer = GetMaxBarsPerLayer(b, d, settings);

                string currentSol = "";
                if (nTotal <= nMaxOneLayer)
                {
                    if (nTotal < 2) nTotal = 2;
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
        /// UNIFIED ROUNDING for rebar area values across DTS_REBAR system.
        /// Rule: &lt;1 → 4 decimal places, ≥1 → 2 decimal places
        /// </summary>
        public static string FormatRebarValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return "-";
            if (value < 1)
                return value.ToString("F4");
            return value.ToString("F2");
        }

        /// <summary>
        /// Round rebar area value for XData storage (same logic as FormatRebarValue but returns double).
        /// </summary>
        public static double RoundRebarValue(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return 0;
            if (value < 1)
                return Math.Round(value, 4);
            return Math.Round(value, 2);
        }



        private static int GetMaxBarsPerLayer(double beamWidth, int barDiameter, DtsSettings settings)
        {
            double cover = settings.Beam?.CoverSide ?? 25;
            double stirrupDia = settings.Beam?.EstimatedStirrupDiameter ?? 0;
            if (stirrupDia <= 0)
            {
                var inventory = settings.General?.AvailableDiameters ?? new List<int> { 6, 8, 10 };
                var stirrups = DiameterParser.ParseRange(settings.Beam?.StirrupBarRange ?? "8-10", inventory);
                stirrupDia = stirrups.Any() ? stirrups.Max() : 10;
            }

            double usableWidth = beamWidth - (2 * cover) - (2 * stirrupDia);
            if (usableWidth < barDiameter) return 0;

            double minClearSpacing = settings.Beam?.MinClearSpacing ?? 30;
            double aggregateSpacing = (settings.Beam?.AggregateSize ?? 20) * 1.33;
            double reqClearance = Math.Max(barDiameter, Math.Max(minClearSpacing, aggregateSpacing));

            double val = (usableWidth + reqClearance) / (barDiameter + reqClearance);
            int maxBars = (int)Math.Floor(val);

            int minBars = settings.Beam?.MinBarsPerLayer ?? 2;
            return maxBars < minBars ? minBars : maxBars;
        }

        public static List<(int, int)> ParseAutoLegsRules(string rules)
        {
            var result = new List<(int, int)>();
            if (string.IsNullOrWhiteSpace(rules)) return result;
            var parts = rules.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var kv = part.Split('-');
                if (kv.Length == 2 && int.TryParse(kv[0], out int w) && int.TryParse(kv[1], out int l))
                    result.Add((w, l));
            }
            return result.OrderBy(x => x.Item1).ToList();
        }



        public static int GetAutoLegs(double beamWidthMm, DtsSettings settings)
        {
            var beamCfg = settings.Beam;
            if (beamCfg == null) return 2;
            var rules = ParseAutoLegsRules(beamCfg.AutoLegsRules);
            if (rules.Count == 0)
            {
                if (beamWidthMm <= 250) return 2;
                if (beamWidthMm <= 400) return 3;
                if (beamWidthMm <= 600) return 4;
                return 5;
            }
            foreach (var rule in rules)
            {
                if (beamWidthMm <= rule.Item1) return rule.Item2;
            }
            return rules.Last().Item2;
        }

        private static string TryFindSpacing(double totalAreaPerLen, int d, int legs, List<int> spacings, int minSpacingAcceptable)
        {
            double as1Layer = (Math.PI * d * d / 400.0) * legs;
            double maxSpacingReq = (as1Layer / totalAreaPerLen) * 10.0;
            foreach (var s in spacings.OrderByDescending(x => x))
            {
                if (s <= maxSpacingReq && s >= minSpacingAcceptable) return $"{legs}-d{d}a{s}";
            }
            return null;
        }



        public static string CalculateStirrup(double shearArea, double ttArea, double beamWidthMm, DtsSettings settings)
        {
            double totalAreaPerLen = shearArea + 2 * ttArea;
            if (totalAreaPerLen <= 0.001) return "-";
            var beamCfg = settings.Beam;
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 8, 10 };
            var diameters = DiameterParser.ParseRange(beamCfg?.StirrupBarRange ?? "8-10", inventory);
            if (diameters.Count == 0) diameters = new List<int> { 8, 10 };
            var spacings = beamCfg?.StirrupSpacings;
            if (spacings == null || spacings.Count == 0) spacings = new List<int> { 100, 150, 200, 250 };
            int minSpacingAcceptable = 100;
            int baseLegs = GetAutoLegs(beamWidthMm, settings);
            var legOptions = new List<int> { baseLegs };
            if (baseLegs - 1 >= 2) legOptions.Insert(0, baseLegs - 1);
            legOptions.Add(baseLegs + 1);
            legOptions.Add(baseLegs + 2);
            if (beamCfg?.AllowOddLegs != true) legOptions = legOptions.Where(l => l % 2 == 0).ToList();
            if (legOptions.Count == 0) legOptions = new List<int> { 2, 4 };
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
                if (reqArea > 0.01) nTorsion = (int)Math.Ceiling(reqArea / as1);
                int nConstructive = needConstructive ? 2 : 0;
                int nFinal = Math.Max(nTorsion, nConstructive);
                if (nFinal > 0 && nFinal % 2 != 0) nFinal++;
                if (nFinal > 0 && nFinal <= 6) return $"{nFinal}d{d}";
            }
            int dMax = diameters.Max();
            double asMax = Math.PI * dMax * dMax / 400.0;
            int nMax = reqArea > 0.01 ? (int)Math.Ceiling(reqArea / asMax) : (needConstructive ? 2 : 0);
            if (nMax % 2 != 0) nMax++;
            if (nMax == 0) return "-";
            return $"{nMax}d{dMax}";
        }

        public static double ParseRebarArea(string rebarStr)
        {
            if (string.IsNullOrEmpty(rebarStr) || rebarStr == "-") return 0;
            double total = 0;
            var parts = rebarStr.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                var s = part.Trim();
                var match = System.Text.RegularExpressions.Regex.Match(s, @"(\d+)[dD](\d+)");
                if (match.Success)
                {
                    int n = int.Parse(match.Groups[1].Value);
                    int d = int.Parse(match.Groups[2].Value);
                    double as1 = Math.PI * d * d / 400.0;
                    total += n * as1;
                }
            }
            return total;
        }

        public static double ParseStirrupAreaPerLen(string stirrupStr, int defaultLegs = 2)
        {
            if (string.IsNullOrEmpty(stirrupStr) || stirrupStr == "-") return 0;
            var match = System.Text.RegularExpressions.Regex.Match(stirrupStr, @"(?:(\d+)-)?[dD](\d+)[aA](\d+)");
            if (match.Success)
            {
                int nLegs = defaultLegs;
                if (match.Groups[1].Success && int.TryParse(match.Groups[1].Value, out int n)) nLegs = n;
                int d = int.Parse(match.Groups[2].Value);
                int spacing = int.Parse(match.Groups[3].Value);
                if (spacing <= 0) return 0;
                double as1 = Math.PI * d * d / 400.0;
                double areaPerLen = (nLegs * as1) / (spacing / 10.0);
                return areaPerLen;
            }
            return 0;
        }

        #endregion
    }
}
