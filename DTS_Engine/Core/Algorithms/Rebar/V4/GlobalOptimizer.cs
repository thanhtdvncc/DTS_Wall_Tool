using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Algorithms.Rebar.Models;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Algorithms.Rebar.Utils;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// Tổng hợp giải pháp toàn cục từ các phương án cục bộ.
    /// Tìm Backbone tối ưu và ghép nối thành ContinuousBeamSolution.
    /// Hỗ trợ N nhịp, N layers linh hoạt, không giới hạn số lượng.
    /// 
    /// ISO 25010: Performance Efficiency - Polynomial complexity O(B * N * M).
    /// ISO 12207: Implementation Phase - Algorithm optimization.
    /// </summary>
    public class GlobalOptimizer
    {
        #region Configuration

        /// <summary>Số backbone candidates tối đa để thử</summary>
        public int MaxBackboneCandidates { get; set; } = 50;

        /// <summary>Số solutions tối đa trả về</summary>
        public int MaxSolutions { get; set; } = 5;

        /// <summary>Tolerance cho việc kiểm tra đủ thép</summary>
        public double AreaTolerance { get; set; } = 0.98;

        #endregion

        #region Dependencies

        private readonly DtsSettings _settings;
        private readonly List<int> _allowedDiameters;
        private readonly int _minBarsPerSide;
        private readonly int _maxBarsPerSide;

        #endregion

        #region Constructor

        public GlobalOptimizer(DtsSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));

            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 12, 14, 16, 18, 20, 22, 25, 28, 32 };
            _allowedDiameters = DiameterParser.ParseRange(
                settings.Beam?.MainBarRange ?? "16-25",
                inventory);

            if (settings.Beam?.PreferEvenDiameter == true)
            {
                _allowedDiameters = DiameterParser.FilterEvenDiameters(_allowedDiameters);
            }

            if (_allowedDiameters.Count == 0)
            {
                _allowedDiameters = inventory.Where(d => d >= 16 && d <= 25).ToList();
            }

            // Get constraints from settings
            _minBarsPerSide = settings.Beam?.MinBarsPerLayer ?? 2;
            _maxBarsPerSide = settings.Beam?.MaxBarsPerLayer ?? 8;
        }

        #endregion

        #region Public API

        /// <summary>
        /// Tìm các backbone tốt nhất và tổng hợp thành solutions.
        /// </summary>
        /// <param name="sections">Danh sách sections đã được giải và merge</param>
        /// <param name="group">Thông tin BeamGroup</param>
        /// <param name="externalConstraints">Ràng buộc bên ngoài (nếu có)</param>
        /// <returns>Top N solutions sắp theo TotalScore</returns>
        public List<ContinuousBeamSolution> FindBestSolutions(
            List<DesignSection> sections,
            BeamGroup group,
            ExternalConstraints externalConstraints = null)
        {
            if (sections == null || sections.Count == 0)
            {
                return new List<ContinuousBeamSolution>
                {
                    CreateErrorSolution("Không có dữ liệu mặt cắt")
                };
            }

            // Log input
            Utils.RebarLogger.Log($"GlobalOptimizer: {sections.Count} sections, {group?.Spans?.Count ?? 0} spans");

            // Bước 1: Sinh danh sách Backbone Candidates
            var candidates = GenerateBackboneCandidates(sections, externalConstraints);

            if (candidates.Count == 0)
            {
                return new List<ContinuousBeamSolution>
                {
                    CreateErrorSolution("Không tìm được backbone phù hợp với bề rộng dầm")
                };
            }

            RebarLogger.Log($"Generated {candidates.Count} backbone candidates");

            // Bước 2: Đánh giá và sắp xếp candidates
            var evaluatedCandidates = EvaluateCandidates(candidates, sections, group);

            if (evaluatedCandidates.Count == 0)
            {
                // Không có candidate nào valid -> Relaxed mode
                var relaxedCandidate = FindRelaxedBackbone(candidates, sections);
                if (relaxedCandidate != null)
                {
                    CalculateCandidateMetrics(relaxedCandidate, sections, group);
                    evaluatedCandidates.Add(relaxedCandidate);
                }
            }

            if (evaluatedCandidates.Count == 0)
            {
                return new List<ContinuousBeamSolution>
                {
                    CreateErrorSolution("Không tìm được phương án bố trí thép khả thi")
                };
            }

            // Bước 3: Xây dựng ContinuousBeamSolution cho mỗi valid candidate
            var solutions = new List<ContinuousBeamSolution>();

            foreach (var candidate in evaluatedCandidates.OrderByDescending(c => c.TotalScore).Take(MaxSolutions))
            {
                var solution = BuildSolution(candidate, sections, group);
                if (solution != null)
                {
                    solutions.Add(solution);
                }
            }

            // Bước 4: Sắp xếp theo TotalScore
            var finalSolutions = solutions
                .OrderByDescending(s => s.TotalScore)
                .Take(MaxSolutions)
                .ToList();

            // Log output
            foreach (var sol in finalSolutions)
            {
                RebarLogger.Log($"  Solution: {sol.OptionName} | Score={sol.TotalScore:F1} | Valid={sol.IsValid}");
            }

            return finalSolutions;
        }

        #endregion

        #region Backbone Generation

        /// <summary>
        /// Sinh danh sách backbone candidates linh hoạt cho N spans.
        /// </summary>
        private List<BackboneCandidate> GenerateBackboneCandidates(
            List<DesignSection> sections,
            ExternalConstraints constraints)
        {
            var candidates = new List<BackboneCandidate>();

            // Lấy bề rộng nhỏ nhất (constraint cho backbone)
            double minWidth = sections.Min(s => s.UsableWidth);
            if (minWidth <= 0) return candidates;

            // Xác định diameters cần thử
            List<int> diametersToTry = GetDiametersToTry(constraints);

            // Xác định range số thanh
            int forcedCountTop = constraints?.ForcedBackboneCountTop ?? -1;
            int forcedCountBot = constraints?.ForcedBackboneCountBot ?? -1;

            foreach (int dia in diametersToTry)
            {
                // Tính số thanh tối đa cho bề rộng nhỏ nhất
                int maxBars = CalculateMaxBarsForWidth(minWidth, dia);
                if (maxBars < _minBarsPerSide) continue;

                // Giới hạn max để tránh tràn
                int effectiveMax = Math.Min(maxBars, _maxBarsPerSide);

                // Sinh candidates
                for (int nTop = _minBarsPerSide; nTop <= effectiveMax; nTop++)
                {
                    if (forcedCountTop >= 0 && nTop != forcedCountTop) continue;

                    for (int nBot = _minBarsPerSide; nBot <= effectiveMax; nBot++)
                    {
                        if (forcedCountBot >= 0 && nBot != forcedCountBot) continue;

                        // Chỉ giữ các tổ hợp hợp lý
                        if (!IsValidCombination(nTop, nBot)) continue;

                        candidates.Add(new BackboneCandidate
                        {
                            Diameter = dia,
                            CountTop = nTop,
                            CountBot = nBot
                        });

                        if (candidates.Count >= MaxBackboneCandidates) break;
                    }
                    if (candidates.Count >= MaxBackboneCandidates) break;
                }
                if (candidates.Count >= MaxBackboneCandidates) break;
            }

            return candidates;
        }

        /// <summary>
        /// Lấy danh sách diameters cần thử.
        /// </summary>
        private List<int> GetDiametersToTry(ExternalConstraints constraints)
        {
            if (constraints?.ForcedBackboneDiameter.HasValue == true)
            {
                return new List<int> { constraints.ForcedBackboneDiameter.Value };
            }

            return _allowedDiameters.OrderByDescending(d => d).ToList();
        }

        /// <summary>
        /// Tính số thanh tối đa cho bề rộng.
        /// </summary>
        private int CalculateMaxBarsForWidth(double usableWidth, int diameter)
        {
            double minSpacing = _settings.Beam?.MinClearSpacing ?? 25;
            double aggregateSpacing = 1.33 * (_settings.Beam?.AggregateSize ?? 20);
            double minClear = Math.Max(diameter, Math.Max(minSpacing, aggregateSpacing));

            double nMax = (usableWidth + minClear) / (diameter + minClear);
            return Math.Max(0, (int)Math.Floor(nMax));
        }

        /// <summary>
        /// Kiểm tra tổ hợp Top/Bot có hợp lý không.
        /// </summary>
        private bool IsValidCombination(int nTop, int nBot)
        {
            // Chênh lệch không quá 3 thanh
            if (Math.Abs(nTop - nBot) > 3) return false;

            return true;
        }

        #endregion

        #region Candidate Evaluation

        /// <summary>
        /// Đánh giá tất cả candidates.
        /// </summary>
        private List<BackboneCandidate> EvaluateCandidates(
            List<BackboneCandidate> candidates,
            List<DesignSection> sections,
            BeamGroup group)
        {
            var validCandidates = new List<BackboneCandidate>();

            foreach (var candidate in candidates)
            {
                var evaluation = EvaluateSingleCandidate(candidate, sections);

                if (evaluation.isValid)
                {
                    candidate.IsGloballyValid = true;
                    candidate.FitCount = sections.Count;
                    CalculateCandidateMetrics(candidate, sections, group);
                    validCandidates.Add(candidate);
                }
                else if (evaluation.fitCount >= sections.Count * 0.8) // Fit >= 80% sections
                {
                    candidate.IsGloballyValid = false;
                    candidate.FitCount = evaluation.fitCount;
                    candidate.FailedSections = evaluation.failedSections;
                    CalculateCandidateMetrics(candidate, sections, group);
                    validCandidates.Add(candidate);
                }
            }

            return validCandidates;
        }

        /// <summary>
        /// Đánh giá một candidate.
        /// </summary>
        private (bool isValid, int fitCount, List<string> failedSections) EvaluateSingleCandidate(
            BackboneCandidate candidate,
            List<DesignSection> sections)
        {
            int fitCount = 0;
            var failedSections = new List<string>();

            foreach (var section in sections)
            {
                bool topOk = section.ReqTop <= 0.01 ||
                    CanFitBackbone(section.ValidArrangementsTop, candidate.CountTop, candidate.Diameter, section.ReqTop);

                bool botOk = section.ReqBot <= 0.01 ||
                    CanFitBackbone(section.ValidArrangementsBot, candidate.CountBot, candidate.Diameter, section.ReqBot);

                if (topOk && botOk)
                {
                    fitCount++;
                }
                else
                {
                    failedSections.Add(section.SectionId);
                }
            }

            return (fitCount == sections.Count, fitCount, failedSections);
        }

        /// <summary>
        /// Kiểm tra backbone có fit được không.
        /// </summary>
        private bool CanFitBackbone(
            List<SectionArrangement> arrangements,
            int backboneCount,
            int backboneDiameter,
            double reqArea)
        {
            // Nếu backbone đã đủ diện tích
            double backboneArea = backboneCount * Math.PI * backboneDiameter * backboneDiameter / 400.0;
            if (backboneArea >= reqArea * AreaTolerance) return true;

            // Tìm arrangement có thể bổ sung
            return arrangements.Any(arr =>
                arr.ContainsBackbone(backboneCount, backboneDiameter) &&
                arr.TotalArea >= reqArea * AreaTolerance);
        }

        /// <summary>
        /// Tìm backbone có thể fit nhiều sections nhất (Relaxed mode).
        /// </summary>
        private BackboneCandidate FindRelaxedBackbone(
            List<BackboneCandidate> candidates,
            List<DesignSection> sections)
        {
            BackboneCandidate best = null;
            int maxFit = 0;

            foreach (var candidate in candidates)
            {
                var eval = EvaluateSingleCandidate(candidate, sections);
                if (eval.fitCount > maxFit)
                {
                    maxFit = eval.fitCount;
                    best = candidate;
                    best.FitCount = eval.fitCount;
                    best.FailedSections = eval.failedSections;
                }
            }

            return best;
        }

        #endregion

        #region Metrics & Scoring

        /// <summary>
        /// Tính metrics cho candidate (weight, score).
        /// </summary>
        private void CalculateCandidateMetrics(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            BeamGroup group)
        {
            double totalLength = CalculateTotalLength(group, sections);

            // Backbone weight (kg)
            double weightPerMeter = 0.00617 * candidate.Diameter * candidate.Diameter;
            double backboneWeight = weightPerMeter * totalLength * (candidate.CountTop + candidate.CountBot);

            // Addon weight (estimate per section)
            double addonWeight = EstimateAddonWeight(candidate, sections, group);

            candidate.EstimatedWeight = backboneWeight + addonWeight;

            // Scoring (0-100)
            double score = CalculateCandidateScore(candidate, sections, totalLength);

            candidate.TotalScore = Math.Max(0, Math.Min(100, score));
        }

        /// <summary>
        /// Tính tổng chiều dài dầm.
        /// </summary>
        private double CalculateTotalLength(BeamGroup group, List<DesignSection> sections)
        {
            if (group?.Spans != null && group.Spans.Count > 0)
            {
                return group.Spans.Sum(s => s.Length);
            }

            if (sections.Count > 0)
            {
                return sections.Max(s => s.Position);
            }

            return 6; // Default 6m
        }

        /// <summary>
        /// Ước tính trọng lượng thép addon.
        /// </summary>
        private double EstimateAddonWeight(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            BeamGroup group)
        {
            double addonWeight = 0;

            foreach (var section in sections)
            {
                double spanLength = GetSpanLength(section, group);

                // TOP addon
                var topArr = GetBestArrangement(section.ValidArrangementsTop, candidate.CountTop, candidate.Diameter);
                if (topArr != null && topArr.TotalCount > candidate.CountTop)
                {
                    var addon = topArr.GetAddon(candidate.CountTop, candidate.Diameter);
                    // Addon chạy khoảng 1/3 chiều dài nhịp (tại gối) hoặc 1/2 (tại giữa nhịp)
                    double addonLen = section.Type == SectionType.MidSpan ? spanLength * 0.5 : spanLength * 0.33;
                    addonWeight += 0.00617 * addon.diameter * addon.diameter * addonLen * addon.count;
                }

                // BOT addon
                var botArr = GetBestArrangement(section.ValidArrangementsBot, candidate.CountBot, candidate.Diameter);
                if (botArr != null && botArr.TotalCount > candidate.CountBot)
                {
                    var addon = botArr.GetAddon(candidate.CountBot, candidate.Diameter);
                    // BOT addon thường chạy dài hơn (0.7-0.8 span)
                    double addonLen = spanLength * 0.7;
                    addonWeight += 0.00617 * addon.diameter * addon.diameter * addonLen * addon.count;
                }
            }

            // Tránh đếm trùng: Mỗi span có 3 sections, addon chỉ tính 1 lần per position
            return addonWeight / 3.0;
        }

        /// <summary>
        /// Tính điểm cho candidate.
        /// </summary>
        private double CalculateCandidateScore(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            double totalLength)
        {
            double score = 100;

            // 1. Weight penalty (-30 max): Lighter = better
            double normalizedWeight = candidate.EstimatedWeight / (totalLength * 10);
            score -= Math.Min(30, normalizedWeight * 10);

            // 2. Validity penalty (-20 max): More fit = better
            if (!candidate.IsGloballyValid)
            {
                double fitRatio = (double)candidate.FitCount / sections.Count;
                score -= (1 - fitRatio) * 20;
            }

            // 3. Constructability bonuses (+15 max)
            int preferredDia = _settings.Beam?.PreferredDiameter ?? 20;
            if (candidate.Diameter == preferredDia) score += 5;
            if (candidate.CountTop == candidate.CountBot) score += 5;
            if (candidate.CountTop % 2 == 0 && candidate.CountBot % 2 == 0) score += 5;

            // 4. Layer penalty (-15 max): Fewer layers = better
            int maxLayers = GetMaxLayerCount(sections);
            score -= (maxLayers - 1) * 7;

            // 5. Bar count penalty (-10 max): Fewer bars = simpler
            int totalBackboneBars = candidate.CountTop + candidate.CountBot;
            score -= Math.Max(0, (totalBackboneBars - 4) * 2);

            return score;
        }

        /// <summary>
        /// Lấy số lớp tối đa trong các sections.
        /// </summary>
        private int GetMaxLayerCount(List<DesignSection> sections)
        {
            return sections
                .SelectMany(s => s.ValidArrangementsTop.Concat(s.ValidArrangementsBot))
                .Select(a => a.LayerCount)
                .DefaultIfEmpty(1)
                .Max();
        }

        /// <summary>
        /// Lấy arrangement tốt nhất cho backbone.
        /// </summary>
        private SectionArrangement GetBestArrangement(
            List<SectionArrangement> arrangements,
            int backboneCount,
            int backboneDiameter)
        {
            return arrangements
                .Where(a => a.ContainsBackbone(backboneCount, backboneDiameter))
                .OrderByDescending(a => a.Score)
                .ThenBy(a => a.TotalCount) // Prefer fewer bars
                .FirstOrDefault();
        }

        /// <summary>
        /// Lấy chiều dài nhịp chứa section.
        /// </summary>
        private double GetSpanLength(DesignSection section, BeamGroup group)
        {
            if (group?.Spans != null && section.SpanIndex < group.Spans.Count)
            {
                return group.Spans[section.SpanIndex].Length;
            }
            return 5; // Default 5m
        }

        #endregion

        #region Solution Building

        /// <summary>
        /// Xây dựng ContinuousBeamSolution từ BackboneCandidate.
        /// </summary>
        private ContinuousBeamSolution BuildSolution(
            BackboneCandidate candidate,
            List<DesignSection> sections,
            BeamGroup group)
        {
            var solution = new ContinuousBeamSolution
            {
                OptionName = candidate.DisplayLabel,
                BackboneDiameter_Top = candidate.Diameter,
                BackboneDiameter_Bot = candidate.Diameter,
                BackboneCount_Top = candidate.CountTop,
                BackboneCount_Bot = candidate.CountBot,
                As_Backbone_Top = candidate.AreaTop,
                As_Backbone_Bot = candidate.AreaBot,
                TotalSteelWeight = candidate.EstimatedWeight,
                TotalScore = candidate.TotalScore,
                IsValid = candidate.IsGloballyValid,
                Reinforcements = new Dictionary<string, RebarSpec>(),
                StirrupDesigns = new Dictionary<string, string>()
            };

            // Xây dựng reinforcements cho từng span
            var spanGroups = sections.GroupBy(s => s.SpanIndex).OrderBy(g => g.Key);

            foreach (var spanGroup in spanGroups)
            {
                var spanResult = BuildSpanResult(candidate, spanGroup.ToList(), group);
                if (spanResult != null)
                {
                    // Thêm vào Reinforcements dictionary (legacy format)
                    AddReinforcementsFromSpanResult(solution, spanResult);
                }
            }

            // Update section selections
            UpdateSectionSelections(candidate, sections);

            // Áp dụng logic nối thép dựa trên Curtailment Settings
            OptimizeAndCleanSolution(solution, group);

            // Tính metrics cuối cùng
            CalculateFinalMetrics(solution, group, sections);

            // Validate solution
            ValidateSolution(solution, sections, candidate);

            return solution;
        }

        /// <summary>
        /// Xây dựng SpanRebarResult cho một nhịp.
        /// </summary>
        private SpanRebarResult BuildSpanResult(
            BackboneCandidate candidate,
            List<DesignSection> spanSections,
            BeamGroup group)
        {
            if (spanSections.Count == 0) return null;

            var firstSection = spanSections.First();
            string spanId = firstSection.SpanId;

            var result = new SpanRebarResult
            {
                SpanIndex = firstSection.SpanIndex,
                SpanId = spanId,
                TopBackbone = new RebarInfo
                {
                    Count = candidate.CountTop,
                    Diameter = candidate.Diameter,
                    LayerCounts = new List<int> { candidate.CountTop }
                },
                BotBackbone = new RebarInfo
                {
                    Count = candidate.CountBot,
                    Diameter = candidate.Diameter,
                    LayerCounts = new List<int> { candidate.CountBot }
                }
            };

            // Xử lý từng section trong span
            foreach (var section in spanSections)
            {
                string positionName = GetPositionName(section);
                if (string.IsNullOrEmpty(positionName)) continue;

                // TOP addon
                var topArr = GetBestArrangement(section.ValidArrangementsTop, candidate.CountTop, candidate.Diameter);
                if (topArr != null && topArr.TotalCount > candidate.CountTop)
                {
                    var addon = topArr.GetAddon(candidate.CountTop, candidate.Diameter);
                    if (addon.count > 0)
                    {
                        result.TopAddons[positionName] = new RebarInfo
                        {
                            Count = addon.count,
                            Diameter = addon.diameter,
                            LayerCounts = addon.layerBreakdown
                        };
                    }
                }

                // BOT addon
                var botArr = GetBestArrangement(section.ValidArrangementsBot, candidate.CountBot, candidate.Diameter);
                if (botArr != null && botArr.TotalCount > candidate.CountBot)
                {
                    var addon = botArr.GetAddon(candidate.CountBot, candidate.Diameter);
                    if (addon.count > 0)
                    {
                        result.BotAddons[positionName] = new RebarInfo
                        {
                            Count = addon.count,
                            Diameter = addon.diameter,
                            LayerCounts = addon.layerBreakdown
                        };
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Lấy tên vị trí từ section.
        /// </summary>
        private string GetPositionName(DesignSection section)
        {
            if (section.Type == SectionType.Support)
            {
                return section.IsSupportLeft ? "Left" : "Right";
            }
            else if (section.Type == SectionType.MidSpan)
            {
                return "Mid";
            }
            else if (section.Type == SectionType.QuarterSpan)
            {
                return section.RelativePosition < 0.5 ? "QuarterLeft" : "QuarterRight";
            }

            return null;
        }

        /// <summary>
        /// Thêm reinforcements từ SpanRebarResult vào solution dictionary (legacy format).
        /// </summary>
        private void AddReinforcementsFromSpanResult(ContinuousBeamSolution solution, SpanRebarResult spanResult)
        {
            string spanId = spanResult.SpanId;

            // TOP addons
            foreach (var kvp in spanResult.TopAddons)
            {
                string key = $"{spanId}_Top_{kvp.Key}";
                solution.Reinforcements[key] = new RebarSpec
                {
                    Diameter = kvp.Value.Diameter,
                    Count = kvp.Value.Count,
                    Position = "Top",
                    LayerBreakdown = kvp.Value.LayerCounts
                };
            }

            // BOT addons
            foreach (var kvp in spanResult.BotAddons)
            {
                string key = $"{spanId}_Bot_{kvp.Key}";
                solution.Reinforcements[key] = new RebarSpec
                {
                    Diameter = kvp.Value.Diameter,
                    Count = kvp.Value.Count,
                    Position = "Bot",
                    LayerBreakdown = kvp.Value.LayerCounts
                };
            }
        }

        /// <summary>
        /// Cập nhật selected arrangements cho sections.
        /// </summary>
        private void UpdateSectionSelections(BackboneCandidate candidate, List<DesignSection> sections)
        {
            foreach (var section in sections)
            {
                section.SelectedTop = GetBestArrangement(
                    section.ValidArrangementsTop,
                    candidate.CountTop,
                    candidate.Diameter);

                section.SelectedBot = GetBestArrangement(
                    section.ValidArrangementsBot,
                    candidate.CountBot,
                    candidate.Diameter);
            }
        }

        /// <summary>
        /// Tính các metrics cuối cùng cho solution.
        /// </summary>
        private void CalculateFinalMetrics(ContinuousBeamSolution solution, BeamGroup group, List<DesignSection> sections)
        {
            double totalLength = CalculateTotalLength(group, sections);

            // Constructability score
            solution.ConstructabilityScore = ConstructabilityScoring.CalculateScore(solution, group, _settings);

            // Efficiency score (based on weight)
            double baseWeight = totalLength * 10; // 10 kg/m baseline
            solution.EfficiencyScore = Math.Max(0, 100 - (solution.TotalSteelWeight / baseWeight * 10));

            // Total score
            double efficiencyWeight = _settings.Beam?.EfficiencyScoreWeight ?? 0.6;
            double constructabilityWeight = 1.0 - efficiencyWeight;

            solution.TotalScore = efficiencyWeight * solution.EfficiencyScore +
                                  constructabilityWeight * solution.ConstructabilityScore;

            // Description
            solution.Description = GenerateDescription(solution);
        }

        /// <summary>
        /// Sinh mô tả cho solution.
        /// </summary>
        private string GenerateDescription(ContinuousBeamSolution solution)
        {
            var parts = new List<string>();

            if (solution.BackboneCount_Top == solution.BackboneCount_Bot &&
                solution.BackboneDiameter_Top == solution.BackboneDiameter_Bot)
            {
                parts.Add($"Backbone: {solution.BackboneCount_Top}D{solution.BackboneDiameter_Top}");
            }
            else
            {
                parts.Add($"T:{solution.BackboneCount_Top}D{solution.BackboneDiameter_Top}/B:{solution.BackboneCount_Bot}D{solution.BackboneDiameter_Bot}");
            }

            if (solution.Reinforcements.Count > 0)
            {
                int addonCount = solution.Reinforcements.Values.Sum(r => r.Count);
                parts.Add($"+ {addonCount} addon");
            }

            parts.Add($"({solution.TotalSteelWeight:F1}kg)");

            return string.Join(" ", parts);
        }

        /// <summary>
        /// Validate solution: Kiểm tra đủ thép tại tất cả sections.
        /// </summary>
        private void ValidateSolution(ContinuousBeamSolution solution, List<DesignSection> sections, BackboneCandidate candidate)
        {
            var deficits = new List<string>();

            foreach (var section in sections)
            {
                // TOP check
                if (section.ReqTop > 0.01)
                {
                    double provided = CalculateProvidedArea(solution, section, candidate, isTop: true);

                    if (provided < section.ReqTop * AreaTolerance)
                    {
                        deficits.Add($"{section.SectionId} Top: {provided:F2}/{section.ReqTop:F2}");
                    }
                }

                // BOT check
                if (section.ReqBot > 0.01)
                {
                    double provided = CalculateProvidedArea(solution, section, candidate, isTop: false);

                    if (provided < section.ReqBot * AreaTolerance)
                    {
                        deficits.Add($"{section.SectionId} Bot: {provided:F2}/{section.ReqBot:F2}");
                    }
                }
            }

            if (deficits.Count > 0)
            {
                solution.IsValid = false;
                solution.ValidationMessage = "Thiếu thép: " + string.Join("; ", deficits.Take(3));
                if (deficits.Count > 3)
                {
                    solution.ValidationMessage += $" (+{deficits.Count - 3} khác)";
                }
            }
        }

        /// <summary>
        /// Tính diện tích thép cung cấp tại section.
        /// </summary>
        private double CalculateProvidedArea(
            ContinuousBeamSolution solution,
            DesignSection section,
            BackboneCandidate candidate,
            bool isTop)
        {
            double provided = isTop ? candidate.AreaTop : candidate.AreaBot;

            string positionName = GetPositionName(section);
            if (string.IsNullOrEmpty(positionName)) return provided;

            string key = $"{section.SpanId}_{(isTop ? "Top" : "Bot")}_{positionName}";

            if (solution.Reinforcements.TryGetValue(key, out var spec))
            {
                provided += spec.Count * Math.PI * spec.Diameter * spec.Diameter / 400.0;
            }

            return provided;
        }

        /// <summary>
        /// Tạo solution lỗi.
        /// </summary>
        private ContinuousBeamSolution CreateErrorSolution(string message)
        {
            return new ContinuousBeamSolution
            {
                OptionName = "ERROR",
                IsValid = false,
                ValidationMessage = message,
                TotalScore = 0,
                Reinforcements = new Dictionary<string, RebarSpec>()
            };
        }

        /// <summary>
        /// Áp dụng Logic Nối thép dựa trên Curtailment Settings.
        /// Merge thép gối trái và gối phải nếu khoảng hở < ngưỡng.
        /// </summary>
        private void OptimizeAndCleanSolution(ContinuousBeamSolution solution, BeamGroup group)
        {
            if (group?.Spans == null || group.Spans.Count == 0) return;

            // 1. Xác định loại cấu kiện để lấy Curtailment Config phù hợp
            bool isGirder = (group.GroupName ?? "").StartsWith("G") ||
                            (group.GroupName ?? "").IndexOf("Girder", StringComparison.OrdinalIgnoreCase) >= 0;

            var curtailment = isGirder
                ? (_settings.Beam?.GirderCurtailment ?? new CurtailmentConfig())
                : (_settings.Beam?.BeamCurtailment ?? new CurtailmentConfig());

            // 2. Logic Nối thông (Bridging) dựa trên TopSupportExtRatio
            double extRatio = curtailment.TopSupportExtRatio; // VD: 0.25

            foreach (var span in group.Spans)
            {
                string leftKey = $"{span.SpanId}_Top_Left";
                string rightKey = $"{span.SpanId}_Top_Right";

                if (solution.Reinforcements.TryGetValue(leftKey, out var sLeft) &&
                    solution.Reinforcements.TryGetValue(rightKey, out var sRight))
                {
                    // Chỉ nối nếu cùng đường kính và số lượng
                    if (sLeft.Diameter != sRight.Diameter || sLeft.Count != sRight.Count) continue;

                    // Tính khoảng hở giữa 2 đoạn thép
                    double spanLength = span.Length > 0 ? span.Length : 5000; // mm
                    double gap = spanLength * (1.0 - 2 * extRatio);

                    // Quy tắc heuristic: Nếu gap < 40d hoặc < 1000mm -> Nối
                    double limit = Math.Max(1000, 40 * sLeft.Diameter);

                    if (gap < limit)
                    {
                        Utils.RebarLogger.Log($"  Merging {leftKey} + {rightKey} (gap={gap:F0}mm < {limit:F0}mm)");

                        // Merge thành Top_Full
                        solution.Reinforcements.Remove(leftKey);
                        solution.Reinforcements.Remove(rightKey);
                        solution.Reinforcements[$"{span.SpanId}_Top_Full"] = new RebarSpec
                        {
                            Count = sLeft.Count,
                            Diameter = sLeft.Diameter,
                            Position = "Top",
                            IsRunningThrough = true,
                            LayerBreakdown = sLeft.LayerBreakdown
                        };
                    }
                }
            }
        }

        #endregion
    }
}
