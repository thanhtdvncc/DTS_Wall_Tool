using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Interfaces;
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
                var activePatterns = SapUtils.GetActiveLoadPatterns();
                var allPatterns = SapUtils.GetLoadPatterns();
                
                if (allPatterns.Count == 0)
                {
                    WriteError("Không tìm thấy Load Pattern nào trong model.");
                    return;
                }

                // ✅ FIX: Sử dụng PromptKeywordOptions với pagination
                var selectedPatterns = new List<string>();
                int pageSize = 10;
                int pageIndex = 0;
                int totalPages = (int)Math.Ceiling(allPatterns.Count / (double)pageSize);

                while (true)
                {
                    WriteMessage($"\n--- LOAD PATTERNS (Page {pageIndex + 1}/{totalPages}) ---");
                    
                    var patOpt = new PromptKeywordOptions("\nChọn Pattern (hoặc Next/Prev/All/Done): ");
                    patOpt.AllowNone = false;
                    
                    // Thêm patterns của trang hiện tại
                    int start = pageIndex * pageSize;
                    int end = Math.Min(allPatterns.Count, start + pageSize);
                    
                    for (int i = start; i < end; i++)
                    {
                        string patName = allPatterns[i];
                        patOpt.Keywords.Add(patName);
                        
                        // Hiển thị info
                        double loadVal = activePatterns.FirstOrDefault(p => p.Name == patName)?.TotalEstimatedLoad ?? 0;
                        string info = loadVal > 0.001 ? $"[~{loadVal:N0} kN]" : "[-]";
                        WriteMessage($" {i + 1,2}. {patName,-15} {info}");
                    }
                    
                    // Navigation keywords
                    if (pageIndex < totalPages - 1) patOpt.Keywords.Add("Next");
                    if (pageIndex > 0) patOpt.Keywords.Add("Prev");
                    patOpt.Keywords.Add("All");
                    patOpt.Keywords.Add("Done");
                    
                    patOpt.Keywords.Default = end < allPatterns.Count ? "Next" : "Done";
                    
                    var patRes = Ed.GetKeywords(patOpt);
                    
                    if (patRes.Status != PromptStatus.OK)
                    {
                        WriteMessage("Đã hủy.");
                        return;
                    }
                    
                    string choice = patRes.StringResult;
                    
                    if (choice == "Next" && pageIndex < totalPages - 1)
                    {
                        pageIndex++;
                    }
                    else if (choice == "Prev" && pageIndex > 0)
                    {
                        pageIndex--;
                    }
                    else if (choice == "All")
                    {
                        selectedPatterns = allPatterns.ToList();
                        break;
                    }
                    else if (choice == "Done")
                    {
                        if (selectedPatterns.Count == 0)
                        {
                            WriteWarning("Chưa chọn pattern nào.");
                            continue;
                        }
                        break;
                    }
                    else
                    {
                        // Pattern được chọn
                        if (!selectedPatterns.Contains(choice))
                        {
                            selectedPatterns.Add(choice);
                            WriteSuccess($"Đã thêm: {choice} (Tổng: {selectedPatterns.Count})");
                        }
                        else
                        {
                            WriteWarning($"{choice} đã được chọn rồi.");
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

                // 6. Chọn đơn vị
                var unitOpt = new PromptKeywordOptions("\nChọn đơn vị xuất báo cáo [Ton/kN/kgf]: ");
                unitOpt.AllowNone = false;
                unitOpt.Keywords.Add("Ton");
                unitOpt.Keywords.Add("kN");
                unitOpt.Keywords.Add("kgf");
                unitOpt.Keywords.Default = "kN";
                var unitRes = Ed.GetKeywords(unitOpt);
                string selectedUnit = (unitRes.Status == PromptStatus.OK) ? unitRes.StringResult : "kN";

                // ===============================================================
                // BƯỚC 8: COMPOSITION ROOT - Dependency Injection Assembly
                // ===============================================================
                // Đây là nơi duy nhất khởi tạo và lắp ráp các dependencies.
                // Tuân thủ SOLID: Dependency Inversion Principle.
                //
                // WORKFLOW:
                // 1. Chuẩn bị Infrastructure (SAP Connection)
                // 2. Khởi tạo ModelInventory (Data Layer Dependency)
                // 3. Khởi tạo LoadReader với Inventory (Data Access Layer)
                // 4. Inject LoadReader vào Engine (Business Logic Layer)
                //
                // RATIONALE:
                // - Tất cả dependencies được resolve ở đây, không ở bên trong Engine
                // - Engine không biết SAP, chỉ biết ISapLoadReader interface
                // - Dễ test: Mock ISapLoadReader thay vì mock cả SAP API
                // ===============================================================

                WriteMessage($"\n>> Đang khởi tạo hệ thống...");

                // STEP 1: Infrastructure - Get SAP Model
                var model = SapUtils.GetModel();
                if (model == null)
                {
                    WriteError("Không thể lấy SAP Model. Vui lòng kiểm tra kết nối.");
                    return;
                }

                // STEP 2: Build ModelInventory (CRITICAL for Vector calculation)
                // Inventory chỉ build 1 lần, tái sử dụng cho mọi pattern
                WriteMessage("   [1/3] Building Model Inventory...");
                var inventory = new ModelInventory();
                inventory.Build();
                WriteMessage($"   {inventory.GetStatistics()}");

                // STEP 3: Initialize Load Reader (Data Access Layer)
                WriteMessage("   [2/3] Initializing Load Reader...");
                ISapLoadReader loadReader = CreateApiFirstReader(inventory);
                if (loadReader == null)
                {
                    WriteError("Không thể khởi tạo SapDatabaseReader (API). Kiểm tra kết nối SAP.");
                    return;
                }
                 
                 // STEP 4: Initialize Audit Engine (Business Logic Layer)
                 WriteMessage("   [3/3] Initializing Audit Engine...");
                 var engine = new AuditEngine(loadReader);

                WriteMessage("   >> System ready. Processing patterns...\n");

                // ===============================================================
                // END OF COMPOSITION ROOT
                // ===============================================================

                // Quick helper: create ModelInventory + SapDatabaseReader (API-first)
                Func<ISapLoadReader> createReader = () =>
                {
                    var inv = new ModelInventory();
                    inv.Build();
                    return new SapDatabaseReader(model, inv);
                };

                // 9. Chạy Audit cho từng Pattern
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

        // Helper: create API-first load reader
        private ISapLoadReader CreateApiFirstReader(ModelInventory inventory = null)
        {
            var model = SapUtils.GetModel();
            if (model == null) return null;
            if (inventory != null)
            {
                return new SapDatabaseReader(model, inventory);
            }
            var inv = new ModelInventory();
            inv.Build();
            return new SapDatabaseReader(model, inv);
        }

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

                WriteMessage($"\nModel: {SapUtils.GetModelName() ?? "Unknown"}");
                WriteMessage($"Đơn vị: {UnitManager.Info}");
                WriteMessage($"\nLoad Patterns ({patterns.Count}):");

                // Use API-first reader to count loads
                var reader = CreateApiFirstReader();

                foreach (var pattern in patterns)
                {
                    int frameLoadCount = 0;
                    int areaLoadCount = 0;
                    int pointLoadCount = 0;

                    if (reader != null)
                    {
                        try
                        {
                            var loads = reader.ReadAllLoads(pattern);
                            frameLoadCount = loads.Count(l => l.LoadType != null && l.LoadType.IndexOf("Frame", StringComparison.OrdinalIgnoreCase) >= 0);
                            areaLoadCount = loads.Count(l => l.LoadType != null && l.LoadType.IndexOf("Area", StringComparison.OrdinalIgnoreCase) >= 0);
                            pointLoadCount = loads.Count(l => l.LoadType != null && l.LoadType.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0);
                        }
                        catch
                        {
                            // fallback to SapUtils if reader fails
                            frameLoadCount = SapUtils.GetAllFrameDistributedLoads(pattern).Count;
                            areaLoadCount = SapUtils.GetAllAreaUniformLoads(pattern).Count;
                            pointLoadCount = SapUtils.GetAllPointLoads(pattern).Count;
                        }
                    }
                    else
                    {
                        frameLoadCount = SapUtils.GetAllFrameDistributedLoads(pattern).Count;
                        areaLoadCount = SapUtils.GetAllAreaUniformLoads(pattern).Count;
                        pointLoadCount = SapUtils.GetAllPointLoads(pattern).Count;
                    }

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

                // Use API-first reader
                var reader = CreateApiFirstReader();
                List<RawSapLoad> allLoads;

                if (reader != null)
                {
                    try { allLoads = reader.ReadAllLoads(pattern); }
                    catch { allLoads = new List<RawSapLoad>(); }
                }
                else
                {
                    allLoads = new List<RawSapLoad>();
                    allLoads.AddRange(SapUtils.GetAllFrameDistributedLoads(pattern));
                    allLoads.AddRange(SapUtils.GetAllAreaUniformLoads(pattern));
                    allLoads.AddRange(SapUtils.GetAllPointLoads(pattern));
                }

                WriteMessage($"\n--- {pattern} ---");

                // Frame loads
                var frameLoads = allLoads.Where(l => l.LoadType != null && l.LoadType.IndexOf("Frame", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (frameLoads.Count > 0)
                {
                    WriteMessage($"\nFRAME DISTRIBUTED ({frameLoads.Count}):");
                    var grouped = frameLoads.GroupBy(l => l.Value1).OrderByDescending(g => g.Key);
                    foreach (var g in grouped.Take(10))
                    {
                        var names = g.Select(l => l.ElementName).Take(10);
                        WriteMessage($"  {g.Key:0.00} kN/m: {string.Join(", ", names)}{(g.Count() > 10 ? "..." : "")} ");
                    }
                }

                // Area loads
                var areaLoads = allLoads.Where(l => l.LoadType != null && l.LoadType.IndexOf("Area", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (areaLoads.Count > 0)
                {
                    WriteMessage($"\nAREA UNIFORM ({areaLoads.Count}):");
                    var grouped = areaLoads.GroupBy(l => l.Value1).OrderByDescending(g => g.Key);
                    foreach (var g in grouped.Take(10))
                    {
                        var names = g.Select(l => l.ElementName).Take(10);
                        WriteMessage($"  {g.Key:0.00} kN/m²: {string.Join(", ", names)}{(g.Count() > 10 ? "..." : "")} ");
                    }
                }

                // Point loads
                var pointLoads = allLoads.Where(l => l.LoadType != null && l.LoadType.IndexOf("Point", StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                if (pointLoads.Count > 0)
                {
                    WriteMessage($"\nPOINT FORCE ({pointLoads.Count}):");
                    var grouped = pointLoads.GroupBy(l => l.Value1).OrderByDescending(g => g.Key);
                    foreach (var g in grouped.Take(10))
                    {
                        var names = g.Select(l => l.ElementName).Take(10);
                        WriteMessage($"  {g.Key:0.00} kN: {string.Join(", ", names)}{(g.Count() > 10 ? "..." : "")} ");
                    }
                }

                int total = frameLoads.Count + areaLoads.Count + pointLoads.Count;
                WriteMessage($"\nTổng: {total} bản ghi tải.");
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

                // Use API-first reader
                var reader = CreateApiFirstReader();
                var allLoads = new List<RawSapLoad>();

                if (reader != null)
                {
                    try
                    {
                        if (exportAll)
                        {
                            // Read patterns then aggregate
                            var patterns = SapUtils.GetLoadPatterns();
                            foreach (var p in patterns) allLoads.AddRange(reader.ReadAllLoads(p));
                        }
                        else
                        {
                            allLoads.AddRange(reader.ReadAllLoads(patternInput));
                        }
                    }
                    catch
                    {
                        // fallback to SapUtils
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
                    }
                }
                else
                {
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
