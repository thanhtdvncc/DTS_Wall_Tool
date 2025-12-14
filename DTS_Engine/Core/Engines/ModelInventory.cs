using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// In-Memory Database lưu thông tin hình học và trục địa phương.
    /// [v6.0 REFACTORED]: Segregated Storage - Tách riêng Frame/Area/Point để tránh xung đột tên.
    /// SAP2000 cho phép Frame "240" và Point "240" tồn tại đồng thời.
    /// </summary>
    public class ModelInventory
    {
        #region Data Structures

        public class ElementInfo
        {
            public string Name { get; set; }
            public string ElementType { get; set; }
            public Vector3D LocalAxis1 { get; set; } // Trục 1 (Đỏ)
            public Vector3D LocalAxis2 { get; set; } // Trục 2 (Trắng/Xanh lá)
            public Vector3D LocalAxis3 { get; set; } // Trục 3 (Xanh dương - Pháp tuyến)
            public double Length { get; set; }
            public double Area { get; set; }
            public double AverageZ { get; set; }
            public double MinZ { get; set; }
            public double MaxZ { get; set; }
            public bool IsVertical { get; set; }
            public string GlobalAxisName { get; set; } // "Global +Z", "Global -X", etc.
            public int DirectionSign { get; set; } // +1 or -1

            // Full Geometry Objects for Audit Engine
            public SapFrame FrameGeometry { get; set; }
            public SapArea AreaGeometry { get; set; }
            public SapUtils.SapPoint PointGeometry { get; set; }

            public double GetStoryElevation()
            {
                // [FIX v8.1] SAP2000 Convention: Vertical elements belong to Top story
                return IsVertical ? MaxZ : AverageZ;
            }
        }

        #endregion

        #region Segregated Storage - v6.0

        // =============================================================
        // SEGREGATED STORAGE: Tách riêng các kho lưu trữ
        // Không dùng chung key string để tránh xung đột tên
        // =============================================================
        private Dictionary<string, ElementInfo> _frames;
        private Dictionary<string, ElementInfo> _areas;
        private Dictionary<string, ElementInfo> _points;

        private bool _isBuilt = false;

        #endregion

        public ModelInventory()
        {
            _frames = new Dictionary<string, ElementInfo>(StringComparer.OrdinalIgnoreCase);
            _areas = new Dictionary<string, ElementInfo>(StringComparer.OrdinalIgnoreCase);
            _points = new Dictionary<string, ElementInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public void Build()
        {
            if (_isBuilt) return;
            var sw = Stopwatch.StartNew();

            // Reset sạch sẽ
            _frames.Clear();
            _areas.Clear();
            _points.Clear();

            BuildFrames();
            BuildAreas();
            BuildPoints();

            sw.Stop();
            _isBuilt = true;
            Debug.WriteLine($"[Inventory] Built: {_frames.Count} Frames, {_areas.Count} Areas, {_points.Count} Points in {sw.ElapsedMilliseconds}ms.");
        }

        #region Build Methods

        private void BuildFrames()
        {
            var rawFrames = SapUtils.GetAllFramesGeometry();
            Debug.WriteLine($"[BuildFrames] GetAllFramesGeometry returned {rawFrames.Count} frames");

            foreach (var f in rawFrames)
            {
                // Luôn lấy vector từ API, không tự tính
                var vectors = SapUtils.GetElementVectors(f.Name);

                Vector3D v1 = vectors?.L1 ?? Vector3D.UnitX;
                Vector3D v2 = vectors?.L2 ?? Vector3D.UnitY;
                Vector3D v3 = vectors?.L3 ?? Vector3D.UnitZ;

                // Determine Global Axis from L3 (Normal)
                AnalyzeGlobalAxis(v3, out string axisName, out int sign);

                // [v6.0] Sử dụng VECTOR MATH để phân loại Beam/Column
                // Thay vì dùng Length2D có thể bị ảnh hưởng bởi đơn vị
                bool isVertical = DetermineFrameVerticalByVector(v3, f);

                var info = new ElementInfo
                {
                    Name = f.Name,
                    ElementType = "Frame",
                    LocalAxis1 = v1,
                    LocalAxis2 = v2,
                    LocalAxis3 = v3,
                    Length = f.Length3D, // Use 3D length
                    AverageZ = f.AverageZ,
                    MinZ = Math.Min(f.Z1, f.Z2),
                    MaxZ = Math.Max(f.Z1, f.Z2),
                    IsVertical = isVertical,
                    GlobalAxisName = axisName,
                    DirectionSign = sign,
                    FrameGeometry = f
                };

                // Lưu vào kho Frame riêng biệt
                if (!_frames.ContainsKey(f.Name))
                    _frames.Add(f.Name, info);
            }
        }

        private void BuildAreas()
        {
            var rawAreas = SapUtils.GetAllAreasGeometry();

            foreach (var a in rawAreas)
            {
                var vectors = SapUtils.GetElementVectors(a.Name);

                Vector3D l1 = vectors?.L1 ?? Vector3D.UnitX;
                Vector3D l2 = vectors?.L2 ?? Vector3D.UnitY;
                Vector3D l3 = vectors?.L3 ?? Vector3D.UnitZ;

                AnalyzeGlobalAxis(l3, out string axisName, out int sign);

                // Xác định hướng đứng/ngang dựa trên pháp tuyến thật (L3)
                // L3 ~ (0,0,1) -> Sàn ngang. L3 ~ (1,0,0) hoặc (0,1,0) -> Vách đứng.
                bool isVertical = Math.Abs(l3.Z) < AuditConfig.VERTICAL_AXIS_THRESHOLD;

                double minZ = a.ZValues.Count > 0 ? a.ZValues.Min() : a.AverageZ;
                double maxZ = a.ZValues.Count > 0 ? a.ZValues.Max() : a.AverageZ;

                var info = new ElementInfo
                {
                    Name = a.Name,
                    ElementType = "Area",
                    LocalAxis1 = l1,
                    LocalAxis2 = l2,
                    LocalAxis3 = l3,
                    Area = a.Area,
                    AverageZ = a.AverageZ,
                    MinZ = minZ,
                    MaxZ = maxZ,
                    IsVertical = isVertical,
                    GlobalAxisName = axisName,
                    DirectionSign = sign,
                    AreaGeometry = a
                };

                // Lưu vào kho Area riêng biệt - KHÔNG CÒN LO GHI ĐÈ FRAME
                if (!_areas.ContainsKey(a.Name))
                    _areas.Add(a.Name, info);
            }
        }

        private void BuildPoints()
        {
            var rawPoints = SapUtils.GetAllPoints();

            foreach (var p in rawPoints)
            {
                var vectors = SapUtils.GetElementVectors(p.Name);

                Vector3D v3 = vectors?.L3 ?? Vector3D.UnitZ;
                AnalyzeGlobalAxis(v3, out string axisName, out int sign);

                var info = new ElementInfo
                {
                    Name = p.Name,
                    ElementType = "Point",
                    LocalAxis1 = vectors?.L1 ?? Vector3D.UnitX,
                    LocalAxis2 = vectors?.L2 ?? Vector3D.UnitY,
                    LocalAxis3 = v3,
                    AverageZ = p.Z,
                    MinZ = p.Z,
                    MaxZ = p.Z,
                    IsVertical = false,
                    GlobalAxisName = axisName,
                    DirectionSign = sign,
                    PointGeometry = p
                };

                // Lưu vào kho Point riêng biệt - KHÔNG CÒN LO GHI ĐÈ FRAME
                if (!_points.ContainsKey(p.Name))
                    _points.Add(p.Name, info);
            }
        }

        #endregion

        #region Helper Methods

        /// <summary>
        /// [v6.0] Phân loại Frame là Vertical (Column) hay Horizontal (Beam) bằng VECTOR MATH
        /// Thay vì dùng Length2D có thể bị ảnh hưởng bởi đơn vị model.
        /// 
        /// Logic: Trục L1 của Frame luôn chạy dọc theo chiều dài Frame.
        /// - Nếu L1.Z > threshold (0.5) → Frame nghiêng/đứng → Column
        /// - Nếu L1.Z < threshold → Frame nằm ngang → Beam
        /// </summary>
        private bool DetermineFrameVerticalByVector(Vector3D l3, SapFrame frame)
        {
            // Lấy trục L1 (trục dọc theo chiều dài Frame)
            var vectors = SapUtils.GetElementVectors(frame.Name);
            Vector3D l1 = vectors?.L1 ?? Vector3D.UnitX;

            // Nếu L1.Z (hướng đứng của trục dọc) lớn → Frame đứng/nghiêng
            // Threshold 0.5 tương đương góc ~60 độ so với phương ngang
            if (Math.Abs(l1.Z) > AuditConfig.VERTICAL_AXIS_THRESHOLD)
            {
                return true; // Column/Vertical Frame
            }

            return false; // Beam/Horizontal Frame
        }

        private void AnalyzeGlobalAxis(Vector3D l3, out string axisName, out int sign)
        {
            double gx = l3.X;
            double gy = l3.Y;
            double gz = l3.Z;

            double threshold = AuditConfig.STRICT_AXIS_THRESHOLD;

            if (Math.Abs(gx) > threshold)
            {
                axisName = gx > 0 ? "Global +X" : "Global -X";
                sign = gx > 0 ? 1 : -1;
            }
            else if (Math.Abs(gy) > threshold)
            {
                axisName = gy > 0 ? "Global +Y" : "Global -Y";
                sign = gy > 0 ? 1 : -1;
            }
            else if (Math.Abs(gz) > threshold)
            {
                axisName = gz > 0 ? "Global +Z" : "Global -Z";
                sign = gz > 0 ? 1 : -1;
            }
            else
            {
                axisName = "Mix";
                sign = 1; // Default
            }
        }

        #endregion

        #region Public API - Type-Safe Access (v6.0)

        /// <summary>
        /// Lấy thông tin Frame (Dầm/Cột)
        /// </summary>
        public ElementInfo GetFrame(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _frames.TryGetValue(name, out var info);
            return info;
        }

        /// <summary>
        /// Lấy thông tin Area (Sàn/Vách)
        /// </summary>
        public ElementInfo GetArea(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _areas.TryGetValue(name, out var info);
            return info;
        }

        /// <summary>
        /// Lấy thông tin Point (Nút)
        /// </summary>
        public ElementInfo GetPoint(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _points.TryGetValue(name, out var info);
            return info;
        }

        /// <summary>
        /// [Legacy Support] Lấy phần tử bất kỳ.
        /// Thứ tự ưu tiên: Frame > Area > Point
        /// Dùng cho các hàm cũ chưa biết rõ type.
        /// </summary>
        public ElementInfo GetElement(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (_frames.TryGetValue(name, out var f)) return f;
            if (_areas.TryGetValue(name, out var a)) return a;
            if (_points.TryGetValue(name, out var p)) return p;

            return null;
        }

        public Vector3D? GetLocalAxis(string elementName, int axisNumber)
        {
            var info = GetElement(elementName);
            if (info == null) return null;
            switch (axisNumber)
            {
                case 1: return info.LocalAxis1;
                case 2: return info.LocalAxis2;
                case 3: return info.LocalAxis3;
                default: return null;
            }
        }

        #endregion

        #region Statistics

        public int FrameCount => _frames.Count;
        public int AreaCount => _areas.Count;
        public int PointCount => _points.Count;
        public int Count => _frames.Count + _areas.Count + _points.Count;

        public string GetStatistics() => $"Inventory: {_frames.Count} Frames, {_areas.Count} Areas, {_points.Count} Points (Segregated Storage v6.0).";

        public void Reset()
        {
            _frames.Clear();
            _areas.Clear();
            _points.Clear();
            _isBuilt = false;
        }

        #endregion
    }
}