using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Newtonsoft.Json;
using DTS_Engine.Core.Data;
using System.Linq;

namespace DTS_Engine.UI.Forms
{
    /// <summary>
    /// Cửa sổ cấu hình thông số cốt thép (Modern UI - 4 Tabs)
    /// Sử dụng DtsSettings phân cấp: General, Beam, Column, Naming
    /// HTML được load từ Embedded Resource
    /// </summary>
    public class RebarConfigDialog : Form
    {
        private WebView2 _webView;
        private DtsSettings _settings;
        private bool _isInitialized = false;

        // Obfuscation key for .dtss files
        private const string OBFUSCATION_KEY = "dtsst";
        private const string FILE_SIGNATURE = "DTSS_REBAR_V1";

        public RebarConfigDialog()
        {
            _settings = DtsSettings.Instance;
            InitializeComponent();
            this.Shown += RebarConfigDialog_Shown;
        }

        private void InitializeComponent()
        {
            this.Text = "DTS Engine | Cấu hình Cốt thép";
            this.Size = new Size(680, 680);
            this.StartPosition = FormStartPosition.CenterScreen;
            this.FormBorderStyle = FormBorderStyle.FixedDialog;
            this.MaximizeBox = false;
            this.MinimizeBox = false;

            _webView = new WebView2();
            _webView.Dock = DockStyle.Fill;
            this.Controls.Add(_webView);
        }

        private async void RebarConfigDialog_Shown(object sender, EventArgs e)
        {
            if (_isInitialized) return;
            _isInitialized = true;
            await InitializeWebViewAsync();
        }

        private async System.Threading.Tasks.Task InitializeWebViewAsync()
        {
            try
            {
                if (this.IsDisposed || _webView.IsDisposed) return;

                string userDataFolder = Path.Combine(Path.GetTempPath(), "DTS_RebarConfig_Profile");
                var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);

                if (this.IsDisposed || _webView.IsDisposed) return;
                await _webView.EnsureCoreWebView2Async(env);

                if (this.IsDisposed || _webView.IsDisposed || _webView.CoreWebView2 == null) return;

                _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                _webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
                _webView.WebMessageReceived += WebView_WebMessageReceived;

                // Load HTML từ Embedded Resource
                string html = LoadHtmlFromResource();
                _webView.NavigateToString(html);
            }
            catch (ObjectDisposedException) { }
            catch (Exception ex)
            {
                if (!this.IsDisposed)
                    MessageBox.Show("Lỗi khởi tạo: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Đọc file HTML từ Embedded Resource và thay thế placeholder bằng JSON settings
        /// </summary>
        private string LoadHtmlFromResource()
        {
            string resourceName = "DTS_Engine.UI.Resources.RebarConfig.html";

            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    var names = assembly.GetManifestResourceNames();
                    throw new Exception($"Không tìm thấy resource '{resourceName}'. Available: {string.Join(", ", names)}");
                }

                using (StreamReader reader = new StreamReader(stream))
                {
                    string html = reader.ReadToEnd();

                    // Serialize settings ra JSON
                    string settingsJson = JsonConvert.SerializeObject(_settings);
                    html = html.Replace("__SETTINGS_JSON__", settingsJson);

                    return html;
                }
            }
        }

        private void WebView_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                string message = e.TryGetWebMessageAsString();

                // Handle special commands - invoke on UI thread to prevent crash
                if (message == "CANCEL")
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        this.DialogResult = DialogResult.Cancel;
                        this.Close();
                    }));
                    return;
                }

                if (message == "IMPORT")
                {
                    this.BeginInvoke(new Action(HandleImport));
                    return;
                }

                if (message == "EXPORT")
                {
                    this.BeginInvoke(new Action(HandleExport));
                    return;
                }

                // Handle GET_STORIES - Fetch stories from SAP2000
                if (message == "GET_STORIES")
                {
                    this.BeginInvoke(new Action(HandleGetStories));
                    return;
                }

                // Normal save: JSON payload - invoke on UI thread
                this.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // Use DeserializeObject instead of PopulateObject to avoid list merging issues
                        var serSettings = new JsonSerializerSettings
                        {
                            ObjectCreationHandling = ObjectCreationHandling.Replace,
                            NullValueHandling = NullValueHandling.Ignore
                        };

                        // Deserialize to a new object first
                        var newSettings = JsonConvert.DeserializeObject<DtsSettings>(message, serSettings);

                        if (newSettings != null)
                        {
                            // Copy all properties from new settings to singleton
                            _settings.General = newSettings.General;
                            _settings.Beam = newSettings.Beam;
                            _settings.Column = newSettings.Column;
                            _settings.Naming = newSettings.Naming;
                            _settings.Anchorage = newSettings.Anchorage;
                            _settings.Detailing = newSettings.Detailing;
                            _settings.StoryConfigs = newSettings.StoryConfigs;
                            _settings.StoryTolerance = newSettings.StoryTolerance;
                            _settings.UserPresets = newSettings.UserPresets;
                        }

                        // DEBUG: Log to verify data
                        System.Diagnostics.Debug.WriteLine($"[RebarConfigDialog] Saving settings...");
                        System.Diagnostics.Debug.WriteLine($"[RebarConfigDialog] StoryConfigs count: {_settings.StoryConfigs?.Count ?? 0}");
                        System.Diagnostics.Debug.WriteLine($"[RebarConfigDialog] Anchorage.ConcreteGrades count: {_settings.Anchorage?.ConcreteGrades?.Count ?? 0}");

                        // Save to default file
                        _settings.Save();

                        System.Diagnostics.Debug.WriteLine($"[RebarConfigDialog] Settings saved successfully!");

                        this.DialogResult = DialogResult.OK;
                        this.Close();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Lỗi lưu cấu hình: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }));
            }
            catch (Exception ex)
            {
                this.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show("Lỗi xử lý: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }));
            }
        }

        /// <summary>
        /// Xử lý Import settings từ file .dtss
        /// </summary>
        private void HandleImport()
        {
            try
            {
                using (var ofd = new OpenFileDialog())
                {
                    ofd.Title = "Import DTS Settings";
                    ofd.Filter = "DTS Settings (*.dtss)|*.dtss|All files (*.*)|*.*";
                    ofd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (ofd.ShowDialog(this) == DialogResult.OK)
                    {
                        // Đọc và giải mã
                        string encryptedContent = File.ReadAllText(ofd.FileName, Encoding.UTF8);
                        string json = DecryptContent(encryptedContent);

                        if (json == null)
                        {
                            MessageBox.Show("File không hợp lệ hoặc không phải định dạng .dtss", "Error",
                                MessageBoxButtons.OK, MessageBoxIcon.Warning);
                            return;
                        }

                        // Import và refresh UI
                        var imported = JsonConvert.DeserializeObject<DtsSettings>(json);
                        if (imported != null)
                        {
                            // Copy values sang instance hiện tại
                            JsonConvert.PopulateObject(json, _settings, new JsonSerializerSettings
                            {
                                ObjectCreationHandling = ObjectCreationHandling.Replace
                            });

                            // Reload HTML với settings mới
                            string html = LoadHtmlFromResource();
                            _webView.NavigateToString(html);

                            MessageBox.Show($"Đã import settings từ:\n{ofd.FileName}", "Import thành công",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi import: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Xử lý Export settings ra file .dtss (có mã hóa nhẹ)
        /// </summary>
        private void HandleExport()
        {
            try
            {
                using (var sfd = new SaveFileDialog())
                {
                    sfd.Title = "Export DTS Settings";
                    sfd.Filter = "DTS Settings (*.dtss)|*.dtss";
                    sfd.FileName = "RebarConfig.dtss";
                    sfd.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

                    if (sfd.ShowDialog(this) == DialogResult.OK)
                    {
                        // Serialize và mã hóa
                        string json = JsonConvert.SerializeObject(_settings, Formatting.None);
                        string encrypted = EncryptContent(json);

                        File.WriteAllText(sfd.FileName, encrypted, Encoding.UTF8);

                        MessageBox.Show($"Đã export settings ra:\n{sfd.FileName}", "Export thành công",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi export: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        /// <summary>
        /// Xử lý Get Stories từ SAP2000
        /// </summary>
        private async void HandleGetStories()
        {
            try
            {
                // Run on background thread to avoid UI freeze
                var stories = await System.Threading.Tasks.Task.Run(() =>
                {
                    var result = new System.Collections.Generic.List<Core.Data.StoryNamingConfig>();

                    // Try to get stories from SAP using existing GetStories method
                    var gridItems = Core.Utils.SapUtils.GetStories();
                    if (gridItems == null || gridItems.Count == 0)
                        return result;

                    // Filter Z items (stories) and convert to StoryNamingConfig
                    // Pattern: Match DTS_TEST_SAP - use Trim().StartsWith()
                    var zItems = gridItems
                        .Where(g => !string.IsNullOrEmpty(g.AxisDir) && g.AxisDir.Trim().StartsWith("Z", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(g => g.Coordinate)
                        .ToList();

                    int idx = 1;
                    foreach (var z in zItems)
                    {
                        result.Add(new Core.Data.StoryNamingConfig
                        {
                            StoryName = z.Name,
                            Elevation = z.Coordinate, // Already in mm from SapUtils.GetStories
                            StartIndex = idx, // Start at 1 for each story
                            BeamPrefix = "B",
                            GirderPrefix = "G",
                            ColumnPrefix = "C",
                            Suffix = ""
                        });
                        idx++;
                    }

                    return result;
                });

                // Send stories back to WebView as JSON
                string json = JsonConvert.SerializeObject(stories);
                string escapedJson = json.Replace("'", "\\'").Replace("\\", "\\\\");
                await _webView.CoreWebView2.ExecuteScriptAsync($"onStoriesReceived('{escapedJson}')");
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi lấy stories từ SAP: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);

                // Reset button state via JS
                try
                {
                    await _webView.CoreWebView2.ExecuteScriptAsync("onStoriesReceived('[]')");
                }
                catch { }
            }
        }

        /// <summary>
        /// Mã hóa nhẹ nội dung JSON (XOR với key + Base64)
        /// </summary>
        private string EncryptContent(string plainText)
        {
            byte[] data = Encoding.UTF8.GetBytes(plainText);
            byte[] key = Encoding.UTF8.GetBytes(OBFUSCATION_KEY);

            // XOR với key
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = (byte)(data[i] ^ key[i % key.Length]);
            }

            // Base64 encode
            string base64 = Convert.ToBase64String(data);

            // Thêm signature để nhận diện
            return FILE_SIGNATURE + "|" + base64;
        }

        /// <summary>
        /// Giải mã nội dung từ file .dtss
        /// </summary>
        private string DecryptContent(string encryptedText)
        {
            try
            {
                // Kiểm tra signature
                if (!encryptedText.StartsWith(FILE_SIGNATURE + "|"))
                    return null;

                string base64 = encryptedText.Substring(FILE_SIGNATURE.Length + 1);

                // Base64 decode
                byte[] data = Convert.FromBase64String(base64);
                byte[] key = Encoding.UTF8.GetBytes(OBFUSCATION_KEY);

                // XOR với key (reverse)
                for (int i = 0; i < data.Length; i++)
                {
                    data[i] = (byte)(data[i] ^ key[i % key.Length]);
                }

                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                return null;
            }
        }
    }
}
