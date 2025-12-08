using ClosedXML.Excel;
using DTS_Engine.Core.Data;
using System;
using System.IO;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Excel Report Generator using ClosedXML.
    /// 
    /// OUTPUT FORMAT (matches text report exactly):
    /// Location | Explanation | Quantity | UnitLoad | Fx | Fy | Fz | Elements
    /// 
    /// FORCE FORMULA:
    /// Force_Vector = Quantity × UnitLoad × Direction
    /// 
    /// ISO/IEC 25010: Usability + Maintainability
    /// </summary>
    public class ExcelReportGenerator
    {
        #region Constants

        private const string SHEET_NAME = "Load Audit Report";

        // Colors (ClosedXML format)
        private static readonly XLColor HEADER_COLOR = XLColor.FromArgb(68, 114, 196); // Blue
        private static readonly XLColor SUBHEADER_COLOR = XLColor.FromArgb(217, 225, 242); // Light blue
        private static readonly XLColor SUMMARY_COLOR = XLColor.FromArgb(255, 242, 204); // Light yellow
        private static readonly XLColor STORY_HEADER_COLOR = XLColor.FromArgb(189, 215, 238); // Pale blue

        #endregion

        #region Public API

        /// <summary>
        /// Generate Excel report and save to file.
        /// Returns the full path to the generated file.
        /// </summary>
        public static string GenerateExcelReport(
            AuditReport report,
            string targetUnit = "kN",
            string language = "English",
            string outputPath = null)
        {
            if (report == null)
                throw new ArgumentNullException(nameof(report));

            double forceFactor = CalculateForceFactor(targetUnit);
            bool isVN = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);

            using (var workbook = new XLWorkbook())
            {
                var worksheet = workbook.Worksheets.Add(SHEET_NAME);

                int currentRow = 1;

                // 1. Report Header
                currentRow = WriteHeader(worksheet, report, targetUnit, isVN, currentRow);

                // 2. Detail by Story
                currentRow = WriteStoryDetails(worksheet, report, forceFactor, targetUnit, isVN, currentRow);

                // 3. Summary Section
                currentRow = WriteSummary(worksheet, report, forceFactor, targetUnit, isVN, currentRow);

                // 4. Format worksheet
                FormatWorksheet(worksheet);

                // 5. Save to file
                if (string.IsNullOrEmpty(outputPath))
                {
                    string tempFolder = Path.GetTempPath();
                    string safeModel = string.IsNullOrWhiteSpace(report.ModelName)
                        ? "Model"
                        : Path.GetFileNameWithoutExtension(report.ModelName);
                    string fileName = $"DTS_Audit_{safeModel}_{report.LoadPattern}_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx";
                    outputPath = Path.Combine(tempFolder, fileName);
                }

                workbook.SaveAs(outputPath);
                return outputPath;
            }
        }

        #endregion

        #region Helper Methods - Unit Conversion

        private static double CalculateForceFactor(string targetUnit)
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
                return 1.0;
        }

        #endregion

        #region Section Writers

        private static int WriteHeader(IXLWorksheet ws, AuditReport report, string targetUnit, bool isVN, int startRow)
        {
            int row = startRow;

            // Title
            var titleCell = ws.Cell(row, 1);
            titleCell.Value = isVN
                ? "KIỂM TOÁN TẢI TRỌNG SAP2000 (DTS ENGINE v5.0)"
                : "SAP2000 LOAD AUDIT REPORT (DTS ENGINE v5.0)";
            titleCell.Style.Font.Bold = true;
            titleCell.Style.Font.FontSize = 14;
            titleCell.Style.Fill.BackgroundColor = HEADER_COLOR;
            titleCell.Style.Font.FontColor = XLColor.White;
            ws.Range(row, 1, row, 8).Merge().Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
            row++;

            // Project Info
            ws.Cell(row, 1).Value = isVN ? "Dự án:" : "Project:";
            ws.Cell(row, 2).Value = report.ModelName ?? "Unknown";
            row++;

            ws.Cell(row, 1).Value = isVN ? "Tổ hợp tải:" : "Load Pattern:";
            ws.Cell(row, 2).Value = report.LoadPattern;
            row++;

            ws.Cell(row, 1).Value = isVN ? "Ngày tính:" : "Audit Date:";
            ws.Cell(row, 2).Value = report.AuditDate.ToString("yyyy-MM-dd HH:mm:ss");
            row++;

            ws.Cell(row, 1).Value = isVN ? "Đơn vị:" : "Report Unit:";
            ws.Cell(row, 2).Value = targetUnit;
            row += 2;

            return row;
        }

        private static int WriteStoryDetails(
            IXLWorksheet ws,
            AuditReport report,
            double forceFactor,
            string targetUnit,
            bool isVN,
            int startRow)
        {
            int row = startRow;

            // Section header
            var sectionCell = ws.Cell(row, 1);
            sectionCell.Value = isVN ? "CHI TIẾT THEO TẦNG:" : "DETAIL BY STORY:";
            sectionCell.Style.Font.Bold = true;
            sectionCell.Style.Font.FontSize = 12;
            ws.Range(row, 1, row, 8).Merge();
            row += 2;

            foreach (var story in report.Stories.OrderByDescending(s => s.Elevation))
            {
                // Calculate story totals from vector components
                double storyFx = story.LoadTypes.Sum(lt => lt.SubTotalFx) * forceFactor;
                double storyFy = story.LoadTypes.Sum(lt => lt.SubTotalFy) * forceFactor;
                double storyFz = story.LoadTypes.Sum(lt => lt.SubTotalFz) * forceFactor;

                // Story Header
                var storyHeaderCell = ws.Cell(row, 1);
                storyHeaderCell.Value = $">>> {(isVN ? "TẦNG" : "STORY")}: {story.StoryName} | Z={story.Elevation:0}mm | " +
                    $"Fx={storyFx:0.00} Fy={storyFy:0.00} Fz={storyFz:0.00} {targetUnit}";
                storyHeaderCell.Style.Font.Bold = true;
                storyHeaderCell.Style.Fill.BackgroundColor = STORY_HEADER_COLOR;
                ws.Range(row, 1, row, 8).Merge();
                row += 2;

                foreach (var loadType in story.LoadTypes)
                {
                    // Load Type subtotals
                    double typeFx = loadType.SubTotalFx * forceFactor;
                    double typeFy = loadType.SubTotalFy * forceFactor;
                    double typeFz = loadType.SubTotalFz * forceFactor;

                    // Load Type Header
                    var typeHeaderCell = ws.Cell(row, 1);
                    typeHeaderCell.Value = $"[{loadType.LoadTypeName}] " +
                        $"Subtotal: Fx={typeFx:0.00} Fy={typeFy:0.00} Fz={typeFz:0.00} {targetUnit}";
                    typeHeaderCell.Style.Font.Bold = true;
                    ws.Range(row, 1, row, 8).Merge();
                    row++;

                    // Column headers - NEW FORMAT
                    // Location | Explanation | Quantity | UnitLoad | Fx | Fy | Fz | Elements
                    string qtyUnit = loadType.Entries.FirstOrDefault()?.QuantityUnit ?? "m²";

                    string[] headers = isVN
                        ? new[] { "Vị trí", "Diễn giải", $"Khối lượng({qtyUnit})", $"Tải đơn vị({targetUnit}/{qtyUnit})", $"Fx({targetUnit})", $"Fy({targetUnit})", $"Fz({targetUnit})", "Phần tử" }
                        : new[] { "Location", "Explanation", $"Quantity({qtyUnit})", $"UnitLoad({targetUnit}/{qtyUnit})", $"Fx({targetUnit})", $"Fy({targetUnit})", $"Fz({targetUnit})", "Elements" };

                    for (int i = 0; i < headers.Length; i++)
                    {
                        var headerCell = ws.Cell(row, i + 1);
                        headerCell.Value = headers[i];
                        headerCell.Style.Font.Bold = true;
                        headerCell.Style.Fill.BackgroundColor = SUBHEADER_COLOR;
                        headerCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                    }
                    row++;

                    // Data Rows
                    var entries = loadType.Entries?.OrderByDescending(e => Math.Abs(e.TotalForce)) ?? Enumerable.Empty<AuditEntry>();
                    foreach (var entry in entries)
                    {
                        ws.Cell(row, 1).Value = entry.GridLocation ?? "Unknown";

                        ws.Cell(row, 2).Value = entry.Explanation ?? "";

                        ws.Cell(row, 3).Value = entry.Quantity;
                        ws.Cell(row, 3).Style.NumberFormat.Format = "0.00";

                        // UnitLoad with unit conversion
                        ws.Cell(row, 4).Value = entry.UnitLoad * forceFactor;
                        ws.Cell(row, 4).Style.NumberFormat.Format = "0.00";

                        // Force components with unit conversion
                        ws.Cell(row, 5).Value = entry.ForceX * forceFactor;
                        ws.Cell(row, 5).Style.NumberFormat.Format = "0.00";

                        ws.Cell(row, 6).Value = entry.ForceY * forceFactor;
                        ws.Cell(row, 6).Style.NumberFormat.Format = "0.00";

                        ws.Cell(row, 7).Value = entry.ForceZ * forceFactor;
                        ws.Cell(row, 7).Style.NumberFormat.Format = "0.00";

                        // Full element list (no truncation for Excel)
                        ws.Cell(row, 8).Value = entry.ElementCount > 0
                            ? string.Join(", ", entry.ElementList)
                            : "";

                        // Apply borders
                        ws.Range(row, 1, row, 8).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        row++;
                    }

                    row++; // Empty row between load types
                }

                row++; // Empty row between stories
            }

            return row;
        }

        private static int WriteSummary(
            IXLWorksheet ws,
            AuditReport report,
            double forceFactor,
            string targetUnit,
            bool isVN,
            int startRow)
        {
            int row = startRow;

            // Section header
            var summaryHeaderCell = ws.Cell(row, 1);
            summaryHeaderCell.Value = isVN ? "TỔNG KIỂM TOÁN:" : "AUDIT SUMMARY:";
            summaryHeaderCell.Style.Font.Bold = true;
            summaryHeaderCell.Style.Font.FontSize = 12;
            summaryHeaderCell.Style.Fill.BackgroundColor = SUMMARY_COLOR;
            ws.Range(row, 1, row, 4).Merge();
            row += 2;

            // Recalculate from entries for consistency
            double totalFx = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFx)) * forceFactor;
            double totalFy = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFy)) * forceFactor;
            double totalFz = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFz)) * forceFactor;
            double totalMagnitude = Math.Sqrt(totalFx * totalFx + totalFy * totalFy + totalFz * totalFz);

            // Summary data
            var summaryData = new[]
            {
                (isVN ? "Tổng lực (Magnitude):" : "Total Force (Mag):", totalMagnitude),
                (isVN ? "Thành phần Fx:" : "Component Fx:", totalFx),
                (isVN ? "Thành phần Fy:" : "Component Fy:", totalFy),
                (isVN ? "Thành phần Fz:" : "Component Fz:", totalFz)
            };

            foreach (var (label, value) in summaryData)
            {
                ws.Cell(row, 1).Value = label;
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = value;
                ws.Cell(row, 2).Style.NumberFormat.Format = "0.00";
                ws.Cell(row, 3).Value = targetUnit;
                row++;
            }

            if (report.IsAnalyzed)
            {
                double sapReaction = Math.Abs(report.SapBaseReaction) * forceFactor;
                double diff = totalMagnitude - sapReaction;
                double diffPercent = (sapReaction > 0) ? (diff / sapReaction) * 100.0 : 0;

                ws.Cell(row, 1).Value = isVN ? "Phản lực SAP:" : "SAP Base Reaction:";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = sapReaction;
                ws.Cell(row, 2).Style.NumberFormat.Format = "0.00";
                ws.Cell(row, 3).Value = targetUnit;
                row++;

                ws.Cell(row, 1).Value = isVN ? "Sai số:" : "Difference:";
                ws.Cell(row, 1).Style.Font.Bold = true;
                ws.Cell(row, 2).Value = diff;
                ws.Cell(row, 2).Style.NumberFormat.Format = "0.00";
                ws.Cell(row, 3).Value = $"{targetUnit} ({diffPercent:0.00}%)";
                row++;
            }
            else
            {
                ws.Cell(row, 1).Value = isVN
                    ? "Lưu ý: Chưa phân tích - Vui lòng kiểm tra thủ công"
                    : "Note: Not analyzed - Please verify manually";
                ws.Range(row, 1, row, 4).Merge();
                row++;
            }

            return row;
        }

        private static void FormatWorksheet(IXLWorksheet ws)
        {
            // Auto-fit columns
            ws.Columns().AdjustToContents();

            // Set reasonable max widths
            foreach (var col in ws.ColumnsUsed())
            {
                if (col.Width > 50) col.Width = 50;
            }

            // Freeze header rows
            ws.SheetView.FreezeRows(1);
        }

        #endregion
    }
}
