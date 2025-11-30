using System;
using System.Collections.Generic;
using System.Linq;
using static DTS_Wall_Tool.Core.Geometry; // Để dùng hàm sắp xếp, lọc

namespace DTS_Wall_Tool.Core
{
    // Class chứa kết quả mapping
    public class MappingResult
    {
        public SapFrame TargetFrame { get; set; } // Dầm được chọn
        public double OverlapLength { get; set; } // Chiều dài chồng lấn
        public double DistFromI { get; set; }     // Điểm bắt đầu tải (tính từ đầu dầm)
        public double DistToJ { get; set; }       // Điểm kết thúc tải
        public string MatchType { get; set; }     // "EXACT" (khít), "PARTIAL" (một phần)

        public override string ToString()
        {
            return $"{MatchType} -> {TargetFrame.Name} (L={OverlapLength:0.0}, Từ {DistFromI:0.0} đến {DistToJ:0.0})";
        }
    }

    public static class MappingEngine
    {
        // Dung sai (Tùy chỉnh được)
        private const double TOLERANCE_Z = 200.0;    // Lệch cao độ tối đa 200mm
        private const double TOLERANCE_DIST = 300.0; // Lệch tim trục tối đa 300mm
        private const double MIN_OVERLAP = 100.0;    // Phải chồng lên nhau ít nhất 100mm

        /// <summary>
        /// Hàm chính: Tìm các dầm đỡ một bức tường
        /// </summary>
        /// <param name="wStart">Điểm đầu tường</param>
        /// <param name="wEnd">Điểm cuối tường</param>
        /// <param name="wallZ">Cao độ tường</param>
        /// <param name="allFrames">Danh sách tất cả frame trong SAP</param>
        public static List<MappingResult> FindSupportingFrames(Point2D wStart, Point2D wEnd, double wallZ, List<SapFrame> allFrames)
        {
            var results = new List<MappingResult>();
            double wallLen = wStart.DistanceTo(wEnd);
            if (wallLen < 1.0) return results;

            // 1. Duyệt qua tất cả các Frame
            foreach (var frame in allFrames)
            {
                // --- BỘ LỌC 1: Loại bỏ Cột và Dầm sai cao độ ---
                if (frame.IsVertical) continue; // Bỏ cột

                // Lấy cao độ trung bình của dầm (thường dầm nằm ngang thì Z1=Z2)
                double frameZ = (frame.Z1 + frame.Z2) / 2.0;

                // Nếu cao độ lệch quá nhiều -> Bỏ qua
                if (Math.Abs(frameZ - wallZ) > TOLERANCE_Z) continue;

                // --- BỘ LỌC 2: Kiểm tra song song ---
                // Tính góc tường và góc dầm
                double wallAng = GeoAlgo.Angle2D(wStart, wEnd);
                double frameAng = GeoAlgo.Angle2D(frame.StartPt, frame.EndPt);

                // Dùng hàm kiểm tra song song trong Geometry.cs (dung sai 10 độ ~ 0.17 rad)
                if (!GeoAlgo.IsParallel(wallAng, frameAng, 0.17)) continue;

                // --- BỘ LỌC 3: Khoảng cách tim (Offset) ---
                // Tính khoảng cách từ trung điểm tường đến đường thẳng dầm
                Point2D wallMid = new Point2D((wStart.X + wEnd.X) / 2, (wStart.Y + wEnd.Y) / 2);
                double dist = GeoAlgo.DistPointToLine(wallMid, frame.StartPt, frame.EndPt, true); // true = đường thẳng vô tận

                if (dist > TOLERANCE_DIST) continue;

                // --- TÍNH TOÁN CHỒNG LẤN (OVERLAP) ---
                // Chiếu tường lên dầm để xem nó nằm ở đâu trên dầm
                // (Giả sử dầm là trục số từ 0 đến L_Frame)
                double t1 = GetProjectionT(wStart, frame);
                double t2 = GetProjectionT(wEnd, frame);

                // Sắp xếp t1 < t2
                double startT = Math.Min(t1, t2);
                double endT = Math.Max(t1, t2);

                // Giao của đoạn [startT, endT] (tường) và đoạn [0, 1] (dầm)
                double overlapStart = Math.Max(startT, 0.0);
                double overlapEnd = Math.Min(endT, 1.0);

                if (overlapEnd > overlapStart)
                {
                    double overlapLen = (overlapEnd - overlapStart) * frame.Length2D;

                    // Nếu phần chồng lấn đủ lớn -> CHẤP NHẬN
                    if (overlapLen >= MIN_OVERLAP)
                    {
                        MappingResult res = new MappingResult();
                        res.TargetFrame = frame;
                        res.OverlapLength = overlapLen;

                        // Quy đổi từ tỉ lệ [0..1] ra mm thực tế trên dầm
                        res.DistFromI = overlapStart * frame.Length2D;
                        res.DistToJ = overlapEnd * frame.Length2D;

                        // Phân loại khớp
                        if (overlapLen >= wallLen * 0.95) res.MatchType = "EXACT"; // Khớp > 95%
                        else res.MatchType = "PARTIAL";

                        results.Add(res);
                    }
                }
            }

            // Sắp xếp kết quả ưu tiên dầm nào đỡ nhiều nhất
            return results.OrderByDescending(x => x.OverlapLength).ToList();
        }

        // Hàm phụ: Tính vị trí hình chiếu của điểm P lên dầm (trả về t từ 0 đến 1)
        private static double GetProjectionT(Point2D P, SapFrame frame)
        {
            double dx = frame.EndPt.X - frame.StartPt.X;
            double dy = frame.EndPt.Y - frame.StartPt.Y;
            double len2 = dx * dx + dy * dy;

            if (len2 < GeoAlgo.EPSILON) return 0;

            // Công thức vector projection
            return ((P.X - frame.StartPt.X) * dx + (P.Y - frame.StartPt.Y) * dy) / len2;
        }
    }
}