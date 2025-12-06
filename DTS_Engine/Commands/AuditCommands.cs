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
                var activePatterns = SapUtils.GetActiveLoadPatterns(); // Danh sách có tải (để sắp xếp)
                var allPatterns = SapUtils.GetLoadPatterns();          // Toàn bộ danh sách (để validate)

                if (allPatterns.Count == 0)
                {
                    WriteError("Không tìm thấy Load Pattern nào trong model.");
                    return;
                }

                // Map pattern name -> estimated load để hiển thị
                var loadSummaryMap = activePatterns.ToDictionary(p => p.Name, p => p.TotalEstimatedLoad, StringComparer.OrdinalIgnoreCase);

                // Sắp xếp patterns: Có tải lên trước, sau đó sắp A-Z
                var displayList = allPatterns.OrderByDescending(name =>
                    loadSummaryMap.ContainsKey(name) ? loadSummaryMap[name] : -1).ToList();

                // 4. Hiển thị Menu đánh số
                WriteMessage("\n--- DANH SÁCH LOAD PATTERN ---");
                int maxDisplay = 30;
                for (int i = 0; i < displayList.Count; i++)
                {
                    if (i >= maxDisplay)
                    {
                        WriteMessage($" ... và {displayList.Count - maxDisplay} pattern khác (vẫn có thể chọn bằng tên)");
                        break;
                    }
                    string patName = displayList[i];
                    double loadVal = loadSummaryMap.ContainsKey(patName) ? loadSummaryMap[patName] : 0;
                    string info = loadVal > 0.001 ? $"[~{loadVal:N0} kN]" : "[-]";
                    WriteMessage($" {i + 1,2}. {patName,-15} {info}");
                }
                WriteMessage(" --------------------------------------------");
                WriteMessage(" A. All (Chọn tất cả)");
                WriteMessage(" 0. Cancel");

                // 5. Yêu cầu nhập (dùng string để tránh parser issues)
                var pso = new PromptStringOptions("\nNhập số thứ tự (ví dụ: 1, 3) hoặc tên Pattern [All]: ");
                pso.AllowSpaces = true;
                pso.DefaultValue = "All";
                pso.UseDefaultValue = true;

                PromptResult pres = Ed.GetString(pso);
                if (pres.Status != PromptStatus.OK) return;

                string input = pres.StringResult.Trim();
                var selectedPatterns = new List<string>();

                var parts = input.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    string p = part.Trim();
                    if (string.IsNullOrEmpty(p)) continue;

                    if (p.Equals("All", StringComparison.OrdinalIgnoreCase) || p.Equals("A", StringComparison.OrdinalIgnoreCase))
                    {
                        selectedPatterns = allPatterns.ToList();
                        break;
                    }
                    else if (int.TryParse(p, out int idx))
                    {
                        if (idx == 0) return; // cancel
                        if (idx > 0 && idx <= displayList.Count)
                        {
                            selectedPatterns.Add(displayList[idx - 1]);
                        }
                        else
                        {
                            WriteWarning($"Số {idx} không hợp lệ (Max: {displayList.Count})");
                        }
                    }
                    else
                    {
                        var match = allPatterns.FirstOrDefault(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
                        if (match != null) selectedPatterns.Add(match);
                        else
                        {
                            string sanitizedInput = p.Replace("-", "").Replace("_", "");
                            var fuzzyMatch = allPatterns.FirstOrDefault(x => x.Replace("_", "").Replace("-", "").Equals(sanitizedInput, StringComparison.OrdinalIgnoreCase));
                            if (fuzzyMatch != null) selectedPatterns.Add(fuzzyMatch);
                            else WriteWarning($"Không tìm thấy pattern '{p}'");
                        }
                    }
                }

                if (selectedPatterns.Count == 0)
                {
                    WriteWarning("Không có Load Pattern nào được chọn.");
                    return;
                }

                selectedPatterns = selectedPatterns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                WriteMessage($"\n>> Đã chọn {selectedPatterns.Count} patterns: {string.Join(", ", selectedPatterns.Take(5))}{(selectedPatterns.Count > 5 ? "..." : "")}\n");

                // 7. Chọn đơn vị (sử dụng Keywords.Add để tránh lỗi parser)
                var unitOpt = new PromptKeywordOptions("\nChọn đơn vị xuất báo cáo [Ton/kN/kgf]: ");
                unitOpt.AllowNone = false;
                unitOpt.Keywords.Add("Ton");
                unitOpt.Keywords.Add("kN");
                unitOpt.Keywords.Add("kgf");
                unitOpt.Keywords.Default = "kN";
                var unitRes = Ed.GetKeywords(unitOpt);
                string selectedUnit = (unitRes.Status == PromptStatus.OK) ? unitRes.StringResult : "kN";

                // 8. Chạy Audit
                WriteMessage($"\n>> Đang xử lý...");
                var engine = new AuditEngine();
                string tempFolder = Path.GetTempPath();
                int fileCounter = 0;
                string firstFilePath = null;

                foreach (var pat in selectedPatterns)
                {
                    WriteMessage($"\n   Processing: {pat}...");
                    var report = engine.RunSingleAudit(pat);
                    string content = engine.GenerateTextReport(report, selectedUnit, selectedLang);

                    fileCounter++;
                    string safeModel = string.IsNullOrWhiteSpace(report.ModelName) ? "Model" : Path.GetFileNameWithoutExtension(report.ModelName);
                    string fileName = $"DTS_Audit_{safeModel}_{pat}_{fileCounter:D2}.txt";
                    string filePath = Path.Combine(tempFolder, fileName);
                    File.WriteAllText(filePath, content, Encoding.UTF8);

                    WriteMessage($"   -> Done: {fileName}");
                    if (firstFilePath == null) firstFilePath = filePath;
                }

                if (!string.IsNullOrEmpty(firstFilePath))
                {
                    WriteSuccess($"\nHoàn thành! Đã tạo {fileCounter} báo cáo tại {tempFolder}");
                    try { Process.Start(firstFilePath); } catch { }
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
