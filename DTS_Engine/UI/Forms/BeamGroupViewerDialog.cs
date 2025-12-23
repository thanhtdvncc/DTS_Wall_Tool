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
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

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
            DtsSettings.Reload();
            _groups = groups ?? new List<BeamGroup>();
            _onApply = onApply;

            // FIX: STRICTLY enforce Left-to-Right orientation for Viewer consistency
            EnsureLeftToRightOrientation(_groups);

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
                        // Extract ALL individual beam segments for Plan View independence
                        // Plan View must NOT depend on grouping - it reads raw beam data
                        var allBeams = new List<object>();
                        var capturedHandles = new HashSet<string>();

                        if (_groups != null)
                        {
                            foreach (var group in _groups)
                            {
                                if (group.Spans == null) continue;

                                foreach (var span in group.Spans)
                                {
                                    if (span.Segments == null) continue;

                                    foreach (var seg in span.Segments)
                                    {
                                        if (!capturedHandles.Contains(seg.EntityHandle))
                                            capturedHandles.Add(seg.EntityHandle);

                                        // === FIX: Use GroupName (display) NOT Name (label) ===
                                        // GroupName = axis-based display name ("GX-B (3 spans)")
                                        // Name = Label for rebar grouping ("1GHY4")
                                        // GroupId = unique identifier (mother handle) for matching
                                        string displayName = !string.IsNullOrEmpty(group.GroupName)
                                            ? group.GroupName
                                            : (!string.IsNullOrEmpty(group.Name) ? group.Name : $"Group_{group.EntityHandles?.FirstOrDefault() ?? ""}");

                                        string groupId = group.EntityHandles?.FirstOrDefault() ?? "";

                                        allBeams.Add(new
                                        {
                                            Handle = seg.EntityHandle,
                                            StartX = seg.StartPoint?[0] ?? 0,
                                            StartY = seg.StartPoint?[1] ?? 0,
                                            EndX = seg.EndPoint?[0] ?? 0,
                                            EndY = seg.EndPoint?[1] ?? 0,
                                            AxisName = group.AxisName ?? "",
                                            Width = group.Width,
                                            Height = group.Height,
                                            LevelZ = group.LevelZ,
                                            GroupName = displayName,  // For dropdown display
                                            GroupId = groupId         // For unique matching
                                        });
                                    }
                                }
                            }
                        }

                        // === FIX: Extract neighbors for full plan view ===
                        var neighbors = ExtractNeighborBeams(capturedHandles, _groups);
                        allBeams.AddRange(neighbors);

                        // Extract grid lines from dts_axis layer
                        var allGrids = ExtractGridLinesFromLayer("dts_axis");

                        // Extract columns from dts_point layer 
                        var allColumns = ExtractColumnsFromLayer("dts_point");

                        var data = new
                        {
                            mode = viewMode,
                            groups = _groups,
                            allBeams = allBeams, // Independent beam data for Plan View
                            allGrids = allGrids, // Grid lines from dts_axis layer
                            allColumns = allColumns, // Columns from dts_point layer
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

                        // V5: XData-only persistence (No NOD)
                        // Sync rebar strings directly to beam XData entities
                        Commands.RebarCommands.SyncGroupSpansToXData(_groups);
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
                    // Format: LOCK_DESIGN|groupIndex|lockedDesignJson
                    var parts = message.Substring(12).Split(new[] { '|' }, 2);
                    if (parts.Length >= 2 && int.TryParse(parts[0], out int groupIndex) &&
                        groupIndex >= 0 && groupIndex < _groups.Count)
                    {
                        var group = _groups[groupIndex];

                        // Parse the locked design JSON
                        var jsonSettings = new JsonSerializerSettings
                        {
                            NullValueHandling = NullValueHandling.Ignore,
                            MissingMemberHandling = MissingMemberHandling.Ignore
                        };

                        // Parse as JObject first to extract _capturedSpans
                        var jObj = Newtonsoft.Json.Linq.JObject.Parse(parts[1]);

                        // Deserialize the main design
                        var design = jObj.ToObject<ContinuousBeamSolution>(JsonSerializer.Create(jsonSettings));
                        group.SelectedDesign = design;
                        group.LockedAt = DateTime.Now;
                        group.IsManuallyEdited = true;

                        // CRITICAL: Apply captured spans back to group.Spans
                        var capturedSpans = jObj["_capturedSpans"] as Newtonsoft.Json.Linq.JArray;
                        if (capturedSpans != null && group.Spans != null)
                        {
                            foreach (var cs in capturedSpans)
                            {
                                string spanId = cs["SpanId"]?.ToString();
                                int spanIndex = cs["SpanIndex"]?.ToObject<int>() ?? -1;

                                var span = group.Spans.FirstOrDefault(s => s.SpanId == spanId);
                                if (span == null && spanIndex >= 0 && spanIndex < group.Spans.Count)
                                {
                                    span = group.Spans[spanIndex];
                                }

                                if (span != null)
                                {
                                    // Apply captured data back to span
                                    var topBackbone = cs["TopBackbone"]?.ToObject<RebarInfo>();
                                    var botBackbone = cs["BotBackbone"]?.ToObject<RebarInfo>();

                                    if (topBackbone != null) span.TopBackbone = topBackbone;
                                    if (botBackbone != null) span.BotBackbone = botBackbone;

                                    span.TopAddLeft = cs["TopAddLeft"]?.ToObject<RebarInfo>();
                                    span.TopAddMid = cs["TopAddMid"]?.ToObject<RebarInfo>();
                                    span.TopAddRight = cs["TopAddRight"]?.ToObject<RebarInfo>();
                                    span.BotAddLeft = cs["BotAddLeft"]?.ToObject<RebarInfo>();
                                    span.BotAddMid = cs["BotAddMid"]?.ToObject<RebarInfo>();
                                    span.BotAddRight = cs["BotAddRight"]?.ToObject<RebarInfo>();

                                    span.SideBar = cs["SideBar"]?.ToString();

                                    // Mark as manually modified
                                    bool userEdited = cs["_userEdited"]?.ToObject<bool>() ?? false;
                                    span.IsManualModified = userEdited;
                                    span.LastManualEdit = DateTime.Now;
                                }
                            }
                        }

                        System.Diagnostics.Debug.WriteLine(
                            $"[BeamGroupViewer] Design locked for group {groupIndex}, " +
                            $"SelectedDesign and Spans updated");
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

                var proposals = RebarCalculator.CalculateProposalsForGroup(group, spanResults, settings);

                // --- DIAGNOSTIC START ---
                if (group.Spans.Count > 0)
                {
                    var s0 = group.Spans[0];
                    double v0 = (s0.As_Top != null && s0.As_Top.Length > 0) ? s0.As_Top[0] : -999;
                    DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.Log($"[DIAGNOSTIC] After Calc: Span[0].As_Top[0] = {v0}");

                    // Test Serialization locally
                    try
                    {
                        string testJson = Newtonsoft.Json.JsonConvert.SerializeObject(group);
                        bool containsValue = testJson.Contains("21.76") || testJson.Contains($"{v0}");
                        DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.Log($"[DIAGNOSTIC] JSON check: Contains '{v0}'? {containsValue}. Length: {testJson.Length}");
                        // Dump first 500 chars of Span 0 part if possible or just verifying existence
                    }
                    catch (Exception ex)
                    {
                        DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.Log($"[DIAGNOSTIC] JSON Serialization Failed: {ex.Message}");
                    }
                }
                // --- DIAGNOSTIC END ---

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

                // NOTE: ApplySolutionToGroup is already called INSIDE V4RebarCalculator.Calculate()
                // Do NOT call ApplySolutionToSpanData here to avoid overwriting with potentially
                // mismatched data. The calculator has already synced SpanResults to Spans.

                // Only re-apply if we need to force-sync from a different solution
                // (e.g., user selected a non-best option)
                // For now, trust the calculator's sync.

                // Push updated group back to WebView
                await SendGroupUpdatedToWebViewAsync(groupIndex, group);

                // FORCE LOG OPEN
                DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.OpenLogFile();
            }
            catch (Exception ex)
            {
                await SendToastSimpleAsync("Lỗi tính thép: " + ex.Message);
            }
        }



        private void EnsureLeftToRightOrientation(List<BeamGroup> groups)
        {
            if (groups == null) return;
            foreach (var group in groups)
            {
                if (group.Spans == null || group.Spans.Count < 2) continue;

                // 1. Check Overall Group Orientation
                // Sort Spans by StartPoint.X
                var sortedSpans = group.Spans.OrderBy(s =>
                {
                    var seg = s.Segments?.FirstOrDefault();
                    if (seg != null && seg.StartPoint != null) return seg.StartPoint[0];
                    return double.MaxValue;
                }).ToList();

                // If the original '0' index is NOT the Leftmost one, the group is reversed/mixed.
                // We compel Strict Left-to-Right Sort.
                if (group.Spans[0] != sortedSpans[0])
                {
                    DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.Log($"[Viewer] Re-sorting Group {group.Name} to Left-to-Right (First Item was X={GetSpanX(group.Spans[0]):F1}, New First is X={GetSpanX(sortedSpans[0]):F1})");

                    // Apply Loop: Reverse Supports/Spans if it looks like a simple reversal
                    // If it is just mixed up, simple sort is safer.
                    // But Supports are tricky. If supports define positions 0, L1, L1+L2...
                    // We must assume the Supports match the Spans order.

                    // Simple Reverse Logic (usually it's just R->L vs L->R)
                    group.Spans.Reverse();
                    group.Supports.Reverse();

                    // Fix Support Positions (Mirror)
                    if (group.Supports.Count > 0)
                    {
                        double totalLen = group.Supports.Max(s => s.Position);
                        foreach (var s in group.Supports)
                        {
                            s.Position = Math.Abs(totalLen - s.Position);
                        }
                        // Sort supports after recalc just to be sure
                        group.Supports = group.Supports.OrderBy(s => s.Position).ToList();
                        for (int i = 0; i < group.Supports.Count; i++) group.Supports[i].SupportIndex = i;
                    }

                    // Re-Index Spans
                    for (int i = 0; i < group.Spans.Count; i++)
                    {
                        group.Spans[i].SpanIndex = i;
                        group.Spans[i].SpanId = $"S{i + 1}";

                        // Fix Left/Right Support References (IDs)
                        if (i < group.Supports.Count - 1)
                        {
                            group.Spans[i].LeftSupportId = group.Supports[i].SupportId;
                            group.Spans[i].RightSupportId = group.Supports[i + 1].SupportId;
                        }
                    }
                }
            }
        }

        private double GetSpanX(SpanData span)
        {
            return span?.Segments?.FirstOrDefault()?.StartPoint?[0] ?? 0;
        }

        private static List<BeamResultData> ExtractSpanResultsForGroup(Autodesk.AutoCAD.DatabaseServices.Transaction tr, BeamGroup group)
        {
            var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
            var db = doc?.Database;
            if (db == null || group == null) return new List<BeamResultData>();

            var results = new List<BeamResultData>();

            if (group.Spans != null && group.Spans.Count > 0)
            {
                foreach (var span in group.Spans)
                {
                    try
                    {
                        var seg = span?.Segments?.FirstOrDefault();
                        var h = seg?.EntityHandle;


                        DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.Log($"[ExtractSpanResults] SpanId: {span?.SpanId}, Handle: {h}, StartX: {seg?.StartPoint?[0]:F1}, EndX: {seg?.EndPoint?[0]:F1}");

                        // Check direction of this specific span (R->L Geometry Check)
                        bool isReversedSpan = false;
                        if (seg != null && seg.StartPoint != null && seg.EndPoint != null)
                        {
                            if (seg.StartPoint[0] > seg.EndPoint[0] + 1.0)
                            {
                                isReversedSpan = true;
                            }
                        }
                        if (string.IsNullOrWhiteSpace(h) && group.EntityHandles != null)
                        {
                            int idx = group.Spans.IndexOf(span);
                            if (idx >= 0 && idx < group.EntityHandles.Count)
                            {
                                h = group.EntityHandles[idx];
                            }
                        }

                        if (string.IsNullOrWhiteSpace(h))
                        {
                            results.Add(null);
                            continue;
                        }

                        var objId = AcadUtils.GetObjectIdFromHandle(h);
                        if (objId == Autodesk.AutoCAD.DatabaseServices.ObjectId.Null)
                        {
                            results.Add(null);
                            continue;
                        }

                        var obj = tr.GetObject(objId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                        if (obj == null)
                        {
                            results.Add(null);
                            continue;
                        }

                        var data = XDataUtils.ReadRebarData(obj);
                        if (data != null)
                        {
                            // CRITICAL: If Span is Geometric R->L, Flip Result to match Viewer L->R
                            if (isReversedSpan) ReverseBeamResultData(data);
                            results.Add(data);
                            continue;
                        }

                        var beamData = XDataUtils.ReadBeamData(obj);
                        if (beamData != null)
                        {
                            results.Add(new BeamResultData
                            {
                                Width = beamData.Width.HasValue ? (beamData.Width.Value / 10.0) : 0,
                                SectionHeight = beamData.Height.HasValue ? (beamData.Height.Value / 10.0) : 0
                            });
                            continue;
                        }

                        results.Add(null);
                    }
                    catch
                    {
                        results.Add(null);
                    }
                }
            }
            // Fallback for groups without spans populated
            else if (group.EntityHandles != null)
            {
                foreach (var handle in group.EntityHandles)
                {
                    try
                    {
                        var objId = AcadUtils.GetObjectIdFromHandle(handle);
                        if (objId == Autodesk.AutoCAD.DatabaseServices.ObjectId.Null)
                        {
                            results.Add(null);
                            continue;
                        }
                        var obj = tr.GetObject(objId, Autodesk.AutoCAD.DatabaseServices.OpenMode.ForRead);
                        var data = XDataUtils.ReadRebarData(obj);
                        results.Add(data);
                    }
                    catch
                    {
                        results.Add(null);
                    }
                }
            }

            return results;
        }

        private static void ReverseBeamResultData(BeamResultData data)
        {
            if (data == null) return;
            // Arrays: Start, Mid, End -> End, Mid, Start
            // Standard length is 3
            ReverseArray(data.TopArea);
            ReverseArray(data.BotArea);
            ReverseArray(data.TorsionArea);
            ReverseArray(data.ShearArea);
            ReverseArray(data.TTArea);
            ReverseArray(data.TopRebarString);
            ReverseArray(data.BotRebarString);
            ReverseArray(data.TopAreaProv);
            ReverseArray(data.BotAreaProv);
            ReverseArray(data.StirrupString);
            ReverseArray(data.WebBarString);
        }

        private static void ReverseArray<T>(T[] arr)
        {
            if (arr != null && arr.Length > 0) Array.Reverse(arr);
        }

        private static void ApplySolutionToSpanData(BeamGroup group, ContinuousBeamSolution sol)
        {
            if (group?.Spans == null || sol == null) return;

            // Create backbone RebarInfo
            var backboneTop = new RebarInfo { Count = sol.BackboneCount_Top, Diameter = sol.BackboneDiameter };
            var backboneBot = new RebarInfo { Count = sol.BackboneCount_Bot, Diameter = sol.BackboneDiameter };

            string backboneTopStr = $"{sol.BackboneCount_Top}D{sol.BackboneDiameter}";
            string backboneBotStr = $"{sol.BackboneCount_Bot}D{sol.BackboneDiameter}";

            for (int spanIdx = 0; spanIdx < group.Spans.Count; spanIdx++)
            {
                var span = group.Spans[spanIdx];
                if (span == null) continue;
                if (span.IsManualModified) continue;

                // Ensure arrays exist
                if (span.TopRebarInternal == null || span.TopRebarInternal.GetLength(0) < 1 || span.TopRebarInternal.GetLength(1) < 5)
                    span.TopRebarInternal = new string[3, 6];
                if (span.BotRebarInternal == null || span.BotRebarInternal.GetLength(0) < 1 || span.BotRebarInternal.GetLength(1) < 5)
                    span.BotRebarInternal = new string[3, 6];
                if (span.As_Top == null || span.As_Top.Length < 6)
                    span.As_Top = new double[6];
                if (span.As_Bot == null || span.As_Bot.Length < 6)
                    span.As_Bot = new double[6];

                // ═══════════════════════════════════════════════════════════════
                // CRITICAL FIX: Build spanId matching the format used in Discretize
                // Discretize uses: span.SpanId ?? $"S{i + 1}" (1-based)
                // ═══════════════════════════════════════════════════════════════
                string spanId = span.SpanId ?? $"S{spanIdx + 1}";

                // ═══════════════════════════════════════════════════════════════
                // SYNC As_req FROM SpanResults (Critical for Viewer display)
                // ═══════════════════════════════════════════════════════════════
                var spanResult = sol.SpanResults?.FirstOrDefault(sr =>
                    sr.SpanId == spanId || sr.SpanIndex == spanIdx);

                if (spanResult != null)
                {
                    // Map 3 zones [Left, Mid, Right] to 6 positions [0,1,2,3,4,5]
                    // Position 0,1 = Left; 2,3 = Mid; 4,5 = Right
                    if (spanResult.ReqTop != null && spanResult.ReqTop.Length >= 3)
                    {
                        span.As_Top[0] = spanResult.ReqTop[0];
                        span.As_Top[1] = spanResult.ReqTop[0];
                        span.As_Top[2] = spanResult.ReqTop[1];
                        span.As_Top[3] = spanResult.ReqTop[1];
                        span.As_Top[4] = spanResult.ReqTop[2];
                        span.As_Top[5] = spanResult.ReqTop[2];
                    }
                    if (spanResult.ReqBot != null && spanResult.ReqBot.Length >= 3)
                    {
                        span.As_Bot[0] = spanResult.ReqBot[0];
                        span.As_Bot[1] = spanResult.ReqBot[0];
                        span.As_Bot[2] = spanResult.ReqBot[1];
                        span.As_Bot[3] = spanResult.ReqBot[1];
                        span.As_Bot[4] = spanResult.ReqBot[2];
                        span.As_Bot[5] = spanResult.ReqBot[2];
                    }
                }

                // ═══════════════════════════════════════════════════════════════
                // STRUCTURED DATA: Set RebarInfo objects (for Viewer JSON)
                // ═══════════════════════════════════════════════════════════════
                span.TopBackbone = backboneTop;
                span.BotBackbone = backboneBot;

                // TOP Reinforcements - Lookup with correct spanId
                span.TopAddLeft = null;
                span.TopAddMid = null;
                span.TopAddRight = null;
                if (sol.Reinforcements != null)
                {
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Left", out var tL))
                        span.TopAddLeft = new RebarInfo { Count = tL.Count, Diameter = tL.Diameter, LayerCounts = tL.LayerBreakdown };
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Mid", out var tM))
                        span.TopAddMid = new RebarInfo { Count = tM.Count, Diameter = tM.Diameter, LayerCounts = tM.LayerBreakdown };
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Top_Right", out var tR))
                        span.TopAddRight = new RebarInfo { Count = tR.Count, Diameter = tR.Diameter, LayerCounts = tR.LayerBreakdown };
                }

                // Also try from SpanResult.TopAddons (if Reinforcements missed)
                if (spanResult?.TopAddons != null)
                {
                    if (span.TopAddLeft == null && spanResult.TopAddons.TryGetValue("Left", out var tL2))
                        span.TopAddLeft = tL2;
                    if (span.TopAddMid == null && spanResult.TopAddons.TryGetValue("Mid", out var tM2))
                        span.TopAddMid = tM2;
                    if (span.TopAddRight == null && spanResult.TopAddons.TryGetValue("Right", out var tR2))
                        span.TopAddRight = tR2;
                }

                // BOT Reinforcements
                span.BotAddLeft = null;
                span.BotAddMid = null;
                span.BotAddRight = null;
                if (sol.Reinforcements != null)
                {
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Left", out var bL))
                        span.BotAddLeft = new RebarInfo { Count = bL.Count, Diameter = bL.Diameter, LayerCounts = bL.LayerBreakdown };
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Mid", out var bM))
                        span.BotAddMid = new RebarInfo { Count = bM.Count, Diameter = bM.Diameter, LayerCounts = bM.LayerBreakdown };
                    if (sol.Reinforcements.TryGetValue($"{spanId}_Bot_Right", out var bR))
                        span.BotAddRight = new RebarInfo { Count = bR.Count, Diameter = bR.Diameter, LayerCounts = bR.LayerBreakdown };
                }

                // Also try from SpanResult.BotAddons (if Reinforcements missed)
                if (spanResult?.BotAddons != null)
                {
                    if (span.BotAddLeft == null && spanResult.BotAddons.TryGetValue("Left", out var bL2))
                        span.BotAddLeft = bL2;
                    if (span.BotAddMid == null && spanResult.BotAddons.TryGetValue("Mid", out var bM2))
                        span.BotAddMid = bM2;
                    if (span.BotAddRight == null && spanResult.BotAddons.TryGetValue("Right", out var bR2))
                        span.BotAddRight = bR2;
                }

                // ═══════════════════════════════════════════════════════════════
                // LEGACY STRING DATA: Keep for backward compatibility
                // ═══════════════════════════════════════════════════════════════
                string topLeft = backboneTopStr;
                string topMid = backboneTopStr;
                string topRight = backboneTopStr;
                if (span.TopAddLeft != null) topLeft += $"+{span.TopAddLeft.DisplayString}";
                if (span.TopAddMid != null) topMid += $"+{span.TopAddMid.DisplayString}";
                if (span.TopAddRight != null) topRight += $"+{span.TopAddRight.DisplayString}";

                span.TopRebarInternal[0, 0] = topLeft;
                span.TopRebarInternal[0, 2] = topMid;
                span.TopRebarInternal[0, 4] = topRight;

                string botLeft = backboneBotStr;
                string botMid = backboneBotStr;
                string botRight = backboneBotStr;
                if (span.BotAddLeft != null) botLeft += $"+{span.BotAddLeft.DisplayString}";
                if (span.BotAddMid != null) botMid += $"+{span.BotAddMid.DisplayString}";
                if (span.BotAddRight != null) botRight += $"+{span.BotAddRight.DisplayString}";

                span.BotRebarInternal[0, 0] = botLeft;
                span.BotRebarInternal[0, 2] = botMid;
                span.BotRebarInternal[0, 4] = botRight;
            }
        }

        private async Task SendGroupUpdatedToWebViewAsync(int groupIndex, BeamGroup group)
        {
            try
            {
                if (_webView?.CoreWebView2 == null) return;

                // DEBUG: Dump Span Data to check for Mismatch
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[SendGroupUpdatedToWebViewAsync] Group {groupIndex} ({group.Spans.Count} spans):");
                for (int i = 0; i < group.Spans.Count; i++)
                {
                    var s = group.Spans[i];
                    string h = s.Segments?.FirstOrDefault()?.EntityHandle ?? "null";
                    double asTop0 = (s.As_Top != null && s.As_Top.Length > 0) ? s.As_Top[0] : -1;
                    double asTopLast = (s.As_Top != null && s.As_Top.Length > 0) ? s.As_Top[s.As_Top.Length - 1] : -1;
                    sb.AppendLine($"  Span[{i}] ID='{s.SpanId}' Handle='{h}' As_Top[0]={asTop0:F2} As_Top[Last]={asTopLast:F2}");
                }
                DTS_Engine.Core.Algorithms.Rebar.Utils.RebarLogger.Log(sb.ToString());

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

        #region Plan View Data Extraction

        /// <summary>
        /// Extract grid lines from specified layer (e.g., "dts_axis")
        /// </summary>
        private List<object> ExtractGridLinesFromLayer(string layerName)
        {
            var grids = new List<object>();

            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                var db = doc.Database;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in btr)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // Filter by layer
                        if (ent.Layer != layerName) continue;

                        // Only process Lines
                        if (ent is Line line)
                        {
                            // Extract line geometry
                            double x1 = line.StartPoint.X;
                            double y1 = line.StartPoint.Y;
                            double x2 = line.EndPoint.X;
                            double y2 = line.EndPoint.Y;
                            double z = line.StartPoint.Z;

                            // Try to read XData from DTS_GRID
                            string gridName = "";
                            double gridCoordinate = 0;
                            string orientation = "";

                            try
                            {
                                var xdata = ent.GetXDataForApplication("DTS_GRID");
                                if (xdata != null)
                                {
                                    var values = xdata.AsArray();
                                    if (values.Length >= 4)
                                    {
                                        // Format: (appName, Name, Coordinate, Orientation)
                                        gridName = values[1].Value?.ToString() ?? "";
                                        gridCoordinate = Convert.ToDouble(values[2].Value);
                                        orientation = values[3].Value?.ToString() ?? "";
                                    }
                                }
                            }
                            catch { }

                            // Fallback: detect from geometry if no XData
                            if (string.IsNullOrEmpty(gridName))
                            {
                                bool isVertical = Math.Abs(x1 - x2) < 1.0;
                                bool isHorizontal = Math.Abs(y1 - y2) < 1.0;
                                orientation = isVertical ? "X" : (isHorizontal ? "Y" : "DIAGONAL");
                                gridCoordinate = isVertical ? x1 : y1;
                                gridName = $"{orientation}_{gridCoordinate:0}";
                            }

                            grids.Add(new
                            {
                                StartX = x1,
                                StartY = y1,
                                EndX = x2,
                                EndY = y2,
                                Z = z,
                                Orientation = orientation,
                                Name = gridName,
                                Coordinate = gridCoordinate
                            });
                        }
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting grids: {ex.Message}");
            }

            return grids;
        }

        /// <summary>
        /// Extract columns from specified layer (e.g., "dts_point")
        /// Columns are drawn as Circles by DTS_PLOT_FROM_SAP
        /// </summary>
        private List<object> ExtractColumnsFromLayer(string layerName)
        {
            var columns = new List<object>();

            try
            {
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                var db = doc.Database;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in btr)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // Filter by layer
                        if (ent.Layer != layerName) continue;

                        // Only process Circles
                        if (ent is Circle circle)
                        {
                            double x = circle.Center.X;
                            double y = circle.Center.Y;
                            double z = circle.Center.Z; // Geometric Z (may be 0 in 2D)
                            double radius = circle.Radius;

                            // Try to read ColumnData from XData
                            string sectionName = "";
                            string sectionType = "Rectangular"; // Default
                            double width = radius * 2; // Default from circle
                            double depth = radius * 2; // Section depth (Y dimension)

                            try
                            {
                                var elemData = XDataUtils.ReadElementData(ent);
                                if (elemData is ColumnData colData)
                                {
                                    sectionName = colData.SectionName ?? "";
                                    sectionType = colData.SectionType ?? "Rectangular";
                                    width = colData.Width ?? (radius * 2);
                                    depth = colData.Depth ?? (radius * 2);  // Read Depth, not Height

                                    // FIX: Prioritize XData's BaseZ over geometric Z (2D drawings have geometric Z=0)
                                    if (colData.BaseZ.HasValue && colData.BaseZ.Value != 0)
                                    {
                                        z = colData.BaseZ.Value;
                                    }
                                }
                            }
                            catch { }

                            columns.Add(new
                            {
                                X = x,
                                Y = y,
                                Z = z,
                                Radius = radius,
                                Width = width,
                                Depth = depth,  // Send as Depth to frontend
                                SectionName = sectionName,
                                SectionType = sectionType,
                                Handle = ent.Handle.ToString()
                            });
                        }
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting columns: {ex.Message}");
            }

            return columns;
        }

        #endregion

        /// <summary>
        /// Extract neighbor beams on the same layers and levels as the selected groups (for full Plan View).
        /// This ensures the viewer shows the entire floor plan, not just selected beams.
        /// </summary>
        private List<object> ExtractNeighborBeams(HashSet<string> capturedHandles, List<BeamGroup> selectedGroups)
        {
            var neighbors = new List<object>();
            if (selectedGroups == null || selectedGroups.Count == 0) return neighbors;

            try
            {
                // 1. Identify Context (Layers and Levels)
                var targetLayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var targetLevels = new HashSet<double>();

                // Collect layers from selected groups requires Transaction because BeamGroup only has Handles
                var doc = Autodesk.AutoCAD.ApplicationServices.Application.DocumentManager.MdiActiveDocument;
                var db = doc.Database;

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (var group in selectedGroups)
                    {
                        targetLevels.Add(group.LevelZ);
                        if (group.EntityHandles != null)
                        {
                            foreach (var h in group.EntityHandles)
                            {
                                ObjectId id = AcadUtils.GetObjectIdFromHandle(h);
                                if (id != ObjectId.Null)
                                {
                                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                                    if (ent != null) targetLayers.Add(ent.Layer);
                                }
                            }
                        }
                    }

                    // 2. Scan ModelSpace for candidates
                    var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                    var btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                    foreach (ObjectId id in btr)
                    {
                        var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // Type filter: Beams are usually Line or Polyline
                        if (!(ent is Curve)) continue;
                        if (ent.Layer == null || !targetLayers.Contains(ent.Layer)) continue;

                        // Skip if already captured
                        string handle = ent.Handle.ToString();
                        if (capturedHandles.Contains(handle)) continue;

                        // Z Level check - FIX: Prioritize XData's BaseZ over geometric Z (2D drawings have geometric Z=0)
                        double z = 0;

                        // First try to get Z from XData
                        BeamData beamData = null;
                        try
                        {
                            beamData = XDataUtils.ReadElementData(ent) as BeamData;
                            if (beamData?.BaseZ != null && beamData.BaseZ.Value != 0)
                            {
                                z = beamData.BaseZ.Value;
                            }
                            else
                            {
                                // Fallback to geometric Z
                                if (ent is Line line) z = line.StartPoint.Z;
                                else if (ent is Polyline pl) z = pl.Elevation;
                                else if (ent is Polyline2d pl2) z = pl2.Elevation;
                                else continue; // Skip other curves
                            }
                        }
                        catch
                        {
                            // Fallback to geometric Z on error
                            if (ent is Line line) z = line.StartPoint.Z;
                            else if (ent is Polyline pl) z = pl.Elevation;
                            else if (ent is Polyline2d pl2) z = pl2.Elevation;
                            else continue;
                        }

                        // Tolerance 500mm (match story grouping tolerance)
                        if (!targetLevels.Any(lvl => Math.Abs(lvl - z) < 500)) continue;

                        // Extract info for Neighbor
                        string groupName = "";
                        double width = 200;
                        double height = 400;

                        // Use already-read beamData if available
                        if (beamData != null)
                        {
                            groupName = beamData.GroupLabel ?? "";
                            if (beamData.Width.HasValue) width = beamData.Width.Value;
                            if (beamData.Height.HasValue) height = beamData.Height.Value;
                        }

                        // Extract Geometry
                        double x1 = 0, y1 = 0, x2 = 0, y2 = 0;
                        if (ent is Line l)
                        {
                            x1 = l.StartPoint.X; y1 = l.StartPoint.Y;
                            x2 = l.EndPoint.X; y2 = l.EndPoint.Y;
                        }
                        else if (ent is Polyline p)
                        {
                            // Approximate polyline as segment from Start to End (simplified)
                            // Ideally we iterate segments, but for context view this is usually enough 
                            // as beams are mostly single lines.
                            x1 = p.StartPoint.X; y1 = p.StartPoint.Y;
                            x2 = p.EndPoint.X; y2 = p.EndPoint.Y;
                        }

                        neighbors.Add(new
                        {
                            Handle = handle,
                            StartX = x1,
                            StartY = y1,
                            EndX = x2,
                            EndY = y2,
                            AxisName = "", // Context beam usually doesn't need axis unless analyzed
                            Width = width,
                            Height = height,
                            LevelZ = z,
                            GroupName = groupName,
                            GroupId = "" // Not in selected group
                        });
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error extracting neighbor beams: {ex.Message}");
            }

            return neighbors;
        }

        private class DataWrapper
        {
            public List<BeamGroup> groups { get; set; }
        }
    }
}
