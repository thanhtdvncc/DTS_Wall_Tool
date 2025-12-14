using ClosedXML.Excel;
using DTS_Engine.Core.Data;
using System;
using System.IO;
using System.Linq;

namespace DTS_Engine.Core.Utils
{
    /// <summary>
    /// Excel Report Generator using ClosedXML
    /// FIX BUG #6: Complete Excel export implementation
    /// 
    /// ARCHITECTURE:
    /// - Clean separation: Reads AuditReport data model
    /// - Generates formatted Excel workbook with styling
    /// - Supports unit conversion and bilingual output
    /// 
    /// ISO/IEC 25010 Compliance:
    /// - Usability: Professional formatting with merged cells and colors
    /// - Maintainability: Single responsibility - Excel generation only
    /// - Performance: Single-pass write with efficient ClosedXML API
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

            // Calculate force factor for unit conversion
            double forceFactor = CalculateForceFactor(targetUnit);
            bool isVN = language.Equals("Vietnamese", StringComparison.OrdinalIgnoreCase);

            // Create workbook
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
                return 1.0; // Default to kN
        }

        #endregion

        #region Section Writers

        private static int WriteHeader(IXLWorksheet ws, AuditReport report, string targetUnit, bool isVN, int startRow)
        {
            int row = startRow;

            // Title
            var titleCell = ws.Cell(row, 1);
            titleCell.Value = isVN 
                ? "KIỂM TOÁN TẢI TRỌNG SAP2000 (DTS ENGINE v11.0)" 
                : "SAP2000 LOAD AUDIT REPORT (DTS ENGINE v11.0)";
            titleCell.Style.Font.Bold = true;
            titleCell.Style.Font.FontSize = 14;
            titleCell.Style.Fill.BackgroundColor = HEADER_COLOR;
            titleCell.Style.Font.FontColor = XLColor.White;
            ws.Range(row, 1, row, 7).Merge().Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
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
            row += 2; // Empty row

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
            ws.Range(row, 1, row, 7).Merge();
            row += 2;

            foreach (var story in report.Stories.OrderByDescending(s => s.Elevation))
            {
                // [v11.0] Calculate story total from vector components
                double storyFx = story.LoadTypes.Sum(lt => lt.SubTotalFx) * forceFactor;
                double storyFy = story.LoadTypes.Sum(lt => lt.SubTotalFy) * forceFactor;
                double storyFz = story.LoadTypes.Sum(lt => lt.SubTotalFz) * forceFactor;

                // Story Header with vector breakdown
                var storyHeaderCell = ws.Cell(row, 1);
                storyHeaderCell.Value = $">>> {(isVN ? "TẦNG" : "STORY")}: {story.StoryName} | Z={story.Elevation:0}mm | " +
                    $"[Fx={storyFx:0.00}, Fy={storyFy:0.00}, Fz={storyFz:0.00}]";
                storyHeaderCell.Style.Font.Bold = true;
                storyHeaderCell.Style.Fill.BackgroundColor = STORY_HEADER_COLOR;
                ws.Range(row, 1, row, 7).Merge();
                row += 2;

                foreach (var loadType in story.LoadTypes)
                {
                    // Group by Structural Type (Slab, Wall, Beam, Column, Point)
                    var typeGroups = loadType.Entries.GroupBy(e => e.StructuralType).OrderBy(g => g.Key);

                    foreach (var subGroup in typeGroups)
                    {
                        string structHeader = subGroup.Key;
                        if (isVN)
                        {
                            if (structHeader == "Slab Elements") structHeader = "Sàn (Slab)";
                            else if (structHeader == "Wall Elements") structHeader = "Vách (Wall)";
                            else if (structHeader == "Beam Elements") structHeader = "Dầm (Beam)";
                            else if (structHeader == "Column Elements") structHeader = "Cột (Column)";
                            else if (structHeader == "Point Elements") structHeader = "Nút (Point)";
                        }

                        // Structural Type Header
                        var typeHeaderCell = ws.Cell(row, 1);
                        typeHeaderCell.Value = $"[{loadType.LoadTypeName}] - {structHeader}";
                        typeHeaderCell.Style.Font.Bold = true;
                        ws.Range(row, 1, row, 7).Merge();
                        row++;

                        // [v11.0] Column headers
                        string valueUnit = subGroup.FirstOrDefault()?.QuantityUnit ?? "m²";
                        
                        string[] headers = isVN
                            ? new[] { "Vị trí trục", "Chi tiết tính toán", $"Value({valueUnit})", $"Unit Load", "Hướng", $"Force({targetUnit})", "Phần tử" }
                            : new[] { "Grid Location", "Calculator", $"Value({valueUnit})", $"Unit Load", "Dir", $"Force({targetUnit})", "Elements" };

                        for (int i = 0; i < headers.Length; i++)
                        {
                            var headerCell = ws.Cell(row, i + 1);
                            headerCell.Value = headers[i];
                            headerCell.Style.Font.Bold = true;
                            headerCell.Style.Fill.BackgroundColor = SUBHEADER_COLOR;
                            headerCell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                        }
                        row++;

                        // [v11.0] FLAT SORT: Direction -> Axis -> Span (NO HEADERS)
                        var sortedEntries = subGroup
                            .OrderBy(e => GetDirectionPriority(e.Direction))
                            .ThenBy(e => GetAxisSortValue(GetBaseGridName(e.GridLocation)))
                            .ThenBy(e => GetCrossAxisSortKey(e.GridLocation))
                            .ToList();

                        foreach (var entry in sortedEntries)
                        {
                            // [v11.0] FULL DATA - NO TRUNCATION
                            ws.Cell(row, 1).Value = entry.GridLocation ?? "";
                            ws.Cell(row, 2).Value = entry.Explanation ?? "";
                            ws.Cell(row, 3).Value = entry.Quantity;
                            ws.Cell(row, 3).Style.NumberFormat.Format = "0.00";
                            
                            // Apply forceFactor to UnitLoad for display consistency
                            double displayUnitLoad = entry.UnitLoad * forceFactor;
                            ws.Cell(row, 4).Value = displayUnitLoad;
                            ws.Cell(row, 4).Style.NumberFormat.Format = "0.00";
                            
                            // Direction
                            ws.Cell(row, 5).Value = entry.Direction ?? "";
                            
                            // Display primary force component (matching text report logic)
                            double forceX = entry.ForceX * forceFactor;
                            double forceY = entry.ForceY * forceFactor;
                            double forceZ = entry.ForceZ * forceFactor;
                            double displayForce = 0;
                            if (Math.Abs(forceX) > Math.Abs(forceY) && Math.Abs(forceX) > Math.Abs(forceZ))
                                displayForce = forceX;
                            else if (Math.Abs(forceY) > Math.Abs(forceZ))
                                displayForce = forceY;
                            else
                                displayForce = forceZ;
                            
                            ws.Cell(row, 6).Value = displayForce;
                            ws.Cell(row, 6).Style.NumberFormat.Format = "0.00";
                            
                            // [v11.0] FULL element list (NO truncation for Excel)
                            ws.Cell(row, 7).Value = entry.ElementList != null && entry.ElementList.Count > 0 
                                ? string.Join(", ", entry.ElementList)
                                : "";

                            // Apply borders
                            ws.Range(row, 1, row, 7).Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                            row++;
                        }

                        row++; // Empty row between structural types
                    }
                }

                row++; // Empty row between stories
            }

            return row;
        }

        #region Sorting Helpers (v11.0)

        // [Priority 1] Direction priority: +X, -X, +Y, -Y, +Z, -Z
        private static int GetDirectionPriority(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return 99;
            dir = dir.Trim().ToUpper();
            if (dir == "+X") return 1;
            if (dir == "-X") return 2;
            if (dir == "+Y") return 3;
            if (dir == "-Y") return 4;
            if (dir == "+Z") return 5;
            if (dir == "-Z") return 6;
            return 10;
        }

        // [Priority 2] Axis sort: Numbers first, Letters after
        private static double GetAxisSortValue(string axis)
        {
            if (string.IsNullOrEmpty(axis)) return 9999999;
            if (double.TryParse(axis, out double val)) return val;
            return 1000000.0 + (int)axis[0] * 1000 + (axis.Length > 1 ? (int)axis[1] : 0);
        }

        // Extract base grid name (Grid D x 1-2 -> D)
        private static string GetBaseGridName(string gridLoc)
        {
            if (string.IsNullOrEmpty(gridLoc)) return "Unknown";
            var parts = gridLoc.Split(new[] { " x " }, StringSplitOptions.RemoveEmptyEntries);
            string mainPart = parts[0].Replace("Grid", "").Trim();
            int parenIndex = mainPart.IndexOf('(');
            if (parenIndex > 0) mainPart = mainPart.Substring(0, parenIndex).Trim();
            return mainPart;
        }

        // Sort key for span (part after x)
        private static string GetCrossAxisSortKey(string gridLoc)
        {
            var parts = gridLoc.Split(new[] { " x " }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 1) return parts[1].Trim();
            return "ZZZ";
        }

        #endregion

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
            ws.Range(row, 1, row, 3).Merge();
            row += 2;

            // ====================================================================================
            // CRITICAL FIX v4.3: RECALCULATE FROM VISUAL DATA
            // Tính lại từ report.Stories thay vì tin report.CalculatedFx (có thể từ Raw Data)
            // ====================================================================================

            // 1. Calculate Visual Sums from Processed Data
            double visualFx = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFx));
            double visualFy = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFy));
            double visualFz = report.Stories.Sum(s => s.LoadTypes.Sum(lt => lt.SubTotalFz));

            // 2. Apply unit conversion
            double displayFx = visualFx * forceFactor;
            double displayFy = visualFy * forceFactor;
            double displayFz = visualFz * forceFactor;

            // 3. Calculate magnitude
            double displayTotal = Math.Sqrt(displayFx * displayFx + displayFy * displayFy + displayFz * displayFz);

            // Summary data
            var summaryData = new[]
            {
                (isVN ? "Tổng lực tính toán:" : "Total Calculated Force:", displayTotal),
                (isVN ? "Thành phần Fx:" : "Force Component Fx:", displayFx),
                (isVN ? "Thành phần Fy:" : "Force Component Fy:", displayFy),
                (isVN ? "Thành phần Fz:" : "Force Component Fz:", displayFz)
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
                double diff = displayTotal - sapReaction;
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
                ws.Range(row, 1, row, 3).Merge();
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
