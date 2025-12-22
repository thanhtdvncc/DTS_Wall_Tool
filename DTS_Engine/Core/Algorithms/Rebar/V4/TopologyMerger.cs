using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// Áp dụng ràng buộc Topology để đồng bộ hóa phương án giữa các mặt cắt liên quan.
    /// Xử lý ràng buộc Type 3 (Gối đỡ): Left và Right của cùng gối phải thống nhất.
    /// 
    /// SMART UPDATE:
    /// - Sử dụng cơ chế Intersection thông minh.
    /// - Validate chặt chẽ với yêu cầu gốc (Original Requirement).
    /// - Fail-Safe: Nếu không thể merge hợp lý, giữ nguyên phương án riêng để GlobalOptimizer xử lý Addon.
    /// </summary>
    public class TopologyMerger
    {
        #region Configuration

        public double PositionTolerance { get; set; } = 0.02;
        public int MaxBarCountDifference { get; set; } = 2;
        public int MaxLayerCountDifference { get; set; } = 1;

        #endregion

        #region Dependencies

        private readonly DtsSettings _settings;

        #endregion

        #region Constructor

        public TopologyMerger(DtsSettings settings)
        {
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        }

        #endregion

        #region Public API

        public bool ApplyConstraints(List<DesignSection> sections)
        {
            if (sections == null || sections.Count == 0) return false;

            // Bước 1: Liên kết các cặp gối
            var supportPairs = IdentifySupportPairs(sections);
            Utils.RebarLogger.Log($"TopologyMerger: Found {supportPairs.Count} support pairs");

            // Bước 2: Áp dụng SMART MERGE
            foreach (var pair in supportPairs)
            {
                MergeSmart(pair);
            }

            // Bước 3 & 4: Các ràng buộc phụ
            ApplyStirrupCompatibility(sections);
            ApplyVerticalAlignment(sections);

            return ValidateAllSectionsHaveOptions(sections);
        }

        public List<SupportPair> GetSupportPairs(List<DesignSection> sections)
        {
            return IdentifySupportPairs(sections);
        }

        #endregion

        #region Support Pair Identification

        public class SupportPair
        {
            public int SupportIndex { get; set; }
            public DesignSection LeftSection { get; set; }
            public DesignSection RightSection { get; set; }
            public double Position { get; set; }
            public bool IsMerged { get; set; }
            public List<SectionArrangement> MergedTop { get; set; }
            public List<SectionArrangement> MergedBot { get; set; }
        }

        private List<SupportPair> IdentifySupportPairs(List<DesignSection> sections)
        {
            var pairs = new List<SupportPair>();
            var supports = sections.Where(s => s.Type == SectionType.Support).OrderBy(s => s.Position).ToList();
            var groupedByPosition = new Dictionary<double, List<DesignSection>>();

            foreach (var support in supports)
            {
                var matchingKey = groupedByPosition.Keys
                    .Where(k => Math.Abs(k - support.Position) <= PositionTolerance)
                    .Cast<double?>().FirstOrDefault();

                if (matchingKey.HasValue) groupedByPosition[matchingKey.Value].Add(support);
                else groupedByPosition[support.Position] = new List<DesignSection> { support };
            }

            int supportIndex = 0;
            foreach (var group in groupedByPosition.OrderBy(g => g.Key))
            {
                if (group.Value.Count >= 2)
                {
                    var leftSection = group.Value.FirstOrDefault(s => s.IsSupportRight); // End of left span
                    var rightSection = group.Value.FirstOrDefault(s => s.IsSupportLeft); // Start of right span

                    if (leftSection != null && rightSection != null && leftSection != rightSection)
                    {
                        leftSection.LinkedSection = rightSection;
                        rightSection.LinkedSection = leftSection;

                        pairs.Add(new SupportPair
                        {
                            SupportIndex = supportIndex,
                            LeftSection = leftSection,
                            RightSection = rightSection,
                            Position = group.Key
                        });
                    }
                }
                supportIndex++;
            }
            return pairs;
        }

        #endregion

        #region SMART MERGE LOGIC

        /// <summary>
        /// Thực hiện Merge thông minh với Validation.
        /// </summary>
        private void MergeSmart(SupportPair pair)
        {
            var left = pair.LeftSection;
            var right = pair.RightSection;

            Utils.RebarLogger.Log($"MERGE Support {pair.SupportIndex} (Pos {pair.Position:F1}):");

            // --- MERGE TOP ---
            var mergedTop = ExecuteMergeStrategy(left.ValidArrangementsTop, right.ValidArrangementsTop,
                                                 left.ReqTop, right.ReqTop, "Top");

            if (mergedTop != null && mergedTop.Count > 0)
            {
                // Merge Success: Apply to both
                pair.MergedTop = mergedTop;
                left.ValidArrangementsTop = mergedTop.Select(CloneArrangement).ToList();
                right.ValidArrangementsTop = mergedTop.Select(CloneArrangement).ToList();
                LogMergeDetails("Top", mergedTop);
                Utils.RebarLogger.Log($"  -> Top Merged: {mergedTop.Count} options shared.");
            }
            else
            {
                // Merge Failed/Rejected: Keep separate
                Utils.RebarLogger.Log($"  -> Top Merge ABORTED. Keeping separate (Left={left.ValidArrangementsTop.Count}, Right={right.ValidArrangementsTop.Count}). GlobalOptimizer will handle.");
            }

            // --- MERGE BOT ---
            var mergedBot = ExecuteMergeStrategy(left.ValidArrangementsBot, right.ValidArrangementsBot,
                                                 left.ReqBot, right.ReqBot, "Bot");

            if (mergedBot != null && mergedBot.Count > 0)
            {
                pair.MergedBot = mergedBot;
                left.ValidArrangementsBot = mergedBot.Select(CloneArrangement).ToList();
                right.ValidArrangementsBot = mergedBot.Select(CloneArrangement).ToList();
                LogMergeDetails("Bot", mergedBot);
                Utils.RebarLogger.Log($"  -> Bot Merged: {mergedBot.Count} options shared.");
            }
            else
            {
                Utils.RebarLogger.Log($"  -> Bot Merge ABORTED. Keeping separate.");
            }

            pair.IsMerged = (mergedTop != null || mergedBot != null);
        }

        private void LogMergeDetails(string side, List<SectionArrangement> list)
        {
            if (list == null || list.Count == 0) return;
            var details = list.Take(5).Select(a => $"{a.TotalCount}D{a.PrimaryDiameter}({a.TotalArea:F2})");
            string more = list.Count > 5 ? "..." : "";
            Utils.RebarLogger.Log($"    {side}: {string.Join(", ", details)}{more}");
        }

        /// <summary>
        /// Chiến lược cốt lõi: Intersection -> Fallback -> Validate Original Req.
        /// Trả về null nếu không tìm được phương án hợp lý.
        /// </summary>
        private List<SectionArrangement> ExecuteMergeStrategy(
            List<SectionArrangement> list1,
            List<SectionArrangement> list2,
            double req1,
            double req2,
            string sideName)
        {
            double governingReq = Math.Max(req1, req2);
            double safetyFactor = _settings.Rules?.SafetyFactor ?? 1.0;
            double tolerance = 1.0 - safetyFactor;
            if (tolerance < 0) tolerance = 0;

            // 1. Filter BOTH lists by Governing Req first
            var valid1 = list1?.Where(a => a.TotalArea >= governingReq * (1 - tolerance)).ToList() ?? new List<SectionArrangement>();
            var valid2 = list2?.Where(a => a.TotalArea >= governingReq * (1 - tolerance)).ToList() ?? new List<SectionArrangement>();

            List<SectionArrangement> candidates = new List<SectionArrangement>();

            // 2. Try Find Intersection (Giao thoa)
            var intersection = FindIntersection(valid1, valid2);

            if (intersection.Count > 0)
            {
                candidates = intersection;
            }
            else
            {
                // 3. Fallback: Intersection rỗng - Lấy từ Governing Side
                if (req1 >= req2 && valid1.Count > 0)
                {
                    candidates = valid1.Select(CloneArrangement).ToList();
                }
                else if (req2 > req1 && valid2.Count > 0)
                {
                    candidates = valid2.Select(CloneArrangement).ToList();
                }
                else
                {
                    // Cả 2 đều rỗng -> Fail
                    return null;
                }
            }

            // 4. CRITICAL: Post-Merge Validation
            if (!ValidateMergedCandidates(candidates, req1, tolerance) ||
                !ValidateMergedCandidates(candidates, req2, tolerance))
            {
                // Nếu vi phạm yêu cầu gốc -> Fail
                return null;
            }

            return candidates;
        }

        private List<SectionArrangement> FindIntersection(List<SectionArrangement> list1, List<SectionArrangement> list2)
        {
            var result = new List<SectionArrangement>();

            if (list1 == null || list2 == null) return result;

            foreach (var item1 in list1)
            {
                var match = list2.FirstOrDefault(item2 =>
                    item2.PrimaryDiameter == item1.PrimaryDiameter &&
                    item2.TotalCount == item1.TotalCount &&
                    Math.Abs(item2.LayerCount - item1.LayerCount) <= MaxLayerCountDifference);

                if (match != null)
                {
                    result.Add(CloneArrangement(item1.Score >= match.Score ? item1 : match));
                }
            }

            return result.GroupBy(x => new { x.PrimaryDiameter, x.TotalCount })
                         .Select(g => g.First())
                         .OrderByDescending(a => a.Score)
                         .ToList();
        }

        private bool ValidateMergedCandidates(List<SectionArrangement> candidates, double reqArea, double tolerance)
        {
            if (reqArea <= 0.01) return true;
            if (candidates == null || candidates.Count == 0) return false;
            return candidates.Any(a => a.TotalArea >= reqArea * (1 - tolerance));
        }

        #endregion

        #region Additional Constraints (Stirrup & Vertical)

        private void ApplyStirrupCompatibility(List<DesignSection> sections)
        {
            if (_settings.Stirrup?.EnableAdvancedRules != true) return;

            foreach (var section in sections)
            {
                var validTopBotPairs = new List<(SectionArrangement top, SectionArrangement bot)>();
                foreach (var topArr in section.ValidArrangementsTop)
                {
                    foreach (var botArr in section.ValidArrangementsBot)
                    {
                        if (IsStirrupCompatible(topArr, botArr, section))
                            validTopBotPairs.Add((topArr, botArr));
                    }
                }

                if (validTopBotPairs.Count > 0)
                {
                    var validTops = validTopBotPairs.Select(c => c.top).Distinct().ToList();
                    var validBots = validTopBotPairs.Select(c => c.bot).Distinct().ToList();

                    section.ValidArrangementsTop = section.ValidArrangementsTop.Intersect(validTops).ToList();
                    section.ValidArrangementsBot = section.ValidArrangementsBot.Intersect(validBots).ToList();
                }
            }
        }

        private bool IsStirrupCompatible(SectionArrangement topArr, SectionArrangement botArr, DesignSection section)
        {
            if (topArr.TotalCount == 0 && botArr.TotalCount == 0) return true;
            int topCount = topArr.TotalCount;
            int botCount = botArr.TotalCount;
            int legs = _settings.Stirrup?.GetLegCount(Math.Max(topCount, botCount), topCount > 2 || botCount > 2) ?? 2;
            int cells = legs - 1;
            if (cells <= 0) return topCount <= 2 && botCount <= 2;
            int maxBars = 2 * cells + 2;
            return topCount <= maxBars && botCount <= maxBars;
        }

        private void ApplyVerticalAlignment(List<DesignSection> sections)
        {
            if (_settings.Beam?.PreferVerticalAlignment != true) return;

            double alignmentPenalty = _settings.Rules?.AlignmentPenaltyScore ?? 25.0;
            double scaledPenalty = alignmentPenalty / 5.0;

            foreach (var section in sections)
            {
                var alignedPairs = new List<(SectionArrangement top, SectionArrangement bot)>();
                foreach (var topArr in section.ValidArrangementsTop)
                {
                    foreach (var botArr in section.ValidArrangementsBot)
                    {
                        if (topArr.IsEvenCount == botArr.IsEvenCount)
                            alignedPairs.Add((topArr, botArr));
                    }
                }

                if (alignedPairs.Count > 0)
                {
                    var alignedTops = alignedPairs.Select(p => p.top).Distinct().ToList();
                    var alignedBots = alignedPairs.Select(p => p.bot).Distinct().ToList();

                    foreach (var arr in section.ValidArrangementsTop)
                        if (!alignedTops.Contains(arr)) arr.Score = Math.Max(0, arr.Score - scaledPenalty);

                    foreach (var arr in section.ValidArrangementsBot)
                        if (!alignedBots.Contains(arr)) arr.Score = Math.Max(0, arr.Score - scaledPenalty);
                }
            }
        }

        #endregion

        #region Validation & Helpers

        private bool ValidateAllSectionsHaveOptions(List<DesignSection> sections)
        {
            bool allOk = true;
            foreach (var section in sections)
            {
                bool topOk = section.ReqTop <= 0.01 || section.ValidArrangementsTop.Count > 0;
                bool botOk = section.ReqBot <= 0.01 || section.ValidArrangementsBot.Count > 0;

                if (!topOk || !botOk)
                {
                    Utils.RebarLogger.LogError($"Section {section.SectionId} has no valid arrangements post-merge: TopOK={topOk}, BotOK={botOk}");
                    allOk = false;
                }
            }
            return allOk;
        }

        private SectionArrangement CloneArrangement(SectionArrangement source)
        {
            if (source == null) return SectionArrangement.Empty;
            return new SectionArrangement
            {
                TotalCount = source.TotalCount,
                TotalArea = source.TotalArea,
                LayerCount = source.LayerCount,
                BarsPerLayer = new List<int>(source.BarsPerLayer ?? new List<int>()),
                DiametersPerLayer = new List<int>(source.DiametersPerLayer ?? new List<int>()),
                PrimaryDiameter = source.PrimaryDiameter,
                BarDiameters = new List<int>(source.BarDiameters ?? new List<int>()),
                IsSymmetric = source.IsSymmetric,
                FitsStirrupLayout = source.FitsStirrupLayout,
                ClearSpacing = source.ClearSpacing,
                VerticalSpacing = source.VerticalSpacing,
                Score = source.Score,
                WasteCount = source.WasteCount,
                Efficiency = source.Efficiency
            };
        }

        #endregion
    }
}
