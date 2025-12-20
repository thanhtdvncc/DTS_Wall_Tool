using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using DTS_Engine.Core.Algorithms;
using DTS_Engine.Core.Algorithms.Rebar;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Utils;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;

namespace DTS_Engine.UI.Forms
{
    /// <summary>
    /// Dialog hiển thị BeamGroupViewer cho dải dầm liên tục.
    /// Sử dụng WebView2 để render HTML/Canvas.
    /// </summary>
    public partial class BeamGroupViewerDialog : Form
    {
        private WebView2 _webView;
        private bool _isInitialized = false;
        private List<BeamGroup> _groups;
        private Action<List<BeamGroup>> _onApply;

        public BeamGroupViewerDialog(List<BeamGroup> groups, Action<List<BeamGroup>> onApply = null)
        {
            _groups = groups ?? new List<BeamGroup>();
            _onApply = onApply;

            InitializeComponent();
            this.Shown += Dialog_Shown;
            this.FormClosing += Dialog_FormClosing;
        }

        private void InitializeComponent()
        {
            this.Text = "DTS Beam Group Viewer";
            this.Size = new Size(1200, 800);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.Sizable;
            this.MaximizeBox = true;
            this.MinimizeBox = true;
            this.MinimumSize = new Size(900, 600);

            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            this.Controls.Add(_webView);
        }

        private async void Dialog_Shown(object sender, EventArgs e)
        {
            if (_isInitialized) return;
            _isInitialized = true;

            try
            {
                string userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DTS_Engine", "WebView2");
                Directory.CreateDirectory(userDataFolder);

                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
                await _webView.EnsureCoreWebView2Async(env);

                // === SECURITY SETTINGS - Prevent user inspection ===
                _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;           // Enabled for debug
                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false; // Disable right-click menu
                _webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false; // Disable Ctrl+U, Ctrl+Shift+I, etc.
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;          // Disable Ctrl+scroll zoom
                _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;            // Hide status bar
                _webView.CoreWebView2.Settings.IsBuiltInErrorPageEnabled = false;     // Hide error pages

                _webView.WebMessageReceived += WebView_WebMessageReceived;

                string html = LoadHtmlFromResource();

                // Safety check: WebView2 NavigateToString has content size limits
                if (html.Length > 2_000_000) // 2MB limit for NavigateToString
                {
                    // Fallback to file-based approach
                    string tempPath = Path.Combine(Path.GetTempPath(), "dts_beam_viewer.html");
                    File.WriteAllText(tempPath, html, System.Text.Encoding.UTF8);
                    _webView.CoreWebView2.Navigate("file:///" + tempPath.Replace("\\", "/"));
                }
                else
                {
                    _webView.NavigateToString(html);
                }
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (!this.IsDisposed)
                    MessageBox.Show("Lỗi khởi tạo: " + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private string LoadHtmlFromResource()
        {
            // Load BeamGroupViewer.html directly (contains Beam namespace)
            string resourceName = "DTS_Engine.UI.Resources.BeamGroupViewer.html";
            Assembly assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return "<html><body><h1>BeamGroupViewer not found</h1></body></html>";
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    string html = reader.ReadToEnd();

                    // Inject data with settings
                    var settings = DtsSettings.Instance;
                    string viewMode = (_groups != null && _groups.Count > 0) ? "groups" : "single";

                    try
                    {
                        var data = new
                        {
                            mode = viewMode,
                            groups = _groups,
                            settings = new
                            {
                                ConcreteGradeName = settings.General?.ConcreteGradeName ?? "B25",
                                SteelGradeName = settings.General?.SteelGradeName ?? "CB400-V",
                                SteelGradeMain = settings.General?.SteelGradeMain ?? 400,
                                MaxLayers = settings.Beam?.MaxLayers ?? 2,
                                MainBarRange = settings.Beam?.MainBarRange ?? "16-25",
                                StirrupBarRange = settings.Beam?.StirrupBarRange ?? "8-10"
                            }
                        };

                        // Safe JSON serialization with reference loop handling
                        var jsonSettings = new JsonSerializerSettings
                        {
                            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
                            MaxDepth = 32,
                            NullValueHandling = NullValueHandling.Ignore
                        };
                        string json = JsonConvert.SerializeObject(data, jsonSettings);

                        // Check JSON size - WebView2 NavigateToString has ~2MB limit
                        if (json.Length > 1_500_000) // 1.5MB limit for safety
                        {
                            // Too large - send minimal data with error message
                            var errorData = new { mode = "error", error = $"Data too large ({json.Length / 1024}KB). Chọn ít đối tượng hơn (tối đa ~50 groups)." };
                            json = JsonConvert.SerializeObject(errorData);
                        }

                        html = html.Replace("__DATA_JSON__", json);
                    }
                    catch (Exception ex)
                    {
                        // JSON serialization failed - send error message instead of crashing
                        var errorData = new { mode = "error", error = $"Data error: {ex.Message}" };
                        string errorJson = JsonConvert.SerializeObject(errorData);
                        html = html.Replace("__DATA_JSON__", errorJson);
                    }

                    return html;
                }
            }
        }

        private void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string message = e.TryGetWebMessageAsString();
            if (string.IsNullOrEmpty(message)) return;

            if (message == "CANCEL")
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
                return;
            }

            if (message == "EXPORT")
            {
                this.BeginInvoke(new Action(HandleExport));
                return;
            }

            if (message == "IMPORT")
            {
                this.BeginInvoke(new Action(HandleImport));
                return;
            }

            if (message.StartsWith("SAVE|"))
            {
                try
                {
                    string json = message.Substring(5);
                    var data = JsonConvert.DeserializeObject<DataWrapper>(json);
                    if (data?.groups != null)
                    {
                        _groups = data.groups;

                        // Persist to DWG (NOD) so next open is consistent
                        var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                        if (doc != null)
                        {
                            using (doc.LockDocument())
                            using (var tr = doc.Database.TransactionManager.StartTransaction())
                            {
                                string nodJson = JsonConvert.SerializeObject(_groups);
                                XDataUtils.SaveBeamGroupsToNOD(doc.Database, tr, nodJson);
                                tr.Commit();
                            }

                            // XDATA-FIRST: Sync rebar strings to beam XData entities
                            Commands.RebarCommands.SyncGroupSpansToXData(_groups);
                        }
                    }

                    MessageBox.Show("Đã lưu dữ liệu!", "Save",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Lỗi save: " + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            if (message.StartsWith("APPLY|"))
            {
                try
                {
                    string json = message.Substring(6);
                    var data = JsonConvert.DeserializeObject<DataWrapper>(json);
                    if (data?.groups != null)
                    {
                        _groups = data.groups;

                        // XDATA-FIRST: Sync rebar strings to beam XData entities
                        Commands.RebarCommands.SyncGroupSpansToXData(_groups);

                        _onApply?.Invoke(_groups);
                    }
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Lỗi apply: " + ex.Message, "Error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                return;
            }

            if (message.StartsWith("HIGHLIGHT|"))
            {
                try
                {
                    string json = message.Substring(10);
                    var handles = JsonConvert.DeserializeObject<List<string>>(json);
                    if (handles != null && handles.Count > 0)
                    {
                        // Invoke on main thread to highlight in CAD
                        this.BeginInvoke(new Action(() => HighlightBeamsInCAD(handles)));
                    }
                }
                catch { }
                return;
            }

            // Handle SET_OPACITY message for transparency when highlight mode is ON
            if (message.StartsWith("SET_OPACITY|"))
            {
                try
                {
                    string opacityStr = message.Substring(12);
                    if (double.TryParse(opacityStr, System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out double opacity))
                    {
                        this.BeginInvoke(new Action(() =>
                        {
                            this.Opacity = Math.Max(0.1, Math.Min(1.0, opacity));
                        }));
                    }
                }
                catch { }
                return;
            }

            // === LOCK_DESIGN: Update _groups to keep SelectedDesign in sync ===
            if (message.StartsWith("LOCK_DESIGN|"))
            {
                try
                {
                    // Format: LOCK_DESIGN|groupIndex|selectedDesignJson
                    var parts = message.Substring(12).Split(new[] { '|' }, 2);
                    if (parts.Length >= 1 && int.TryParse(parts[0], out int groupIndex) && groupIndex >= 0 && groupIndex < _groups.Count)
                    {
                        if (parts.Length >= 2 && !string.IsNullOrEmpty(parts[1]))
                        {
                            // Deserialize SelectedDesign from JS
                            var design = JsonConvert.DeserializeObject<ContinuousBeamSolution>(parts[1]);
                            _groups[groupIndex].SelectedDesign = design;
                            _groups[groupIndex].LockedAt = DateTime.Now;
                        }
                        System.Diagnostics.Debug.WriteLine($"[BeamGroupViewer] Design locked for group {groupIndex}, SelectedDesign updated in _groups");
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[BeamGroupViewer] LOCK_DESIGN error: {ex.Message}");
                }
                return;
            }

            if (message.StartsWith("UNLOCK_DESIGN|"))
            {
                try
                {
                    string idxStr = message.Substring(14);
                    if (int.TryParse(idxStr, out int groupIndex) && groupIndex >= 0 && groupIndex < _groups.Count)
                    {
                        // Clear SelectedDesign
                        _groups[groupIndex].SelectedDesign = null;
                        _groups[groupIndex].LockedAt = null;
                        System.Diagnostics.Debug.WriteLine($"[BeamGroupViewer] Design unlocked for group {groupIndex}");
                    }
                }
                catch { }
                return;
            }

            // === QUICK_CALC: Request rebar calculation for current group ===
            if (message.StartsWith("QUICK_CALC|"))
            {
                try
                {
                    string idxStr = message.Substring(11);
                    if (int.TryParse(idxStr, out int groupIndex) && groupIndex >= 0 && groupIndex < _groups.Count)
                    {
                        System.Diagnostics.Debug.WriteLine($"[BeamGroupViewer] Quick Calc requested for group index {groupIndex}");
                        this.BeginInvoke(new Action(() =>
                        {
                            _ = RunQuickCalcAndRefreshAsync(groupIndex);
                        }));
                    }
                }
                catch { }
                return;
            }
        }

        private async Task RunQuickCalcAndRefreshAsync(int groupIndex)
        {
            if (groupIndex < 0 || groupIndex >= (_groups?.Count ?? 0)) return;

            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                await SendToastSimpleAsync("⚠️ Không tìm thấy AutoCAD document để tính.");
                return;
            }

            try
            {
                var group = _groups[groupIndex];
                if (group == null)
                {
                    await SendToastSimpleAsync("⚠️ Group không hợp lệ.");
                    return;
                }

                var settings = DtsSettings.Instance;

                List<BeamResultData> spanResults;
                using (doc.LockDocument())
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    spanResults = ExtractSpanResultsForGroup(tr, group);
                    tr.Commit();
                }

                if (spanResults == null || spanResults.Count == 0)
                {
                    await SendToastSimpleAsync("⚠️ Không đọc được nội lực/XData của dầm trong group.");
                    return;
                }

                var proposals = RebarCalculatorV3.CalculateProposalsForGroup(group, spanResults, settings);
                if (proposals == null || proposals.Count == 0)
                {
                    await SendToastSimpleAsync("❌ Không thể tạo phương án.");
                    return;
                }

                var errorSol = proposals.FirstOrDefault(p => p != null && p.IsValid == false);
                if (errorSol != null)
                {
                    await SendToastSimpleAsync("❌ " + (errorSol.ValidationMessage ?? "Thiếu dữ liệu/không hợp lệ."));
                    // Still update proposals so user can inspect
                }

                group.BackboneOptions = proposals;
                group.SelectedBackboneIndex = 0;

                // Update displayed span rebar text for preview (do not overwrite manual-modified spans)
                var bestSolution = proposals.FirstOrDefault(p => p != null && p.IsValid);
                System.Diagnostics.Debug.WriteLine($"[QuickCalc] bestSolution={bestSolution?.OptionName ?? "NULL"}, IsDesignLocked={group.IsDesignLocked}, Reinforcements.Count={bestSolution?.Reinforcements?.Count ?? 0}");

                if (bestSolution != null && group.IsDesignLocked == false)
                {
                    System.Diagnostics.Debug.WriteLine("[QuickCalc] --> Calling ApplySolutionToSpanData");
                    ApplySolutionToSpanData(group, bestSolution);
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"[QuickCalc] --> SKIP ApplySolutionToSpanData (bestSolution null={bestSolution == null}, IsDesignLocked={group.IsDesignLocked})");
                }

                // Push updated group back to WebView
                await SendGroupUpdatedToWebViewAsync(groupIndex, group);
            }
            catch (Exception ex)
            {
                await SendToastSimpleAsync("Lỗi tính thép: " + ex.Message);
            }
        }

        private static List<BeamResultData> ExtractSpanResultsForGroup(Autodesk.AutoCAD.DatabaseServices.Transaction tr, BeamGroup group)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc?.Database;
            if (db == null || group == null) return new List<BeamResultData>();

            // Prefer span-based mapping (1 result per span) to keep ordering deterministic
            var handles = new List<string>();
            if (group.Spans != null && group.Spans.Count > 0)
            {
                foreach (var span in group.Spans)
                {
                    var h = span?.Segments?.FirstOrDefault()?.EntityHandle;
                    if (!string.IsNullOrWhiteSpace(h)) handles.Add(h);
                }
            }

            // Fallback: use group.EntityHandles if span segments not populated
            if (handles.Count == 0 && group.EntityHandles != null && group.EntityHandles.Count > 0)
            {
                handles.AddRange(group.EntityHandles.Where(h => !string.IsNullOrWhiteSpace(h)));
            }

            var results = new List<BeamResultData>();
            foreach (var handle in handles)
            {
                try
                {
                    var objId = AcadUtils.GetObjectIdFromHandle(handle);
                    if (objId == Autodesk.AutoCAD.DatabaseServices.ObjectId.Null) continue;

                    var obj = tr.GetObject(objId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                    if (obj == null) continue;

                    var data = XDataUtils.ReadRebarData(obj);
                    if (data != null)
                    {
                        results.Add(data);
                        continue;
                    }

                    // Fallback: some drawings may store BeamData instead of BeamResultData
                    var beamData = XDataUtils.ReadBeamData(obj);
                    if (beamData != null)
                    {
                        results.Add(new BeamResultData
                        {
                            // BeamResultData uses cm for Width/SectionHeight in most pipelines
                            Width = beamData.Width.HasValue ? (beamData.Width.Value / 10.0) : 0,
                            SectionHeight = beamData.Height.HasValue ? (beamData.Height.Value / 10.0) : 0
                        });
                    }
                }
                catch
                {
                    // ignore individual beam failures
                }
            }

            return results;
        }

        private static void ApplySolutionToSpanData(BeamGroup group, ContinuousBeamSolution sol)
        {
            if (group?.Spans == null || sol == null) return;

            System.Diagnostics.Debug.WriteLine($"[ApplySolutionToSpanData] Solution: {sol.OptionName}, Reinforcements.Count = {sol.Reinforcements?.Count ?? 0}");
            if (sol.Reinforcements != null)
            {
                foreach (var kv in sol.Reinforcements)
                    System.Diagnostics.Debug.WriteLine($"  --> {kv.Key} = {kv.Value.Count}D{kv.Value.Diameter}");
            }

            string backboneTop = $"{sol.BackboneCount_Top}D{sol.BackboneDiameter}";
            string backboneBot = $"{sol.BackboneCount_Bot}D{sol.BackboneDiameter}";

            foreach (var span in group.Spans)
            {
                if (span == null) continue;
                if (span.IsManualModified)
                {
                    System.Diagnostics.Debug.WriteLine($"[ApplySolutionToSpanData] SKIP {span.SpanId} (IsManualModified=true)");
                    continue;
                }

                // Ensure arrays exist (Json.NET may hydrate as jagged arrays in UI, but here we keep the original 2D array)
                if (span.TopRebar == null || span.TopRebar.GetLength(0) < 1 || span.TopRebar.GetLength(1) < 5)
                    span.TopRebar = new string[3, 6];
                if (span.BotRebar == null || span.BotRebar.GetLength(0) < 1 || span.BotRebar.GetLength(1) < 5)
                    span.BotRebar = new string[3, 6];

                string spanId = span.SpanId ?? "S?";

                // TOP: Left/Mid/Right
                string topLeft = backboneTop;
                string topMid = backboneTop;
                string topRight = backboneTop;
                if (sol.Reinforcements != null)
                {
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Left", out var tL)) topLeft += $"+{tL.Count}D{tL.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Mid", out var tM)) topMid += $"+{tM.Count}D{tM.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Right", out var tR)) topRight += $"+{tR.Count}D{tR.Diameter}";
                }

                span.TopRebar[0, 0] = topLeft;
                span.TopRebar[0, 2] = topMid;
                span.TopRebar[0, 4] = topRight;

                // BOT: Left/Mid/Right with full support
                string botLeft = backboneBot;
                string botMid = backboneBot;
                string botRight = backboneBot;
                if (sol.Reinforcements != null)
                {
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Left", out var bL)) botLeft += $"+{bL.Count}D{bL.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Mid", out var bM)) botMid += $"+{bM.Count}D{bM.Diameter}";
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Right", out var bR)) botRight += $"+{bR.Count}D{bR.Diameter}";
                }

                span.BotRebar[0, 0] = botLeft;
                span.BotRebar[0, 2] = botMid;
                span.BotRebar[0, 4] = botRight;

                System.Diagnostics.Debug.WriteLine($"[ApplySolutionToSpanData] {spanId}: Top=[{topLeft}, {topMid}, {topRight}], Bot=[{botLeft}, {botMid}, {botRight}]");
            }
        }

        private async Task SendGroupUpdatedToWebViewAsync(int groupIndex, BeamGroup group)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;
                // Pass JSON as a safely-escaped JS string (then JS will JSON.parse it)
                string json = JsonConvert.SerializeObject(group);
                string jsStringLiteral = JsonConvert.SerializeObject(json);
                await _webView.CoreWebView2.ExecuteScriptAsync($"onGroupUpdated({groupIndex}, {jsStringLiteral})");
            }
            catch { }
        }

        private async Task SendToastSimpleAsync(string message)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;
                string jsStringLiteral = JsonConvert.SerializeObject(message ?? string.Empty);
                await _webView.CoreWebView2.ExecuteScriptAsync($"showToast({jsStringLiteral})");
            }
            catch { }
        }

        /// <summary>
        /// REALTIME SAP SYNC: Tạo/cập nhật section trong SAP khi user chốt phương án.
        /// </summary>
        private void SyncSectionToSAP(BeamGroup group)
        {
            if (group == null || string.IsNullOrEmpty(group.Name)) return;

            // Check SAP connection
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    System.Diagnostics.Debug.WriteLine($"[SapSync] Cannot connect: {msg}");
                    return;
                }
            }

            var engine = new SapDesignEngine();
            if (!engine.IsReady) return;

            // Create/update section
            var result = engine.EnsureSection(group.Name, group.Width, group.Height, "C25");

            if (result.Success)
            {
                string actionText = result.Action == SectionAction.Created ? "Created" :
                                   result.Action == SectionAction.Updated ? "Updated" : "OK";
                System.Diagnostics.Debug.WriteLine($"[SapSync] Section '{group.Name}': {actionText}");

                // Show toast in UI
                _ = SendToastToWebView($"✅ SAP Section '{group.Name}' {actionText.ToLower()}!", "success");
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[SapSync] Failed: {result.Message}");
                _ = SendToastToWebView($"⚠️ SAP Sync failed: {result.Message}", "warning");
            }
        }

        private async Task SendToastToWebView(string message, string type)
        {
            try
            {
                string escaped = message.Replace("'", "\\'");
                await _webView.CoreWebView2.ExecuteScriptAsync($"showToast('{escaped}', '{type}')");
            }
            catch { }
        }

        private void HighlightBeamsInCAD(List<string> handles)
        {
            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                var ids = new List<Autodesk.AutoCAD.DatabaseServices.ObjectId>();
                using (var tr = doc.Database.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (var handle in handles)
                    {
                        try
                        {
                            var h = new Autodesk.AutoCAD.DatabaseServices.Handle(
                                long.Parse(handle, System.Globalization.NumberStyles.HexNumber));
                            var objId = doc.Database.GetObjectId(false, h, 0);
                            if (objId != Autodesk.AutoCAD.DatabaseServices.ObjectId.Null && !objId.IsErased)
                            {
                                ids.Add(objId);
                            }
                        }
                        catch { }
                    }
                }

                if (ids.Count > 0)
                {
                    // Use transient graphics or selection to highlight
                    doc.Editor.SetImpliedSelection(ids.ToArray());
                    doc.Editor.UpdateScreen();
                }
            }
            catch { }
        }

        private void HandleExport()
        {
            try
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = "Export Beam Groups";
                    sfd.Filter = "DTS Beam Groups (*.dtsbg)|*.dtsbg";
                    sfd.FileName = "BeamGroups.dtsbg";
                    sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        string json = JsonConvert.SerializeObject(_groups, Formatting.Indented);
                        File.WriteAllText(sfd.FileName, json);
                        MessageBox.Show($"Đã export ra:\n{sfd.FileName}", "Export thành công",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi export: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void HandleImport()
        {
            try
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "Import Beam Groups";
                    ofd.Filter = "DTS Beam Groups (*.dtsbg)|*.dtsbg|All files (*.*)|*.*";
                    ofd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        string json = File.ReadAllText(ofd.FileName);
                        var imported = JsonConvert.DeserializeObject<List<BeamGroup>>(json);

                        if (imported != null && imported.Count > 0)
                        {
                            _groups = imported;
                            // Reload WebView với data mới
                            string html = LoadHtmlFromResource();
                            _webView.NavigateToString(html);
                            MessageBox.Show($"Đã import {imported.Count} nhóm dầm!", "Import thành công",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                        else
                        {
                            MessageBox.Show("File không có dữ liệu hợp lệ!", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi import: " + ex.Message, "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Dialog_FormClosing(object sender, FormClosingEventArgs e)
        {
            // Có thể prompt save nếu có thay đổi
        }

        public List<BeamGroup> GetResults()
        {
            return _groups;
        }

        private class DataWrapper
        {
            public List<BeamGroup> groups { get; set; }
        }
    }
}
