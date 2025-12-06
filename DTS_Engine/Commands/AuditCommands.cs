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
        /// Lệnh chính để kiểm toán tải trọng SAP2000.
        /// Nhập các Load Pattern cần kiểm tra, xuất báo cáo ra file text.
        /// </summary>
        [CommandMethod("DTS_AUDIT_SAP2000")]
        public void DTS_AUDIT_SAP2000()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("\n==============================================");
                WriteMessage("      DTS ENGINE - AUDIT TẢI TRỌNG SAP2000     ");
                WriteMessage("==============================================");

                // 1. Chọn ngôn ngữ
                var langOpt = new PromptKeywordOptions("\nChọn ngôn ngữ báo cáo [English/Vietnamese]: ");
                langOpt.Keywords.Add("English");
                langOpt.Keywords.Add("Vietnamese");
                langOpt.Keywords.Default = "English";
                langOpt.AllowNone = true;
                var langRes = Ed.GetKeywords(langOpt);
                string selectedLang = (langRes.Status == PromptStatus.OK) ? langRes.StringResult : "English";

                // 2. Kết nối SAP
                if (!EnsureSapConnection()) return;

                // 3. Lấy dữ liệu Pattern
                WriteMessage("\nĐang quét dữ liệu Load Patterns...");
                var activePatterns = SapUtils.GetActiveLoadPatterns(); // Danh sách có tải
                var allPatterns = SapUtils.GetLoadPatterns();          // Toàn bộ danh sách

                if (allPatterns.Count == 0)
                {
                    WriteError("Không tìm thấy Load Pattern nào trong model.");
                    return;
                }

                // 4. Xây dựng Menu chọn thông minh
                var pko = new PromptKeywordOptions("\nChọn Load Case (Click chọn hoặc nhập danh sách cách nhau dấu phẩy): ");
                pko.AllowArbitraryInput = true; // <--- CẢI TIẾN: Cho phép nhập chuỗi bất kỳ
                pko.AllowNone = false;

                // Thêm top 15 pattern có tải làm Keyword để click nhanh
                var displayList = activePatterns.Take(15).ToList();
                
                // Nếu ít pattern quá, điền thêm từ danh sách all cho đủ 5 (để menu đỡ trống)
                if (displayList.Count < 5)
                {
                    foreach(var p in allPatterns)
                    {
                        if (displayList.Count >= 5) break;
                        if (!displayList.Any(dp => string.Equals(dp.Name, p, StringComparison.OrdinalIgnoreCase)))
                            displayList.Add(new SapUtils.PatternSummary { Name = p, TotalEstimatedLoad = 0 });
                    }
                }

                WriteMessage("\n--- CÁC LOAD PATTERN GỢI Ý ---");
                foreach (var pat in displayList)
                {
                    try
                    {
                        // Guard empty/null names
                        if (string.IsNullOrWhiteSpace(pat.Name)) continue;

                        // Thêm vào keyword để user có thể click chuột
                        pko.Keywords.Add(pat.Name);

                        string status = pat.TotalEstimatedLoad > 0.001 ? $"[Tải: ~{pat.TotalEstimatedLoad:N0}]" : "[Trống]";
                        WriteMessage($" - {pat.Name} {status}");
                    }
                    catch
                    {
                        // Bỏ qua nếu tên pattern chứa ký tự đặc biệt mà AutoCAD không chấp nhận làm keyword
                        // User vẫn có thể nhập tay pattern này
                    }
                }

                // Thêm lựa chọn All
                bool hasAllKeyword = false;
                foreach (var kw in pko.Keywords)
                {
                    if (string.Equals(kw.ToString(), "All", StringComparison.OrdinalIgnoreCase))
                    {
                        hasAllKeyword = true;
                        break;
                    }
                }
                if (!hasAllKeyword) pko.Keywords.Add("All");

                // 5. Lấy input từ người dùng
                PromptResult res = Ed.GetKeywords(pko);
                
                if (res.Status != PromptStatus.OK) return;

                string inputResult = res.StringResult;
                var selectedPatterns = new List<string>();

                // 6. Xử lý input (Keyword hoặc Chuỗi nhập tay)
                if (string.Equals(inputResult, "All", StringComparison.OrdinalIgnoreCase))
                {
                    // Nếu chọn All -> Lấy tất cả pattern có tải thực tế
                    selectedPatterns = activePatterns.Select(p => p.Name).ToList();
                    // Nếu không có pattern nào có tải, lấy toàn bộ danh sách
                    if (selectedPatterns.Count == 0) selectedPatterns = allPatterns;
                }
                else
                {
                    // Tự động tách chuỗi theo dấu phẩy, chấm phẩy hoặc khoảng trắng
                    // Ví dụ: "DL, SDL" hoặc "DL SDL" đều được
                    var parts = inputResult.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    
                    foreach (var part in parts)
                    {
                        string cleanPat = part.Trim();
                        if (string.IsNullOrEmpty(cleanPat)) continue;

                        // Try to find canonical name from allPatterns (case-insensitive)
                        var canonical = allPatterns.FirstOrDefault(a => a.Equals(cleanPat, StringComparison.OrdinalIgnoreCase));
                        string useName = canonical ?? cleanPat;

                        // Kiểm tra tồn tại trong SAP (không phân biệt hoa thường)
                        if (SapUtils.LoadPatternExists(useName))
                        {
                            selectedPatterns.Add(useName);
                        }
                        else
                        {
                            WriteWarning($"Pattern '{cleanPat}' không tồn tại trong SAP. Đã bỏ qua.");
                        }
                    }
                }

                if (selectedPatterns.Count == 0)
                {
                    WriteWarning("Không có Load Pattern hợp lệ nào được chọn.");
                    return;
                }

                // Loại bỏ trùng lặp
                selectedPatterns = selectedPatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                // 7. Chọn đơn vị
                var unitOpt = new PromptKeywordOptions("\nChọn đơn vị xuất báo cáo [Ton/kN/kgf]: ", "Ton kN kgf");
                var unitRes = Ed.GetKeywords(unitOpt);
                string selectedUnit = (unitRes.Status == PromptStatus.OK) ? unitRes.StringResult : "kN";

                // 8. Chạy Audit
                WriteMessage($"\n>> Đang xử lý {selectedPatterns.Count} patterns...");
                var engine = new AuditEngine();
                
                string tempFolder = Path.GetTempPath();
                int fileCounter = 0;
                string firstFilePath = null;

                foreach (var pat in selectedPatterns)
                {
                    WriteMessage($"\n   Processing: {pat}...");
                    var report = engine.RunSingleAudit(pat);
                    
                    // Tạo nội dung báo cáo
                    string content = engine.GenerateTextReport(report, selectedUnit, selectedLang);

                    // Lưu file
                    fileCounter++;
                    string safeModel = string.IsNullOrWhiteSpace(report.ModelName) ? "Model" : Path.GetFileNameWithoutExtension(report.ModelName);
                    string fileName = $"DTS_Audit_{safeModel}_{pat}_{fileCounter:D2}.txt";
                    string filePath = Path.Combine(tempFolder, fileName);

                    File.WriteAllText(filePath, content, Encoding.UTF8);
                    
                    WriteMessage($"   -> Done: {fileName}");
                    
                    if (firstFilePath == null) firstFilePath = filePath;
                }

                // 9. Mở file kết quả đầu tiên
                if (!string.IsNullOrEmpty(firstFilePath))
                {
                    WriteSuccess($"\nHoàn thành! Đã tạo {fileCounter} báo cáo tại {tempFolder}");
                    try { System.Diagnostics.Process.Start(firstFilePath); }
                    catch (System.Exception ex)     
                    {
                        System.Diagnostics.Debug.WriteLine($"Failed to open audit report: {ex}");
                    }
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

                // Tạo CSV
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

                // Mở file
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
