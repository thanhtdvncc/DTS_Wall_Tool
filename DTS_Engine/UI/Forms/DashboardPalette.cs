using Autodesk.AutoCAD.Windows;
using System.Drawing;

namespace DTS_Engine.UI.Forms
{
    /// <summary>
    /// DashboardPalette - Mini-Toolbar trôi nổi dạng Photoshop
    /// Hiển thị thanh công cụ ngang nhỏ gọn với các lệnh DTS phổ biến
    /// </summary>
    public static class DashboardPalette
    {
        private static PaletteSet _ps;
        private static DashboardControl _control;

        /// <summary>
        /// Hiện Dashboard Mini-Toolbar
        /// </summary>
        public static void ShowPalette()
        {
            if (_ps == null)
            {
                _ps = new PaletteSet("DTS", new System.Guid("E8F3D5A1-7B2C-4D6E-9A8F-1C3B5D7E9F0A"));

                // Kích thước siêu nhỏ gọn
                // Chiều rộng = 420px (10 nút)
                // Chiều cao = 40px (chỉ đủ icon, không có tab header)
                int barWidth = 420;
                int barHeight = 40;

                _ps.MinimumSize = new Size(barWidth, barHeight);
                _ps.Size = new Size(barWidth, barHeight);

                // Trôi nổi, không dính vào cạnh
                _ps.Dock = DockSides.None;

                // Style tối giản - tắt tất cả header buttons để tiết kiệm không gian
                _ps.Style = PaletteSetStyles.ShowCloseButton;

                // Ẩn tab để tiết kiệm thêm không gian
                _ps.SetSize(new Size(barWidth, barHeight));

                _control = new DashboardControl();
                _ps.Add("", _control); // Tên trống để ẩn tab
            }

            _ps.Visible = true;
        }

        /// <summary>
        /// Ẩn Dashboard
        /// </summary>
        public static void HidePalette()
        {
            if (_ps != null)
            {
                _ps.Visible = false;
            }
        }

        /// <summary>
        /// Toggle hiện/ẩn Dashboard
        /// </summary>
        public static void TogglePalette()
        {
            if (_ps == null)
            {
                ShowPalette();
            }
            else
            {
                _ps.Visible = !_ps.Visible;
            }
        }

        /// <summary>
        /// Đóng hoàn toàn Dashboard (khi unload DLL)
        /// </summary>
        public static void ClosePalette()
        {
            if (_ps != null)
            {
                _ps.Visible = false;
            }
        }
    }
}
