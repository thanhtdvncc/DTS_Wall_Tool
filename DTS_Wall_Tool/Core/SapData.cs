using System;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.Core
{
    public class SapFrame
    {
        public string Name { get; set; }
        public Point2D StartPt { get; set; }
        public Point2D EndPt { get; set; }

        // Thêm cao độ Z để kiểm tra
        public double Z1 { get; set; }
        public double Z2 { get; set; }

        // Chiều dài trên mặt bằng 2D
        public double Length2D => StartPt.DistanceTo(EndPt);

        // Nhận diện Cột: Nếu chiều dài trên mặt bằng ~ 0 thì là Cột
        public bool IsVertical => Length2D < 1.0;

        public override string ToString()
        {
            string type = IsVertical ? "[CỘT]" : "[DẦM]";
            return $"{type} {Name}: L={Length2D:0.0} | Z={Z1:0.#}->{Z2:0.#} | {StartPt}";
        }
    }
}