using DTS_Engine.Core.Algorithms.Rebar.Models;

namespace DTS_Engine.Core.Algorithms.Rebar.Rules
{
    /// <summary>
    /// Rule phạt điểm nặng cho các phương án có thép lãng phí do ràng buộc cấu tạo.
    /// VD: Bump từ 3+1 lên 3+2 = 1 thanh waste → -20 điểm/thanh
    /// Mục đích: Ưu tiên phương án 2+2 (không waste) thắng 3+2 (có waste)
    /// </summary>
    public class WastePenaltyRule : IDesignRule
    {
        public string RuleName { get { return "WastePenalty"; } }
        public int Priority { get { return 15; } } // Run after other rules

        /// <summary>
        /// Điểm phạt cho mỗi thanh waste (thanh thêm vào chỉ để đủ cấu tạo, không đóng góp lực)
        /// </summary>
        private const double PENALTY_PER_WASTE_BAR = 20.0;

        public ValidationResult Validate(SolutionContext context)
        {
            if (context?.CurrentSolution == null)
                return ValidationResult.Pass(RuleName);

            int totalWaste = context.AccumulatedWasteCount;

            if (totalWaste <= 0)
                return ValidationResult.Pass(RuleName);

            // Tính penalty và trừ vào ConstructabilityScore
            double penaltyPerCount = context.Settings?.Rules?.WastePenaltyScore ?? 20.0;
            double penalty = totalWaste * penaltyPerCount;
            context.CurrentSolution.ConstructabilityScore -= penalty;

            return new ValidationResult
            {
                RuleName = RuleName,
                Level = SeverityLevel.Warning,
                PenaltyScore = penalty,
                Message = $"Có {totalWaste} thanh thép lãng phí do cấu tạo (penalty: -{penalty:F0} điểm)"
            };
        }
    }
}
