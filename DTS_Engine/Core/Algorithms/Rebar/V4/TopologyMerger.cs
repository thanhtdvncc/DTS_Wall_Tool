using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// Áp dụng ràng buộc Topology để đồng bộ hóa phương án giữa các mặt cắt liên quan.
    /// Xử lý ràng buộc Type 3 (Gối đỡ): Left và Right của cùng gối phải thống nhất.
    /// Hỗ trợ N nhịp linh hoạt, không giới hạn số gối.
    /// 
    /// ISO 25010: Functional Correctness - Constraint Propagation pattern.
    /// ISO 12207: Design Phase - Clean separation of concerns.
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
        /// Bước này sẽ prune (cắt tỉa) các phương án không tương thích.
        /// </summary>
        /// <param name="sections">Danh sách sections đã được giải bởi SectionSolver</param>
        /// <returns>True nếu còn ít nhất 1 phương án hợp lệ cho mỗi section</returns>
        public bool ApplyConstraints(List<DesignSection> sections)
        {
            if (sections == null || sections.Count == 0) return false;

            // Bước 1: Liên kết các cặp gối (Support Left - Support Right)
            var supportPairs = IdentifySupportPairs(sections);

            // Bước 2: Áp dụng ràng buộc Type 3 (Gối) cho từng cặp
            if (!MergeAllSupportPairs(supportPairs))
            {
                return false;
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
            /// <summary>Index của gối (0 = gối đầu tiên)</summary>
            public int SupportIndex { get; set; }

            /// <summary>Section bên trái (Right of left span)</summary>
            public DesignSection LeftSection { get; set; }

            /// <summary>Section bên phải (Left of right span)</summary>
            public DesignSection RightSection { get; set; }

            /// <summary>Vị trí gối (m từ đầu dầm)</summary>
            public double Position { get; set; }

            /// <summary>True nếu đã merge thành công</summary>
            public bool IsMerged { get; set; }

            /// <summary>Phương án TOP đã merge (governing)</summary>
            public List<SectionArrangement> MergedTop { get; set; }

            /// <summary>Phương án BOT đã merge (governing)</summary>
            public List<SectionArrangement> MergedBot { get; set; }
        }

        /// <summary>
        /// Xác định tất cả các cặp gối trong dải dầm.
        /// </summary>
        private List<SupportPair> IdentifySupportPairs(List<DesignSection> sections)
        {
            var pairs = new List<SupportPair>();

            // Tìm các section Type=Support
            var supports = sections
                .Where(s => s.Type == SectionType.Support)
                .OrderBy(s => s.Position)
                .ToList();

            // Nhóm theo vị trí (cùng gối)
            var groupedByPosition = new Dictionary<double, List<DesignSection>>();

            foreach (var support in supports)
            {
                // Tìm key đã có (trong tolerance)
                // NOTE: FirstOrDefault on double returns 0.0, not null - use Any() check instead
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

            // Tạo cặp từ các nhóm có >= 2 sections
            int supportIndex = 0;
            foreach (var group in groupedByPosition.OrderBy(g => g.Key))
            {
                if (group.Value.Count >= 2)
                {
                    // Xác định Left (IsSupportRight=true) và Right (IsSupportLeft=true)
                    var leftSection = group.Value.FirstOrDefault(s => s.IsSupportRight);
                    var rightSection = group.Value.FirstOrDefault(s => s.IsSupportLeft);

                    if (leftSection != null && rightSection != null && leftSection != rightSection)
                    {
                        // Liên kết 2 chiều
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

        #region Merge Logic

        /// <summary>
        /// Merge tất cả các cặp gối.
        /// </summary>
        private bool MergeAllSupportPairs(List<SupportPair> pairs)
        {
            foreach (var pair in pairs)
            {
                if (!MergeSinglePair(pair))
                {
                    // Log warning nhưng tiếp tục (có thể relax sau)
                    Utils.RebarLogger.LogError($"Merge failed at support {pair.SupportIndex} (Pos={pair.Position:F2}m)");

                    // Thử relaxed merge
                    if (!MergeSinglePairRelaxed(pair))
                    {
                        return false;
                    }
                }

                // Áp dụng kết quả merge về cả 2 sections
                if (pair.IsMerged)
                {
                    pair.LeftSection.ValidArrangementsTop = pair.MergedTop;
                    pair.RightSection.ValidArrangementsTop = pair.MergedTop;

                    if (pair.MergedBot != null && pair.MergedBot.Count > 0)
                    {
                        pair.LeftSection.ValidArrangementsBot = pair.MergedBot;
                        pair.RightSection.ValidArrangementsBot = pair.MergedBot;
                    }
                }
            }

            return true;
        }

        /// <summary>
        /// Merge một cặp gối (strict mode).
        /// </summary>
        private bool MergeSinglePair(SupportPair pair)
        {
            var left = pair.LeftSection;
            var right = pair.RightSection;

            // Merge TOP
            var mergedTop = FindCompatibleArrangements(
                left.ValidArrangementsTop,
                right.ValidArrangementsTop,
                strict: true);

            if (mergedTop.Count == 0 && left.ReqTop > 0.01 && right.ReqTop > 0.01)
            {
                return false;
            }

            // Merge BOT
            var mergedBot = FindCompatibleArrangements(
                left.ValidArrangementsBot,
                right.ValidArrangementsBot,
                strict: true);

            // BOT tại gối thường nhỏ, cho phép flexible hơn
            if (mergedBot.Count == 0)
            {
                // Lấy max của 2 bên
                mergedBot = TakeGoverningArrangements(left.ValidArrangementsBot, right.ValidArrangementsBot);
            }

            pair.MergedTop = mergedTop;
            pair.MergedBot = mergedBot;
            pair.IsMerged = true;

            return true;
        }

        /// <summary>
        /// Merge một cặp gối (relaxed mode - khi strict fails).
        /// </summary>
        private bool MergeSinglePairRelaxed(SupportPair pair)
        {
            var left = pair.LeftSection;
            var right = pair.RightSection;

            // Relax: Lấy governing (lớn hơn) cho TOP
            var mergedTop = TakeGoverningArrangements(
                left.ValidArrangementsTop,
                right.ValidArrangementsTop);

            if (mergedTop.Count == 0)
            {
                // Fallback: Tạo phương án từ max requirement
                double maxReq = Math.Max(left.ReqTop, right.ReqTop);
                if (maxReq > 0.01)
                {
                    // Không thể tạo - fail
                    return false;
                }
                mergedTop.Add(SectionArrangement.Empty);
            }

            // Relax: BOT
            var mergedBot = TakeGoverningArrangements(
                left.ValidArrangementsBot,
                right.ValidArrangementsBot);

            if (mergedBot.Count == 0)
            {
                mergedBot.Add(SectionArrangement.Empty);
            }

            pair.MergedTop = mergedTop;
            pair.MergedBot = mergedBot;
            pair.IsMerged = true;

            return true;
        }

        /// <summary>
        /// Tìm các phương án tương thích giữa 2 danh sách.
        /// </summary>
        private List<SectionArrangement> FindCompatibleArrangements(
            List<SectionArrangement> list1,
            List<SectionArrangement> list2,
            bool strict)
        {
            var result = new List<SectionArrangement>();

            // Handle empty cases
            if ((list1 == null || list1.Count == 0) && (list2 == null || list2.Count == 0))
            {
                result.Add(SectionArrangement.Empty);
                return result;
            }

            if (list1 == null || list1.Count == 0) return new List<SectionArrangement>(list2 ?? new List<SectionArrangement>());
            if (list2 == null || list2.Count == 0) return new List<SectionArrangement>(list1);

            // Find intersection
            foreach (var arr1 in list1)
            {
                foreach (var arr2 in list2)
                {
                    bool compatible = strict
                        ? AreStrictlyCompatible(arr1, arr2)
                        : AreLooselyCompatible(arr1, arr2);

                    if (compatible)
                    {
                        // Lấy governing (diện tích lớn hơn)
                        var governing = arr1.TotalArea >= arr2.TotalArea ? arr1 : arr2;

                        if (!result.Any(r => r.Equals(governing)))
                        {
                            var cloned = CloneArrangement(governing);
                            // Điều chỉnh score
                            cloned.Score = (arr1.Score + arr2.Score) / 2.0;
                            result.Add(cloned);
                        }
                    }
                }
            }

            // Sort by score
            return result.OrderByDescending(r => r.Score).ToList();
        }

        /// <summary>
        /// Lấy phương án governing từ 2 danh sách.
        /// </summary>
        private List<SectionArrangement> TakeGoverningArrangements(
            List<SectionArrangement> list1,
            List<SectionArrangement> list2)
        {
            // Tìm phương án có diện tích lớn nhất từ mỗi bên
            var max1 = list1?.OrderByDescending(a => a.TotalArea).FirstOrDefault();
            var max2 = list2?.OrderByDescending(a => a.TotalArea).FirstOrDefault();

            var result = new List<SectionArrangement>();

            if (max1 == null && max2 == null)
            {
                result.Add(SectionArrangement.Empty);
                return result;
            }

            if (max1 == null) { result.Add(CloneArrangement(max2)); return result; }
            if (max2 == null) { result.Add(CloneArrangement(max1)); return result; }

            // Lấy cái lớn hơn
            var governing = max1.TotalArea >= max2.TotalArea ? max1 : max2;
            result.Add(CloneArrangement(governing));

            return result;
        }

        /// <summary>
        /// Kiểm tra 2 phương án có tương thích nghiêm ngặt không.
        /// </summary>
        private bool AreStrictlyCompatible(SectionArrangement arr1, SectionArrangement arr2)
        {
            // Cùng đường kính chính
            if (arr1.PrimaryDiameter != arr2.PrimaryDiameter) return false;

            // Chênh lệch số thanh trong giới hạn
            if (Math.Abs(arr1.TotalCount - arr2.TotalCount) > MaxBarCountDifference) return false;

            // Cùng số lớp hoặc chênh trong giới hạn
            if (Math.Abs(arr1.LayerCount - arr2.LayerCount) > MaxLayerCountDifference) return false;

            return true;
        }

        /// <summary>
        /// Kiểm tra 2 phương án có tương thích lỏng lẻo không.
        /// </summary>
        private bool AreLooselyCompatible(SectionArrangement arr1, SectionArrangement arr2)
        {
            // Chỉ cần cùng đường kính
            if (arr1.PrimaryDiameter != arr2.PrimaryDiameter) return false;

            return true;
        }

        #endregion

        #region Additional Constraints

        /// <summary>
        /// Áp dụng ràng buộc Đai (Stirrup Compatibility).
        /// </summary>
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
                    // Lọc chỉ giữ các phương án có trong combinations hợp lệ
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

        /// <summary>
        /// Kiểm tra Top-Bot có phù hợp với layout đai không.
        /// </summary>
        private bool IsStirrupCompatible(SectionArrangement topArr, SectionArrangement botArr, DesignSection section)
        {
            if (topArr.TotalCount == 0 && botArr.TotalCount == 0) return true;

            int topCount = topArr.TotalCount;
            int botCount = botArr.TotalCount;

            // Lấy số nhánh đai từ StirrupConfig
            int legs = _settings.Stirrup?.GetLegCount(
                Math.Max(topCount, botCount),
                hasAddon: topCount > 2 || botCount > 2) ?? 2;

            // Số thanh tối đa có thể bố trí với số nhánh đai này
            int cells = legs - 1;
            if (cells <= 0) return topCount <= 2 && botCount <= 2;

            int maxBars = 2 * cells + 2;
            return topCount <= maxBars && botCount <= maxBars;
        }

        /// <summary>
        /// Áp dụng ràng buộc Vertical Alignment (Top-Bot cùng chẵn/lẻ).
        /// </summary>
        private void ApplyVerticalAlignment(List<DesignSection> sections)
        {
            if (_settings.Beam?.PreferVerticalAlignment != true) return;

            foreach (var section in sections)
            {
                // Tìm các cặp Top-Bot cùng chẵn/lẻ
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

                // Penalty cho các phương án không aligned
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

        /// <summary>
        /// Kiểm tra tất cả sections có ít nhất 1 phương án.
        /// </summary>
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

        /// <summary>
        /// Clone một SectionArrangement.
        /// </summary>
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
