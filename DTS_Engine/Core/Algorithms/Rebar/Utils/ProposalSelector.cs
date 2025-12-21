using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.Utils
{
    /// <summary>
    /// V3.5: Strategy-based proposal selector.
    /// Selects diverse solutions representing different engineering strategies.
    /// Avoids returning 5 nearly identical proposals.
    /// </summary>
    public static class ProposalSelector
    {
        /// <summary>
        /// Chọn ra Top N giải pháp dựa trên các chiến lược kỹ thuật khác nhau.
        /// Tránh việc trả về 5 giải pháp giống hệt nhau.
        /// </summary>
        /// <param name="allProposals">All valid proposals from pipeline</param>
        /// <param name="maxCount">Maximum number of proposals to return (default 5)</param>
        /// <returns>Diverse set of proposals with StrategyLabel assigned</returns>
        public static List<ContinuousBeamSolution> SelectDiverseSolutions(
            IEnumerable<ContinuousBeamSolution> allProposals,
            int maxCount = 5)
        {
            var validProposals = allProposals?.Where(p => p != null && p.IsValid).ToList()
                                 ?? new List<ContinuousBeamSolution>();
            var results = new List<ContinuousBeamSolution>();

            if (!validProposals.Any()) return results;

            // ═══════════════════════════════════════════════════════════════
            // STRATEGY 1: BEST BALANCED (Điểm hiệu quả cao nhất)
            // ═══════════════════════════════════════════════════════════════
            var bestScore = validProposals
                .OrderByDescending(p => p.EfficiencyScore)
                .ThenByDescending(p => p.ConstructabilityScore)
                .FirstOrDefault();

            if (bestScore != null)
            {
                bestScore.StrategyLabel = "Tối ưu nhất";
                results.Add(bestScore);
            }

            // ═══════════════════════════════════════════════════════════════
            // STRATEGY 2: MOST ECONOMICAL (Trọng lượng thép nhẹ nhất)
            // ═══════════════════════════════════════════════════════════════
            var mostEconomical = validProposals
                .Except(results)
                .OrderBy(p => p.TotalSteelWeight)
                .ThenByDescending(p => p.EfficiencyScore)
                .FirstOrDefault();

            if (mostEconomical != null)
            {
                mostEconomical.StrategyLabel = "Tiết kiệm nhất";
                results.Add(mostEconomical);
            }

            // ═══════════════════════════════════════════════════════════════
            // STRATEGY 3: ROBUST/SAFE (Backbone lớn, ít phụ thuộc thép tăng cường)
            // ═══════════════════════════════════════════════════════════════
            var robust = validProposals
                .Except(results)
                .OrderByDescending(p => p.BackboneCount_Top + p.BackboneCount_Bot)
                .ThenByDescending(p => p.BackboneDiameter)
                .ThenByDescending(p => p.EfficiencyScore)
                .FirstOrDefault();

            if (robust != null)
            {
                robust.StrategyLabel = "An toàn (Backbone lớn)";
                results.Add(robust);
            }

            // ═══════════════════════════════════════════════════════════════
            // STRATEGY 4: CONSTRUCTION FRIENDLY (Dễ thi công - ít lớp thép)
            // ═══════════════════════════════════════════════════════════════
            var simple = validProposals
                .Except(results)
                .OrderBy(p => CountLayer2Positions(p))  // Ít vị trí lên lớp 2
                .ThenBy(p => p.Reinforcements?.Count ?? 0)  // Ít vị trí cắt thép
                .ThenByDescending(p => p.ConstructabilityScore)
                .FirstOrDefault();

            if (simple != null)
            {
                simple.StrategyLabel = "Dễ thi công";
                results.Add(simple);
            }

            // ═══════════════════════════════════════════════════════════════
            // STRATEGY 5: HARMONIOUS (Đồng bộ đường kính - Addon = Backbone)
            // ═══════════════════════════════════════════════════════════════
            var harmonious = validProposals
                .Except(results)
                .OrderByDescending(p => CalculateUniformityScore(p))
                .ThenByDescending(p => p.EfficiencyScore)
                .FirstOrDefault();

            if (harmonious != null)
            {
                harmonious.StrategyLabel = "Đồng bộ ĐK";
                results.Add(harmonious);
            }

            // ═══════════════════════════════════════════════════════════════
            // FILLER: Điền nốt bằng các phương án điểm cao còn lại
            // ═══════════════════════════════════════════════════════════════
            if (results.Count < maxCount)
            {
                var remainders = validProposals
                    .Except(results)
                    .OrderByDescending(p => p.EfficiencyScore)
                    .Take(maxCount - results.Count)
                    .ToList();

                foreach (var r in remainders)
                {
                    if (string.IsNullOrEmpty(r.StrategyLabel))
                        r.StrategyLabel = "Phương án khác";
                    results.Add(r);
                }
            }

            return results;
        }

        /// <summary>
        /// Count positions that require Layer 2 bars (harder construction).
        /// </summary>
        private static int CountLayer2Positions(ContinuousBeamSolution sol)
        {
            if (sol?.Reinforcements == null) return 0;
            return sol.Reinforcements.Values.Count(r => r.Layer >= 2);
        }

        /// <summary>
        /// Calculate uniformity score (higher = more uniform diameters).
        /// Addon bars matching backbone diameter get bonus points.
        /// </summary>
        private static int CalculateUniformityScore(ContinuousBeamSolution sol)
        {
            if (sol?.Reinforcements == null) return 0;

            int score = 0;
            foreach (var r in sol.Reinforcements.Values)
            {
                // Bonus if addon diameter matches backbone (Top or Bot)
                if (r.Diameter == sol.BackboneDiameter_Top || r.Diameter == sol.BackboneDiameter_Bot)
                    score += 2;
                // Partial bonus if addon diameter is similar (±2mm)
                else if (Math.Abs(r.Diameter - sol.BackboneDiameter_Top) <= 2 ||
                         Math.Abs(r.Diameter - sol.BackboneDiameter_Bot) <= 2)
                    score += 1;
            }

            // Extra bonus if Top and Bot backbone are same diameter
            if (sol.BackboneDiameter_Top == sol.BackboneDiameter_Bot)
                score += 5;

            return score;
        }
    }
}
