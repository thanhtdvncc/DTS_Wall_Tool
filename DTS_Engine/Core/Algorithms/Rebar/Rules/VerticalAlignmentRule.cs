using DTS_Engine.Core.Algorithms.Rebar.Models;

namespace DTS_Engine.Core.Algorithms.Rebar.Rules
{
    /// <summary>
    /// Rule kiểm tra căn chỉnh thẳng đứng giữa thép trên và dưới.
    /// 
    /// Vấn đề: Nếu Top=3 cây (1 giữa + 2 biên) và Bot=4 cây (2 cụm),
    /// thì nhánh đai thẳng đứng không thẳng hàng → Khó thi công.
    /// 
    /// Quy tắc:
    /// - Nếu N_top chẵn thì N_bot phải chẵn
    /// - Nếu N_top lẻ thì N_bot phải lẻ
    /// 
    /// Mục đích: Đảm bảo các nhánh đai có thể buộc thẳng hàng từ thép trên xuống thép dưới.
    /// </summary>
    public class VerticalAlignmentRule : IDesignRule
    {
        public string RuleName { get { return "VerticalAlignment"; } }
        public int Priority { get { return 12; } } // After Pyramid, before WastePenalty

        /// <summary>
        /// Điểm phạt cho lệch pha chẵn/lẻ (construction nightmare)
        /// </summary>
        private const double MISALIGNMENT_PENALTY = 25.0;

        public ValidationResult Validate(SolutionContext context)
        {
            if (context?.CurrentSolution == null)
                return ValidationResult.Pass(RuleName);

            var sol = context.CurrentSolution;
            int nTop = sol.BackboneCount_Top;
            int nBot = sol.BackboneCount_Bot;

            // Check odd/even match
            bool topEven = nTop % 2 == 0;
            bool botEven = nBot % 2 == 0;

            if (topEven != botEven)
            {
                // Mismatch: Top chẵn/Bot lẻ hoặc ngược lại
                double penaltyScore = context.Settings?.Rules?.AlignmentPenaltyScore ?? 25.0;
                context.CurrentSolution.ConstructabilityScore -= penaltyScore;

                return new ValidationResult
                {
                    RuleName = RuleName,
                    Level = SeverityLevel.Warning,
                    PenaltyScore = MISALIGNMENT_PENALTY,
                    Message = $"Lệch pha Chẵn/Lẻ: Top={nTop}({(topEven ? "chẵn" : "lẻ")}), " +
                              $"Bot={nBot}({(botEven ? "chẵn" : "lẻ")}) - Khó buộc đai thẳng hàng"
                };
            }

            // Additional check: Difference too large
            int diff = System.Math.Abs(nTop - nBot);
            if (diff > 2)
            {
                double extraPenalty = (diff - 2) * 5.0;
                context.CurrentSolution.ConstructabilityScore -= extraPenalty;

                return new ValidationResult
                {
                    RuleName = RuleName,
                    Level = SeverityLevel.Warning,
                    PenaltyScore = extraPenalty,
                    Message = $"Chênh lệch lớn: Top={nTop}, Bot={nBot} (diff={diff}) - Khung cốt thép không đều"
                };
            }

            return ValidationResult.Pass(RuleName);
        }
    }
}
