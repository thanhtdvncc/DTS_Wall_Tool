using System;
using DTS_Engine.Core.Algorithms.Rebar.Models;

namespace DTS_Engine.Core.Algorithms.Rebar.Strategies
{
    /// <summary>
    /// Chiến thuật GREEDY: Ưu tiên nhồi lớp 1 trước, chỉ tràn sang lớp 2 khi cần.
    /// Ưu điểm: Tập trung thép, dễ đổ bê tông.
    /// Nhược điểm: Có thể cần nhiều thanh hơn nếu lớp 1 đầy.
    /// </summary>
    public class GreedyFillingStrategy : IFillingStrategy
    {
        public string StrategyName { get { return "Greedy"; } }

        public FillingResult Calculate(FillingContext context)
        {
            var settings = context.Settings;
            int maxLayers = settings.Beam?.MaxLayers ?? 2;
            bool preferSymmetric = settings.Beam?.PreferSymmetric ?? true;

            int capacity = context.LayerCapacity;
            int backboneCount = context.BackboneCount;
            int legCount = context.StirrupLegCount;

            // Calculate missing area (what backbone doesn't cover)
            double missing = context.RequiredArea - context.BackboneArea;

            // ═══════════════════════════════════════════════════════════════
            // HOTFIX: Nếu thép chủ đã đủ, trả về cấu hình hiện tại
            // Trước đây trả về TotalBars=0 gây ra phép trừ âm ở ReinforcementFiller
            // ═══════════════════════════════════════════════════════════════
            if (missing <= 0.01)
            {
                return new FillingResult
                {
                    IsValid = true,
                    TotalBars = backboneCount,     // Đã có backbone
                    CountLayer1 = backboneCount,   // Nằm hết ở lớp 1
                    CountLayer2 = 0
                };
            }

            // Calculate total bars needed (including backbone)
            double barArea = Math.PI * context.BackboneDiameter * context.BackboneDiameter / 400.0;
            int totalNeeded = (int)Math.Ceiling(context.RequiredArea / barArea);

            // GREEDY: Fill layer 1 first, but must include backbone
            int n1 = Math.Min(totalNeeded, capacity);
            n1 = Math.Max(n1, backboneCount);  // CRITICAL: n1 must >= backboneCount
            int n2 = Math.Max(0, totalNeeded - n1);

            // Apply constructability constraints
            return ApplyConstraints(n1, n2, capacity, backboneCount, legCount, maxLayers, preferSymmetric);
        }

        private FillingResult ApplyConstraints(
            int n1, int n2, int capacity, int backboneCount, int legCount,
            int maxLayers, bool preferSymmetric)
        {
            // CONSTRAINT 1: Pyramid Rule (L2 <= L1)
            if (n2 > n1)
            {
                return new FillingResult
                {
                    IsValid = false,
                    FailReason = "Vi phạm quy tắc kim tự tháp (L2 > L1)"
                };
            }

            // CONSTRAINT 2: Max Layers
            if (n2 > 0 && maxLayers < 2)
            {
                return new FillingResult
                {
                    IsValid = false,
                    FailReason = "Vượt quá số lớp cho phép"
                };
            }

            // CONSTRAINT 3: Snap-to-Structure (Stirrup Legs)
            if (n2 > 0 && legCount > 2)
            {
                if (n2 >= legCount - 1 && n2 < legCount && n2 <= n1)
                    n2 = legCount;
            }

            // CONSTRAINT 4: Symmetry
            if (preferSymmetric)
            {
                if (n1 % 2 != 0 && n1 + 1 <= capacity) n1++;
                if (n2 > 0 && n2 % 2 != 0 && n2 + 1 <= n1) n2++;
            }

            // CONSTRAINT 5: Vertical Alignment
            if (n2 > 0 && n1 % 2 == 0 && n2 % 2 != 0 && n2 + 1 <= n1)
                n2++;

            // ═══════════════════════════════════════════════════════════════
            // CONSTRAINT 6: MinBarsPerLayer (CRITICAL)
            // Nếu L2 có thanh thì phải có tối thiểu 2 thanh (không được 1 thanh lẻ)
            // Trường hợp 3+1: Phải bump lên 3+2, hoặc fail để BalancedStrategy thử 2+2
            // ═══════════════════════════════════════════════════════════════
            const int MIN_BARS_PER_LAYER = 2;
            int wasteCount = 0;
            if (n2 > 0 && n2 < MIN_BARS_PER_LAYER)
            {
                // Bump L2 lên tối thiểu 2 nếu còn thỏa pyramid
                if (MIN_BARS_PER_LAYER <= n1)
                {
                    wasteCount = MIN_BARS_PER_LAYER - n2; // Số thanh waste (thường = 1)
                    n2 = MIN_BARS_PER_LAYER;
                }
                else
                {
                    // Không thể bump → Fail phương án này để BalancedStrategy thử cách khác
                    return new FillingResult
                    {
                        IsValid = false,
                        FailReason = $"L2 chỉ có {n2} thanh, cần tối thiểu {MIN_BARS_PER_LAYER}"
                    };
                }
            }

            // Re-check constraints after adjustments
            if (n2 > n1 || n1 > capacity)
            {
                return new FillingResult
                {
                    IsValid = false,
                    FailReason = "Không thể thỏa mãn ràng buộc sau khi điều chỉnh"
                };
            }

            return new FillingResult
            {
                IsValid = true,
                CountLayer1 = n1,
                CountLayer2 = n2,
                TotalBars = n1 + n2,
                WasteCount = wasteCount
            };
        }
    }
}
