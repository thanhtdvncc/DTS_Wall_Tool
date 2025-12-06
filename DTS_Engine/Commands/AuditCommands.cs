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
    /// Các lệnh kiểm toán tải trọng SAP2000.
    /// Hỗ trợ xuất báo cáo thống kê chi tiết theo tầng, loại tải và vị trí.
    ///
    /// TÍNH NĂNG:
    /// - Đọc toàn bộ tải trọng từ SAP2000 (Frame, Area, Point)
    /// - Nhóm theo tầng và loại tải
    /// - Tính tổng diện tích/chiều dài
    /// - So sánh với phản lực đáy
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
                WriteMessage("\n==============================================");
                WriteMessage("      DTS ENGINE - AUDIT TẢI TRỌNG SAP2000     ");
                WriteMessage("==============================================");

                var langOpt = new PromptKeywordOptions("\nChọn ngôn ngữ báo cáo [English/Vietnamese]: ");
                langOpt.Keywords.Add("English");
                langOpt.Keywords.Add("Vietnamese");
                langOpt.Keywords.Default = "English";
                langOpt.AllowNone = true;
                var langRes = Ed.GetKeywords(langOpt);
                string selectedLang = (langRes.Status == PromptStatus.OK) ? langRes.StringResult : "English";

                // 2. Kiểm tra kết nối SAP
                if (!EnsureSapConnection()) return;

                // 2. Lấy danh sách Pattern có tải (Smart Filter)
                WriteMessage("\nĐang quét dữ liệu Load Patterns...");
                var activePatterns = SapUtils.GetActiveLoadPatterns();
                
                // Lọc bỏ pattern rỗng (Total = 0) nếu danh sách quá dài
                var nonEmptyPatterns = activePatterns.Where(p => p.TotalEstimatedLoad > 0.001).ToList();
                if (nonEmptyPatterns.Count == 0) nonEmptyPatterns = activePatterns; // Fallback

                // 3. Xây dựng Menu chọn
                var pko = new PromptKeywordOptions("\nChọn Load Pattern cần kiểm toán:");
                pko.AllowNone = true;

                // Thêm 10 pattern nặng nhất vào menu
                int maxMenu = Math.Min(10, nonEmptyPatterns.Count);
                for (int i = 0; i < maxMenu; i++)
                {
                    pko.Keywords.Add(nonEmptyPatterns[i].Name);
                }
                
                if (nonEmptyPatterns.Count > maxMenu) pko.Keywords.Add("Other");
                pko.Keywords.Add("All");
                pko.Keywords.Default = maxMenu > 0 ? nonEmptyPatterns[0].Name : "All";

                // Hiển thị danh sách gợi ý
                WriteMessage("\nDanh sách Pattern có tải trọng lớn nhất:");
                for (int i = 0; i < maxMenu; i++)
                {
                    WriteMessage($"  - {nonEmptyPatterns[i].Name} (Est: {nonEmptyPatterns[i].TotalEstimatedLoad:N0})");
                }

                PromptResult res = Ed.GetKeywords(pko);
                if (res.Status != PromptStatus.OK) return;

                var selectedPatterns = new List<string>();

                if (res.StringResult == "All")
                {
                    selectedPatterns = nonEmptyPatterns.Select(p => p.Name).ToList();
                }
                else if (res.StringResult == "Other")
                {
                    var strOpt = new PromptStringOptions("\nNhập tên Load Pattern (cách nhau bởi dấu phẩy, chấm phẩy hoặc khoảng trắng): ");
                    var strRes = Ed.GetString(strOpt);
                    if (strRes.Status == PromptStatus.OK && !string.IsNullOrWhiteSpace(strRes.StringResult))
                    {
                        // ⚠️ FIX: Parse multiple delimiters and ensure distinct patterns
                        selectedPatterns = strRes.StringResult
                            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                            .Select(s => s.Trim())
                            .Where(s => !string.IsNullOrEmpty(s))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();
                    }
                }
                else
                {
                    selectedPatterns.Add(res.StringResult);
                }

                if (selectedPatterns.Count == 0)
                {
                    WriteWarning("Không có Pattern nào được chọn.");
                    return;
                }

                // 5. Chọn đơn vị
                var unitOpt = new PromptKeywordOptions("\nChọn đơn vị xuất báo cáo [Ton/kN/kgf/lb]: ");
                unitOpt.Keywords.Add("Ton");
                unitOpt.Keywords.Add("kN");
                unitOpt.Keywords.Add("kgf");
                unitOpt.Keywords.Add("lb");
                unitOpt.Keywords.Default = "Ton"; // Kỹ sư VN thích Tấn
                var unitRes = Ed.GetKeywords(unitOpt);
                if (unitRes.Status != PromptStatus.OK) return;
                string selectedUnit = unitRes.StringResult;

                // 5. Chạy Audit
                WriteMessage($"\nĐang xử lý {selectedPatterns.Count} patterns...");
                var engine = new AuditEngine();
                
                string tempFolder = Path.GetTempPath();
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                var reportFiles = new List<string>();

                int fileCounter = 0; // ⚠️ FIX: Add counter to prevent file overwrite

                foreach (var pat in selectedPatterns)
                {
                    WriteMessage($"Đang xử lý {pat}...");
                    var report = engine.RunSingleAudit(pat);
                    
                    if (report.Stories.Count == 0)
                    {
                        WriteWarning($"  -> {pat}: Không tìm thấy dữ liệu hoặc tải trọng = 0.");
                        continue;
                    }

                    fileCounter++;
                    string safeModel = string.IsNullOrWhiteSpace(report.ModelName) ? "Model" : Path.GetFileNameWithoutExtension(report.ModelName);
                    string fileName = $"DTS_Audit_{safeModel}_{pat}_{timestamp}_{fileCounter:D2}.txt";
                    string filePath = Path.Combine(tempFolder, fileName);
                    
                    // ⚠️ NEW: Pass language to report generator
                    string content = engine.GenerateTextReport(report, selectedUnit, selectedLang);

                    File.WriteAllText(filePath, content, Encoding.UTF8);
                    reportFiles.Add(filePath);
                    
                    WriteMessage($"  -> Tạo file: {fileName}");
                }

                // 7. Kết quả
                if (reportFiles.Count > 0)
                {
                    WriteSuccess($"Đã tạo {reportFiles.Count} báo cáo.");
                    
                    // Mở file đầu tiên ngay lập tức (UX: Instant Feedback)
                    try 
                    { 
                        Process.Start(reportFiles[0]); 
                        if (reportFiles.Count > 1) WriteMessage($"Các file khác nằm tại: {tempFolder}");
                    } 
                    catch { }
                }
                else
                {
                    WriteWarning("Không tạo được báo cáo nào (Model trống hoặc không có tải).");
                }
            });
        }

        #endregion

        #region Quick Summary Command

        /// <summary>
        /// Lệnh xem tóm tắt nhanh tải trọng theo Load Pattern
        /// </summary>
        [CommandMethod("DTS_LOAD_SUMMARY")]
        public void DTS_LOAD_SUMMARY()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== TÓM TẮT TẢI TRỌNG SAP2000 ===");

                if (!EnsureSapConnection())
                    return;

                // Lấy tất cả patterns
                var patterns = SapUtils.GetLoadPatterns();
                if (patterns.Count == 0)
                {
                WriteError("Không tìm thấy Load Pattern nào.");
                    return;
                }

                WriteMessage($"\nModel: {SapUtils.GetModelName()}");
                WriteMessage($"Đơn vị: {UnitManager.Info}");
                WriteMessage($"\nLoad Patterns ({patterns.Count}):");

                foreach (var pattern in patterns)
                {
                    // Đếm số tải theo loại
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
                        WriteMessage($"  {pattern}: (không có tải)");
                    }
                }

                WriteMessage("\nDùng lệnh DTS_AUDIT_SAP2000 để xem chi tiết.");
            });
        }

        #endregion

        #region List Elements Command

        /// <summary>
        /// Liệt kê phần tử có tải theo pattern
        /// </summary>
        [CommandMethod("DTS_LIST_LOADED_ELEMENTS")]
        public void DTS_LIST_LOADED_ELEMENTS()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== DANH SÁCH PHẦN TỬ CÓ TẢI ===");

                if (!EnsureSapConnection())
                    return;

                // Nhập pattern
                var patternOpt = new PromptStringOptions("\nNhập Load Pattern: ");
                patternOpt.DefaultValue = "DL";
                var patternRes = Ed.GetString(patternOpt);

                if (patternRes.Status != PromptStatus.OK)
                    return;

                string pattern = patternRes.StringResult.Trim().ToUpper();

                if (!SapUtils.LoadPatternExists(pattern))
                {
                    WriteError($"Load Pattern '{pattern}' không tồn tại.");
                    return;
                }

                // Lấy tải
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
                WriteMessage($"\nTổng: {total} bản ghi tải.");
            });
        }

        #endregion

        #region Reaction Check Command

        /// <summary>
        /// Kiểm tra phản lực đáy cho các load case
        /// </summary>
        [CommandMethod("DTS_CHECK_REACTIONS")]
        public void DTS_CHECK_REACTIONS()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== KIỂM TRA PHẢN LỰC ĐÁY ===");

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
                    WriteWarning("\nKhông có phản lực nào. Model có thể chưa được phân tích.");
                    WriteMessage("Chạy phân tích trong SAP2000 rồi thử lại.");
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
        /// Xuất dữ liệu tải sang CSV để xử lý trong Excel
        /// </summary>
        [CommandMethod("DTS_EXPORT_LOADS_CSV")]
        public void DTS_EXPORT_LOADS_CSV()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n=== XUẤT TẢI TRỌNG RA CSV ===");

                if (!EnsureSapConnection())
                    return;

                // Nhập pattern
                var patternOpt = new PromptStringOptions("\nNhập Load Pattern (hoặc * cho tất cả): ");
                patternOpt.DefaultValue = "*";
                var patternRes = Ed.GetString(patternOpt);

                if (patternRes.Status != PromptStatus.OK)
                    return;

                string patternInput = patternRes.StringResult.Trim();
                bool exportAll = patternInput == "*";

                // Thu thập dữ liệu
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
                    WriteWarning("Không có dữ liệu tải để xuất.");
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

                WriteSuccess($"Đã xuất {allLoads.Count} bản ghi ra:");
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
        /// Đảm bảo đã kết nối SAP2000
        /// </summary>
        private bool EnsureSapConnection()
        {
            if (SapUtils.IsConnected)
                return true;

            WriteMessage("Đang kết nối SAP2000...");

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
