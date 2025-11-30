using System;

namespace DTS_Wall_Tool.Core
{
    // Point2D: điểm 2D và các phép toán vector cơ bản.
    // Ghi chú dành cho người mới:
    // - X, Y là toạ độ theo cùng đơn vị với bản vẽ (ví dụ mm).
    // - Struct này hỗ trợ các phép toán để viết biểu thức toán học trực tiếp: Point2D c = a + b;

    public struct Point2D
    {
        public double X;
        public double Y;

        // Tạo điểm mới tại (x, y)
        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        // DistanceTo:
        // - Trả khoảng cách Euclid giữa điểm này và 'other'.
        // - Dùng để đo chiều dài, kiểm tra sai số, tìm điểm gần nhất, v.v.
        public double DistanceTo(Point2D other)
        {
            return Math.Sqrt(Math.Pow(other.X - X, 2) + Math.Pow(other.Y - Y, 2));
        }

        // Operator + : cộng vector theo từng thành phần
        public static Point2D operator +(Point2D a, Point2D b) => new Point2D(a.X + b.X, a.Y + b.Y);

        // Operator - : trừ vector theo từng thành phần
        public static Point2D operator -(Point2D a, Point2D b) => new Point2D(a.X - b.X, a.Y - b.Y);

        // Nhân vector với scalar
        public static Point2D operator *(Point2D a, double s) => new Point2D(a.X * s, a.Y * s);

        // Chia vector cho scalar
        public static Point2D operator /(Point2D a, double s) => new Point2D(a.X / s, a.Y / s);

        // MidpointTo:
        // - Trả trung điểm giữa điểm này và other.
        // - Dùng khi cần tâm của đoạn nối hai điểm.
        public Point2D MidpointTo(Point2D other) => new Point2D((X + other.X) / 2.0, (Y + other.Y) / 2.0);

        // Dot product:
        // - a.Dot(b) = |a|*|b|*cos(theta)
        // - Dùng cho chiếu, kiểm tra góc, v.v.
        public double Dot(Point2D other) => X * other.X + Y * other.Y;

        // Cross product (2D scalar):
        // - a.Cross(b) = a.x*b.y - a.y*b.x
        // - Giá trị tuyệt đối liên quan đến diện tích hình bình hành; dấu cho biết hướng.
        public double Cross(Point2D other) => X * other.Y - Y * other.X;

        // Length: độ dài vector từ gốc toạ độ tới điểm này
        public double Length => Math.Sqrt(X * X + Y * Y);

        // Normalized:
        // - Trả vector đơn vị cùng hướng, hoặc (0,0) nếu độ dài ~0.
        public Point2D Normalized => Length > GeoAlgo.EPSILON ? this / Length : new Point2D(0, 0);

        // Chuỗi dễ đọc cho debug
        public override string ToString() => $"({X:0.00}, {Y:0.00})";

        // So sánh bằng với sai số EPSILON để tránh lỗi số học dấu phẩy động
        public override bool Equals(object obj)
        {
            if (!(obj is Point2D)) return false;
            var other = (Point2D)obj;
            return Math.Abs(X - other.X) < GeoAlgo.EPSILON && Math.Abs(Y - other.Y) < GeoAlgo.EPSILON;
        }

        // Hash đơn giản: làm tròn tới số nguyên để ổn định khi dùng làm key
        public override int GetHashCode()
        {
            int hx = (int)Math.Round(X);
            int hy = (int)Math.Round(Y);
            return hx.GetHashCode() ^ (hy.GetHashCode() << 16);
        }
    }

    // LineSegment2D: đoạn thẳng có hướng từ Start -> End.
    // Ghi chú cho người mới:
    // - Dùng Start/End để truy cập hai đầu đoạn.
    // - Angle trả bằng radian (sử dụng Math.Atan2).
    // - Direction là vector đơn vị hướng từ Start đến End.
    public struct LineSegment2D
    {
        public Point2D Start;
        public Point2D End;

        // Khởi tạo từ hai điểm
        public LineSegment2D(Point2D start, Point2D end)
        {
            Start = start;
            End = end;
        }

        // Khởi tạo từ toạ độ
        public LineSegment2D(double x1, double y1, double x2, double y2)
        {
            Start = new Point2D(x1, y1);
            End = new Point2D(x2, y2);
        }

        // Độ dài đoạn
        public double Length => Start.DistanceTo(End);

        // Trung điểm đoạn
        public Point2D Midpoint => Start.MidpointTo(End);

        // Vector đơn vị chỉ hướng từ Start tới End
        public Point2D Direction => (End - Start).Normalized;

        // Góc hướng của đoạn (-PI .. PI)
        public double Angle => Math.Atan2(End.Y - Start.Y, End.X - Start.X);

        // NormalizedAngle:
        // - Chuẩn hoá góc về [0, PI) để coi hai hướng ngược nhau là cùng một đường thẳng.
        // - Dùng khi so sánh song song không quan tâm chiều.
        public double NormalizedAngle
        {
            get
            {
                double a = Angle;
                while (a < 0) a += Math.PI;
                while (a >= Math.PI) a -= Math.PI;
                return a;
            }
        }

        public override string ToString() => $"[{Start} -> {End}]";
    }

    // ProjectionResult: lưu toạ độ chiếu (scalar) của hai đầu đoạn lên một trục tham chiếu.
    // - StartProj, EndProj: vị trí scalar của Start và End trên trục.
    // - MinProj/MaxProj: tiện ích để kiểm tra giao nhau.
    public struct ProjectionResult
    {
        public double StartProj;
        public double EndProj;
        public double MinProj => Math.Min(StartProj, EndProj);
        public double MaxProj => Math.Max(StartProj, EndProj);
        public double Length => MaxProj - MinProj;
    }

    // OverlapResult: kết quả khi so sánh hai đoạn/chiếu.
    // - HasOverlap: có giao hay tiếp xúc không.
    // - OverlapLength: độ dài phần giao.
    // - OverlapPercent: tỉ lệ so với đoạn ngắn hơn.
    public struct OverlapResult
    {
        public bool HasOverlap;
        public double OverlapLength;
        public double OverlapPercent;
    }

    // BoundingBox: hộp chữ nhật song song với trục (AABB) để kiểm tra nhanh.
    // Lưu ý: kiểm tra AABB trước giúp loại bỏ nhiều trường hợp không giao nhau nhanh chóng.
    public struct BoundingBox
    {
        public double MinX, MaxX, MinY, MaxY;

        public BoundingBox(LineSegment2D seg)
        {
            MinX = Math.Min(seg.Start.X, seg.End.X);
            MaxX = Math.Max(seg.Start.X, seg.End.X);
            MinY = Math.Min(seg.Start.Y, seg.End.Y);
            MaxY = Math.Max(seg.Start.Y, seg.End.Y);
        }

        // Kiểm tra hai AABB có giao nhau không (với lề margin)
        public bool Intersects(BoundingBox other, double margin = 0)
        {
            return MaxX + margin >= other.MinX && MinX - margin <= other.MaxX &&
                   MaxY + margin >= other.MinY && MinY - margin <= other.MaxY;
        }

        // Mở rộng hộp bởi margin ở mọi phía
        public void Expand(double margin)
        {
            MinX -= margin;
            MaxX += margin;
            MinY -= margin;
            MaxY += margin;
        }
    }

    // GeoAlgo: các hàm thuật toán hình học dùng chung.
    // Ghi chú cho người mới:
    // - Gọi theo dạng: GeoAlgo.Method(...)
    // - Tất cả đơn vị dùng cùng đơn vị với Point2D.
    // - EPSILON là sai số nhỏ dùng để so sánh số thực.
    public static class GeoAlgo
    {
        // Sai số để so sánh số thực
        public const double EPSILON = 1e-6;
        public const double PI = Math.PI;
        public const double HALF_PI = Math.PI / 2.0;
        public const double DEG_TO_RAD = Math.PI / 180.0;
        public const double RAD_TO_DEG = 180.0 / Math.PI;

        #region Basic Distance Functions

        // DistPointToSegment:
        // - Tính khoảng cách từ điểm P đến đoạn [A,B] (không phải đường thẳng vô hạn).
        // - Bước: tính tham số t của hình chiếu, kẹp t về [0,1], rồi tính điểm chiếu và trả khoảng cách.
        public static double DistPointToSegment(Point2D P, Point2D A, Point2D B)
        {
            var AB = B - A;
            var AP = P - A;

            double len2 = AB.X * AB.X + AB.Y * AB.Y;
            if (len2 < EPSILON) return P.DistanceTo(A);

            double t = AP.Dot(AB) / len2;
            t = Math.Max(0.0, Math.Min(1.0, t)); // kẹp về [0,1]

            var proj = new Point2D(A.X + t * AB.X, A.Y + t * AB.Y);
            return P.DistanceTo(proj);
        }

        // DistPointToInfiniteLine:
        // - Khoảng cách vuông góc từ P đến đường thẳng vô hạn đi qua A và B.
        // - Nếu A trùng B, trả khoảng cách tới A.
        public static double DistPointToInfiniteLine(Point2D P, Point2D A, Point2D B)
        {
            var AB = B - A;
            double len = AB.Length;
            if (len < EPSILON) return P.DistanceTo(A);

            // Công thức: |(AB cross AP)| / |AB|
            return Math.Abs(AB.Y * (P.X - A.X) - AB.X * (P.Y - A.Y)) / len;
        }

        // DistBetweenParallelSegments:
        // - Ước lượng khoảng cách vuông góc giữa hai đoạn gần song song bằng trung bình 2 đầu.
        public static double DistBetweenParallelSegments(LineSegment2D seg1, LineSegment2D seg2)
        {
            double d1 = DistPointToInfiniteLine(seg2.Start, seg1.Start, seg1.End);
            double d2 = DistPointToInfiniteLine(seg2.End, seg1.Start, seg1.End);
            return (d1 + d2) / 2.0;
        }

        #endregion

        #region Projection Functions

        // ProjectPointOnSegment:
        // - Chiếu điểm P lên đường thẳng của segment.
        // - Trả tham số t (không kẹp): projection = Start + t*(End-Start)
        // - Trả thêm projection qua out.
        // - Nếu segment gần như điểm thì trả Start và t = 0.
        public static double ProjectPointOnSegment(Point2D P, LineSegment2D segment, out Point2D projection)
        {
            var AB = segment.End - segment.Start;
            var AP = P - segment.Start;

            double len2 = AB.X * AB.X + AB.Y * AB.Y;
            if (len2 < EPSILON)
            {
                projection = segment.Start;
                return 0.0;
            }

            double t = AP.Dot(AB) / len2;
            projection = new Point2D(segment.Start.X + t * AB.X, segment.Start.Y + t * AB.Y);
            return t;
        }

        // ProjectSegmentOnVector:
        // - Chiếu đoạn lên trục định bởi refPoint và góc refAngle.
        // - Trả toạ độ scalar của Start và End trên trục để dùng cho kiểm tra giao nhau theo 1D.
        public static ProjectionResult ProjectSegmentOnVector(LineSegment2D segment, Point2D refPoint, double refAngle)
        {
            double cosA = Math.Cos(refAngle);
            double sinA = Math.Sin(refAngle);

            var result = new ProjectionResult();

            double dx1 = segment.Start.X - refPoint.X;
            double dy1 = segment.Start.Y - refPoint.Y;
            result.StartProj = dx1 * cosA + dy1 * sinA;

            double dx2 = segment.End.X - refPoint.X;
            double dy2 = segment.End.Y - refPoint.Y;
            result.EndProj = dx2 * cosA + dy2 * sinA;

            return result;
        }

        #endregion

        #region Angle Functions

        // IsParallel:
        // - Kiểm tra hai góc có gần như song song không (bỏ qua khác dấu PI).
        public static bool IsParallel(double angle1, double angle2, double toleranceRad = 5 * DEG_TO_RAD)
        {
            double diff = Math.Abs(angle1 - angle2);
            while (diff > PI) diff -= PI;
            return diff <= toleranceRad || (PI - diff) <= toleranceRad;
        }

        // IsPerpendicular:
        // - Kiểm tra vuông góc trong sai số cho phép.
        public static bool IsPerpendicular(double angle1, double angle2, double toleranceRad = 5 * DEG_TO_RAD)
        {
            double diff = Math.Abs(angle1 - angle2);
            while (diff > PI) diff -= PI;
            return Math.Abs(diff - HALF_PI) <= toleranceRad;
        }

        // SnapToCardinalAngle:
        // - Nếu góc gần 0, 90, 180, 270 độ thì làm tròn về góc đó.
        // - Hữu ích khi muốn căn chỉnh tường gần vuông/góc chuẩn.
        public static double SnapToCardinalAngle(double angleRad, double toleranceRad = 5 * DEG_TO_RAD)
        {
            while (angleRad < 0) angleRad += 2 * PI;
            while (angleRad >= 2 * PI) angleRad -= 2 * PI;

            double[] cardinals = { 0, HALF_PI, PI, 3 * HALF_PI, 2 * PI };
            foreach (var c in cardinals)
            {
                if (Math.Abs(angleRad - c) <= toleranceRad)
                    return c == 2 * PI ? 0 : c;
            }

            return angleRad;
        }

        // NormalizeAngle:
        // - Chuẩn hoá góc về [0, PI) khi không quan tâm chiều.
        public static double NormalizeAngle(double angleRad)
        {
            while (angleRad < 0) angleRad += PI;
            while (angleRad >= PI) angleRad -= PI;
            return angleRad;
        }

        // Angle2D:
        // - Tiện ích tính góc từ p1 tới p2 (Atan2).
        public static double Angle2D(Point2D p1, Point2D p2)
        {
            return Math.Atan2(p2.Y - p1.Y, p2.X - p1.X);
        }

        #endregion

        #region Intersection Functions

        // GetLineIntersection:
        // - Tìm giao điểm của hai đường thẳng vô hạn (A1-A2, B1-B2).
        // - Trả true nếu không song song; intersection hợp lệ khi true.
        public static bool GetLineIntersection(Point2D A1, Point2D A2, Point2D B1, Point2D B2, out Point2D intersection)
        {
            intersection = new Point2D(0, 0);

            double dx1 = A2.X - A1.X;
            double dy1 = A2.Y - A1.Y;
            double dx2 = B2.X - B1.X;
            double dy2 = B2.Y - B1.Y;

            double denom = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(denom) < EPSILON) return false; // song song hoặc gần song song

            double t = ((B1.X - A1.X) * dy2 - (B1.Y - A1.Y) * dx2) / denom;
            intersection = new Point2D(A1.X + t * dx1, A1.Y + t * dy1);
            return true;
        }

        // GetSegmentIntersection:
        // - Tìm giao điểm của hai đoạn hữu hạn seg1 và seg2.
        // - tolerance: cho phép sai số tương đối (dùng khi hai đoạn chỉ chạm tại đầu/mút).
        // - Trả true nếu giao hoặc chạm trong tolerance; intersection là toạ độ giao.
        public static bool GetSegmentIntersection(LineSegment2D seg1, LineSegment2D seg2, out Point2D intersection, double tolerance = 0.0)
        {
            intersection = new Point2D(0, 0);

            double dx1 = seg1.End.X - seg1.Start.X;
            double dy1 = seg1.End.Y - seg1.Start.Y;
            double dx2 = seg2.End.X - seg2.Start.X;
            double dy2 = seg2.End.Y - seg2.Start.Y;

            double denom = dx1 * dy2 - dy1 * dx2;
            if (Math.Abs(denom) < EPSILON) return false; // song song hoặc gần

            double t = ((seg2.Start.X - seg1.Start.X) * dy2 - (seg2.Start.Y - seg1.Start.Y) * dx2) / denom;
            double u = ((seg2.Start.X - seg1.Start.X) * dy1 - (seg2.Start.Y - seg1.Start.Y) * dx1) / denom;

            double tol = tolerance / Math.Max(seg1.Length, seg2.Length);
            if (t >= -tol && t <= 1.0 + tol && u >= -tol && u <= 1.0 + tol)
            {
                intersection = new Point2D(seg1.Start.X + t * dx1, seg1.Start.Y + t * dy1);
                return true;
            }

            return false;
        }

        #endregion

        #region Overlap and Merge Functions

        // CalculateOverlap:
        // - Chiếu hai đoạn lên trục của seg1 rồi tính khoảng giao.
        // - HasOverlap = true nếu chồng lấn hoặc chạm (cho EPSILON).
        // - OverlapPercent tính theo đoạn ngắn hơn.
        public static OverlapResult CalculateOverlap(LineSegment2D seg1, LineSegment2D seg2)
        {
            var result = new OverlapResult();

            double refAngle = seg1.Angle;
            var refPoint = seg1.Start;

            var proj1 = ProjectSegmentOnVector(seg1, refPoint, refAngle);
            var proj2 = ProjectSegmentOnVector(seg2, refPoint, refAngle);

            double overlapStart = Math.Max(proj1.MinProj, proj2.MinProj);
            double overlapEnd = Math.Min(proj1.MaxProj, proj2.MaxProj);

            result.OverlapLength = overlapEnd - overlapStart;
            result.HasOverlap = result.OverlapLength > -EPSILON; // cho phép tiếp xúc

            if (result.HasOverlap && result.OverlapLength > 0.0)
            {
                double shorterLength = Math.Min(proj1.Length, proj2.Length);
                result.OverlapPercent = shorterLength > EPSILON ? result.OverlapLength / shorterLength : 0.0;
            }
            else
            {
                result.OverlapPercent = 0.0;
            }

            return result;
        }

        // CalculateGapDistance:
        // - Nếu cùng đường thẳng, trả khoảng cách giữa hai đoạn (dương) hoặc -1 nếu chồng lấn.
        public static double CalculateGapDistance(LineSegment2D seg1, LineSegment2D seg2)
        {
            double refAngle = seg1.Angle;
            var refPoint = seg1.Start;

            var proj1 = ProjectSegmentOnVector(seg1, refPoint, refAngle);
            var proj2 = ProjectSegmentOnVector(seg2, refPoint, refAngle);

            if (proj2.MinProj >= proj1.MaxProj)
                return proj2.MinProj - proj1.MaxProj;
            else if (proj1.MinProj >= proj2.MaxProj)
                return proj1.MinProj - proj2.MaxProj;
            else
                return -1.0; // chồng lấn
        }

        // AreCollinear:
        // - Kiểm tra hai đoạn có nằm trên cùng một đường thẳng (trong sai số).
        // - Cách làm: kiểm tra góc song song rồi kiểm tra khoảng cách vuông góc của các đầu mút.
        public static bool AreCollinear(LineSegment2D seg1, LineSegment2D seg2, double angleTolerance = 1 * DEG_TO_RAD, double distTolerance = 30.0)
        {
            if (!IsParallel(seg1.Angle, seg2.Angle, angleTolerance))
                return false;

            double d1 = DistPointToInfiniteLine(seg2.Start, seg1.Start, seg1.End);
            double d2 = DistPointToInfiniteLine(seg2.End, seg1.Start, seg1.End);
            double d3 = DistPointToInfiniteLine(seg1.Start, seg2.Start, seg2.End);
            double d4 = DistPointToInfiniteLine(seg1.End, seg2.Start, seg2.End);

            return d1 <= distTolerance && d2 <= distTolerance &&
                   d3 <= distTolerance && d4 <= distTolerance;
        }

        // MergeCollinearSegments:
        // - Nối hai đoạn đồng tuyến thành một đoạn bao phủ cả hai.
        // - Bước: chọn đoạn "dominant" (dài hơn) làm trục tham chiếu, chiếu 4 điểm, lấy min/max rồi chuyển ngược về XY.
        public static LineSegment2D MergeCollinearSegments(LineSegment2D seg1, LineSegment2D seg2)
        {
            var dominant = seg1.Length >= seg2.Length ? seg1 : seg2;
            double refAngle = SnapToCardinalAngle(dominant.Angle);
            var refPoint = dominant.Start;

            double cosA = Math.Cos(refAngle);
            double sinA = Math.Sin(refAngle);

            var points = new[] { seg1.Start, seg1.End, seg2.Start, seg2.End };

            double minProj = double.MaxValue;
            double maxProj = double.MinValue;

            foreach (var pt in points)
            {
                double dx = pt.X - refPoint.X;
                double dy = pt.Y - refPoint.Y;
                double proj = dx * cosA + dy * sinA;
                if (proj < minProj) minProj = proj;
                if (proj > maxProj) maxProj = proj;
            }

            return new LineSegment2D(
                new Point2D(refPoint.X + minProj * cosA, refPoint.Y + minProj * sinA),
                new Point2D(refPoint.X + maxProj * cosA, refPoint.Y + maxProj * sinA)
            );
        }

        #endregion

        #region Bounding Box Helpers

        // BuildBoundingBox: tiện ích tạo AABB từ đoạn
        public static BoundingBox BuildBoundingBox(LineSegment2D seg) => new BoundingBox(seg);

        #endregion
    }
}