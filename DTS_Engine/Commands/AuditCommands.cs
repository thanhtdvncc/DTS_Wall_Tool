using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace DTS_Engine.Commands
{
    /// <summary>
    /// Các l?nh ki?m toán t?i tr?ng SAP2000.
    /// H? tr? xu?t báo cáo th?ng kê chi ti?t theo t?ng, lo?i t?i và v? trí.
    /// 
    /// ?? TÍNH N?NG:
    /// - ??c toàn b? t?i tr?ng t? SAP2000 (Frame, Area, Point)
    /// - Nhóm theo t?ng và lo?i t?i
    /// - Tính t?ng di?n tích/chi?u dài
    /// - So sánh v?i ph?n l?c ?áy
    /// </summary>
    public class AuditCommands : CommandBase
    {
        #region Main Audit Command

        /// <summary>
        /// L?nh chính ?? ki?m toán t?i tr?ng SAP2000.
        /// Nh?p các Load Pattern c?n ki?m tra, xu?t báo cáo ra file text.
        /// </summary>
        [CommandMethod("DTS_AUDIT_SAP2000")]
        public void DTS_AUDIT_SAP2000()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== KI?M TOÁN T?I TR?NG SAP2000 ===");

                // 1. Ki?m tra k?t n?i SAP
                if (!EnsureSapConnection())
                    return;

                // 2. Hi?n th? danh sách Load Pattern có s?n
                var availablePatterns = SapUtils.GetLoadPatterns();
                if (availablePatterns.Count == 0)
                {
                    WriteError("Không tìm th?y Load Pattern nào trong model SAP2000.");
                    return;
                }

                WriteMessage($"\nLoad Patterns có s?n: {string.Join(", ", availablePatterns)}");

                // 3. Nh?p Load Patterns c?n ki?m tra
                var patternOpt = new PromptStringOptions("\nNh?p Load Pattern(s) c?n ki?m tra (cách nhau b?ng d?u ph?y): ");
                patternOpt.DefaultValue = "DL";
                patternOpt.AllowSpaces = true;

                var patternRes = Ed.GetString(patternOpt);
                if (patternRes.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(patternRes.StringResult))
                {
                    WriteMessage("?ã h?y l?nh.");
                    return;
                }

                string inputPatterns = patternRes.StringResult.Trim();

                // 4. Validate patterns
                var requestedPatterns = inputPatterns.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(p => p.Trim().ToUpper())
                    .Distinct()
                    .ToList();

                var invalidPatterns = requestedPatterns
                    .Where(p => !availablePatterns.Any(ap => ap.Equals(p, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

                if (invalidPatterns.Count > 0)
                {
                    WriteWarning($"Các pattern không t?n t?i s? b? b? qua: {string.Join(", ", invalidPatterns)}");
                }

                var validPatterns = requestedPatterns.Except(invalidPatterns).ToList();
                if (validPatterns.Count == 0)
                {
                    WriteError("Không có Load Pattern h?p l? ?? ki?m tra.");
                    return;
                }

                // 5. Ch?y ki?m toán
                WriteMessage($"\n?ang trích xu?t d? li?u cho: {string.Join(", ", validPatterns)}...");
                WriteMessage("(Quá trình này có th? m?t vài giây tùy kích th??c model)");

                var engine = new AuditEngine();
                var reports = engine.RunAudit(string.Join(",", validPatterns));

                if (reports.Count == 0)
                {
                    WriteWarning("Không có d? li?u t?i tr?ng nào ???c tìm th?y.");
                    return;
                }

                // 6. Xu?t báo cáo
                string tempFolder = Path.GetTempPath();
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var reportFiles = new List<string>();

                foreach (var report in reports)
                {
                    string fileName = $"DTS_Audit_{report.LoadPattern}_{timestamp}.txt";
                    string filePath = Path.Combine(tempFolder, fileName);

                    string reportContent = engine.GenerateTextReport(report);

                    File.WriteAllText(filePath, reportContent, Encoding.UTF8);
                    reportFiles.Add(filePath);

                    // Hi?n th? tóm t?t
                    WriteMessage($"\n--- {report.LoadPattern} ---");
                    WriteMessage($"  S? t?ng: {report.Stories.Count}");
                    WriteMessage($"  T?ng tính toán: {report.TotalCalculatedForce:n2} kN");

                    if (Math.Abs(report.SapBaseReaction) > 0.01)
                    {
                        WriteMessage($"  Base Reaction: {report.SapBaseReaction:n2} kN");
                        WriteMessage($"  Sai l?ch: {report.DifferencePercent:0.00}%");

                        if (Math.Abs(report.DifferencePercent) < 1.0)
                        {
                            WriteSuccess($"  Ki?m tra OK (< 1%)");
                        }
                        else if (Math.Abs(report.DifferencePercent) < 5.0)
                        {
                            WriteWarning($"  Ch?p nh?n (< 5%)");
                        }
                        else
                        {
                            WriteError($"  C?n xem xét (> 5%)");
                        }
                    }
                    else
                    {
                        WriteWarning("  Base Reaction: Ch?a có (ch?y phân tích model ?? l?y)");
                    }
                }

                // 7. M? file báo cáo
                WriteMessage($"\n--- ?ã t?o {reportFiles.Count} file báo cáo ---");
                foreach (var file in reportFiles)
                {
                    WriteMessage($"  {file}");
                }

                // H?i có mu?n m? file không
                var openOpt = new PromptKeywordOptions("\nM? file báo cáo? [Yes/No]");
                openOpt.Keywords.Add("Yes");
                openOpt.Keywords.Add("No");
                openOpt.Keywords.Default = "Yes";
                openOpt.AllowNone = true;

                var openRes = Ed.GetKeywords(openOpt);
                if (openRes.Status == PromptStatus.OK && openRes.StringResult == "Yes")
                {
                    foreach (var file in reportFiles)
                    {
                        try
                        {
                            Process.Start(file);
                        }
                        catch (System.Exception ex)
                        {
                            WriteWarning($"Không th? m? file: {ex.Message}");
                        }
                    }
                }

                WriteSuccess("Ki?m toán hoàn t?t.");
            });
        }

        #endregion

        #region Quick Summary Command

        /// <summary>
        /// L?nh xem tóm t?t nhanh t?i tr?ng theo Load Pattern
        /// </summary>
        [CommandMethod("DTS_LOAD_SUMMARY")]
        public void DTS_LOAD_SUMMARY()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== TÓM T?T T?I TR?NG SAP2000 ===");

                if (!EnsureSapConnection())
                    return;

                // L?y t?t c? patterns
                var patterns = SapUtils.GetLoadPatterns();
                if (patterns.Count == 0)
                {
                    WriteError("Không tìm th?y Load Pattern nào.");
                    return;
                }

                WriteMessage($"\nModel: {SapUtils.GetModelName()}");
                WriteMessage($"??n v?: {UnitManager.Info}");
                WriteMessage($"\nLoad Patterns ({patterns.Count}):");

                foreach (var pattern in patterns)
                {
                    // ??m s? t?i theo lo?i
                    int frameLoadCount = SapUtils.GetAllFrameDistributedLoads(pattern).Count;
                    int areaLoadCount = SapUtils.GetAllAreaUniformLoads(pattern).Count;
                    int pointLoadCount = SapUtils.GetAllPointLoads(pattern).Count;

                    int total = frameLoadCount + areaLoadCount + pointLoadCount;

                    if (total > 0)
                    {
                        WriteMessage($"  {pattern}: Frame={frameLoadCount}, Area={areaLoadCount}, Point={pointLoadCount}");
                    }
                    else
                    {
                        WriteMessage($"  {pattern}: (không có t?i)");
                    }
                }

                WriteMessage("\nDùng l?nh DTS_AUDIT_SAP2000 ?? xem chi ti?t.");
            });
        }

        #endregion

        #region List Elements Command

        /// <summary>
        /// Li?t kê ph?n t? có t?i theo pattern
        /// </summary>
        [CommandMethod("DTS_LIST_LOADED_ELEMENTS")]
        public void DTS_LIST_LOADED_ELEMENTS()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DANH SÁCH PH?N T? CÓ T?I ===");

                if (!EnsureSapConnection())
                    return;

                // Nh?p pattern
                var patternOpt = new PromptStringOptions("\nNh?p Load Pattern: ");
                patternOpt.DefaultValue = "DL";
                var patternRes = Ed.GetString(patternOpt);

                if (patternRes.Status != PromptStatus.OK)
                    return;

                string pattern = patternRes.StringResult.Trim().ToUpper();

                if (!SapUtils.LoadPatternExists(pattern))
                {
                    WriteError($"Load Pattern '{pattern}' không t?n t?i.");
                    return;
                }

                // L?y t?i
                var frameLoads = SapUtils.GetAllFrameDistributedLoads(pattern);
                var areaLoads = SapUtils.GetAllAreaUniformLoads(pattern);
                var pointLoads = SapUtils.GetAllPointLoads(pattern);

                WriteMessage($"\n--- {pattern} ---");

                // Frame loads
                if (frameLoads.Count > 0)
                {
                    WriteMessage($"\nFRAME DISTRIBUTED ({frameLoads.Count}):");
                    var grouped = frameLoads.GroupBy(l => l.Value1).OrderByDescending(g => g.Key);
                    foreach (var g in grouped.Take(10))
                    {
                        var names = g.Select(l => l.ElementName).Take(10);
                        WriteMessage($"  {g.Key:0.00} kN/m: {string.Join(", ", names)}{(g.Count() > 10 ? "..." : "")}");
                    }
                }

                // Area loads
                if (areaLoads.Count > 0)
                {
                    WriteMessage($"\nAREA UNIFORM ({areaLoads.Count}):");
                    var grouped = areaLoads.GroupBy(l => l.Value1).OrderByDescending(g => g.Key);
                    foreach (var g in grouped.Take(10))
                    {
                        var names = g.Select(l => l.ElementName).Take(10);
                        WriteMessage($"  {g.Key:0.00} kN/m²: {string.Join(", ", names)}{(g.Count() > 10 ? "..." : "")}");
                    }
                }

                // Point loads
                if (pointLoads.Count > 0)
                {
                    WriteMessage($"\nPOINT FORCE ({pointLoads.Count}):");
                    var grouped = pointLoads.GroupBy(l => l.Value1).OrderByDescending(g => g.Key);
                    foreach (var g in grouped.Take(10))
                    {
                        var names = g.Select(l => l.ElementName).Take(10);
                        WriteMessage($"  {g.Key:0.00} kN: {string.Join(", ", names)}{(g.Count() > 10 ? "..." : "")}");
                    }
                }

                int total = frameLoads.Count + areaLoads.Count + pointLoads.Count;
                WriteMessage($"\nT?ng: {total} b?n ghi t?i.");
            });
        }

        #endregion

        #region Reaction Check Command

        /// <summary>
        /// Ki?m tra ph?n l?c ?áy cho các load case
        /// </summary>
        [CommandMethod("DTS_CHECK_REACTIONS")]
        public void DTS_CHECK_REACTIONS()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== KI?M TRA PH?N L?C ?ÁY ===");

                if (!EnsureSapConnection())
                    return;

                var patterns = SapUtils.GetLoadPatterns();

                WriteMessage($"\nModel: {SapUtils.GetModelName()}");
                WriteMessage("\nBase Reaction Z theo Load Pattern:");
                WriteMessage(new string('-', 50));

                bool hasAnyReaction = false;

                foreach (var pattern in patterns)
                {
                    double reaction = SapUtils.GetBaseReactionZ(pattern);
                    if (Math.Abs(reaction) > 0.01)
                    {
                        hasAnyReaction = true;
                        WriteMessage($"  {pattern,-15}: {reaction,15:n2} kN");
                    }
                }

                if (!hasAnyReaction)
                {
                    WriteWarning("\nKhông có ph?n l?c nào. Model có th? ch?a ???c phân tích.");
                    WriteMessage("Ch?y phân tích trong SAP2000 r?i th? l?i.");
                }
                else
                {
                    WriteMessage(new string('-', 50));
                }
            });
        }

        #endregion

        #region Export to CSV Command

        /// <summary>
        /// Xu?t d? li?u t?i sang CSV ?? x? lý trong Excel
        /// </summary>
        [CommandMethod("DTS_EXPORT_LOADS_CSV")]
        public void DTS_EXPORT_LOADS_CSV()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== XU?T T?I TR?NG RA CSV ===");

                if (!EnsureSapConnection())
                    return;

                // Nh?p pattern
                var patternOpt = new PromptStringOptions("\nNh?p Load Pattern (ho?c * cho t?t c?): ");
                patternOpt.DefaultValue = "*";
                var patternRes = Ed.GetString(patternOpt);

                if (patternRes.Status != PromptStatus.OK)
                    return;

                string patternInput = patternRes.StringResult.Trim();
                bool exportAll = patternInput == "*";

                // Thu th?p d? li?u
                var allLoads = new List<RawSapLoad>();

                if (exportAll)
                {
                    allLoads.AddRange(SapUtils.GetAllFrameDistributedLoads());
                    allLoads.AddRange(SapUtils.GetAllAreaUniformLoads());
                    allLoads.AddRange(SapUtils.GetAllPointLoads());
                }
                else
                {
                    allLoads.AddRange(SapUtils.GetAllFrameDistributedLoads(patternInput));
                    allLoads.AddRange(SapUtils.GetAllAreaUniformLoads(patternInput));
                    allLoads.AddRange(SapUtils.GetAllPointLoads(patternInput));
                }

                if (allLoads.Count == 0)
                {
                    WriteWarning("Không có d? li?u t?i ?? xu?t.");
                    return;
                }

                // T?o CSV
                var sb = new StringBuilder();
                sb.AppendLine("Element,LoadPattern,LoadType,Value,Direction,Z");

                foreach (var load in allLoads)
                {
                    sb.AppendLine($"{load.ElementName},{load.LoadPattern},{load.LoadType},{load.Value1:0.00},{load.Direction},{load.ElementZ:0}");
                }

                string fileName = $"DTS_Loads_{DateTime.Now:yyyyMMdd_HHmmss}.csv";
                string filePath = Path.Combine(Path.GetTempPath(), fileName);

                File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);

                WriteSuccess($"?ã xu?t {allLoads.Count} b?n ghi ra:");
                WriteMessage($"  {filePath}");

                // M? file
                try
                {
                    Process.Start(filePath);
                }
                catch { }
            });
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// ??m b?o ?ã k?t n?i SAP2000
        /// </summary>
        private bool EnsureSapConnection()
        {
            if (SapUtils.IsConnected)
                return true;

            WriteMessage("?ang k?t n?i SAP2000...");

            if (!SapUtils.Connect(out string msg))
            {
                WriteError(msg);
                return false;
            }

            WriteSuccess(msg);
            return true;
        }

        #endregion
    }
}
