using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using Newtonsoft.Json;

namespace DTS_Engine.UI.Forms
{
    public partial class CalculationReportDialog : Form
    {
        private WebView2 webView;
        private string _jsonReportData;
        private List<BeamGroup> _groups;

        public CalculationReportDialog(List<BeamGroup> groups)
        {
            InitializeComponent();
            _groups = groups;
            _jsonReportData = ReportDataManager.BuildReportJson(_groups);
            this.Load += CalculationReportDialog_Load;
        }

        public CalculationReportDialog(string jsonReportData)
        {
            InitializeComponent();
            _jsonReportData = jsonReportData;
            this.Load += CalculationReportDialog_Load;
        }

        private void InitializeComponent()
        {
            this.webView = new Microsoft.Web.WebView2.WinForms.WebView2();
            ((System.ComponentModel.ISupportInitialize)(this.webView)).BeginInit();
            this.SuspendLayout();
            // 
            // webView
            // 
            this.webView.AllowExternalDrop = true;
            this.webView.CreationProperties = null;
            this.webView.DefaultBackgroundColor = System.Drawing.Color.White;
            this.webView.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webView.Location = new System.Drawing.Point(0, 0);
            this.webView.Name = "webView";
            this.webView.Size = new System.Drawing.Size(765, 733);
            this.webView.TabIndex = 0;
            this.webView.ZoomFactor = 1D;
            // 
            // CalculationReportDialog
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(765, 733);
            this.Controls.Add(this.webView);
            this.Name = "CalculationReportDialog";
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen;
            this.Text = "Thuyết Minh Tính Toán Chi Tiết Thép Dầm - DTS Engine";
            ((System.ComponentModel.ISupportInitialize)(this.webView)).EndInit();
            this.ResumeLayout(false);

        }

        private async void CalculationReportDialog_Load(object sender, EventArgs e)
        {
            await InitializeWebView();
        }


        private async System.Threading.Tasks.Task InitializeWebView()
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Path.GetTempPath(), "DTS_Engine_Report"));
                await webView.EnsureCoreWebView2Async(env);

                webView.CoreWebView2.WebMessageReceived += CoreWebView2_WebMessageReceived;
                webView.CoreWebView2.DOMContentLoaded += CoreWebView2_DOMContentLoaded;

                // Load HTML from embedded resource (same pattern as BeamGroupViewerDialog)
                string html = LoadHtmlFromResource();
                webView.CoreWebView2.NavigateToString(html);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi khởi tạo WebView2: " + ex.Message);
            }
        }

        private string LoadHtmlFromResource()
        {
            string resourceName = "DTS_Engine.UI.Resources.CalculationReport.html";
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            using (var stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    return "<html><body><h1>CalculationReport.html not found</h1></body></html>";
                }

                using (var reader = new StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        private void CoreWebView2_DOMContentLoaded(object sender, CoreWebView2DOMContentLoadedEventArgs e)
        {
            // Inject dữ liệu ngay khi DOM sẵn sàng
            InjectData();
        }

        private void InjectData()
        {
            if (string.IsNullOrEmpty(_jsonReportData)) return;

            // FIX: _jsonReportData đã là JSON string, không cần JsonConvert.ToString() 
            // vì nó sẽ double-escape string. Truyền trực tiếp vào initReport.
            string script = $"initReport({_jsonReportData})";
            webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private void CoreWebView2_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            // FIX: Use WebMessageAsJson because JS posts an object, not a string.
            // TryGetWebMessageAsString throws ArgumentException for object messages.
            string json = e.WebMessageAsJson;
            if (string.IsNullOrEmpty(json)) return;

            try
            {
                var obj = JsonConvert.DeserializeObject<dynamic>(json);
                string command = obj?.command;

                if (command == "export_excel")
                {
                    bool isSimple = obj.isSimple ?? false;
                    HandleExcelExport(obj.data, isSimple);
                }
                else if (command == "refresh_data")
                {
                    HandleRefresh();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WebMessageReceived error: {ex.Message}");
            }
        }

        private void HandleRefresh()
        {
            if (_groups == null || _groups.Count == 0) return;
            try
            {
                _jsonReportData = ReportDataManager.BuildReportJson(_groups);
                InjectData();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi làm mới dữ liệu: " + ex.Message);
            }
        }

        private void HandleExcelExport(dynamic data, bool isSimple)
        {
            try
            {
                // Deserialize data from JS to ReportGroupData
                string json = JsonConvert.SerializeObject(data);
                var reportData = JsonConvert.DeserializeObject<ReportGroupData>(json);

                string filePath = CalculationReportExcelGenerator.Generate(reportData, isSimple: isSimple);
                if (!string.IsNullOrEmpty(filePath))
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(filePath) { UseShellExecute = true });
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi xuất Excel: " + ex.Message);
            }
        }
    }
}
