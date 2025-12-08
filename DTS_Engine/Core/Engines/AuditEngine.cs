using DTS_Engine.Core.Data;
using DTS_Engine.Core.Interfaces;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// Orchestrator for the Load Audit Pipeline.
    /// 
    /// ARCHITECTURE (ISO/IEC 25010 - Modularity):
    /// Stage 1: LoadEnricher - Calculate GlobalCenter, GridLocation, VectorKey
    /// Stage 2: GeometryProcessor - NTS decomposition, Quantity vs Explanation separation
    /// Stage 3: LoadGrouper - Group by Story → LoadType → Location
    /// Stage 4: ReportBuilder - Build AuditReport with Force = Qty × Load × Dir
    /// 
    /// This class ONLY orchestrates the pipeline - no processing logic here.
    /// </summary>
    public class AuditEngine
    {
        #region Dependencies

        private readonly ISapLoadReader _loadReader;
        private readonly LoadEnricher _enricher;
        private readonly GeometryProcessor _geometryProcessor;
        private readonly LoadGrouper _grouper;
        private readonly ReportBuilder _reportBuilder;

        #endregion

        #region Constructor

        public AuditEngine(ISapLoadReader loadReader)
        {
            _loadReader = loadReader ?? throw new ArgumentNullException(nameof(loadReader));

            // Initialize pipeline components
            _enricher = new LoadEnricher();
            _geometryProcessor = new GeometryProcessor();
            _grouper = new LoadGrouper();
            _reportBuilder = new ReportBuilder(_enricher, _geometryProcessor);
        }

        #endregion

        #region Public API

        /// <summary>
        /// Run audit for multiple load patterns (comma-separated).
        /// </summary>
        public List<AuditReport> RunAudit(string loadPatterns)
        {
            var reports = new List<AuditReport>();

            if (string.IsNullOrWhiteSpace(loadPatterns))
                return reports;

            var patterns = loadPatterns.Split(',')
                .Select(p => p.Trim())
                .Where(p => !string.IsNullOrEmpty(p))
                .ToList();

            foreach (var pattern in patterns)
            {
                var report = RunSingleAudit(pattern);
                if (report != null)
                    reports.Add(report);
            }

            return reports;
        }

        /// <summary>
        /// Run audit for a single load pattern.
        /// 
        /// PIPELINE:
        /// 1. Read raw loads from SAP
        /// 2. Enrich with geometry (GlobalCenter, GridLocation)
        /// 3. Group by Story → LoadType → Location
        /// 4. Build report with force calculations
        /// </summary>
        public AuditReport RunSingleAudit(string loadPattern)
        {
            // Get model name for report
            string modelName = SapUtils.GetModelName();

            // STAGE 1: Read and Enrich
            var loads = _loadReader.ReadAllLoads(loadPattern);
            if (loads == null || loads.Count == 0)
            {
                return CreateEmptyReport(loadPattern, modelName);
            }

            _enricher.EnrichLoads(loads);

            // STAGE 2 & 3: Group (geometry processing happens in Stage 4)
            var storyBuckets = _grouper.GroupLoads(loads);

            // STAGE 4: Build Report
            var report = _reportBuilder.BuildReport(storyBuckets, loadPattern, modelName);

            return report;
        }

        #endregion

        #region Report Generation

        /// <summary>
        /// Generate formatted text report.
        /// 
        /// OUTPUT FORMAT:
        /// Location | Explanation | Quantity | UnitLoad | Fx | Fy | Fz | Elements
        /// </summary>
        public string GenerateTextReport(AuditReport report, string targetUnit = "kN", string language = "English")
        {
            if (report == null)
                return "No report data.";

            var sb = new StringBuilder();
            bool isVN = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);
            double forceFactor = CalculateForceFactor(targetUnit);

            // Header
            WriteReportHeader(sb, report, targetUnit, isVN);

            // Detail by Story
            foreach (var story in report.Stories.OrderByDescending(s => s.Elevation))
            {
                WriteStorySection(sb, story, forceFactor, targetUnit, isVN);
            }

            // Summary
            WriteReportSummary(sb, report, forceFactor, targetUnit, isVN);

            return sb.ToString();
        }

        #endregion

        #region Private Methods - Report Formatting

        private void WriteReportHeader(StringBuilder sb, AuditReport report, string targetUnit, bool isVN)
        {
            sb.AppendLine("╔══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╗");
            sb.AppendLine(isVN
                ? "║                               KIỂM TOÁN TẢI TRỌNG SAP2000 (DTS ENGINE v5.0)                                                           ║"
                : "║                               SAP2000 LOAD AUDIT REPORT (DTS ENGINE v5.0)                                                              ║");
            sb.AppendLine("╚══════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════╝");
            sb.AppendLine();

            sb.AppendLine(isVN ? $"  Dự án     : {report.ModelName ?? "Unknown"}" : $"  Project   : {report.ModelName ?? "Unknown"}");
            sb.AppendLine(isVN ? $"  Tổ hợp tải: {report.LoadPattern}" : $"  Load Case : {report.LoadPattern}");
            sb.AppendLine(isVN ? $"  Đơn vị    : {targetUnit}" : $"  Unit      : {targetUnit}");
            sb.AppendLine(isVN ? $"  Ngày tính : {report.AuditDate:yyyy-MM-dd HH:mm}" : $"  Date      : {report.AuditDate:yyyy-MM-dd HH:mm}");
            sb.AppendLine();
        }

        private void WriteStorySection(StringBuilder sb, AuditStoryGroup story, double forceFactor, string targetUnit, bool isVN)
        {
            // Story header
            double storyFx = story.LoadTypes.Sum(lt => lt.SubTotalFx) * forceFactor;
            double storyFy = story.LoadTypes.Sum(lt => lt.SubTotalFy) * forceFactor;
            double storyFz = story.LoadTypes.Sum(lt => lt.SubTotalFz) * forceFactor;

            sb.AppendLine("─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine($">>> {(isVN ? "TẦNG" : "STORY")}: {story.StoryName} | Z={story.Elevation:0}mm | " +
                $"Fx={storyFx:0.00} Fy={storyFy:0.00} Fz={storyFz:0.00} {targetUnit}");
            sb.AppendLine("─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────");
            sb.AppendLine();

            foreach (var loadType in story.LoadTypes)
            {
                WriteLoadTypeSection(sb, loadType, forceFactor, targetUnit, isVN);
            }
        }

        private void WriteLoadTypeSection(StringBuilder sb, AuditLoadTypeGroup loadType, double forceFactor, string targetUnit, bool isVN)
        {
            double typeFx = loadType.SubTotalFx * forceFactor;
            double typeFy = loadType.SubTotalFy * forceFactor;
            double typeFz = loadType.SubTotalFz * forceFactor;

            sb.AppendLine($"  [{loadType.LoadTypeName}] Subtotal: Fx={typeFx:0.00} Fy={typeFy:0.00} Fz={typeFz:0.00} {targetUnit}");
            sb.AppendLine();

            // Column headers
            // Location | Explanation | Quantity | UnitLoad | Fx | Fy | Fz | Elements
            string header = isVN
                ? "    Vị trí              | Diễn giải           | Khối lượng | Tải đơn vị |      Fx |      Fy |      Fz | Phần tử"
                : "    Location            | Explanation         | Quantity   | Unit Load  |      Fx |      Fy |      Fz | Elements";

            sb.AppendLine(header);
            sb.AppendLine("    " + new string('-', 130));

            foreach (var entry in loadType.Entries)
            {
                FormatDataRow(sb, entry, forceFactor, targetUnit);
            }

            sb.AppendLine();
        }

        private void FormatDataRow(StringBuilder sb, AuditEntry entry, double forceFactor, string targetUnit)
        {
            // Format each column with fixed width
            string location = TruncateString(entry.GridLocation ?? "Unknown", 22);
            string explanation = TruncateString(entry.Explanation ?? "", 20);
            string quantity = $"{entry.Quantity:0.00} {entry.QuantityUnit ?? ""}".PadLeft(10);
            string unitLoad = $"{entry.UnitLoad * forceFactor:0.00}".PadLeft(10);
            string fx = $"{entry.ForceX * forceFactor:0.00}".PadLeft(7);
            string fy = $"{entry.ForceY * forceFactor:0.00}".PadLeft(7);
            string fz = $"{entry.ForceZ * forceFactor:0.00}".PadLeft(7);
            string elements = CompressElementList(entry.ElementList, 40);

            sb.AppendLine($"    {location,-22} | {explanation,-20} | {quantity} | {unitLoad} | {fx} | {fy} | {fz} | {elements}");
        }

        private void WriteReportSummary(StringBuilder sb, AuditReport report, double forceFactor, string targetUnit, bool isVN)
        {
            sb.AppendLine();
            sb.AppendLine("═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");
            sb.AppendLine(isVN ? "  TỔNG KIỂM TOÁN:" : "  AUDIT SUMMARY:");
            sb.AppendLine("═════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════════");

            // Recalculate from entries for consistency
            double totalFx = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFx)) * forceFactor;
            double totalFy = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFy)) * forceFactor;
            double totalFz = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFz)) * forceFactor;
            double totalMagnitude = Math.Sqrt(totalFx * totalFx + totalFy * totalFy + totalFz * totalFz);

            sb.AppendLine(isVN
                ? $"  Tổng lực (Magnitude): {totalMagnitude:0.00} {targetUnit}"
                : $"  Total Force (Mag)   : {totalMagnitude:0.00} {targetUnit}");
            sb.AppendLine($"    Fx = {totalFx:0.00} {targetUnit}");
            sb.AppendLine($"    Fy = {totalFy:0.00} {targetUnit}");
            sb.AppendLine($"    Fz = {totalFz:0.00} {targetUnit}");

            if (report.IsAnalyzed)
            {
                double sapReaction = Math.Abs(report.SapBaseReaction) * forceFactor;
                double diff = totalMagnitude - sapReaction;
                double diffPercent = sapReaction > 0 ? (diff / sapReaction) * 100.0 : 0;

                sb.AppendLine();
                sb.AppendLine(isVN ? $"  Phản lực SAP2000    : {sapReaction:0.00} {targetUnit}" : $"  SAP Base Reaction   : {sapReaction:0.00} {targetUnit}");
                sb.AppendLine(isVN ? $"  Sai số              : {diff:0.00} {targetUnit} ({diffPercent:0.00}%)" : $"  Difference          : {diff:0.00} {targetUnit} ({diffPercent:0.00}%)");
            }
            else
            {
                sb.AppendLine();
                sb.AppendLine(isVN ? "  (Chưa phân tích - Vui lòng kiểm tra thủ công)" : "  (Not analyzed - Please verify manually)");
            }

            sb.AppendLine();
        }

        #endregion

        #region Helper Methods

        private AuditReport CreateEmptyReport(string loadPattern, string modelName)
        {
            return new AuditReport
            {
                LoadPattern = loadPattern,
                ModelName = modelName,
                AuditDate = DateTime.Now,
                Stories = new List<AuditStoryGroup>(),
                IsAnalyzed = false
            };
        }

        private double CalculateForceFactor(string targetUnit)
        {
            if (string.IsNullOrWhiteSpace(targetUnit)) return 1.0;

            if (targetUnit.Equals("Ton", StringComparison.OrdinalIgnoreCase) ||
                targetUnit.Equals("Tonf", StringComparison.OrdinalIgnoreCase))
                return 1.0 / 9.81;
            else if (targetUnit.Equals("kgf", StringComparison.OrdinalIgnoreCase))
                return 101.97;
            else if (targetUnit.Equals("lb", StringComparison.OrdinalIgnoreCase))
                return 224.8;
            else
                return 1.0; // Default to kN
        }

        private string TruncateString(string val, int maxLen)
        {
            if (string.IsNullOrEmpty(val)) return "";
            return val.Length <= maxLen ? val : val.Substring(0, maxLen - 2) + "..";
        }

        private string CompressElementList(List<string> elements, int maxDisplay)
        {
            if (elements == null || elements.Count == 0) return "";
            if (elements.Count == 1) return elements[0];

            // Try to create range notation (e.g., "S1-S5" instead of "S1,S2,S3,S4,S5")
            var sorted = elements.OrderBy(e => e, StringComparer.OrdinalIgnoreCase).ToList();

            if (sorted.Count <= 3)
            {
                string result = string.Join(",", sorted);
                return result.Length <= maxDisplay ? result : result.Substring(0, maxDisplay - 2) + "..";
            }

            // Try prefix grouping
            var groups = sorted.GroupBy(e => GetElementPrefix(e)).ToList();
            var parts = new List<string>();

            foreach (var g in groups)
            {
                var items = g.OrderBy(ExtractNumber).ToList();
                if (items.Count >= 3)
                {
                    parts.Add($"{items.First()}to{items.Last()}");
                }
                else
                {
                    parts.AddRange(items);
                }
            }

            string compressed = string.Join(",", parts);
            if (compressed.Length > maxDisplay)
            {
                return $"{sorted.First()}..{sorted.Last()} ({sorted.Count}ea)";
            }
            return compressed;
        }

        private string GetElementPrefix(string element)
        {
            if (string.IsNullOrEmpty(element)) return "";
            int i = 0;
            while (i < element.Length && !char.IsDigit(element[i])) i++;
            return i > 0 ? element.Substring(0, i) : "";
        }

        private int ExtractNumber(string element)
        {
            if (string.IsNullOrEmpty(element)) return 0;
            var digits = new StringBuilder();
            foreach (char c in element)
            {
                if (char.IsDigit(c)) digits.Append(c);
            }
            return digits.Length > 0 && int.TryParse(digits.ToString(), out int num) ? num : 0;
        }

        #endregion
    }
}
