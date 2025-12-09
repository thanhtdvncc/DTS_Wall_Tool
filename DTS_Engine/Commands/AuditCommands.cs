using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Interfaces;
using DTS_Engine.Core.Utils;
using System;
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

                // 3. Lấy dữ liệu Pattern và chọn 1 pattern duy nhất
                WriteMessage("\nĐang quét dữ liệu Load Patterns...");
                var activePatterns = SapUtils.GetActiveLoadPatterns();

                if (activePatterns.Count == 0)
                {
                    WriteError("Không tìm thấy Load Pattern nào trong model.");
                    return;
                }

                // Sort by load descending (heaviest first)
                var sortedPatterns = activePatterns.OrderByDescending(p => p.TotalEstimatedLoad).ToList();

                WriteMessage($"\n--- AVAILABLE LOAD PATTERNS (sorted by estimated load) ---");
                for (int i = 0; i < sortedPatterns.Count; i++)
                {
                    var pattern = sortedPatterns[i];
                    string info = pattern.TotalEstimatedLoad > 0.001
                        ? $"(~{pattern.TotalEstimatedLoad:N0} kN)"
                        : string.Empty;
                    WriteMessage($"[{i + 1}] {pattern.Name} {info}");
                }

                // Prompt for single selection
                var patOpt = new PromptStringOptions("\nNhập số thứ tự hoặc tên Load Pattern: ") { AllowSpaces = true };
                patOpt.DefaultValue = "1";
                var patRes = Ed.GetString(patOpt);

                if (patRes.Status != PromptStatus.OK)
                {
                    WriteMessage("Đã hủy.");
                    return;
                }

                string selectedPattern = null;
                string choice = patRes.StringResult?.Trim();

                // Try parse as number first
                if (int.TryParse(choice, out int idx))
                {
                    if (idx >= 1 && idx <= sortedPatterns.Count)
                    {
                        selectedPattern = sortedPatterns[idx - 1].Name;
                    }
                    else
                    {
                        WriteError("Số thứ tự không hợp lệ.");
                        return;
                    }
                }
                else
                {
                    // Try match by name
                    var matched = sortedPatterns.FirstOrDefault(p => p.Name.Equals(choice, StringComparison.OrdinalIgnoreCase));
                    if (matched != null)
                    {
                        selectedPattern = matched.Name;
                    }
                    else
                    {
                        WriteError($"Không tìm thấy Load Pattern: {choice}");
                        return;
                    }
                }

                WriteMessage($"\n>> Đã chọn: {selectedPattern}\n");

                // 6. Chọn đơn vị
                var unitOpt = new PromptKeywordOptions("\nChọn đơn vị xuất báo cáo [Ton/kN/kgf]: ");
                unitOpt.AllowNone = false;
                unitOpt.Keywords.Add("Ton");
                unitOpt.Keywords.Add("kN");
                unitOpt.Keywords.Add("kgf");
                unitOpt.Keywords.Default = "kN";
                var unitRes = Ed.GetKeywords(unitOpt);
                string selectedUnit = (unitRes.Status == PromptStatus.OK) ? unitRes.StringResult : "kN";

                // 7. Chọn định dạng xuất (Text hoặc Excel)
                var formatOpt = new PromptKeywordOptions("\nChọn định dạng xuất báo cáo [Text/Excel]: ");
                formatOpt.Keywords.Add("Text");
                formatOpt.Keywords.Add("Excel");
                formatOpt.Keywords.Default = "Text";
                formatOpt.AllowNone = true;
                var formatRes = Ed.GetKeywords(formatOpt);
                string exportFormat = (formatRes.Status == PromptStatus.OK) ? formatRes.StringResult : "Text";
                bool exportExcel = exportFormat.Equals("Excel", StringComparison.OrdinalIgnoreCase);
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
                
                // VALIDATION: Check if inventory is empty
                if (inventory.Count == 0 && SapUtils.CountFrames() + SapUtils.CountAreas() > 0)
                {
                     WriteError("Model Inventory is empty! Cannot proceed with Audit.");
                     WriteMessage("   [TROUBLESHOOTING]:");
                     WriteMessage("   - Check if SAP2000 model is unlocked or has geometry tables available.");
                     WriteMessage("   - Verify 'Joint Coordinates', 'Connectivity - Frame', 'Connectivity - Area' tables.");
                     return;
                }

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
                // STEP 4: Initialize Audit Engine (Business Logic Layer)
                WriteMessage("   [3/3] Initializing Audit Engine...");
                var engine = new AuditEngine(loadReader, inventory);

                WriteMessage("   >> System ready. Processing pattern...\n");

                // ===============================================================
                // END OF COMPOSITION ROOT
                // ===============================================================

                // 9. Chạy Audit cho Pattern đã chọn
                WriteMessage($"\n   Processing: {selectedPattern}...");
                var report = engine.RunSingleAudit(selectedPattern);

                string tempFolder = Path.GetTempPath();
                string safeModel = string.IsNullOrWhiteSpace(report.ModelName) ? "Model" : Path.GetFileNameWithoutExtension(report.ModelName);
                string filePath = null;

                if (exportExcel)
                {
                    // Excel export using ClosedXML
                    WriteMessage("\n   Generating Excel report...");

                    try
                    {
                        string excelPath = ExcelReportGenerator.GenerateExcelReport(
                            report,
                            selectedUnit,
                            selectedLang);

                        filePath = excelPath;
                        WriteSuccess($"\nCompleted! Excel report generated at:");
                        WriteMessage($"  {filePath}");

                        try { Process.Start(filePath); } catch { }
                    }
                    catch (System.Exception ex)
                    {
                        WriteError($"\n   Excel generation failed: {ex.Message}");
                        WriteMessage("\n   Falling back to Text export...");

                        // Fallback to text
                        string content = engine.GenerateTextReport(report, selectedUnit, selectedLang);
                        string fileName = $"DTS_Audit_{safeModel}_{selectedPattern}.txt";
                        filePath = Path.Combine(tempFolder, fileName);
                        File.WriteAllText(filePath, content, Encoding.UTF8);

                        WriteSuccess($"\nText report created at:");
                        WriteMessage($"  {filePath}");
                        try { Process.Start(filePath); } catch { }
                    }
                }
                else
                {
                    // Text export
                    string content = engine.GenerateTextReport(report, selectedUnit, selectedLang);
                    string fileName = $"DTS_Audit_{safeModel}_{selectedPattern}.txt";
                    filePath = Path.Combine(tempFolder, fileName);
                    File.WriteAllText(filePath, content, Encoding.UTF8);

                    WriteSuccess($"\nCompleted! Text report created at:");
                    WriteMessage($"  {filePath}");

                    try { Process.Start(filePath); } catch { }
                }
            });
        }

        #endregion

        #region Helper Methods

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
