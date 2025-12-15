using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Parser chuỗi thép (VD: "3d20 + 2d25") thành diện tích.
    /// Hỗ trợ nhiều format: 3d20, 3D20, 3phi20, 3fi20, 3Ø20.
    /// </summary>
    public static class RebarStringParser
    {
        // Pattern: [n][separator][d] where separator can be d, D, phi, fi, Ø
        private static readonly Regex BarPattern = new Regex(
            @"(\d+)\s*[dDfF]?(?:phi|fi|Ø)?\s*(\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Parse chuỗi thép thành tổng diện tích (cm2).
        /// </summary>
        public static double Parse(string rebarString)
        {
            if (string.IsNullOrWhiteSpace(rebarString)) return 0;

            // Clean string: Remove markers like * (forced) and extra whitespace
            rebarString = rebarString.Replace("*", "").Trim();

            double totalArea = 0;

            // Split by '+' for multi-layer
            var parts = rebarString.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var match = BarPattern.Match(part.Trim());
                if (match.Success)
                {
                    int count = int.Parse(match.Groups[1].Value);
                    int diameter = int.Parse(match.Groups[2].Value);

                    // Area = n * π * d² / 4  (d in mm -> Area in mm² -> /100 for cm²)
                    double areaPerBar = Math.PI * diameter * diameter / 400.0; // cm²
                    totalArea += count * areaPerBar;
                }
            }

            return totalArea;
        }

        /// <summary>
        /// Validate chuỗi thép có hợp lệ không.
        /// </summary>
        public static bool IsValid(string rebarString, out string errorMsg)
        {
            errorMsg = null;
            if (string.IsNullOrWhiteSpace(rebarString))
            {
                errorMsg = "Chuỗi thép rỗng.";
                return false;
            }

            // Clean markers
            string cleaned = rebarString.Replace("*", "").Trim();
            if (cleaned == "-") return true; // No rebar required

            var parts = cleaned.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                errorMsg = "Không tìm thấy thông tin thép.";
                return false;
            }

            foreach (var part in parts)
            {
                var match = BarPattern.Match(part.Trim());
                if (!match.Success)
                {
                    errorMsg = $"Định dạng không hợp lệ: '{part.Trim()}'. Mong đợi: nDd (VD: 3d20).";
                    return false;
                }

                int diameter = int.Parse(match.Groups[2].Value);
                if (diameter < 10 || diameter > 40)
                {
                    errorMsg = $"Đường kính không hợp lý: d{diameter}. Cho phép: 10-40mm.";
                    return false;
                }

                int count = int.Parse(match.Groups[1].Value);
                if (count < 1 || count > 20)
                {
                    errorMsg = $"Số thanh không hợp lý: {count}. Cho phép: 1-20 thanh.";
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Lấy chi tiết từng lớp thép.
        /// </summary>
        public static List<(int Count, int Diameter, double Area)> GetDetails(string rebarString)
        {
            var result = new List<(int, int, double)>();
            if (string.IsNullOrWhiteSpace(rebarString)) return result;

            string cleaned = rebarString.Replace("*", "").Trim();
            var parts = cleaned.Split(new[] { '+' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in parts)
            {
                var match = BarPattern.Match(part.Trim());
                if (match.Success)
                {
                    int count = int.Parse(match.Groups[1].Value);
                    int diameter = int.Parse(match.Groups[2].Value);
                    double area = count * Math.PI * diameter * diameter / 400.0;
                    result.Add((count, diameter, area));
                }
            }

            return result;
        }
    }
}
