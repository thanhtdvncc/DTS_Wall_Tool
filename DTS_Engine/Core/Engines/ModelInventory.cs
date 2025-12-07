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
    /// FIX v2.6: Tính toán Vector trực tiếp từ Geometry (Pure Geometry Approach).
    /// </summary>
    public class ModelInventory
    {
        #region Data Structures

        /// <summary>
        /// Element info với thông tin cao độ chi tiết hơn
        /// </summary>
        public class ElementInfo
        {
            public string Name { get; set; }
            public string ElementType { get; set; }
            public Vector3D LocalAxis1 { get; set; }
            public Vector3D LocalAxis2 { get; set; }
            public Vector3D LocalAxis3 { get; set; } // Normal Vector
            public double Length { get; set; } // mm
            public double Area { get; set; }   // mm2
            public double AverageZ { get; set; }
            public double MinZ { get; set; }
            public double MaxZ { get; set; }
            public bool IsVertical { get; set; }
            
            /// <summary>
            /// Lấy cao độ phù hợp cho việc phân tầng
            /// - Với cột/tường đứng: Dùng MinZ (chân cột)
            /// - Với dầm/sàn ngang: Dùng AverageZ
            /// </summary>
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
            BuildAreasOptimized(); // Logic mới

            sw.Stop();
            _isBuilt = true;
            Debug.WriteLine($"[ModelInventory] Build completed in {sw.ElapsedMilliseconds}ms. Total: {_elements.Count}");
        }

        private void BuildFrames()
        {
            var frames = SapUtils.GetAllFramesGeometry();
            foreach (var f in frames)
            {
                // Tính Vector trục dọc thanh (Local 1)
                var vec1 = new Vector3D(f.EndPt.X - f.StartPt.X, f.EndPt.Y - f.StartPt.Y, f.Z2 - f.Z1).Normalized;
                
                // Giả định trục 2 & 3 cho Frame (Thường Local 2 hướng lên cho dầm)
                Vector3D vec2, vec3;
                bool isVertical = Math.Abs(vec1.Z) > 0.99; // Cột thẳng đứng
                
                if (isVertical)
                {
                    vec2 = new Vector3D(1, 0, 0); 
                    vec3 = new Vector3D(0, 1, 0); 
                }
                else // Dầm
                {
                    vec2 = new Vector3D(0, 0, 1); 
                    vec3 = vec1.Cross(vec2).Normalized; 
                    vec2 = vec3.Cross(vec1).Normalized; 
                }

                _elements[f.Name] = new ElementInfo
                {
                    Name = f.Name,
                    ElementType = "Frame",
                    LocalAxis1 = vec1,
                    LocalAxis2 = vec2,
                    LocalAxis3 = vec3,
                    Length = f.Length2D,
                    AverageZ = f.AverageZ,
                    // FIX BUG #3: Thêm thông tin cao độ chi tiết
                    MinZ = Math.Min(f.Z1, f.Z2),
                    MaxZ = Math.Max(f.Z1, f.Z2),
                    IsVertical = isVertical
                };
            }
        }

        private void BuildPoints()
        {
            var points = SapUtils.GetAllPoints();
            foreach (var p in points)
            {
                _elements[p.Name] = new ElementInfo
                {
                    Name = p.Name,
                    ElementType = "Point",
                    LocalAxis1 = Vector3D.UnitX,
                    LocalAxis2 = Vector3D.UnitY,
                    LocalAxis3 = Vector3D.UnitZ,
                    AverageZ = p.Z,
                    MinZ = p.Z,
                    MaxZ = p.Z,
                    IsVertical = false
                };
            }
        }

        /// <summary>
        /// LOGIC CHUẨN SAP2000: Tính trục địa phương từ tọa độ 3 điểm đầu tiên.
        /// </summary>
        private void BuildAreasOptimized()
        {
            var areas = SapUtils.GetAllAreasGeometry();
            
            foreach (var area in areas)
            {
                if (area.BoundaryPoints.Count < 3 || area.ZValues.Count < 3) continue;

                // 1. Lấy tọa độ 3 điểm đầu tiên (j1, j2, j3)
                var p1 = new Vector3D(area.BoundaryPoints[0].X, area.BoundaryPoints[0].Y, area.ZValues[0]);
                var p2 = new Vector3D(area.BoundaryPoints[1].X, area.BoundaryPoints[1].Y, area.ZValues[1]);
                var p3 = new Vector3D(area.BoundaryPoints[2].X, area.BoundaryPoints[2].Y, area.ZValues[2]);

                // 2. Tính Local 3 (Normal) = V12 x V13
                var v12 = p2 - p1;
                var v13 = p3 - p1;
                var local3 = v12.Cross(v13).Normalized;

                // 3. Tính Local 1 & 2 theo quy tắc "Default Orientation"
                Vector3D local1, local2;
                Vector3D globalZ = new Vector3D(0, 0, 1);

                // Nếu tấm nằm ngang (Local 3 song song Z)
                if (Math.Abs(local3.X) < 1e-3 && Math.Abs(local3.Y) < 1e-3) 
                {
                    local2 = new Vector3D(0, 1, 0); // Local 2 = Global Y
                    local1 = local2.Cross(local3).Normalized; // V1 = V2 x V3
                }
                else // Tấm nghiêng hoặc đứng
                {
                    // Local 1 nằm ngang => Vuông góc với Z và Local 3
                    local1 = globalZ.Cross(local3).Normalized;
                    // Local 2 = Local 3 x Local 1 (để tạo tam diện thuận)
                    local2 = local3.Cross(local1).Normalized;
                }

                // Determine orientation: vertical if normal is mostly horizontal
                bool isVertical = Math.Abs(local3.Z) < 0.5;
                double minZ = area.ZValues.Count > 0 ? area.ZValues.Min() : area.AverageZ;
                double maxZ = area.ZValues.Count > 0 ? area.ZValues.Max() : area.AverageZ;

                _elements[area.Name] = new ElementInfo
                {
                    Name = area.Name,
                    ElementType = "Area",
                    LocalAxis1 = local1,
                    LocalAxis2 = local2,
                    LocalAxis3 = local3,
                    Area = area.Area,
                    AverageZ = area.AverageZ,
                    MinZ = minZ,
                    MaxZ = maxZ,
                    IsVertical = isVertical
                };
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

        public string GetStatistics() => $"Inventory: {_elements.Count} elements loaded.";
        public void Reset() { _elements.Clear(); _isBuilt = false; }
    }
}
