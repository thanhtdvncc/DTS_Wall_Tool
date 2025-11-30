using Autodesk.AutoCAD.Geometry;
using System;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.Core
{
    public class Geometry
    {
        //Cấu trúc điểm 2D
        public struct Point2D
        {
            public double X;
            public double Y;


            // Hàm khởi tạo điểm 2D
            public Point2D(double x, double y)
            {
                X = x;
                Y = y;
            }

            //Tinh khoảng cách tới điểm khác    
            public double DistanceTo(Point2D other)
            {
                return Math.Sqrt(Math.Pow(other.X - X, 2) + Math.Pow(other.Y - Y, 2));
            }

            // Phép cộng hai điểm (Vector): p3 = p1 + p2
            public static Point2D operator +(Point2D p1, Point2D p2)
            {
                return new Point2D(p1.X + p2.X, p1.Y + p2.Y);
            }

            // Phép trừ hai điểm (Vector): p3 = p1 - p2
            public static Point2D operator -(Point2D p1, Point2D p2)
            {
                return new Point2D(p1.X - p2.X, p1.Y - p2.Y);
            }

            // Chuyển về chuỗi để kiểm tra (debug)
            public override string ToString()
            {
                return $"({X:0.00}, {Y:0.00})";
            }

        }
    }

    // --- PHẦN 2: THUẬT TOÁN HÌNH HỌC (STATIC CLASS) ---
    // Lớp chứa các thuật toán hình học (Static class - dùng ngay mà không cần khởi tạo object)
    public static class GeoAlgo
    {
        //Sai số cho phép
        public const double EPSILON = 1e-6;
        // Hàm 1: Tính khoảng cách từ điểm P đến đoạn thẳng AB
        public static double DistPointToLine(Geometry.Point2D P, Geometry.Point2D A, Geometry.Point2D B, bool v)
        {
            double dx = B.X - A.X;
            double dy = B.Y - A.Y;

            // Nếu A và B trùng nhau (đoạn thằng là một điểm), dài = 0
            if (Math.Abs(dx) < EPSILON && Math.Abs(dy) < EPSILON)
                return P.DistanceTo(A);

            // Tính tham số t hình chiếu
            double t = ((P.X - A.X) * dx + (P.Y - A.Y) * dy) / (dx * dx + dy * dy);

            // Kẹp t vào đoạn [0, 1] để giới hạn trong đoạn thẳng AB
            //(nếu t < 0 thì điểm gần nhất là A, nếu t > 1 thì điểm gần nhất là B)
            if (t < 0) t = 0;
            else if (t > 1) t = 1;

            // Tính tọa độ điểm gần nhất trên đoạn thẳng(hinh chiếu)
            Geometry.Point2D closest = new Geometry.Point2D(A.X + t * dx, A.Y + t * dy);

            return P.DistanceTo(closest);

        }

        // Hàm 2: Kiểm tra song song (dựa trên góc Radian)
        public static bool IsParallel(double angle1, double angle2, double tolRad)
        {
            double diff = Math.Abs(angle1 - angle2);

            // Chuẩn hóa góc về khoảng [0, π] (vì 0 và π đều là song song với trục)
            while (diff > Math.PI)
            {
                diff -= Math.PI;
            }
            // Nếu góc chênh lệch nhỏ hơn sai số cho phép thì coi là song song
            return (diff < tolRad || Math.Abs(diff - Math.PI) < tolRad);
        }


        public static double Angle2D(Point2D p1, Point2D p2)
        {
            return Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        }

    }

}