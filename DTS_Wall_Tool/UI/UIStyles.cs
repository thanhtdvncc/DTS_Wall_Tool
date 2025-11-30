using System.Drawing;
using System.Windows.Forms;

namespace DTS_Wall_Tool.UI
{
    /// <summary>
    /// Định nghĩa styles chung cho UI
    /// Tuân thủ ISO 25010: User interface aesthetics, Accessibility
    /// </summary>
    public static class UIStyles
    {
        #region Colors

        public static class Colors
        {
            // Primary
            public static readonly Color Primary = Color.FromArgb(0, 122, 204);
            public static readonly Color PrimaryDark = Color.FromArgb(0, 102, 184);
            public static readonly Color PrimaryLight = Color.FromArgb(30, 152, 234);

            // Success
            public static readonly Color Success = Color.FromArgb(76, 175, 80);
            public static readonly Color SuccessDark = Color.FromArgb(56, 155, 60);

            // Warning
            public static readonly Color Warning = Color.FromArgb(255, 152, 0);
            public static readonly Color WarningLight = Color.FromArgb(255, 183, 77);

            // Danger
            public static readonly Color Danger = Color.FromArgb(244, 67, 54);
            public static readonly Color DangerDark = Color.FromArgb(211, 47, 47);

            // Neutral
            public static readonly Color Background = Color.FromArgb(248, 249, 250);
            public static readonly Color Surface = Color.White;
            public static readonly Color Border = Color.FromArgb(206, 212, 218);
            public static readonly Color TextPrimary = Color.FromArgb(33, 37, 41);
            public static readonly Color TextSecondary = Color.FromArgb(108, 117, 125);
            public static readonly Color TextDisabled = Color.FromArgb(173, 181, 189);
        }

        #endregion

        #region Fonts

        public static class Fonts
        {
            public static readonly Font Regular = new Font("Segoe UI", 9F);
            public static readonly Font Bold = new Font("Segoe UI", 9F, FontStyle.Bold);
            public static readonly Font Small = new Font("Segoe UI", 8F);
            public static readonly Font Large = new Font("Segoe UI", 10F);
            public static readonly Font Monospace = new Font("Consolas", 9F);
        }

        #endregion

        #region Button Styles

        /// <summary>
        /// Tạo button Primary (xanh dương)
        /// </summary>
        public static Button CreatePrimaryButton(string text, int width = 80, int height = 28)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = height,
                BackColor = Colors.Primary,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = Fonts.Regular,
                Cursor = Cursors.Hand
            };
        }

        /// <summary>
        /// Tạo button Success (xanh lá)
        /// </summary>
        public static Button CreateSuccessButton(string text, int width = 80, int height = 28)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = height,
                BackColor = Colors.Success,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = Fonts.Regular,
                Cursor = Cursors.Hand
            };
        }

        /// <summary>
        /// Tạo button Danger (đỏ)
        /// </summary>
        public static Button CreateDangerButton(string text, int width = 80, int height = 28)
        {
            return new Button
            {
                Text = text,
                Width = width,
                Height = height,
                BackColor = Colors.Danger,
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = Fonts.Regular,
                Cursor = Cursors.Hand
            };
        }

        /// <summary>
        /// Tạo button thường (outline)
        /// </summary>
        public static Button CreateOutlineButton(string text, int width = 80, int height = 28)
        {
            var btn = new Button
            {
                Text = text,
                Width = width,
                Height = height,
                BackColor = Colors.Surface,
                ForeColor = Colors.TextPrimary,
                FlatStyle = FlatStyle.Flat,
                Font = Fonts.Regular,
                Cursor = Cursors.Hand
            };
            btn.FlatAppearance.BorderColor = Colors.Border;
            return btn;
        }

        #endregion

        #region Apply Styles

        /// <summary>
        /// Áp dụng style cho form
        /// </summary>
        public static void ApplyFormStyle(Form form)
        {
            form.Font = Fonts.Regular;
            form.BackColor = Colors.Background;
        }

        /// <summary>
        /// Áp dụng style cho ListView
        /// </summary>
        public static void ApplyListViewStyle(ListView listView)
        {
            listView.Font = Fonts.Monospace;
            listView.FullRowSelect = true;
            listView.GridLines = true;
            listView.BorderStyle = BorderStyle.FixedSingle;
        }

        /// <summary>
        /// Áp dụng style cho GroupBox
        /// </summary>
        public static void ApplyGroupBoxStyle(GroupBox groupBox)
        {
            groupBox.Font = Fonts.Regular;
            groupBox.ForeColor = Colors.TextPrimary;
        }

        #endregion
    }
}