using System;

namespace DTS_Wall_Tool.Core
{
    // Class chứa thông tin của một Tầng (gán vào vòng tròn đỏ)
    public class StoryData
    {
        public string StoryName { get; set; } = "Tang_1";
        public double Elevation { get; set; } = 0.0; // Cao độ Z (mm)

        // Hàm này giúp hiển thị nhanh thông tin khi cần kiểm tra
        public override string ToString()
        {
            return $"Tầng: {StoryName} (Z={Elevation})";
        }
    }
}