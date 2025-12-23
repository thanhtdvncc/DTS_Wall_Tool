using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;

[assembly: ExtensionApplication(typeof(DTS_Engine.DtsInitializer))]

namespace DTS_Engine
{
    /// <summary>
    /// DtsInitializer - Khởi tạo plugin khi DLL được load
    /// Tự động hiển thị Dashboard Mini-Toolbar
    /// </summary>
    public class DtsInitializer : IExtensionApplication
    {
        /// <summary>
        /// Được gọi khi AutoCAD load DLL
        /// </summary>
        public void Initialize()
        {
            // Đợi AutoCAD sẵn sàng hoàn toàn trước khi hiện palette
            Application.Idle += OnApplicationIdle;
        }

        private void OnApplicationIdle(object sender, System.EventArgs e)
        {
            // Chỉ chạy một lần
            Application.Idle -= OnApplicationIdle;

            try
            {
                // Hiện Dashboard Mini-Toolbar tự động
                DTS_Engine.UI.Forms.DashboardPalette.ShowPalette();

                // Thông báo trong command line
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage("\n[DTS Engine] Đã khởi tạo thành công. Dashboard Mini-Toolbar đã sẵn sàng.\n");
                    doc.Editor.WriteMessage("[DTS Engine] Gõ DTS_DASHBOARD để hiện/ẩn toolbar.\n");
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DTS Init Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Được gọi khi AutoCAD unload DLL (thường là khi đóng AutoCAD)
        /// </summary>
        public void Terminate()
        {
            // Cleanup nếu cần
            DTS_Engine.UI.Forms.DashboardPalette.ClosePalette();
        }
    }
}
