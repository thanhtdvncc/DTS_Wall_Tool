using System;
using System.Drawing;
using Autodesk.AutoCAD.Windows;
using Application = Autodesk.AutoCAD.ApplicationServices.Application;

namespace DTS_Engine.UI.Forms
{
    /// <summary>
    /// DashboardPalette - Mini-Toolbar trôi nổi dạng Photoshop
    /// Hiển thị thanh công cụ ngang nhỏ gọn, neo ở góc trên phải màn hình vẽ
    /// </summary>
    public static class DashboardPalette
    {
        private static PaletteSet _ps;
        private static DashboardControl _control;

        // GUID để AutoCAD nhớ trạng thái palette
        private static readonly Guid PaletteGuid = new Guid("E8F3D5A1-7B2C-4D6E-9A8F-1C3B5D7E9F0A");

        /// <summary>
        /// Hiện Dashboard Mini-Toolbar
        /// </summary>
        public static void ShowPalette()
        {
            if (_ps == null)
            {
                _ps = new PaletteSet("DTS", PaletteGuid);

                // Kích thước siêu nhỏ gọn (420x40)
                int barWidth = 420;
                int barHeight = 40;

                _ps.MinimumSize = new Size(barWidth, barHeight);
                _ps.Size = new Size(barWidth, barHeight);

                // QUAN TRỌNG: Để trôi nổi tự do
                _ps.Dock = DockSides.None;

                // Style tối giản
                _ps.Style = PaletteSetStyles.ShowCloseButton;

                _control = new DashboardControl();
                _ps.Add("", _control);
            }

            // Tính toán vị trí để đưa về góc trên bên phải
            MoveToTopRight();

            _ps.Visible = true;
            _ps.KeepFocus = false; // Để focus trả về AutoCAD ngay giúp gõ lệnh được
        }

        /// <summary>
        /// Hàm tính toán vị trí để đặt bảng trôi nổi ở góc trên phải Canvas
        /// </summary>
        private static void MoveToTopRight()
        {
            if (_ps == null) return;

            try
            {
                // Lấy working area của màn hình chính
                var screen = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea;

                // Các thông số Margin (Khoảng cách)
                int marginTop = 80;    // Né thanh Title Bar và Ribbon
                int marginRight = 40;  // Cách mép phải (né scrollbar/ViewCube)

                // Tính toán tọa độ X, Y (góc trên phải)
                int x = screen.Right - _ps.Size.Width - marginRight;
                int y = screen.Top + marginTop;

                // Kiểm tra an toàn
                if (x < 0) x = 100;
                if (y < 0) y = 100;

                // Gán vị trí mới
                _ps.Location = new Point(x, y);
            }
            catch
            {
                // Fallback nếu không lấy được screen info
            }
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
                if (!_ps.Visible)
                {
                    // Nếu đang ẩn mà hiện lên thì tính lại vị trí
                    MoveToTopRight();
                }
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
                _ps.Dispose();
                _ps = null;
            }
        }
    }
}
