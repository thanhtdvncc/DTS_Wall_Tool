using DTS_Engine.Core.Primitives;

namespace DTS_Engine.Core.Data
{
    /// <summary>
    /// Dữ liệu Frame (dầm/cột) từ SAP2000
    /// </summary>
    public class SapFrame
    {
        /// <summary>
        /// Tên frame trong SAP2000
        /// </summary>
        public string Name { get; set; }

        /// <summary>
        /// Điểm đầu (2D - mặt bằng)
        /// </summary>
        public Point2D StartPt { get; set; }

        /// <summary>
        /// Điểm cuối (2D - mặt bằng)
        /// </summary>
        public Point2D EndPt { get; set; }

        /// <summary>
        /// Cao độ Z đầu frame
        /// </summary>
        public double Z1 { get; set; }

        /// <summary>
        /// Cao độ Z cuối frame
        /// </summary>
        public double Z2 { get; set; }

        /// <summary>
        /// Section name trong SAP2000
        /// </summary>
        public string Section { get; set; } = "";

        /// <summary>
        /// Story name
        /// </summary>
        public string Story { get; set; } = "";

        #region Computed Properties

        /// <summary>
        /// Chiều dài trên mặt bằng 2D
        /// </summary>
        /// <summary>
        /// Chiều dài trên mặt bằng 2D
        /// </summary>
        public double Length2D => StartPt.DistanceTo(EndPt);

        /// <summary>
        /// Chiều dài thực tế 3D
        /// </summary>
        public double Length3D => System.Math.Sqrt(System.Math.Pow(Length2D, 2) + System.Math.Pow(Z2 - Z1, 2));

        /// <summary>
        /// Cao độ trung bình
        /// </summary>
        public double AverageZ => (Z1 + Z2) / 2.0;

        /// <summary>
        /// Nhận diện cột: chiều dài 2D ~ 0
        /// </summary>
        public bool IsVertical => Length2D < 1.0;

        /// <summary>
        /// Nhận diện dầm
        /// </summary>
        public bool IsBeam => !IsVertical;

        /// <summary>
        /// Chuyển đổi sang LineSegment2D
        /// </summary>
        public LineSegment2D AsSegment => new LineSegment2D(StartPt, EndPt);

        /// <summary>
        /// Trung điểm 2D
        /// </summary>
        public Point2D Midpoint => StartPt.MidpointTo(EndPt);

        /// <summary>
        /// Góc trên mặt bằng
        /// </summary>
        public double Angle => System.Math.Atan2(EndPt.Y - StartPt.Y, EndPt.X - StartPt.X);

        #endregion

        public override string ToString()
        {
            string type = IsVertical ? "[COL]" : "[BEAM]";
            return $"{type} {Name}: L={Length2D:0.0} | Z={Z1:0. #}->{Z2:0. #} | {StartPt}";
        }

        /// <summary>
        /// Clone SapFrame
        /// </summary>
        public SapFrame Clone()
        {
            return new SapFrame
            {
                Name = Name,
                StartPt = StartPt,
                EndPt = EndPt,
                Z1 = Z1,
                Z2 = Z2,
                Section = Section,
                Story = Story
            };
        }
    }
}