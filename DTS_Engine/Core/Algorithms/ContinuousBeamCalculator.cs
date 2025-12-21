using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Thuật toán tính toán thép tối ưu cho dải dầm liên tục.
    /// Chọn Backbone (thép chạy suốt) trước, sau đó tính Reinforcement (gia cường).
    /// </summary>
    public static class ContinuousBeamCalculator
    {
        /// <summary>
        /// Tính toán thép tối ưu cho BeamGroup, trả về top 3 phương án
        /// </summary>
        public static List<ContinuousBeamSolution> Optimize(BeamGroup group, DtsSettings settings)
        {
            if (group == null || group.Spans.Count == 0)
                return new List<ContinuousBeamSolution>();

            var solutions = new List<ContinuousBeamSolution>();

            // Bước 1: Phân tích Envelope (As_max, As_min)
            var envelope = AnalyzeEnvelope(group);

            // Bước 2: Sinh các phương án Backbone tiềm năng
            var backboneOptions = GenerateBackboneOptions(envelope, settings);

            // Bước 3: Đánh giá từng phương án
            foreach (var backbone in backboneOptions.Take(10))
            {
                var solution = EvaluateSolution(group, backbone, settings);
                if (solution != null)
                    solutions.Add(solution);
            }

            // Bước 4: Sắp xếp theo EfficiencyScore, lấy top 3
            return solutions
                .OrderByDescending(s => s.EfficiencyScore)
                .Take(3)
                .ToList();
        }

        /// <summary>
        /// Phân tích biểu đồ bao - tìm As_max, As_min cho toàn dải
        /// </summary>
        private static EnvelopeData AnalyzeEnvelope(BeamGroup group)
        {
            double maxTop = 0, maxBot = 0;
            double minTop = double.MaxValue, minBot = double.MaxValue;
            double maxWidth = 0, minWidth = double.MaxValue;

            foreach (var span in group.Spans)
            {
                // Top: Max ở các vị trí gối (0, 4)
                double spanMaxTop = Math.Max(span.As_Top[0], span.As_Top[4]);
                if (spanMaxTop > maxTop) maxTop = spanMaxTop;
                if (spanMaxTop > 0 && spanMaxTop < minTop) minTop = spanMaxTop;

                // Bot: Max ở giữa nhịp (2)
                double spanMaxBot = span.As_Bot[2];
                if (spanMaxBot > maxBot) maxBot = spanMaxBot;
                if (spanMaxBot > 0 && spanMaxBot < minBot) minBot = spanMaxBot;

                if (span.Width > maxWidth) maxWidth = span.Width;
                if (span.Width < minWidth) minWidth = span.Width;
            }

            // Handle edge cases
            if (minTop == double.MaxValue) minTop = maxTop;
            if (minBot == double.MaxValue) minBot = maxBot;

            return new EnvelopeData
            {
                As_Max_Top = maxTop,
                As_Max_Bot = maxBot,
                As_Min_Top = minTop,
                As_Min_Bot = minBot,
                MaxWidth = maxWidth,
                MinWidth = minWidth
            };
        }

        /// <summary>
        /// Sinh các phương án Backbone tiềm năng.
        /// Dynamic: Số thanh tính theo bề rộng dầm, cho phép Top ≠ Bot.
        /// </summary>
        private static List<BackboneSpec> GenerateBackboneOptions(EnvelopeData env, DtsSettings settings)
        {
            var options = new List<BackboneSpec>();

            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var mainRange = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            if (mainRange.Count == 0) mainRange = inventory;

            // Sắp xếp giảm dần (ưu tiên đường kính lớn)
            var diameters = mainRange.OrderByDescending(d => d).ToList();

            // Tính số thanh tối đa dựa theo bề rộng dầm
            // Công thức: (BeamWidth - 2*Cover - 2*StirrupDia) / (BarDia + ClearSpacing)
            double beamWidth = env.MinWidth; // mm
            double cover = settings.Beam.CoverSide;
            double stirrupDia = settings.Beam.EstimatedStirrupDiameter;
            double clearSpacing = settings.Beam?.MinClearSpacing ?? 30;

            // Tính As_min cấu tạo (thường 0.15% bxh cho dầm thường)
            double minRatio = settings.Beam?.MinReinforcementRatio ?? 0.002;
            double asConstruct = minRatio * env.MinWidth * 100; // Ước tính

            foreach (int d in diameters)
            {
                // Tính số thanh tối đa có thể chứa trong 1 lớp
                double availableWidth = beamWidth - 2 * cover - 2 * stirrupDia;
                int maxBars = (int)Math.Floor((availableWidth + clearSpacing) / (d + clearSpacing));
                maxBars = Math.Max(2, Math.Min(maxBars, 10)); // Giới hạn 2-10 thanh

                // Số thanh tối thiểu
                int minBars = settings.Beam?.MinBarsPerLayer ?? 2;

                double as1 = Math.PI * d * d / 400.0;

                // Sinh các tổ hợp (nTop, nBot) khác nhau
                for (int nTop = minBars; nTop <= maxBars; nTop++)
                {
                    for (int nBot = minBars; nBot <= maxBars; nBot++)
                    {
                        double asTop = nTop * as1;
                        double asBot = nBot * as1;

                        // Backbone phải đủ cho As cấu tạo
                        if (asTop < asConstruct * 0.5 || asBot < asConstruct * 0.5)
                            continue;

                        // Tính waste dựa trên so sánh với As_min thực tế
                        double wasteTop = env.As_Min_Top > 0
                            ? Math.Max(0, (asTop - env.As_Min_Top) / env.As_Min_Top)
                            : 0;
                        double wasteBot = env.As_Min_Bot > 0
                            ? Math.Max(0, (asBot - env.As_Min_Bot) / env.As_Min_Bot)
                            : 0;
                        double avgWaste = (wasteTop + wasteBot) / 2;

                        // Penalty cho bất đối xứng (nếu user muốn symmetric)
                        double asymmetryPenalty = 0;
                        if (settings.Beam?.PreferSymmetric == true && nTop != nBot)
                            asymmetryPenalty = 0.1 * Math.Abs(nTop - nBot);

                        options.Add(new BackboneSpec
                        {
                            Diameter = d,
                            CountTop = nTop,
                            CountBot = nBot,
                            As_Top = asTop,
                            As_Bot = asBot,
                            WasteEstimate = avgWaste + asymmetryPenalty
                        });
                    }
                }
            }

            // Giới hạn số lượng options để tránh explosion
            // Sắp xếp: ưu tiên ít thừa, sau đó ưu tiên đường kính lớn
            return options
                .OrderBy(o => o.WasteEstimate)
                .ThenByDescending(o => o.Diameter)
                .Take(50) // Chỉ lấy 50 options tốt nhất
                .ToList();
        }

        /// <summary>
        /// Đánh giá 1 phương án Backbone: tính reinforcement + metrics
        /// </summary>
        private static ContinuousBeamSolution EvaluateSolution(
            BeamGroup group, BackboneSpec backbone, DtsSettings settings)
        {
            var solution = new ContinuousBeamSolution
            {
                OptionName = $"{backbone.CountTop}D{backbone.Diameter}",
                BackboneDiameter_Top = backbone.Diameter,
                BackboneDiameter_Bot = backbone.Diameter, // Same diameter for both in this calculator
                BackboneCount_Top = backbone.CountTop,
                BackboneCount_Bot = backbone.CountBot,
                As_Backbone_Top = backbone.As_Top,
                As_Backbone_Bot = backbone.As_Bot
            };

            double totalWeight = 0;
            double totalRequired = 0;
            double totalProvided = 0;

            bool forceSameDiameter = settings.Beam?.ForceContinuousDiameter ?? true;

            foreach (var span in group.Spans)
            {
                // Tính thép cho 6 vị trí
                for (int pos = 0; pos < 6; pos++)
                {
                    // TOP
                    double asReqTop = span.As_Top[pos];
                    if (asReqTop > 0)
                    {
                        totalRequired += asReqTop;
                        var rebarTop = CalculateReinforcement(
                            asReqTop, backbone.As_Top, backbone.Diameter,
                            forceSameDiameter, settings);
                        totalProvided += rebarTop.AsProvided;

                        if (rebarTop.NeedAddon)
                        {
                            solution.Reinforcements[$"{span.SpanId}_Top_{pos}"] = new RebarSpec
                            {
                                Diameter = rebarTop.AddonDiameter,
                                Count = rebarTop.AddonCount,
                                Position = "Top",
                                Layer = 2
                            };
                        }
                    }

                    // BOT
                    double asReqBot = span.As_Bot[pos];
                    if (asReqBot > 0)
                    {
                        totalRequired += asReqBot;
                        var rebarBot = CalculateReinforcement(
                            asReqBot, backbone.As_Bot, backbone.Diameter,
                            forceSameDiameter, settings);
                        totalProvided += rebarBot.AsProvided;

                        if (rebarBot.NeedAddon)
                        {
                            solution.Reinforcements[$"{span.SpanId}_Bot_{pos}"] = new RebarSpec
                            {
                                Diameter = rebarBot.AddonDiameter,
                                Count = rebarBot.AddonCount,
                                Position = "Bot",
                                Layer = 2
                            };
                        }
                    }
                }

                // Tính weight (ước tính)
                double spanLength = span.Length; // m
                double backboneWeight = (backbone.CountTop + backbone.CountBot)
                    * GetBarWeight(backbone.Diameter) * spanLength;
                totalWeight += backboneWeight;
            }

            // Tính metrics
            solution.TotalSteelWeight = totalWeight;
            solution.WastePercentage = totalRequired > 0
                ? (totalProvided - totalRequired) / totalRequired * 100
                : 0;

            // Efficiency Score: 100 - waste%, nhưng min 0
            solution.EfficiencyScore = Math.Max(0, 100 - solution.WastePercentage);

            // Constructability + TotalScore (0-100)
            solution.ConstructabilityScore = ConstructabilityScoring.CalculateScore(solution, group, settings);
            solution.TotalScore = 0.6 * solution.EfficiencyScore + 0.4 * solution.ConstructabilityScore;

            // Cảnh báo nếu có step change
            if (group.HasStepChange)
                solution.Description = $"{backbone.CountTop}D{backbone.Diameter} suốt (⚠️ giật cấp)";
            else
                solution.Description = $"{backbone.CountTop}D{backbone.Diameter} suốt";

            return solution;
        }

        /// <summary>
        /// Tính thép gia cường cho 1 vị trí
        /// </summary>
        private static ReinforcementResult CalculateReinforcement(
            double asRequired, double asBackbone, int backboneDiameter,
            bool forceSameDiameter, DtsSettings settings)
        {
            double asRemain = asRequired - asBackbone;

            if (asRemain <= 0.01)
            {
                return new ReinforcementResult
                {
                    NeedAddon = false,
                    AsProvided = asBackbone
                };
            }

            // Chọn đường kính gia cường
            int addonDiameter = forceSameDiameter
                ? backboneDiameter
                : FindOptimalDiameter(asRemain, settings);

            double as1 = Math.PI * addonDiameter * addonDiameter / 400.0;
            int count = (int)Math.Ceiling(asRemain / as1);
            if (count < 2) count = 2;

            // Ưu tiên số chẵn nếu PreferSymmetric
            if (settings.Beam?.PreferSymmetric == true && count % 2 != 0)
                count++;

            return new ReinforcementResult
            {
                NeedAddon = true,
                AddonDiameter = addonDiameter,
                AddonCount = count,
                AsProvided = asBackbone + count * as1
            };
        }

        /// <summary>
        /// Tìm đường kính tối ưu cho thép gia cường
        /// </summary>
        private static int FindOptimalDiameter(double asRemain, DtsSettings settings)
        {
            var inventory = settings.General?.AvailableDiameters ?? new List<int> { 16, 18, 20, 22, 25 };
            var mainRange = DiameterParser.ParseRange(settings.Beam?.MainBarRange ?? "16-25", inventory);

            // Tìm đường kính nhỏ nhất mà 2 thanh đủ
            foreach (int d in mainRange.OrderBy(x => x))
            {
                double as2 = 2 * Math.PI * d * d / 400.0;
                if (as2 >= asRemain * 0.9) // Cho phép thiếu 10%
                    return d;
            }

            // Fallback: đường kính lớn nhất
            return mainRange.Count > 0 ? mainRange.Max() : 22;
        }

        /// <summary>
        /// Khối lượng thép kg/m theo đường kính
        /// </summary>
        private static double GetBarWeight(int diameter)
        {
            // Công thức: d²/162 kg/m (với d = mm)
            return diameter * diameter / 162.0;
        }
    }

    // ===== HELPER CLASSES =====

    internal class EnvelopeData
    {
        public double As_Max_Top { get; set; }
        public double As_Max_Bot { get; set; }
        public double As_Min_Top { get; set; }
        public double As_Min_Bot { get; set; }
        public double MaxWidth { get; set; }
        public double MinWidth { get; set; }
    }

    internal class BackboneSpec
    {
        public int Diameter { get; set; }
        public int CountTop { get; set; }
        public int CountBot { get; set; }
        public double As_Top { get; set; }
        public double As_Bot { get; set; }
        public double WasteEstimate { get; set; }
    }

    internal class ReinforcementResult
    {
        public bool NeedAddon { get; set; }
        public int AddonDiameter { get; set; }
        public int AddonCount { get; set; }
        public double AsProvided { get; set; }
    }
}
