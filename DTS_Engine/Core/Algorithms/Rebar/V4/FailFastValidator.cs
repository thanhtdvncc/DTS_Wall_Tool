using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms.Rebar.V4
{
    /// <summary>
    /// Fail-Fast Validator: Kiểm tra đầu vào trước khi xử lý.
    /// Phát hiện sớm các lỗi cấu hình và dữ liệu không hợp lệ.
    /// 
    /// ISO 25010: Reliability - Early failure detection.
    /// ISO 12207: Validation Phase - Input verification.
    /// </summary>
    public static class FailFastValidator
    {
        #region Validation Result

        /// <summary>
        /// Kết quả validation.
        /// </summary>
        public class ValidationResult
        {
            public bool IsValid { get; set; }
            public string Message { get; set; }
            public List<string> Warnings { get; set; } = new List<string>();

            public static ValidationResult Success()
            {
                return new ValidationResult { IsValid = true };
            }

            public static ValidationResult Success(List<string> warnings)
            {
                return new ValidationResult { IsValid = true, Warnings = warnings ?? new List<string>() };
            }

            public static ValidationResult Failure(string message)
            {
                return new ValidationResult { IsValid = false, Message = message };
            }
        }

        #endregion

        #region Main Validation Methods

        /// <summary>
        /// Validate đầu vào cho V4 Calculator.
        /// </summary>
        public static ValidationResult ValidateCalculatorInput(
            BeamGroup group,
            List<BeamResultData> results,
            DtsSettings settings)
        {
            var warnings = new List<string>();

            // 1. Check settings
            var settingsResult = ValidateSettings(settings);
            if (!settingsResult.IsValid)
                return settingsResult;
            warnings.AddRange(settingsResult.Warnings);

            // 2. Check results
            var resultsResult = ValidateResults(results);
            if (!resultsResult.IsValid)
                return resultsResult;
            warnings.AddRange(resultsResult.Warnings);

            // 3. Check group (optional but preferred)
            if (group != null)
            {
                var groupResult = ValidateGroup(group);
                if (!groupResult.IsValid)
                    return groupResult;
                warnings.AddRange(groupResult.Warnings);
            }

            // 4. Cross-validation
            var crossResult = CrossValidate(group, results, settings);
            if (!crossResult.IsValid)
                return crossResult;
            warnings.AddRange(crossResult.Warnings);

            return ValidationResult.Success(warnings);
        }

        #endregion

        #region Component Validators

        /// <summary>
        /// Validate DtsSettings.
        /// </summary>
        public static ValidationResult ValidateSettings(DtsSettings settings)
        {
            var warnings = new List<string>();

            if (settings == null)
                return ValidationResult.Failure("DtsSettings is null - không có cấu hình");

            // General config
            if (settings.General == null)
                return ValidationResult.Failure("GeneralConfig is null - thiếu cấu hình chung");

            if (settings.General.AvailableDiameters == null || settings.General.AvailableDiameters.Count == 0)
                return ValidationResult.Failure("AvailableDiameters is empty - không có đường kính thép khả dụng");

            // Beam config
            if (settings.Beam == null)
                return ValidationResult.Failure("BeamConfig is null - thiếu cấu hình dầm");

            if (settings.Beam.CoverTop <= 0 || settings.Beam.CoverBot <= 0)
                return ValidationResult.Failure("Cover <= 0 - lớp bảo vệ không hợp lệ");

            if (settings.Beam.MaxLayers <= 0)
                return ValidationResult.Failure("MaxLayers <= 0 - số lớp tối đa không hợp lệ");

            if (settings.Beam.MinClearSpacing <= 0)
                return ValidationResult.Failure("MinClearSpacing <= 0 - khoảng hở tối thiểu không hợp lệ");

            // Warnings for suboptimal settings
            if (settings.Beam.MinClearSpacing < 25)
                warnings.Add("MinClearSpacing < 25mm - có thể vi phạm tiêu chuẩn");

            if (settings.Beam.MaxLayers > 3)
                warnings.Add("MaxLayers > 3 - khó thi công, cân nhắc tăng đường kính");

            // Rules config (optional but recommended)
            if (settings.Rules == null)
            {
                warnings.Add("RulesConfig is null - sử dụng giá trị mặc định cho SafetyFactor và Penalty");
            }
            else
            {
                if (settings.Rules.SafetyFactor < 1.0)
                    warnings.Add($"SafetyFactor = {settings.Rules.SafetyFactor} < 1.0 - có thể thiếu an toàn");

                if (settings.Rules.SafetyFactor > 1.1)
                    warnings.Add($"SafetyFactor = {settings.Rules.SafetyFactor} > 1.1 - có thể lãng phí thép");
            }

            return ValidationResult.Success(warnings);
        }

        /// <summary>
        /// Validate BeamResultData list.
        /// </summary>
        public static ValidationResult ValidateResults(List<BeamResultData> results)
        {
            var warnings = new List<string>();

            if (results == null || results.Count == 0)
                return ValidationResult.Failure("Không có dữ liệu nội lực (BeamResultData is null/empty)");

            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                if (r == null)
                {
                    warnings.Add($"Span {i}: BeamResultData is null - bỏ qua");
                    continue;
                }

                // Check arrays
                if (r.TopArea == null || r.TopArea.Length == 0)
                {
                    warnings.Add($"Span {i}: TopArea is null/empty - giả sử = 0");
                }

                if (r.BotArea == null || r.BotArea.Length == 0)
                {
                    warnings.Add($"Span {i}: BotArea is null/empty - giả sử = 0");
                }

                // Check for negative values
                if (r.TopArea != null && r.TopArea.Any(v => v < 0))
                    return ValidationResult.Failure($"Span {i}: TopArea có giá trị âm - dữ liệu không hợp lệ");

                if (r.BotArea != null && r.BotArea.Any(v => v < 0))
                    return ValidationResult.Failure($"Span {i}: BotArea có giá trị âm - dữ liệu không hợp lệ");

                // Check dimensions
                if (r.Width <= 0)
                    warnings.Add($"Span {i}: Width <= 0 - sử dụng giá trị mặc định");

                if (r.SectionHeight <= 0)
                    warnings.Add($"Span {i}: SectionHeight <= 0 - sử dụng giá trị mặc định");

                // Check for unusually high values (possible unit error)
                double maxArea = Math.Max(
                    r.TopArea?.Max() ?? 0,
                    r.BotArea?.Max() ?? 0);

                if (maxArea > 100) // > 100 cm² is very high
                    warnings.Add($"Span {i}: As_max = {maxArea:F1} cm² - giá trị rất lớn, kiểm tra đơn vị");
            }

            // BUG FIX: Fail if ALL spans have no SAP area data
            bool anySpanHasData = results.Any(r => r != null &&
                ((r.TopArea != null && r.TopArea.Length > 0 && r.TopArea.Any(v => v > 0)) ||
                 (r.BotArea != null && r.BotArea.Length > 0 && r.BotArea.Any(v => v > 0))));

            if (!anySpanHasData)
            {
                return ValidationResult.Failure("Không có dữ liệu diện tích thép yêu cầu (As_req). Chưa link kết quả SAP2000?");
            }

            return ValidationResult.Success(warnings);
        }

        /// <summary>
        /// Validate BeamGroup.
        /// </summary>
        public static ValidationResult ValidateGroup(BeamGroup group)
        {
            var warnings = new List<string>();

            if (group == null)
            {
                warnings.Add("BeamGroup is null - sử dụng dữ liệu từ SpanResults");
                return ValidationResult.Success(warnings);
            }

            // Check spans
            if (group.Spans == null || group.Spans.Count == 0)
            {
                warnings.Add("BeamGroup.Spans is empty - tự động tạo từ SpanResults");
            }
            else
            {
                for (int i = 0; i < group.Spans.Count; i++)
                {
                    var span = group.Spans[i];

                    if (span.Width <= 0)
                        warnings.Add($"Span {span.SpanId ?? i.ToString()}: Width <= 0");

                    if (span.Height <= 0)
                        warnings.Add($"Span {span.SpanId ?? i.ToString()}: Height <= 0");

                    if (span.Length <= 0)
                        warnings.Add($"Span {span.SpanId ?? i.ToString()}: Length <= 0");

                    // Check for very long spans
                    if (span.Length > 15) // > 15m
                        warnings.Add($"Span {span.SpanId ?? i.ToString()}: Length = {span.Length:F1}m > 15m - kiểm tra dữ liệu");
                }
            }

            // Check supports
            if (group.Supports == null || group.Supports.Count == 0)
            {
                warnings.Add("BeamGroup.Supports is empty - tự động tạo từ geometry");
            }
            else if (group.Supports.Count < 2)
            {
                warnings.Add("BeamGroup có < 2 gối - kiểm tra dữ liệu");
            }

            return ValidationResult.Success(warnings);
        }

        /// <summary>
        /// Cross-validate giữa các thành phần.
        /// </summary>
        public static ValidationResult CrossValidate(
            BeamGroup group,
            List<BeamResultData> results,
            DtsSettings settings)
        {
            var warnings = new List<string>();

            if (group?.Spans != null && results != null)
            {
                // Check span count mismatch
                if (group.Spans.Count != results.Count)
                {
                    warnings.Add($"Số nhịp không khớp: Group has {group.Spans.Count} spans, Results has {results.Count} items");
                }
            }

            // Check diameter range vs requirements
            if (settings?.General?.AvailableDiameters != null && results != null)
            {
                double maxReq = results
                    .Where(r => r != null)
                    .SelectMany(r => (r.TopArea ?? new double[0]).Concat(r.BotArea ?? new double[0]))
                    .DefaultIfEmpty(0)
                    .Max();

                int maxDia = settings.General.AvailableDiameters.Max();
                double maxAsPerBar = Math.PI * maxDia * maxDia / 400.0; // cm²

                int minBars = settings.Beam?.MinBarsPerLayer ?? 2;
                double maxAsOneLayer = minBars * maxAsPerBar;

                if (maxReq > maxAsOneLayer * 3) // 3 layers
                {
                    warnings.Add($"As_max = {maxReq:F1}cm² rất lớn so với khả năng bố trí ({maxAsOneLayer * 3:F1}cm² với 3 lớp {maxDia}mm)");
                }
            }

            return ValidationResult.Success(warnings);
        }

        #endregion

        #region Quick Checks

        /// <summary>
        /// Quick check - chỉ kiểm tra các lỗi critical.
        /// </summary>
        public static (bool IsValid, string Message) QuickCheck(
            BeamGroup group,
            List<BeamResultData> results)
        {
            // Check geometry
            if (group?.Spans == null && (results == null || results.Count == 0))
                return (false, "Không có dữ liệu hình học hoặc nội lực");

            // Check results
            if (results == null || results.Count == 0)
                return (false, "Không có dữ liệu nội lực");

            // Check basic validity
            bool anyValidResult = results.Any(r => r != null);
            if (!anyValidResult)
                return (false, "Tất cả BeamResultData đều null");

            return (true, null);
        }

        /// <summary>
        /// Validate dimensions for a specific section.
        /// </summary>
        public static bool ValidateSectionDimensions(double width, double height, out string error)
        {
            error = null;

            if (width <= 0)
            {
                error = "Width <= 0";
                return false;
            }

            if (height <= 0)
            {
                error = "Height <= 0";
                return false;
            }

            if (width > height * 2)
            {
                // Width > 2x Height is unusual for beams
                error = $"Width ({width}) > 2x Height ({height}) - unusual for beam";
                return false;
            }

            if (width < 150) // mm
            {
                error = $"Width ({width}mm) < 150mm - too narrow";
                return false;
            }

            if (height < 200) // mm
            {
                error = $"Height ({height}mm) < 200mm - too shallow";
                return false;
            }

            return true;
        }

        #endregion
    }
}
