using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using DTS_Engine.Core.Data;
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

                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.WebMessageReceived += WebView_WebMessageReceived;

                string html = LoadHtmlFromResource();
                _webView.NavigateToString(html);
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
            string resourceName = "DTS_Engine.UI.Resources.BeamGroupViewer.html";
            Assembly assembly = Assembly.GetExecutingAssembly();

            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    // Fallback: return basic HTML
                    return "<html><body><h1>BeamGroupViewer not found</h1></body></html>";
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    string html = reader.ReadToEnd();

                    // Inject data
                    var data = new { groups = _groups };
                    string json = JsonConvert.SerializeObject(data);
                    html = html.Replace("__DATA_JSON__", json);

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
                        _groups = data.groups;
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
