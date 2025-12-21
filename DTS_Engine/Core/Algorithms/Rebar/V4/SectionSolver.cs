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

            // Apply PreferEvenDiameter filter
            if (beamCfg.PreferEvenDiameter && _allowedDiameters.Any(d => d % 2 == 0))
            {
                _allowedDiameters = _allowedDiameters.Where(d => d % 2 == 0).ToList();
            }

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
            double reqArea = position == RebarPosition.Top ? section.ReqTop : section.ReqBot;

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

            // Filter valid and score
            var validResults = results
                .Where(a => a.TotalArea >= reqArea * 0.98) // Allow 2% tolerance
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
            foreach (var section in sections)
            {
                section.ValidArrangementsTop = Solve(section, RebarPosition.Top);
                section.ValidArrangementsBot = Solve(section, RebarPosition.Bot);
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

            // Sort diameters: prefer larger diameters first (fewer bars)
            var sortedDiameters = _settings.Beam?.PreferFewerBars == true
                ? _allowedDiameters.OrderByDescending(d => d).ToList()
                : _allowedDiameters.OrderBy(d => d).ToList();

            foreach (int dia in sortedDiameters)
            {
                double as1 = Math.PI * dia * dia / 400.0;

                // Calculate min bars needed
                int minBars = (int)Math.Ceiling(reqArea / as1);
                if (minBars < _minBarsPerLayer) minBars = _minBarsPerLayer;

                // Apply symmetric preference
                if (_settings.Beam?.PreferSymmetric == true && minBars % 2 != 0)
                {
                    minBars++;
                }

                // Calculate max bars per layer
                int maxPerLayer = CalculateMaxBarsPerLayer(usableWidth, dia);
                int maxLayersForHeight = CalculateMaxLayers(usableHeight, dia);
                int actualMaxLayers = Math.Min(_maxLayers, maxLayersForHeight);

                // Generate arrangements for this diameter
                int maxTotalBars = maxPerLayer * actualMaxLayers;
                
                for (int totalBars = minBars; totalBars <= Math.Min(maxTotalBars, minBars + 6); totalBars++)
                {
                    // Generate layer configurations
                    var configs = GenerateLayerConfigurations(totalBars, maxPerLayer, actualMaxLayers);

                    foreach (var config in configs)
                    {
                        // Validate all layers fit
                        bool allFit = config.All(n => CheckSpacing(usableWidth, n, dia));
                        if (!allFit) continue;

                        var arr = CreateArrangement(config, dia, usableWidth, reqArea);
                        if (arr != null)  // Fixed: was missing opening parenthesis
                        {
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
                    if (totalArea < reqArea * 0.98) continue;

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
            double available = usableWidth;

            // n bars: n * diameter + (n-1) * clearSpacing <= available
            // n * (diameter + clearSpacing) - clearSpacing <= available
            // n <= (available + clearSpacing) / (diameter + clearSpacing)

            double n = (available + minClear) / (diameter + minClear);
            int maxBars = (int)Math.Floor(n);

            return Math.Max(_minBarsPerLayer, Math.Min(maxBars, 10));
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

        private bool CheckSpacing(double usableWidth, int barsInLayer, int diameter)
        {
            if (barsInLayer <= 0) return true;
            if (barsInLayer == 1) return usableWidth >= diameter;

            double clearSpacing = CalculateClearSpacing(usableWidth, barsInLayer, diameter);
            double minRequired = GetMinClearSpacing(diameter);

            return clearSpacing >= minRequired;
        }

        private bool CanFitMixedBars(double usableWidth, int n1, int dia1, int n2, int dia2)
        {
            double totalBarWidth = n1 * dia1 + n2 * dia2;
            int totalBars = n1 + n2;
            double avgDia = totalBarWidth / totalBars;
            double minClear = GetMinClearSpacing((int)avgDia);

            double requiredWidth = totalBarWidth + (totalBars - 1) * minClear;
            return requiredWidth <= usableWidth;
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
                IsSymmetric = totalBars % 2 == 0,
                FitsStirrupLayout = true
            };

            // Calculate waste count (bars beyond requirement)
            int minBars = (int)Math.Ceiling(reqArea / as1);
            arr.WasteCount = Math.Max(0, totalBars - minBars);

            return arr;
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

        private double CalculateScore(SectionArrangement arr, double reqArea, DesignSection section)
        {
            double score = 100.0;

            // 1. Efficiency penalty (waste)
            double wasteRatio = arr.Efficiency > 1 ? (arr.Efficiency - 1.0) : 0;
            score -= wasteRatio * 30; // Up to 30 points for 100% waste

            // 2. Layer penalty
            score -= (arr.LayerCount - 1) * 5; // -5 per extra layer

            // 3. Bar count penalty
            if (arr.TotalCount > 6) score -= (arr.TotalCount - 6) * 2;

            // 4. Spacing bonus (more spacing = better)
            if (arr.ClearSpacing > _maxSpacing * 0.8)
            {
                score += 5;
            }

            // 5. Preference bonuses from settings
            if (_settings.Beam?.PreferSingleDiameter == true && arr.IsSingleDiameter)
            {
                score += 3;
            }

            if (_settings.Beam?.PreferSymmetric == true && arr.IsSymmetric)
            {
                score += 3;
            }

            if (_settings.Beam?.PreferFewerBars == true)
            {
                score += Math.Max(0, 6 - arr.TotalCount);
            }

            // 6. Preferred diameter bonus
            if (_settings.Beam?.PreferredDiameter > 0 && arr.PrimaryDiameter == _settings.Beam.PreferredDiameter)
            {
                score += 5;
            }

            // 7. Waste count penalty from Rules
            double wastePenalty = _settings.Rules?.WastePenaltyScore ?? 2.0;
            score -= arr.WasteCount * wastePenalty;

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
