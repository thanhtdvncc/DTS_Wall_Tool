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
        private const double COLLINEAR_TOLERANCE = 50; // mm - Tolerance cho dầm nối tiếp (Node distance)
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
            List<BeamGeometry> beams,
            List<AxisLine> axes,
            List<SupportGeometry> supports,
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
        private static List<BeamGeometry> QueryBeamsInBuffer(List<BeamGeometry> beams, AxisLine axis, double tolerance)
        {
            return beams.Where(b => IsBeamOnAxis(b, axis, tolerance)).ToList();
        }

        /// <summary>
        /// Kiểm tra dầm có nằm trên trục không
        /// </summary>
        private static bool IsBeamOnAxis(BeamGeometry beam, AxisLine axis, double tolerance)
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
        private static List<List<BeamGeometry>> ChainCollinearBeams(List<BeamGeometry> beams)
        {
            var chains = new List<List<BeamGeometry>>();
            var used = new HashSet<string>();

            // Sắp xếp theo vị trí (X hoặc Y tùy hướng)
            var sorted = beams.OrderBy(b => b.StartX + b.StartY).ToList();

            foreach (var beam in sorted)
            {
                if (used.Contains(beam.Handle)) continue;

                var chain = new List<BeamGeometry> { beam };
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
        private static bool AreBeamsConnected(BeamGeometry b1, BeamGeometry b2)
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
            List<BeamGeometry> chain,
            AxisLine axis,
            List<SupportGeometry> allSupports,
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

            // === SMART NAMING: Populate LevelZ for story matching ===
            // Get Z from first beam's StartZ
            group.LevelZ = chain.First().StartZ;

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
        /// Nhận diện gối đỡ (Column, Wall, Girder) cho nhóm dầm.
        /// SIMPLIFIED NODE-BASED LOGIC:
        /// - Duyệt qua các Nút (đầu/cuối mỗi đoạn dầm)
        /// - Tại mỗi nút: Check va chạm Column/Wall → Girder → FreeEnd
        /// </summary>
        public static void DetectSupports(BeamGroup group, List<BeamGeometry> chain, List<SupportGeometry> allSupports)
        {
            const double NODE_HIT_TOLERANCE = 50; // mm - Vùng hit test tại node

            var foundSupports = new List<SupportData>();
            var processedPositions = new HashSet<double>();

            // Điểm gốc của chain (để tính Position)
            double originX = chain.First().StartX;
            double originY = chain.First().StartY;

            // ===== COLLECT ALL NODES =====
            var nodes = new List<(double X, double Y, double Position, bool IsStart)>();
            double cumLen = 0;

            foreach (var beam in chain)
            {
                // Add start node
                nodes.Add((beam.StartX, beam.StartY, cumLen, true));
                cumLen += beam.Length / 1000.0; // mm to m
                // Add end node
                nodes.Add((beam.EndX, beam.EndY, cumLen, false));
            }

            // Remove duplicate nodes (internal joints)
            var uniqueNodes = new List<(double X, double Y, double Position)>();
            foreach (var node in nodes)
            {
                bool isDuplicate = uniqueNodes.Any(n =>
                    Distance(n.X, n.Y, node.X, node.Y) < NODE_HIT_TOLERANCE);
                if (!isDuplicate)
                {
                    uniqueNodes.Add((node.X, node.Y, node.Position));
                }
            }

            // ===== CHECK EACH UNIQUE NODE =====
            foreach (var node in uniqueNodes)
            {
                double roundedPos = Math.Round(node.Position * 100) / 100;
                if (processedPositions.Contains(roundedPos)) continue;

                // STEP 1: Check Column/Wall hit
                var hitSupport = allSupports.FirstOrDefault(s =>
                    Distance(s.CenterX, s.CenterY, node.X, node.Y) < NODE_HIT_TOLERANCE + s.Width / 2 &&
                    (s.Type?.ToUpper() == "COLUMN" || s.Type?.ToUpper() == "WALL"));

                if (hitSupport != null)
                {
                    // Found Column/Wall at this node
                    foundSupports.Add(new SupportData
                    {
                        SupportId = hitSupport.Name ?? $"S{foundSupports.Count + 1}",
                        Type = hitSupport.Type?.ToUpper() == "WALL" ? SupportType.Wall : SupportType.Column,
                        Width = hitSupport.Width,
                        Position = node.Position,
                        GridName = hitSupport.GridName ?? "",
                        EntityHandle = hitSupport.Handle
                    });
                    processedPositions.Add(roundedPos);
                    continue;
                }

                // STEP 2: Check Girder hit (different beam crossing this node)
                var hitGirder = allSupports.FirstOrDefault(s =>
                    Distance(s.CenterX, s.CenterY, node.X, node.Y) < NODE_HIT_TOLERANCE + 200 &&
                    s.Type?.ToUpper() == "BEAM");

                if (hitGirder != null)
                {
                    // Found Girder support at this node
                    foundSupports.Add(new SupportData
                    {
                        SupportId = $"G{foundSupports.Count + 1}",
                        Type = SupportType.Beam,
                        Width = hitGirder.Width,
                        Position = node.Position,
                        GridName = "",
                        EntityHandle = hitGirder.Handle
                    });
                    processedPositions.Add(roundedPos);
                    continue;
                }

                // STEP 3: Nothing found = FreeEnd
                // Only mark FreeEnd at chain endpoints (first and last nodes)
                bool isChainEndpoint =
                    Math.Abs(node.Position) < 0.01 ||
                    Math.Abs(node.Position - group.TotalLength) < 0.01;

                if (isChainEndpoint)
                {
                    foundSupports.Add(new SupportData
                    {
                        SupportId = node.Position < 0.01 ? "FE_Start" : "FE_End",
                        Type = SupportType.FreeEnd,
                        Width = 0,
                        Position = node.Position
                    });
                    processedPositions.Add(roundedPos);
                    group.HasConsole = true;
                }
                // Internal joints without support = just structural joints, not supports
            }

            // Sort by position
            foundSupports = foundSupports.OrderBy(s => s.Position).ToList();

            // Index supports
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
        private static void CreateSpans(BeamGroup group, List<BeamGeometry> chain, DtsSettings settings)
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
        private static List<BeamGeometry> FindSegmentsInSpan(
            List<BeamGeometry> chain,
            double startPos,
            double endPos)
        {
            if (chain == null || chain.Count == 0)
                return new List<BeamGeometry>();

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

            var result = new List<BeamGeometry>();

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
            List<BeamGeometry> beams,
            List<SupportGeometry> supports,
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

    // ===== HELPER CLASSES MOVED TO Core/Data =====
    // BeamGeometry → Core.Data.BeamGeometry
    // SupportGeometry → Core.Data.SupportGeometry
    // ISO 25010: Maintainability - Centralized DTOs for reusability
}

