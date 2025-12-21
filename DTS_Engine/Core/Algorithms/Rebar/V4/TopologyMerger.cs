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
    /// CRITICAL FIX: Đảm bảo governing logic - gối nào yêu cầu lớn hơn sẽ quyết định.
    /// </summary>
    public class TopologyMerger
    {
        #region Configuration

        /// <summary>Tolerance cho việc xác định cùng vị trí (m)</summary>
        public double PositionTolerance { get; set; } = 0.02;

        /// <summary>Chênh lệch số thanh tối đa cho phép khi merge</summary>
        public int MaxBarCountDifference { get; set; } = 2;

        /// <summary>Chênh lệch số lớp tối đa cho phép khi merge</summary>
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

        /// <summary>
        /// Áp dụng ràng buộc Topology lên danh sách sections.
        /// CRITICAL: Đảm bảo hai mặt cắt cùng gối LUÔN có kết quả giống nhau.
        /// </summary>
        public bool ApplyConstraints(List<DesignSection> sections)
        {
            if (sections == null || sections.Count == 0) return false;

            // Bước 1: Liên kết các cặp gối (Support Left - Support Right)
            var supportPairs = IdentifySupportPairs(sections);
            Utils.RebarLogger.Log($"TopologyMerger: Found {supportPairs.Count} support pairs");

            // Bước 2: Áp dụng GOVERNING MERGE cho từng cặp
            foreach (var pair in supportPairs)
            {
                MergeGoverning(pair);
                Utils.RebarLogger.Log($"  Merged support {pair.SupportIndex}: " +
                    $"TopOptions={pair.MergedTop?.Count ?? 0}, BotOptions={pair.MergedBot?.Count ?? 0}");
            }

            // Bước 3: Áp dụng ràng buộc Stirrup Compatibility
            ApplyStirrupCompatibility(sections);

            // Bước 4: Áp dụng ràng buộc Top-Bot Vertical Alignment
            ApplyVerticalAlignment(sections);

            // Kiểm tra còn phương án không
            return ValidateAllSectionsHaveOptions(sections);
        }

        /// <summary>
        /// Lấy danh sách các cặp gối liên kết.
        /// </summary>
        public List<SupportPair> GetSupportPairs(List<DesignSection> sections)
        {
            return IdentifySupportPairs(sections);
        }

        #endregion

        #region Support Pair Identification

        /// <summary>
        /// Đại diện cho một cặp gối liên kết.
        /// </summary>
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

            var supports = sections
                .Where(s => s.Type == SectionType.Support)
                .OrderBy(s => s.Position)
                .ToList();

            var groupedByPosition = new Dictionary<double, List<DesignSection>>();

            foreach (var support in supports)
            {
                var matchingKey = groupedByPosition.Keys
                    .Where(k => Math.Abs(k - support.Position) <= PositionTolerance)
                    .Cast<double?>()
                    .FirstOrDefault();

                if (matchingKey.HasValue)
                {
                    groupedByPosition[matchingKey.Value].Add(support);
                }
                else
                {
                    groupedByPosition[support.Position] = new List<DesignSection> { support };
                }
            }

            int supportIndex = 0;
            foreach (var group in groupedByPosition.OrderBy(g => g.Key))
            {
                if (group.Value.Count >= 2)
                {
                    var leftSection = group.Value.FirstOrDefault(s => s.IsSupportRight);
                    var rightSection = group.Value.FirstOrDefault(s => s.IsSupportLeft);

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

        #region GOVERNING MERGE - Critical Fix

        /// <summary>
        /// GOVERNING MERGE: Lấy phương án LỚN HƠN từ hai bên gối.
        /// Đảm bảo cả hai mặt cắt đều có cùng kết quả = MAX(left, right).
        /// </summary>
        private void MergeGoverning(SupportPair pair)
        {
            var left = pair.LeftSection;
            var right = pair.RightSection;

            // === MERGE TOP ===
            var mergedTop = MergeGoverningArrangements(
                left.ValidArrangementsTop,
                right.ValidArrangementsTop,
                Math.Max(left.ReqTop, right.ReqTop));

            // Nếu không tìm được intersection, tạo từ governing requirement
            if (mergedTop.Count == 0)
            {
                double governingReq = Math.Max(left.ReqTop, right.ReqTop);
                if (governingReq > 0.01)
                {
                    // Lấy từ bên có requirement lớn hơn
                    var source = left.ReqTop >= right.ReqTop 
                        ? left.ValidArrangementsTop 
                        : right.ValidArrangementsTop;
                    
                    if (source.Count > 0)
                    {
                        mergedTop = source.Select(CloneArrangement).ToList();
                    }
                }
            }

            if (mergedTop.Count == 0)
            {
                mergedTop.Add(SectionArrangement.Empty);
            }

            // === MERGE BOT ===
            var mergedBot = MergeGoverningArrangements(
                left.ValidArrangementsBot,
                right.ValidArrangementsBot,
                Math.Max(left.ReqBot, right.ReqBot));

            if (mergedBot.Count == 0)
            {
                double governingReq = Math.Max(left.ReqBot, right.ReqBot);
                if (governingReq > 0.01)
                {
                    var source = left.ReqBot >= right.ReqBot 
                        ? left.ValidArrangementsBot 
                        : right.ValidArrangementsBot;
                    
                    if (source.Count > 0)
                    {
                        mergedBot = source.Select(CloneArrangement).ToList();
                    }
                }
            }

            if (mergedBot.Count == 0)
            {
                mergedBot.Add(SectionArrangement.Empty);
            }

            // === APPLY TO BOTH SECTIONS ===
            // CRITICAL: Cả hai bên gối PHẢI có cùng danh sách arrangements
            pair.MergedTop = mergedTop;
            pair.MergedBot = mergedBot;
            pair.IsMerged = true;

            left.ValidArrangementsTop = mergedTop.Select(CloneArrangement).ToList();
            right.ValidArrangementsTop = mergedTop.Select(CloneArrangement).ToList();

            left.ValidArrangementsBot = mergedBot.Select(CloneArrangement).ToList();
            right.ValidArrangementsBot = mergedBot.Select(CloneArrangement).ToList();
        }

        /// <summary>
        /// Merge hai danh sách arrangements theo GOVERNING principle.
        /// Kết quả: Các phương án có diện tích >= governingReq, ưu tiên cùng parameters.
        /// </summary>
        private List<SectionArrangement> MergeGoverningArrangements(
            List<SectionArrangement> list1,
            List<SectionArrangement> list2,
            double governingReq)
        {
            var result = new List<SectionArrangement>();

            if ((list1 == null || list1.Count == 0) && (list2 == null || list2.Count == 0))
            {
                return result;
            }

            // Combine and filter by governing requirement
            var allArrangements = new List<SectionArrangement>();
            if (list1 != null) allArrangements.AddRange(list1);
            if (list2 != null) allArrangements.AddRange(list2);

            // Filter: Chỉ giữ những arrangement đủ cho governing requirement
            var validArrangements = allArrangements
                .Where(a => a.TotalArea >= governingReq * 0.98) // Allow 2% tolerance
                .OrderBy(a => a.TotalArea) // Prefer smallest that meets requirement
                .ThenByDescending(a => a.Score)
                .ToList();

            // Group by (Diameter, TotalCount) to find common solutions
            var groups = validArrangements
                .GroupBy(a => new { a.PrimaryDiameter, a.TotalCount })
                .OrderByDescending(g => g.Count()) // Prefer solutions present in both lists
                .ThenBy(g => g.Key.TotalCount) // Then prefer fewer bars
                .ThenByDescending(g => g.Key.PrimaryDiameter); // Then prefer larger diameter

            foreach (var group in groups)
            {
                // Take best from each group
                var best = group.OrderByDescending(a => a.Score).First();
                var cloned = CloneArrangement(best);
                
                // Recalculate efficiency based on governing requirement
                if (governingReq > 0.01)
                {
                    cloned.Efficiency = cloned.TotalArea / governingReq;
                }

                if (!result.Any(r => r.PrimaryDiameter == cloned.PrimaryDiameter && 
                                     r.TotalCount == cloned.TotalCount))
                {
                    result.Add(cloned);
                }

                // Limit results
                if (result.Count >= 10) break;
            }

            return result;
        }

        #endregion

        #region Additional Constraints

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
                        {
                            validTopBotPairs.Add((topArr, botArr));
                        }
                    }
                }

                if (validTopBotPairs.Count > 0)
                {
                    var validTops = validTopBotPairs.Select(c => c.top).Distinct().ToList();
                    var validBots = validTopBotPairs.Select(c => c.bot).Distinct().ToList();

                    section.ValidArrangementsTop = section.ValidArrangementsTop
                        .Where(a => validTops.Any(v => v.Equals(a)))
                        .ToList();

                    section.ValidArrangementsBot = section.ValidArrangementsBot
                        .Where(a => validBots.Any(v => v.Equals(a)))
                        .ToList();
                }
            }
        }

        private bool IsStirrupCompatible(SectionArrangement topArr, SectionArrangement botArr, DesignSection section)
        {
            if (topArr.TotalCount == 0 && botArr.TotalCount == 0) return true;

            int topCount = topArr.TotalCount;
            int botCount = botArr.TotalCount;

            int legs = _settings.Stirrup?.GetLegCount(
                Math.Max(topCount, botCount),
                hasAddon: topCount > 2 || botCount > 2) ?? 2;

            int cells = legs - 1;
            if (cells <= 0) return topCount <= 2 && botCount <= 2;

            int maxBars = 2 * cells + 2;
            return topCount <= maxBars && botCount <= maxBars;
        }

        private void ApplyVerticalAlignment(List<DesignSection> sections)
        {
            if (_settings.Beam?.PreferVerticalAlignment != true) return;

            foreach (var section in sections)
            {
                var alignedPairs = new List<(SectionArrangement top, SectionArrangement bot)>();

                foreach (var topArr in section.ValidArrangementsTop)
                {
                    foreach (var botArr in section.ValidArrangementsBot)
                    {
                        if (topArr.IsEvenCount == botArr.IsEvenCount)
                        {
                            alignedPairs.Add((topArr, botArr));
                        }
                    }
                }

                if (alignedPairs.Count > 0)
                {
                    var alignedTops = alignedPairs.Select(p => p.top).Distinct().ToList();
                    var alignedBots = alignedPairs.Select(p => p.bot).Distinct().ToList();

                    foreach (var arr in section.ValidArrangementsTop)
                    {
                        if (!alignedTops.Any(v => v.Equals(arr)))
                        {
                            arr.Score = Math.Max(0, arr.Score - 5);
                        }
                    }

                    foreach (var arr in section.ValidArrangementsBot)
                    {
                        if (!alignedBots.Any(v => v.Equals(arr)))
                        {
                            arr.Score = Math.Max(0, arr.Score - 5);
                        }
                    }
                }
            }
        }

        #endregion

        #region Validation

        private bool ValidateAllSectionsHaveOptions(List<DesignSection> sections)
        {
            foreach (var section in sections)
            {
                bool topOk = section.ReqTop <= 0.01 || section.ValidArrangementsTop.Count > 0;
                bool botOk = section.ReqBot <= 0.01 || section.ValidArrangementsBot.Count > 0;

                if (!topOk || !botOk)
                {
                    Utils.RebarLogger.LogError($"Section {section.SectionId} has no valid arrangements: TopOK={topOk}, BotOK={botOk}");
                    return false;
                }
            }

            return true;
        }

        #endregion

        #region Helpers

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
