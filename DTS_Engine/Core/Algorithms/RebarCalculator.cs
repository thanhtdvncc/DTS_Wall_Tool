using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Algorithms.Rebar.V4;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Unified Rebar Calculator - V4 is the SOLE ENGINE.
    /// No fallbacks, no legacy pipelines.
    /// 
    /// Architecture:
    /// - V4 Bottom-Up: O(N×M) complexity, supports N spans and N layers
    /// 
    /// ISO 25010: Performance Efficiency, Maintainability
    /// ISO 12207: Clean Architecture - Single Responsibility
    /// </summary>
    public class RebarCalculator
    {
        #region Singleton Calculator Instance

        private readonly V4RebarCalculator _v4Calculator;

        /// <summary>
        /// Create calculator with settings.
        /// </summary>
        public RebarCalculator() : this(DtsSettings.Instance)
        {
        }

        /// <summary>
        /// Create calculator with custom settings.
        /// </summary>
        public RebarCalculator(DtsSettings settings)
        {
            _v4Calculator = new V4RebarCalculator(settings ?? DtsSettings.Instance);
        }

        /// <summary>
        /// Calculate proposals for a BeamGroup.
        /// </summary>
        public List<ContinuousBeamSolution> Calculate(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ExternalConstraints externalConstraints = null)
        {
            var calculator = new V4RebarCalculator(settings);
            return calculator.Calculate(group, spanResults, externalConstraints);
        }

        #endregion

        #region Static Entry Points

        /// <summary>
        /// Static entry point for Continuous Beam Group.
        /// V4 is the SOLE ENGINE - no fallbacks.
        /// </summary>
        /// <param name="group">Beam group to calculate</param>
        /// <param name="spanResults">SAP analysis results per span</param>
        /// <param name="settings">User settings</param>
        /// <returns>Top N solutions ranked by TotalScore</returns>
        public static List<ContinuousBeamSolution> CalculateProposalsForGroup(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings)
        {
            return CalculateProposalsForGroup(group, spanResults, settings, null);
        }

        /// <summary>
        /// Static entry point with external constraints.
        /// V4 is the SOLE ENGINE - no fallbacks.
        /// </summary>
        public static List<ContinuousBeamSolution> CalculateProposalsForGroup(
            BeamGroup group,
            List<BeamResultData> spanResults,
            DtsSettings settings,
            ExternalConstraints externalConstraints)
        {
            // Initialize logging based on settings - DÙNG SETTING
            Rebar.Utils.RebarLogger.Initialize(settings);
            if (Rebar.Utils.RebarLogger.IsEnabled)
            {
                Rebar.Utils.RebarLogger.LogPhase($"CALCULATE GROUP: {group?.GroupName ?? "?"}");
                Rebar.Utils.RebarLogger.LogSettings(settings);
            }

            try
            {
                // V4 is the SOLE ENGINE
                Rebar.Utils.RebarLogger.LogPhase("V4 BOTTOM-UP CALCULATOR");

                var results = V4RebarCalculator.CalculateProposals(
                    group,
                    spanResults,
                    settings,
                    externalConstraints);

                if (results != null && results.Count > 0)
                {
                    // Log results
                    int validCount = results.Count(r => r.IsValid);
                    Rebar.Utils.RebarLogger.LogPhase($"V4 COMPLETE: {results.Count} solutions ({validCount} valid)");

                    // Log best solution summary
                    var best = results.FirstOrDefault(r => r.IsValid) ?? results.FirstOrDefault();
                    if (best != null)
                    {
                        Rebar.Utils.RebarLogger.LogSolutionSummary(best);
                    }

                    Rebar.Utils.RebarLogger.OpenLogFile();
                    return results;
                }
                else
                {
                    // Return error solution
                    Rebar.Utils.RebarLogger.LogError("V4 returned no solutions");
                    return new List<ContinuousBeamSolution>
                    {
                        CreateErrorSolution("Không tìm được phương án bố trí thép")
                    };
                }
            }
            catch (Exception ex)
            {
                Rebar.Utils.RebarLogger.LogError($"Exception: {ex.Message}\n{ex.StackTrace}");

                return new List<ContinuousBeamSolution>
                {
                    CreateErrorSolution($"Lỗi tính toán: {ex.Message}")
                };
            }
            finally
            {
                Rebar.Utils.RebarLogger.OpenLogFile();
            }
        }

        /// <summary>
        /// Tạo solution lỗi.
        /// </summary>
        private static ContinuousBeamSolution CreateErrorSolution(string message)
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

        #region Static Utility Methods

        /// <summary>
        /// Tính toán chọn thép đơn giản (cho một vị trí).
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

        /// <summary>
        /// Tính số thanh tối đa mỗi lớp.
        /// </summary>
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

        /// <summary>
        /// Get stirrup leg count using StirrupConfig.
        /// </summary>
        public static int GetAutoLegs(double beamWidthMm, DtsSettings settings)
        {
            if (settings?.Stirrup?.EnableAdvancedRules == true)
            {
                double density = settings.Beam?.DensityHeuristic ?? 180.0;
                int estimatedBars = Math.Max(2, (int)Math.Ceiling(beamWidthMm / density));

                int legs = settings.Stirrup.GetLegCount(estimatedBars, hasAddon: false);
                if (legs > 0) return legs;
            }

            // Width-based fallback
            if (beamWidthMm <= 250) return 2;
            if (beamWidthMm <= 400) return 3;
            if (beamWidthMm <= 600) return 4;
            return 5;
        }

        public class StirrupResult
        {
            public int Diameter { get; set; }
            public int Legs { get; set; }
            public int Spacing { get; set; }
            public bool IsDeficit { get; set; }

            public override string ToString()
            {
                return $"{Legs}-d{Diameter}a{Spacing}{(IsDeficit ? "*" : "")}";
            }
        }

        #endregion

        #region Stirrup Calculation

        /// <summary>
        /// Tính toán đai - Trả về chuỗi (legacy support)
        /// </summary>
        public static string CalculateStirrup(double shearArea, double ttArea, double beamWidthMm, DtsSettings settings, List<int> customSpacings = null)
        {
            var res = CalculateStirrupDetails(shearArea, ttArea, beamWidthMm, settings, customSpacings);
            return res?.ToString() ?? "-";
        }

        /// <summary>
        /// Tính toán đai chi tiết để phục vụ đồng bộ
        /// </summary>
        public static StirrupResult CalculateStirrupDetails(double shearArea, double ttArea, double beamWidthMm, DtsSettings settings, List<int> customSpacings = null, int? fixedDiameter = null)
        {
            double totalAreaPerLen = shearArea + 2 * ttArea;
            if (totalAreaPerLen <= 0.001) return null;

            var beamCfg = settings.Beam;
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 8, 10 };
            var diameters = DiameterParser.ParseRange(beamCfg?.StirrupBarRange ?? "8-10", inventory);
            if (diameters.Count == 0) diameters = new List<int> { 8, 10 };

            if (fixedDiameter.HasValue)
            {
                diameters = new List<int> { fixedDiameter.Value };
            }

            var spacings = customSpacings;
            if (spacings == null || spacings.Count == 0) spacings = beamCfg?.StirrupSpacings;
            if (spacings == null || spacings.Count == 0) spacings = new List<int> { 100, 120, 150, 200, 250 };
            spacings = spacings.OrderByDescending(x => x).ToList();

            int minSpacingAcceptable = 100;

            // Thứ tự ưu tiên: 
            // 1. Duyệt qua từng Đường kính (Diameter)
            // 2. Với mỗi đường kính, duyệt qua từng số Nhánh đai (Legs)
            // 3. Với mỗi nhánh đai, duyệt qua từng Bước đai (Spacing) từ Lớn đến Nhỏ
            foreach (int d in diameters.OrderBy(x => x))
            {
                int startLegs = GetAutoLegs(beamWidthMm, settings);
                var legOptions = new List<int>();
                for (int l = Math.Max(2, startLegs); l <= 12; l++)
                {
                    if (beamCfg?.AllowOddLegs == true || l % 2 == 0) legOptions.Add(l);
                }

                foreach (int legs in legOptions.OrderBy(x => x))
                {
                    foreach (int s in spacings)
                    {
                        if (s < minSpacingAcceptable) continue;

                        double as1Layer = (Math.PI * d * d / 400.0) * legs;
                        double cap = (as1Layer / s) * 1000.0; // mm2/m

                        if (cap >= totalAreaPerLen * 100.0)
                        {
                            return new StirrupResult { Diameter = d, Legs = legs, Spacing = s };
                        }
                    }
                }
            }

            // Fallback
            int dMax = diameters.Max();
            int lMax = (beamCfg?.AllowOddLegs == true) ? 12 : 12;
            int sMin = spacings.Min();
            return new StirrupResult { Diameter = dMax, Legs = lMax, Spacing = sMin, IsDeficit = true };
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

        #endregion

        #region Other Calculations

        /// <summary>
        /// Tính toán thép thành (web bars).
        /// </summary>
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

        /// <summary>
        /// Parse rebar string to area.
        /// </summary>
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

        /// <summary>
        /// Parse stirrup string to area per length.
        /// </summary>
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
