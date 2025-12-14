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
    /// [UPDATED]: Sử dụng API SAP2000 để lấy Vector trục thực tế, bỏ logic tính từ thứ tự điểm.
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

            // [ADDED] Full Geometry Objects for Audit Engine (replacing local caches)
            public SapFrame FrameGeometry { get; set; }
            public SapArea AreaGeometry { get; set; }
            public SapUtils.SapPoint PointGeometry { get; set; }

            public double GetStoryElevation()
            {
                return IsVertical ? MinZ : AverageZ;
            }
        }

        #endregion

        private Dictionary<string, ElementInfo> _elements;
        private bool _isBuilt = false;

        public ModelInventory()
        {
            _elements = new Dictionary<string, ElementInfo>(StringComparer.OrdinalIgnoreCase);
        }

        public void Build()
        {
            if (_isBuilt) return;
            var sw = Stopwatch.StartNew();
            _elements.Clear();

            BuildFrames();
            BuildPoints();
            BuildAreasOptimized(); // Đã cập nhật logic mới

            sw.Stop();
            _isBuilt = true;
            Debug.WriteLine($"[ModelInventory] Build completed in {sw.ElapsedMilliseconds}ms. Total: {_elements.Count}");
        }

        private void BuildFrames()
        {
            var frames = SapUtils.GetAllFramesGeometry();
            Debug.WriteLine($"[BuildFrames] GetAllFramesGeometry returned {frames.Count} frames");
            foreach (var f in frames)
            {
                // [FIX]: Luôn lấy vector từ API, không tự tính
                var vectors = SapUtils.GetElementVectors(f.Name);

                // Fallback an toàn: Nếu API lỗi, dùng trục Global mặc định (tránh crash)
                // Tuyệt đối không tự tính Cross Product từ điểm vẽ
                Vector3D v1 = vectors?.L1 ?? Vector3D.UnitX;
                Vector3D v2 = vectors?.L2 ?? Vector3D.UnitY;
                Vector3D v3 = vectors?.L3 ?? Vector3D.UnitZ;

                // Determine Global Axis from L3 (Normal) - Updated Logic
                AnalyzeGlobalAxis(v3, out string axisName, out int sign);

                _elements[f.Name] = new ElementInfo
                {
                    Name = f.Name,
                    ElementType = "Frame",
                    LocalAxis1 = v1,
                    LocalAxis2 = v2,
                    LocalAxis3 = v3,
                    Length = f.Length2D,
                    AverageZ = f.AverageZ,
                    MinZ = Math.Min(f.Z1, f.Z2),
                    MaxZ = Math.Max(f.Z1, f.Z2),
                    IsVertical = f.IsVertical,
                    GlobalAxisName = axisName,
                    DirectionSign = sign,
                    FrameGeometry = f // Store full object
                };
            }
        }

        private void BuildPoints()
        {
            var points = SapUtils.GetAllPoints();
            foreach (var p in points)
            {
                // Point cũng có thể bị xoay trục (Restraint/Load), cần lấy chính xác
                var vectors = SapUtils.GetElementVectors(p.Name);
                
                Vector3D v3 = vectors?.L3 ?? Vector3D.UnitZ;
                AnalyzeGlobalAxis(v3, out string axisName, out int sign);

                _elements[p.Name] = new ElementInfo
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
                    PointGeometry = p // Store full object
                };
            }
        }

        private void BuildAreasOptimized()
        {
            var areas = SapUtils.GetAllAreasGeometry();

            foreach (var area in areas)
            {
                // [FIX]: Xóa bỏ logic tính từ BoundaryPoints.
                // Chỉ tin tưởng vào Matrix của SAP.
                var vectors = SapUtils.GetElementVectors(area.Name);

                Vector3D l1 = vectors?.L1 ?? Vector3D.UnitX;
                Vector3D l2 = vectors?.L2 ?? Vector3D.UnitY;
                Vector3D l3 = vectors?.L3 ?? Vector3D.UnitZ; // Pháp tuyến chuẩn xác

                // Xác định hướng đứng/ngang dựa trên pháp tuyến thật (L3)
                // L3 ~ (0,0,1) -> Sàn ngang. L3 ~ (1,0,0) hoặc (0,1,0) -> Vách đứng.
                bool isVertical = Math.Abs(l3.Z) < 0.707;

                double minZ = area.ZValues.Count > 0 ? area.ZValues.Min() : area.AverageZ;
                double maxZ = area.ZValues.Count > 0 ? area.ZValues.Max() : area.AverageZ;

                // Determine Global Axis from L3 (Normal) - Updated Logic
                AnalyzeGlobalAxis(l3, out string axisName, out int sign);

                _elements[area.Name] = new ElementInfo
                {
                    Name = area.Name,
                    ElementType = "Area",
                    LocalAxis1 = l1,
                    LocalAxis2 = l2,
                    LocalAxis3 = l3,
                    Area = area.Area,
                    AverageZ = area.AverageZ,
                    MinZ = minZ,
                    MaxZ = maxZ,
                    IsVertical = isVertical,
                    GlobalAxisName = axisName,
                    DirectionSign = sign,
                    AreaGeometry = area // Store full object
                };
            }
        }

        private void AnalyzeGlobalAxis(Vector3D l3, out string axisName, out int sign)
        {
            double gx = l3.X;
            double gy = l3.Y;
            double gz = l3.Z;

            // Logic copied from SapLoadDiagnostics.DTS_DEBUG_SELECTED
            if (Math.Abs(gx) > 0.9)
            {
                axisName = gx > 0 ? "Global +X" : "Global -X";
                sign = gx > 0 ? 1 : -1;
            }
            else if (Math.Abs(gy) > 0.9)
            {
                axisName = gy > 0 ? "Global +Y" : "Global -Y";
                sign = gy > 0 ? 1 : -1;
            }
            else if (Math.Abs(gz) > 0.9)
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

        public ElementInfo GetElement(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            _elements.TryGetValue(name, out var info);
            return info;
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

        public int Count => _elements.Count;
        public string GetStatistics() => $"Inventory: {_elements.Count} elements loaded (Direct API Vectors).";
        public void Reset() { _elements.Clear(); _isBuilt = false; }
    }
}