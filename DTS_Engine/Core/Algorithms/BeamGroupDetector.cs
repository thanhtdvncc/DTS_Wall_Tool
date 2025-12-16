using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.DataStructures;
using DTS_Engine.Models;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Thuật toán nhận diện nhóm dầm liên tục từ bản vẽ/model.
    /// Sử dụng Grid-first detection + SpatialHash cho hiệu năng tốt.
    /// </summary>
    public static class BeamGroupDetector
    {
        private const double AXIS_TOLERANCE = 400; // mm - Buffer zone quanh trục
        private const double COLLINEAR_TOLERANCE = 100; // mm - Tolerance cho dầm thẳng hàng
        private const double STEP_CHANGE_THRESHOLD = 50; // mm - Ngưỡng giật cấp

        /// <summary>
        /// Nhóm dầm thành các BeamGroup dựa trên geometry và trục.
        /// </summary>
        /// <param name="beams">Danh sách dầm từ model</param>
        /// <param name="axes">Danh sách trục kết cấu</param>
        /// <param name="supports">Danh sách gối đỡ (Column, Wall, Beam)</param>
        /// <param name="settings">Cài đặt</param>
        /// <returns>Danh sách nhóm dầm</returns>
        public static List<BeamGroup> DetectGroups(
            List<BeamData> beams,
            List<AxisLine> axes,
            List<SupportEntity> supports,
            DtsSettings settings)
        {
            var groups = new List<BeamGroup>();
            var processedHandles = new HashSet<string>();

            // BƯỚC 1: NHÓM THEO TRỤC (ưu tiên cao nhất)
            foreach (var axis in axes.OrderBy(a => a.Name))
            {
                var beamsOnAxis = QueryBeamsInBuffer(beams, axis, AXIS_TOLERANCE)
                    .Where(b => !processedHandles.Contains(b.Handle))
                    .ToList();

                if (beamsOnAxis.Count == 0) continue;

                // Nhóm các dầm thẳng hàng thành 1 dải
                var chains = ChainCollinearBeams(beamsOnAxis);

                foreach (var chain in chains.Where(c => c.Count > 0))
                {
                    var group = CreateGroupFromChain(chain, axis, supports, settings);
                    if (group != null && group.Spans.Count > 0)
                    {
                        groups.Add(group);
                        foreach (var b in chain)
                            processedHandles.Add(b.Handle);
                    }
                }
            }

            // BƯỚC 2: DẦM CÒN LẠI (không nằm trên trục)
            var remainingBeams = beams.Where(b => !processedHandles.Contains(b.Handle)).ToList();
            if (remainingBeams.Count > 0)
            {
                var otherGroups = ChainRemainingBeams(remainingBeams, supports, settings);
                groups.AddRange(otherGroups);
            }

            return groups;
        }

        /// <summary>
        /// Lấy các dầm nằm trong buffer zone của trục
        /// </summary>
        private static List<BeamData> QueryBeamsInBuffer(List<BeamData> beams, AxisLine axis, double tolerance)
        {
            return beams.Where(b => IsBeamOnAxis(b, axis, tolerance)).ToList();
        }

        /// <summary>
        /// Kiểm tra dầm có nằm trên trục không
        /// </summary>
        private static bool IsBeamOnAxis(BeamData beam, AxisLine axis, double tolerance)
        {
            // Tính khoảng cách từ trung điểm dầm đến trục
            var midpoint = new Point2D(
                (beam.StartX + beam.EndX) / 2,
                (beam.StartY + beam.EndY) / 2);

            // Dầm phải song song với trục
            double beamAngle = Math.Atan2(beam.EndY - beam.StartY, beam.EndX - beam.StartX);
            double axisAngle = axis.Angle;

            // Normalize angles
            double angleDiff = Math.Abs(NormalizeAngle(beamAngle) - NormalizeAngle(axisAngle));
            if (angleDiff > 0.1 && Math.Abs(angleDiff - Math.PI) > 0.1)
                return false; // Không song song

            // Check khoảng cách đến trục
            return axis.ContainsPoint(midpoint, tolerance);
        }

        private static double NormalizeAngle(double angle)
        {
            while (angle < 0) angle += Math.PI;
            while (angle >= Math.PI) angle -= Math.PI;
            return angle;
        }

        /// <summary>
        /// Nối các dầm thẳng hàng thành chuỗi
        /// </summary>
        private static List<List<BeamData>> ChainCollinearBeams(List<BeamData> beams)
        {
            var chains = new List<List<BeamData>>();
            var used = new HashSet<string>();

            // Sắp xếp theo vị trí (X hoặc Y tùy hướng)
            var sorted = beams.OrderBy(b => b.StartX + b.StartY).ToList();

            foreach (var beam in sorted)
            {
                if (used.Contains(beam.Handle)) continue;

                var chain = new List<BeamData> { beam };
                used.Add(beam.Handle);

                // Tìm các dầm nối tiếp
                bool extended;
                do
                {
                    extended = false;
                    foreach (var other in sorted.Where(b => !used.Contains(b.Handle)))
                    {
                        if (AreBeamsConnected(chain.Last(), other))
                        {
                            chain.Add(other);
                            used.Add(other.Handle);
                            extended = true;
                        }
                        else if (AreBeamsConnected(other, chain.First()))
                        {
                            chain.Insert(0, other);
                            used.Add(other.Handle);
                            extended = true;
                        }
                    }
                } while (extended);

                chains.Add(chain);
            }

            return chains;
        }

        /// <summary>
        /// Kiểm tra 2 dầm có nối tiếp VÀ thẳng hàng không.
        /// Dầm gấp khúc (góc > 5°) sẽ bị từ chối để tránh lỗi Vector Projection.
        /// </summary>
        private static bool AreBeamsConnected(BeamData b1, BeamData b2)
        {
            // 1. Kiểm tra khoảng cách - điểm cuối b1 gần điểm đầu b2
            double dist1 = Distance(b1.EndX, b1.EndY, b2.StartX, b2.StartY);
            double dist2 = Distance(b1.EndX, b1.EndY, b2.EndX, b2.EndY);
            double dist3 = Distance(b1.StartX, b1.StartY, b2.StartX, b2.StartY);
            double dist4 = Distance(b1.StartX, b1.StartY, b2.EndX, b2.EndY);

            double minDist = Math.Min(Math.Min(dist1, dist2), Math.Min(dist3, dist4));
            if (minDist >= COLLINEAR_TOLERANCE)
                return false; // Không nối tiếp

            // 2. Kiểm tra thẳng hàng (Collinear check)
            // Tính góc của từng dầm
            double angle1 = Math.Atan2(b1.EndY - b1.StartY, b1.EndX - b1.StartX);
            double angle2 = Math.Atan2(b2.EndY - b2.StartY, b2.EndX - b2.StartX);

            // Normalize góc về [0, PI) để so sánh
            angle1 = NormalizeAngle(angle1);
            angle2 = NormalizeAngle(angle2);

            // Cho phép sai số 5° = 0.087 rad
            const double MAX_ANGLE_DIFF = 5.0 * Math.PI / 180.0;
            double angleDiff = Math.Abs(angle1 - angle2);

            // Xử lý trường hợp gần 0 và gần PI
            if (angleDiff > Math.PI / 2)
                angleDiff = Math.PI - angleDiff;

            if (angleDiff > MAX_ANGLE_DIFF)
                return false; // Gấp khúc > 5° -> Không cho phép gộp

            return true;
        }

        private static double Distance(double x1, double y1, double x2, double y2)
        {
            return Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
        }

        /// <summary>
        /// Tạo BeamGroup từ chuỗi dầm
        /// </summary>
        private static BeamGroup CreateGroupFromChain(
            List<BeamData> chain,
            AxisLine axis,
            List<SupportEntity> allSupports,
            DtsSettings settings)
        {
            if (chain.Count == 0) return null;

            var group = new BeamGroup
            {
                AxisName = axis?.Name ?? "",
                Direction = axis?.IsHorizontal == true ? "Y" : "X",
                Source = "Auto",
                EntityHandles = chain.Select(b => b.Handle).ToList()
            };

            // Tính dimensions chung
            group.Width = chain.First().Width;
            group.Height = chain.First().Height;
            group.GroupType = chain.First().Width >= 300 ? "Girder" : "Beam";

            // Tính TotalLength
            group.TotalLength = chain.Sum(b => b.Length) / 1000.0; // Convert to m

            // Check RequiresSplice
            double standardLength = settings.Beam?.StandardBarLength ?? 11700;
            group.RequiresSplice = group.TotalLength * 1000 > standardLength;

            // Tìm gối đỡ
            DetectSupports(group, chain, allSupports);

            // Tạo SpanData
            CreateSpans(group, chain, settings);

            // Generate name
            group.GroupName = GenerateGroupName(group, axis);

            return group;
        }

        /// <summary>
        /// Nhận diện gối đỡ (Column, Wall, Beam) cho nhóm.
        /// Public để có thể gọi từ RebarCommands cho manual groups.
        /// </summary>
        public static void DetectSupports(BeamGroup group, List<BeamData> chain, List<SupportEntity> allSupports)
        {
            // Sử dụng SpatialHash để query nhanh
            var spatialHash = new SpatialHash<SupportEntity>(500);
            foreach (var s in allSupports)
            {
                var bounds = new BoundingBox(
                    s.CenterX - s.Width / 2,
                    s.CenterY - s.Depth / 2,
                    s.CenterX + s.Width / 2,
                    s.CenterY + s.Depth / 2);
                spatialHash.InsertWithBounds(s, bounds);
            }

            // Tìm supports giao với chain
            var foundSupports = new List<SupportData>();
            var addedPositions = new HashSet<double>();

            // Điểm đầu của chain
            double startX = chain.First().StartX;
            double startY = chain.First().StartY;

            foreach (var beam in chain)
            {
                // Query tại điểm đầu và cuối của mỗi dầm
                var startBounds = new BoundingBox(
                    beam.StartX - 200, beam.StartY - 200,
                    beam.StartX + 200, beam.StartY + 200);
                var endBounds = new BoundingBox(
                    beam.EndX - 200, beam.EndY - 200,
                    beam.EndX + 200, beam.EndY + 200);

                foreach (var support in spatialHash.QueryBounds(startBounds).Concat(spatialHash.QueryBounds(endBounds)))
                {
                    double pos = Distance(startX, startY, support.CenterX, support.CenterY) / 1000.0;
                    double roundedPos = Math.Round(pos * 100) / 100;

                    if (!addedPositions.Contains(roundedPos))
                    {
                        foundSupports.Add(new SupportData
                        {
                            SupportId = support.Name ?? $"S{foundSupports.Count + 1}",
                            Type = ConvertSupportType(support.Type),
                            Width = support.Width,
                            Position = pos,
                            GridName = support.GridName ?? "",
                            EntityHandle = support.Handle
                        });
                        addedPositions.Add(roundedPos);
                    }
                }
            }

            // Sắp xếp theo vị trí
            foundSupports = foundSupports.OrderBy(s => s.Position).ToList();

            // Check đầu thừa (FreeEnd)
            if (foundSupports.Count == 0 || foundSupports.First().Position > 0.1)
            {
                foundSupports.Insert(0, new SupportData
                {
                    SupportId = "FE_Start",
                    Type = SupportType.FreeEnd,
                    Position = 0,
                    Width = 0
                });
                group.HasConsole = true;
            }

            if (foundSupports.Count == 0 || Math.Abs(foundSupports.Last().Position - group.TotalLength) > 0.1)
            {
                foundSupports.Add(new SupportData
                {
                    SupportId = "FE_End",
                    Type = SupportType.FreeEnd,
                    Position = group.TotalLength,
                    Width = 0
                });
                group.HasConsole = true;
            }

            // Đánh index
            for (int i = 0; i < foundSupports.Count; i++)
                foundSupports[i].SupportIndex = i;

            group.Supports = foundSupports;
        }

        private static SupportType ConvertSupportType(string type)
        {
            if (string.IsNullOrEmpty(type)) return SupportType.Column;
            switch (type.ToLower())
            {
                case "wall": return SupportType.Wall;
                case "beam": return SupportType.Beam;
                default: return SupportType.Column;
            }
        }

        /// <summary>
        /// Tạo SpanData từ danh sách gối
        /// </summary>
        private static void CreateSpans(BeamGroup group, List<BeamData> chain, DtsSettings settings)
        {
            var supports = group.Supports;
            if (supports.Count < 2) return;

            double prevHeight = group.Height;

            for (int i = 0; i < supports.Count - 1; i++)
            {
                var leftSupport = supports[i];
                var rightSupport = supports[i + 1];

                // Tìm các segments thuộc span này
                var segments = FindSegmentsInSpan(chain, leftSupport.Position, rightSupport.Position);

                // Xác định chiều cao (có thể giật cấp)
                double spanHeight = segments.Count > 0 ? segments.Average(b => b.Height) : group.Height;
                bool isStepChange = Math.Abs(spanHeight - prevHeight) > STEP_CHANGE_THRESHOLD;

                var span = new SpanData
                {
                    SpanId = $"S{i + 1}",
                    SpanIndex = i,
                    Length = rightSupport.Position - leftSupport.Position,
                    ClearLength = rightSupport.Position - leftSupport.Position
                                  - (leftSupport.Width / 1000.0 / 2)
                                  - (rightSupport.Width / 1000.0 / 2),
                    Width = segments.Count > 0 ? segments.Average(b => b.Width) : group.Width,
                    Height = spanHeight,
                    LeftSupportId = leftSupport.SupportId,
                    RightSupportId = rightSupport.SupportId,
                    IsStepChange = isStepChange,
                    HeightDifference = spanHeight - prevHeight,
                    IsConsole = leftSupport.IsFreeEnd || rightSupport.IsFreeEnd
                };

                // Thêm physical segments
                foreach (var seg in segments)
                {
                    span.Segments.Add(new PhysicalSegment
                    {
                        EntityHandle = seg.Handle,
                        Length = seg.Length / 1000.0,
                        StartPoint = new[] { seg.StartX, seg.StartY },
                        EndPoint = new[] { seg.EndX, seg.EndY }
                    });
                }

                group.Spans.Add(span);

                if (isStepChange) group.HasStepChange = true;
                prevHeight = spanHeight;
            }
        }

        /// <summary>
        /// Tìm các đoạn dầm vật lý thuộc nhịp logic (dựa theo vị trí tọa độ).
        /// Đoạn dầm thuộc nhịp nếu phần lớn chiều dài nằm giữa startPos và endPos.
        /// </summary>
        private static List<BeamData> FindSegmentsInSpan(
            List<BeamData> chain,
            double startPos,
            double endPos)
        {
            if (chain == null || chain.Count == 0)
                return new List<BeamData>();

            // Xác định hướng chính của chain (X hay Y)
            var first = chain.First();
            var last = chain.Last();

            // Tính điểm gốc của chain (điểm đầu tiên)
            double originX = first.StartX;
            double originY = first.StartY;

            // Vector hướng chạy dầm
            double chainDX = last.EndX - first.StartX;
            double chainDY = last.EndY - first.StartY;
            double chainLength = Math.Sqrt(chainDX * chainDX + chainDY * chainDY);

            if (chainLength < 1) // Tránh chia cho 0
                return chain.ToList();

            // Normalize direction vector
            double dirX = chainDX / chainLength;
            double dirY = chainDY / chainLength;

            var result = new List<BeamData>();

            foreach (var beam in chain)
            {
                // Project điểm đầu và cuối của beam lên trục chain
                double beamStartProj = ProjectPointOntoLine(
                    beam.StartX, beam.StartY,
                    originX, originY,
                    dirX, dirY);

                double beamEndProj = ProjectPointOntoLine(
                    beam.EndX, beam.EndY,
                    originX, originY,
                    dirX, dirY);

                // Đảm bảo beamStartProj < beamEndProj
                double segStart = Math.Min(beamStartProj, beamEndProj);
                double segEnd = Max(beamStartProj, beamEndProj);

                // Convert từ mm sang m để so sánh với startPos/endPos (đơn vị m)
                segStart /= 1000.0;
                segEnd /= 1000.0;

                // Kiểm tra overlap với [startPos, endPos]
                // Đoạn dầm thuộc nhịp nếu có phần chung > 50% chiều dài đoạn
                double overlapStart = Math.Max(segStart, startPos);
                double overlapEnd = Math.Min(segEnd, endPos);
                double overlap = Math.Max(0, overlapEnd - overlapStart);

                double segLength = segEnd - segStart;
                if (segLength > 0.001) // Tránh chia cho 0
                {
                    double overlapRatio = overlap / segLength;

                    // Nếu > 50% đoạn dầm nằm trong nhịp -> thuộc nhịp này
                    if (overlapRatio > 0.5)
                    {
                        result.Add(beam);
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Project điểm P lên đường thẳng đi qua Origin theo hướng Dir.
        /// Trả về khoảng cách từ Origin đến điểm chiếu (có dấu).
        /// </summary>
        private static double ProjectPointOntoLine(
            double px, double py,
            double originX, double originY,
            double dirX, double dirY)
        {
            // Vector từ origin đến point
            double dx = px - originX;
            double dy = py - originY;

            // Dot product
            return dx * dirX + dy * dirY;
        }

        private static double Max(double a, double b) => a > b ? a : b;

        /// <summary>
        /// Nhóm các dầm còn lại không nằm trên trục
        /// </summary>
        private static List<BeamGroup> ChainRemainingBeams(
            List<BeamData> beams,
            List<SupportEntity> supports,
            DtsSettings settings)
        {
            var groups = new List<BeamGroup>();

            // Đơn giản: Mỗi dầm đơn lẻ = 1 group với 1 span
            foreach (var beam in beams)
            {
                var group = new BeamGroup
                {
                    GroupName = $"B_{beam.Name}",
                    GroupType = "Beam",
                    Direction = Math.Abs(beam.EndY - beam.StartY) > Math.Abs(beam.EndX - beam.StartX) ? "Y" : "X",
                    Width = beam.Width,
                    Height = beam.Height,
                    TotalLength = beam.Length / 1000.0,
                    Source = "Auto",
                    EntityHandles = new List<string> { beam.Handle }
                };

                // Thêm 2 supports (đầu và cuối)
                group.Supports.Add(new SupportData { SupportId = "S1", SupportIndex = 0, Type = SupportType.FreeEnd, Position = 0 });
                group.Supports.Add(new SupportData { SupportId = "S2", SupportIndex = 1, Type = SupportType.FreeEnd, Position = group.TotalLength });

                // Thêm 1 span
                group.Spans.Add(new SpanData
                {
                    SpanId = "S1",
                    SpanIndex = 0,
                    Length = group.TotalLength,
                    Width = beam.Width,
                    Height = beam.Height,
                    LeftSupportId = "S1",
                    RightSupportId = "S2"
                });

                groups.Add(group);
            }

            return groups;
        }

        /// <summary>
        /// Sinh tên nhóm theo quy tắc: GX-B x (1-11)
        /// </summary>
        private static string GenerateGroupName(BeamGroup group, AxisLine axis)
        {
            string prefix = group.GroupType == "Girder" ? "G" : "B";
            string dir = group.Direction;
            string axisName = axis?.Name ?? "?";

            if (group.Supports.Count >= 2)
            {
                string start = group.Supports.First().GridName ?? "1";
                string end = group.Supports.Last().GridName ?? group.Supports.Count.ToString();
                return $"{prefix}{dir}-{axisName} x ({start}-{end})";
            }

            return $"{prefix}{dir}-{axisName}";
        }
    }

    // ===== HELPER CLASSES =====

    /// <summary>
    /// Dữ liệu dầm đơn giản (từ CAD/SAP)
    /// </summary>
    public class BeamData
    {
        public string Handle { get; set; }
        public string Name { get; set; }
        public double StartX { get; set; }
        public double StartY { get; set; }
        public double EndX { get; set; }
        public double EndY { get; set; }
        public double Width { get; set; }  // mm
        public double Height { get; set; } // mm
        public double Length => Math.Sqrt(Math.Pow(EndX - StartX, 2) + Math.Pow(EndY - StartY, 2));
    }

    /// <summary>
    /// Gối đỡ entity (Column, Wall, Beam)
    /// </summary>
    public class SupportEntity
    {
        public string Handle { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }   // "Column", "Wall", "Beam"
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Width { get; set; }  // mm (theo hướng X)
        public double Depth { get; set; }  // mm (theo hướng Y)
        public string GridName { get; set; }
    }
}
