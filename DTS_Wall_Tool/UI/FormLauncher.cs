using System;
using System.Windows.Forms;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.UI.Forms;

namespace DTS_Wall_Tool.UI
{
    /// <summary>
    /// Lớp khởi chạy form từ AutoCAD command
    /// </summary>
    public static class FormLauncher
    {
        private static MainForm _mainForm;

        /// <summary>
        /// Hiển thị form chính (Modeless - không chặn AutoCAD)
        /// </summary>
        [CommandMethod("DTS_FORM")]
        public static void ShowMainForm()
        {
            try
            {
                if (_mainForm == null || _mainForm.IsDisposed)
                {
                    _mainForm = new MainForm();
                }

                if (!_mainForm.Visible)
                {
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessDialog(_mainForm);
                }
                else
                {
                    _mainForm.Focus();
                    _mainForm.BringToFront();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening form: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Hiển thị form modal (chặn AutoCAD cho đến khi đóng)
        /// </summary>
        [CommandMethod("DTS_FORM_MODAL")]
        public static void ShowMainFormModal()
        {
            try
            {
                using (var form = new MainForm())
                {
                    Autodesk.AutoCAD.ApplicationServices.Application.ShowModalDialog(form);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error opening form: {ex.Message}", "Error",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        /// <summary>
        /// Đóng form nếu đang mở
        /// </summary>
        [CommandMethod("DTS_CLOSE_FORM")]
        public static void CloseMainForm()
        {
            if (_mainForm != null && !_mainForm.IsDisposed)
            {
                _mainForm.Close();
                _mainForm.Dispose();
                _mainForm = null;
            }
        }

        /// <summary>
        /// Kiểm tra form có đang mở không
        /// </summary>
        public static bool IsFormOpen => _mainForm != null && !_mainForm.IsDisposed && _mainForm.Visible;

        /// <summary>
        /// Lấy instance của MainForm (nếu đang mở)
        /// </summary>
        public static MainForm GetMainForm() => _mainForm;
    }
}