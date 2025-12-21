using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// GlobalOptimizer: Tìm Backbone tối ưu và xây dựng Solution hoàn chỉnh.
    /// CRITICAL FIX: Đảm bảo Addon được tính cho mọi mặt cắt thiếu thép.
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
        public double AreaTolerance { get; set; } = 0.02; // 2%

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
            _maxBarsPerSide = 8;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Tìm top N phương án tối ưu.
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
                // 1. Generate backbone candidates
                var candidates = GenerateBackboneCandidates(sections, externalConstraints);
                Utils.RebarLogger.Log($"Generated {candidates.Count} backbone candidates");

                if (candidates.Count == 0)
                {
                    return new List<ContinuousBeamSolution> { CreateErrorSolution("No valid backbone candidates") };
                }

                // 2. Evaluate candidates
                candidates = EvaluateCandidates(candidates, sections, group);

                // 3. Filter valid and sort by score
                var validCandidates = candidates
                    .Where(c => c.IsGloballyValid)
                    .OrderByDescending(c => c.TotalScore)
                    .Take(MaxBackboneCandidates)
                    .ToList();

                if (validCandidates.Count == 0)
                {
                    // Try relaxed approach
                    var relaxed = FindRelaxedBackbone(candidates, sections);
                    if (relaxed != null)
                    {
                        validCandidates.Add(relaxed);
                    }
                    else
                    {
                        return new List<ContinuousBeamSolution> { CreateErrorSolution("No valid backbone found") };
                    }
                }

                // 4. Build solutions
                var solutions = new List<ContinuousBeamSolution>();
                foreach (var candidate in validCandidates.Take(MaxSolutions))
                {
                    var solution = BuildSolution(candidate, sections, group);
                    if (solution != null)
                    {
                        solutions.Add(solution);
                    }
                }

                // 5. Final validation and sorting
                foreach (var sol in solutions)
                {
                    ValidateSolution(sol, sections, null);
                }

                return solutions.OrderByDescending(s => s.TotalScore).ToList();
            }
            catch (Exception ex)
            {
                Utils.RebarLogger.LogError($"GlobalOptimizer error: {ex.Message}");
                return new List<ContinuousBeamSolution> { CreateErrorSolution($"Optimization failed: {ex.Message}") };
            }
        }

        #endregion

        #region Backbone Generation

        private List<BackboneCandidate> GenerateBackboneCandidates(
            List<DesignSection> sections,
            ExternalConstraints constraints)
        {
            var candidates = new List<BackboneCandidate>();

            // Get minimum usable width across all sections
            double minUsableWidth = sections.Min(s => s.UsableWidth);
            if (minUsableWidth <= 0) minUsableWidth = 200;

            var diameters = GetDiametersToTry(constraints);

            foreach (int dia in diameters)
            {
                int maxBars = CalculateMaxBarsForWidth(minUsableWidth, dia);

                for (int nTop = _minBarsPerSide; nTop <= maxBars; nTop++)
                {
                    for (int nBot = _minBarsPerSide; nBot <= maxBars; nBot++)
                    {
                        if (!IsValidCombination(nTop, nBot)) continue;

                        candidates.Add(new BackboneCandidate
                        {
                            Diameter = dia,
                            CountTop = nTop,
                            CountBot = nBot,
                            IsGloballyValid = false,
                            FailedSections = new List<string>()
                        });
                    }
                }
            }

            return candidates;
        }

        private List<int> GetDiametersToTry(ExternalConstraints constraints)
        {
            if (constraints?.ForcedBackboneDiameter.HasValue == true)
            {
                return new List<int> { constraints.ForcedBackboneDiameter.Value };
            }

            // Prefer larger diameters (fewer bars)
            return _allowedDiameters.OrderByDescending(d => d).ToList();
        }

        private int CalculateMaxBarsForWidth(double usableWidth, int diameter)
        {
            double minClear = Math.Max(diameter, _settings.Beam?.MinClearSpacing ?? 30);
            double n = (usableWidth + minClear) / (diameter + minClear);
            return Math.Max(_minBarsPerSide, Math.Min((int)Math.Floor(n), _maxBarsPerSide));
        }

        private bool IsValidCombination(int nTop, int nBot)
        {
            // Both must meet minimum
            if (nTop < _minBarsPerSide || nBot < _minBarsPerSide) return false;

            // Prefer symmetric if configured
            if (_settings.Beam?.PreferSymmetric == true)
            {
                if (nTop % 2 != 0 || nBot % 2 != 0) return false;
            }

            return true;
        }

        #endregion

        #region Candidate Evaluation

        private List<BackboneCandidate> EvaluateCandidates(
            List<BackboneCandidate> candidates,
            List<DesignSection> sections,
            BeamGroup group)
        {
            foreach (var candidate in candidates)
            {
                var (isValid, fitCount, failedSections) = EvaluateSingleCandidate(candidate, sections);
                candidate.IsGloballyValid = isValid;
                candidate.FitCount = fitCount;
                candidate.FailedSections = failedSections;

                if (isValid)
                {
                    CalculateCandidateMetrics(candidate, sections, group);
                }
            }

            return candidates;
        }

        private (bool isValid, int fitCount, List<string> failedSections) EvaluateSingleCandidate(
            BackboneCandidate candidate,
            List<DesignSection> sections)
        {
            int fitCount = 0;
            var failedSections = new List<string>();
            bool allValid = true;

            foreach (var section in sections)
            {
                // Check TOP
                if (section.ReqTop > 0.01)
                {
                    bool topFits = CanFitBackbone(
                        section.ValidArrangementsTop,
                        candidate.CountTop,
                        candidate.Diameter,
                        section.ReqTop);

                    // Also allow if backbone provides enough area
                    if (!topFits && candidate.AreaTop >= section.ReqTop * (1 - AreaTolerance))
                    {
                        topFits = true;
                    }

                    if (topFits) fitCount++;
                    else
                    {
                        failedSections.Add($"{section.SectionId}_Top");
                        // Only fail if backbone is significantly inadequate
                        if (candidate.AreaTop < section.ReqTop * 0.5)
                        {
                            allValid = false;
                        }
                    }
                }

                // Check BOT
                if (section.ReqBot > 0.01)
                {
                    bool botFits = CanFitBackbone(
                        section.ValidArrangementsBot,
                        candidate.CountBot,
                        candidate.Diameter,
                        section.ReqBot);

                    if (!botFits && candidate.AreaBot >= section.ReqBot * (1 - AreaTolerance))
                    {
                        botFits = true;
                    }

                    if (botFits) fitCount++;
                    else
                    {
                        failedSections.Add($"{section.SectionId}_Bot");
                        if (candidate.AreaBot < section.ReqBot * 0.5)
                        {
                            allValid = false;
                        }
                    }
                }
            }

            return (allValid, fitCount, failedSections);
        }

        private bool CanFitBackbone(
            List<SectionArrangement> arrangements,
            int backboneCount,
            int backboneDiameter,
            double reqArea)
        {
            if (arrangements == null || arrangements.Count == 0) return false;

            // Check if any arrangement can accommodate this backbone
            return arrangements.Any(arr =>
                arr.PrimaryDiameter == backboneDiameter &&
                arr.TotalCount >= backboneCount &&
                arr.TotalArea >= reqArea * (1 - AreaTolerance));
        }

        private BackboneCandidate FindRelaxedBackbone(
            List<BackboneCandidate> candidates,
            List<DesignSection> sections)
        {
            // Find the candidate with highest fit count
            var best = candidates
                .OrderByDescending(c => c.FitCount)
                .ThenByDescending(c => c.AreaTop + c.AreaBot)
                .FirstOrDefault();

            if (best != null)
            {
                best.IsGloballyValid = true;
                Utils.RebarLogger.Log($"Using relaxed backbone: {best.DisplayLabel} (fit {best.FitCount}/{sections.Count * 2})");
            }

            return best;
        }

        private void CalculateCandidateMetrics(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            BeamGroup group)
        {
            double totalLength = CalculateTotalLength(group, sections);

            // Backbone weight
            double backboneWeight = WeightCalculator.CalculateBackboneWeight(
                candidate.Diameter,
                totalLength,
                candidate.CountTop + candidate.CountBot,
                _settings.Beam?.LapSpliceMultiplier ?? 1.02);

            // Estimate addon weight
            double addonWeight = EstimateAddonWeight(candidate, sections, group);

            candidate.EstimatedWeight = backboneWeight + addonWeight;
            candidate.TotalScore = CalculateCandidateScore(candidate, sections, totalLength);
        }

        private double CalculateTotalLength(BeamGroup group, List<DesignSection> sections)
        {
            if (group?.TotalLength > 0) return group.TotalLength * 1000; // m to mm

            // Estimate from spans
            if (group?.Spans?.Count > 0)
            {
                return group.Spans.Sum(s => s.Length) * 1000;
            }

            // Fallback
            return sections.Count * 2000;
        }

        private double EstimateAddonWeight(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            BeamGroup group)
        {
            double totalAddonWeight = 0;

            foreach (var section in sections)
            {
                // TOP addon
                if (section.ReqTop > candidate.AreaTop)
                {
                    double missingArea = section.ReqTop - candidate.AreaTop;
                    var addon = CalculateFallbackAddon(missingArea, section, candidate.Diameter);
                    double spanLength = GetSpanLength(section, group);
                    totalAddonWeight += WeightCalculator.CalculateWeight(
                        addon.Diameter, spanLength * 0.4, addon.Count);
                }

                // BOT addon
                if (section.ReqBot > candidate.AreaBot)
                {
                    double missingArea = section.ReqBot - candidate.AreaBot;
                    var addon = CalculateFallbackAddon(missingArea, section, candidate.Diameter);
                    double spanLength = GetSpanLength(section, group);
                    totalAddonWeight += WeightCalculator.CalculateWeight(
                        addon.Diameter, spanLength * 0.6, addon.Count);
                }
            }

            return totalAddonWeight;
        }

        private double CalculateCandidateScore(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            double totalLength)
        {
            double score = 100.0;

            // Penalty for failed sections
            score -= candidate.FailedSections.Count * 5;

            // Preference for fewer bars (larger diameter)
            if (_settings.Beam?.PreferFewerBars == true)
            {
                score += Math.Max(0, 8 - candidate.CountTop - candidate.CountBot);
            }

            // Preference for symmetric
            if (_settings.Beam?.PreferSymmetric == true && candidate.CountTop == candidate.CountBot)
            {
                score += 5;
            }

            // Weight efficiency
            double weightPerMeter = candidate.EstimatedWeight / (totalLength / 1000);
            if (weightPerMeter < 10) score += 5;
            else if (weightPerMeter > 30) score -= 10;

            return Math.Max(0, Math.Min(100, score));
        }

        private int GetMaxLayerCount(List<DesignSection> sections)
        {
            return _settings.Beam?.MaxLayers ?? 2;
        }

        private SectionArrangement GetBestArrangement(
            List<SectionArrangement> arrangements,
            int backboneCount,
            int backboneDiameter)
        {
            if (arrangements == null || arrangements.Count == 0) return null;

            // Find arrangement that contains this backbone
            return arrangements
                .Where(a => a.PrimaryDiameter == backboneDiameter && a.TotalCount >= backboneCount)
                .OrderByDescending(a => a.Score)
                .FirstOrDefault();
        }

        private double GetSpanLength(DesignSection section, BeamGroup group)
        {
            if (group?.Spans != null && section.SpanIndex < group.Spans.Count)
            {
                return group.Spans[section.SpanIndex].Length * 1000;
            }
            return 5000; // Default 5m
        }

        #endregion

        #region Solution Building

        private ContinuousBeamSolution BuildSolution(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            BeamGroup group)
        {
            double as1 = Math.PI * candidate.Diameter * candidate.Diameter / 400.0;

            var solution = new ContinuousBeamSolution
            {
                OptionName = $"{candidate.CountTop}D{candidate.Diameter}+{candidate.CountBot}D{candidate.Diameter}",
                BackboneDiameter_Top = candidate.Diameter,
                BackboneDiameter_Bot = candidate.Diameter,
                BackboneCount_Top = candidate.CountTop,
                BackboneCount_Bot = candidate.CountBot,
                As_Backbone_Top = candidate.CountTop * as1,
                As_Backbone_Bot = candidate.CountBot * as1,
                Reinforcements = new Dictionary<string, RebarSpec>(),
                SpanResults = new List<SpanRebarResult>(),
                StirrupDesigns = new Dictionary<string, string>(),
                IsValid = true
            };

            // Build span results
            var spanGroups = sections.GroupBy(s => s.SpanIndex).OrderBy(g => g.Key);

            foreach (var spanGroup in spanGroups)
            {
                var spanSections = spanGroup.ToList();
                var spanResult = BuildSpanResult(candidate, spanSections, group);
                solution.SpanResults.Add(spanResult);
            }

            // Calculate final metrics
            CalculateFinalMetrics(solution, group, sections);

            return solution;
        }

        private SpanRebarResult BuildSpanResult(
            BackboneCandidate candidate,
            List<DesignSection> spanSections,
            BeamGroup group)
        {
            var firstSection = spanSections.FirstOrDefault();
            int spanIndex = firstSection?.SpanIndex ?? 0;
            string spanId = firstSection?.SpanId ?? $"S{spanIndex + 1}";

            var result = new SpanRebarResult
            {
                SpanIndex = spanIndex,
                SpanId = spanId,
                TopBackbone = new RebarInfo { Count = candidate.CountTop, Diameter = candidate.Diameter },
                BotBackbone = new RebarInfo { Count = candidate.CountBot, Diameter = candidate.Diameter },
                TopAddons = new Dictionary<string, RebarInfo>(),
                BotAddons = new Dictionary<string, RebarInfo>(),
                Stirrups = new Dictionary<string, string>(),
                WebBars = new Dictionary<string, string>()
            };

            // CRITICAL: Calculate addons for each zone
            foreach (var section in spanSections)
            {
                string zoneName = GetPositionName(section);

                // TOP Addon
                if (section.ReqTop > candidate.AreaTop * (1 - AreaTolerance))
                {
                    double missingArea = section.ReqTop - candidate.AreaTop;
                    if (missingArea > 0.01)
                    {
                        var addon = CalculateFallbackAddon(missingArea, section, candidate.Diameter);
                        result.TopAddons[zoneName] = addon;
                    }
                }

                // BOT Addon
                if (section.ReqBot > candidate.AreaBot * (1 - AreaTolerance))
                {
                    double missingArea = section.ReqBot - candidate.AreaBot;
                    if (missingArea > 0.01)
                    {
                        var addon = CalculateFallbackAddon(missingArea, section, candidate.Diameter);
                        result.BotAddons[zoneName] = addon;
                    }
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

        private RebarInfo CalculateFallbackAddon(double missingArea, DesignSection section, int backboneDiameter)
        {
            if (missingArea <= 0.01)
            {
                return new RebarInfo { Count = 0, Diameter = backboneDiameter };
            }

            // Use same or smaller diameter for addon
            int addonDia = backboneDiameter;
            
            // If mixing is not allowed, use backbone diameter
            if (_settings.Beam?.AllowDiameterMixing != true)
            {
                // Keep same diameter
            }
            else
            {
                // Try smaller diameter
                var smaller = _allowedDiameters.Where(d => d < backboneDiameter).OrderByDescending(d => d).FirstOrDefault();
                if (smaller > 0) addonDia = smaller;
            }

            double as1 = Math.PI * addonDia * addonDia / 400.0;
            int count = (int)Math.Ceiling(missingArea / as1);
            if (count < 2) count = 2;

            // Apply symmetric preference
            if (_settings.Beam?.PreferSymmetric == true && count % 2 != 0)
            {
                count++;
            }

            return new RebarInfo { Count = count, Diameter = addonDia };
        }

        private string GetPositionName(DesignSection section)
        {
            if (section.ZoneIndex == 0) return "Left";
            if (section.ZoneIndex == 2 || section.Type == SectionType.MidSpan) return "Mid";
            return "Right";
        }

        private void AddReinforcementsFromSpanResult(ContinuousBeamSolution solution, SpanRebarResult spanResult)
        {
            foreach (var kvp in spanResult.TopAddons)
            {
                string key = $"{spanResult.SpanId}_Top_{kvp.Key}";
                solution.Reinforcements[key] = new RebarSpec
                {
                    Diameter = kvp.Value.Diameter,
                    Count = kvp.Value.Count,
                    Position = "Top",
                    Layer = 2
                };
            }

            foreach (var kvp in spanResult.BotAddons)
            {
                string key = $"{spanResult.SpanId}_Bot_{kvp.Key}";
                solution.Reinforcements[key] = new RebarSpec
                {
                    Diameter = kvp.Value.Diameter,
                    Count = kvp.Value.Count,
                    Position = "Bot",
                    Layer = 2
                };
            }
        }

        private void UpdateSectionSelections(BackboneCandidate candidate, List<DesignSection> sections)
        {
            foreach (var section in sections)
            {
                section.SelectedTop = GetBestArrangement(
                    section.ValidArrangementsTop, candidate.CountTop, candidate.Diameter);
                section.SelectedBot = GetBestArrangement(
                    section.ValidArrangementsBot, candidate.CountBot, candidate.Diameter);
            }
        }

        private void CalculateFinalMetrics(ContinuousBeamSolution solution, BeamGroup group, List<DesignSection> sections)
        {
            // Calculate total steel weight
            double totalLength = CalculateTotalLength(group, sections);

            double backboneWeight = WeightCalculator.CalculateBackboneWeight(
                solution.BackboneDiameter,
                totalLength,
                solution.BackboneCount_Top + solution.BackboneCount_Bot,
                _settings.Beam?.LapSpliceMultiplier ?? 1.02);

            // Addon weight from SpanResults
            double addonWeight = 0;
            foreach (var sr in solution.SpanResults)
            {
                double spanLength = GetSpanLength(sections.FirstOrDefault(s => s.SpanId == sr.SpanId), group);

                foreach (var addon in sr.TopAddons.Values)
                {
                    addonWeight += WeightCalculator.CalculateWeight(addon.Diameter, spanLength * 0.4, addon.Count);
                }
                foreach (var addon in sr.BotAddons.Values)
                {
                    addonWeight += WeightCalculator.CalculateWeight(addon.Diameter, spanLength * 0.6, addon.Count);
                }
            }

            solution.TotalSteelWeight = backboneWeight + addonWeight;

            // Waste calculation
            double totalRequired = sections.Sum(s => s.ReqTop + s.ReqBot);
            double totalProvided = solution.As_Backbone_Top * sections.Count 
                                 + solution.As_Backbone_Bot * sections.Count
                                 + solution.Reinforcements.Values.Sum(r => r.Count * Math.PI * r.Diameter * r.Diameter / 400.0);

            solution.WastePercentage = totalRequired > 0 
                ? Math.Max(0, (totalProvided - totalRequired) / totalRequired * 100) 
                : 0;

            // Efficiency & Total Score
            solution.EfficiencyScore = Math.Max(0, 100 - solution.WastePercentage);
            solution.ConstructabilityScore = ConstructabilityScoring.CalculateScore(solution, group, _settings);
            solution.TotalScore = 0.5 * solution.EfficiencyScore + 0.5 * solution.ConstructabilityScore;

            // Max required areas
            solution.As_Required_Top_Max = sections.Max(s => s.ReqTop);
            solution.As_Required_Bot_Max = sections.Max(s => s.ReqBot);

            solution.Description = GenerateDescription(solution);
        }

        private string GenerateDescription(ContinuousBeamSolution solution)
        {
            int addonCount = solution.Reinforcements.Count;
            return $"Backbone: {solution.BackboneCount_Top}D{solution.BackboneDiameter} / {solution.BackboneCount_Bot}D{solution.BackboneDiameter}" +
                   (addonCount > 0 ? $" + {addonCount} addons" : "");
        }

        private void ValidateSolution(ContinuousBeamSolution solution, List<DesignSection> sections, BackboneCandidate candidate)
        {
            var problems = new List<string>();

            foreach (var section in sections)
            {
                double providedTop = CalculateProvidedArea(solution, section, candidate, true);
                double providedBot = CalculateProvidedArea(solution, section, candidate, false);

                if (section.ReqTop > 0.01 && providedTop < section.ReqTop * 0.98)
                {
                    problems.Add($"{section.SectionId} Top: need {section.ReqTop:F2}, have {providedTop:F2}");
                }

                if (section.ReqBot > 0.01 && providedBot < section.ReqBot * 0.98)
                {
                    problems.Add($"{section.SectionId} Bot: need {section.ReqBot:F2}, have {providedBot:F2}");
                }
            }

            if (problems.Count > 0)
            {
                solution.IsValid = false;
                solution.ValidationMessage = string.Join("; ", problems.Take(3));
                Utils.RebarLogger.LogError($"Solution validation failed: {solution.ValidationMessage}");
            }
        }

        private double CalculateProvidedArea(
            ContinuousBeamSolution solution,
            DesignSection section,
            BackboneCandidate candidate,
            bool isTop)
        {
            double backboneArea = isTop ? solution.As_Backbone_Top : solution.As_Backbone_Bot;

            // Find matching addon
            string zoneName = GetPositionName(section);
            var spanResult = solution.SpanResults.FirstOrDefault(sr => sr.SpanId == section.SpanId);

            if (spanResult != null)
            {
                var addons = isTop ? spanResult.TopAddons : spanResult.BotAddons;
                if (addons.TryGetValue(zoneName, out var addon))
                {
                    backboneArea += addon.Count * Math.PI * addon.Diameter * addon.Diameter / 400.0;
                }
            }

            return backboneArea;
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

        private void OptimizeAndCleanSolution(ContinuousBeamSolution solution, BeamGroup group)
        {
            // Remove empty reinforcements
            var keysToRemove = solution.Reinforcements
                .Where(kvp => kvp.Value.Count <= 0)
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                solution.Reinforcements.Remove(key);
            }
        }

        #endregion
    }
}
