using DTS_Engine.Core.Primitives;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// In-Memory Database l?u thông tin hình h?c và tr?c ??a ph??ng c?a các ph?n t? SAP2000.
    /// Gi?m thi?u s? l?n g?i API SAP2000 b?ng cách cache d? li?u.
    /// 
    /// CHI?N L??C:
    /// - Kh?i t?o 1 l?n khi b?t ??u Audit
    /// - L?u tr?: Name -> ElementInfo (ch?a 3 Vector tr?c ??a ph??ng)
    /// - Lookup nhanh O(1) khi x? lý t?ng t?i tr?ng
    /// </summary>
    public class ModelInventory
    {
        #region Data Structures

        /// <summary>
        /// Thông tin hình h?c và tr?c ??a ph??ng c?a m?t ph?n t?
        /// </summary>
        public class ElementInfo
        {
            /// <summary>Tên ph?n t?</summary>
            public string Name { get; set; }

            /// <summary>Lo?i ph?n t?: Frame, Area, Point</summary>
            public string ElementType { get; set; }

            /// <summary>Vector tr?c ??a ph??ng 1 (Global coordinates)</summary>
            public Vector3D LocalAxis1 { get; set; }

            /// <summary>Vector tr?c ??a ph??ng 2 (Global coordinates)</summary>
            public Vector3D LocalAxis2 { get; set; }

            /// <summary>Vector tr?c ??a ph??ng 3 (Global coordinates)</summary>
            public Vector3D LocalAxis3 { get; set; }

            /// <summary>Chi?u dài (Frame only, mm)</summary>
            public double Length { get; set; }

            /// <summary>Di?n tích (Area only, mm²)</summary>
            public double Area { get; set; }

            /// <summary>T?a ?? (Point only)</summary>
            public Vector3D Coordinate { get; set; }

            /// <summary>Cao ?? trung bình (mm)</summary>
            public double AverageZ { get; set; }

            public override string ToString()
            {
                return $"{ElementType}[{Name}]: L1={LocalAxis1}, L2={LocalAxis2}, L3={LocalAxis3}";
            }
        }

        #endregion

        #region Fields

        private Dictionary<string, ElementInfo> _elements;
        private bool _isBuilt = false;

        #endregion

        #region Constructor

        public ModelInventory()
        {
            _elements = new Dictionary<string, ElementInfo>(StringComparer.OrdinalIgnoreCase);
        }

        #endregion

        #region Build Methods

        /// <summary>
        /// Xây d?ng toàn b? Inventory t? model SAP2000
        /// G?I 1 L?N DUY NH?T khi b?t ??u Audit
        /// </summary>
        public void Build()
        {
            if (_isBuilt)
            {
                Debug.WriteLine("[ModelInventory] Already built, skipping...");
                return;
            }

            var sw = Stopwatch.StartNew();
            Debug.WriteLine("[ModelInventory] Building inventory...");

            _elements.Clear();

            // Build Frames
            BuildFrames();

            // Build Areas
            BuildAreas();

            // Build Points
            BuildPoints();

            sw.Stop();
            _isBuilt = true;

            Debug.WriteLine($"[ModelInventory] Build completed in {sw.ElapsedMilliseconds}ms. Total elements: {_elements.Count}");
        }

        /// <summary>
        /// Xây d?ng thông tin Frame
        /// </summary>
        private void BuildFrames()
        {
            try
            {
                var frames = SapUtils.GetAllFramesGeometry();
                Debug.WriteLine($"[ModelInventory] Processing {frames.Count} frames...");

                int successCount = 0;
                int failCount = 0;

                foreach (var frame in frames)
                {
                    try
                    {
                        var vectors = SapUtils.GetElementVectors(frame.Name);
                        if (vectors == null)
                        {
                            failCount++;
                            continue;
                        }

                        _elements[frame.Name] = new ElementInfo
                        {
                            Name = frame.Name,
                            ElementType = "Frame",
                            LocalAxis1 = vectors.Value.L1,
                            LocalAxis2 = vectors.Value.L2,
                            LocalAxis3 = vectors.Value.L3,
                            Length = frame.Length2D,
                            AverageZ = frame.AverageZ
                        };

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ModelInventory] Failed to process frame {frame.Name}: {ex.Message}");
                        failCount++;
                    }
                }

                Debug.WriteLine($"[ModelInventory] Frames: {successCount} success, {failCount} failed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelInventory] BuildFrames error: {ex.Message}");
            }
        }

        /// <summary>
        /// Xây d?ng thông tin Area
        /// </summary>
        private void BuildAreas()
        {
            try
            {
                var areas = SapUtils.GetAllAreasGeometry();
                Debug.WriteLine($"[ModelInventory] Processing {areas.Count} areas...");

                int successCount = 0;
                int failCount = 0;

                foreach (var area in areas)
                {
                    try
                    {
                        var vectors = SapUtils.GetElementVectors(area.Name);
                        if (vectors == null)
                        {
                            failCount++;
                            continue;
                        }

                        _elements[area.Name] = new ElementInfo
                        {
                            Name = area.Name,
                            ElementType = "Area",
                            LocalAxis1 = vectors.Value.L1,
                            LocalAxis2 = vectors.Value.L2,
                            LocalAxis3 = vectors.Value.L3,
                            Area = area.Area, // FIXED: Use Area not AreaValue
                            AverageZ = area.AverageZ
                        };

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ModelInventory] Failed to process area {area.Name}: {ex.Message}");
                        failCount++;
                    }
                }

                Debug.WriteLine($"[ModelInventory] Areas: {successCount} success, {failCount} failed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelInventory] BuildAreas error: {ex.Message}");
            }
        }

        /// <summary>
        /// Xây d?ng thông tin Point
        /// </summary>
        private void BuildPoints()
        {
            try
            {
                var points = SapUtils.GetAllPoints();
                Debug.WriteLine($"[ModelInventory] Processing {points.Count} points...");

                int successCount = 0;

                foreach (var pt in points)
                {
                    try
                    {
                        // Points th??ng dùng Global coordinates, nh?ng v?n c?n l?u ?? tra c?u
                        _elements[pt.Name] = new ElementInfo
                        {
                            Name = pt.Name,
                            ElementType = "Point",
                            LocalAxis1 = Vector3D.UnitX,
                            LocalAxis2 = Vector3D.UnitY,
                            LocalAxis3 = Vector3D.UnitZ,
                            Coordinate = new Vector3D(pt.X, pt.Y, pt.Z),
                            AverageZ = pt.Z
                        };

                        successCount++;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[ModelInventory] Failed to process point {pt.Name}: {ex.Message}");
                    }
                }

                Debug.WriteLine($"[ModelInventory] Points: {successCount} success");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ModelInventory] BuildPoints error: {ex.Message}");
            }
        }

        #endregion

        #region Lookup Methods

        /// <summary>
        /// Tra c?u thông tin ph?n t? theo tên
        /// </summary>
        public ElementInfo GetElement(string elementName)
        {
            if (string.IsNullOrEmpty(elementName))
                return null;

            _elements.TryGetValue(elementName, out var info);
            return info;
        }

        /// <summary>
        /// Ki?m tra ph?n t? có t?n t?i không
        /// </summary>
        public bool Contains(string elementName)
        {
            return !string.IsNullOrEmpty(elementName) && _elements.ContainsKey(elementName);
        }

        /// <summary>
        /// L?y Vector tr?c ??a ph??ng theo s? th? t? (1, 2, ho?c 3)
        /// </summary>
        public Vector3D? GetLocalAxis(string elementName, int axisNumber)
        {
            var info = GetElement(elementName);
            if (info == null)
                return null;

            switch (axisNumber)
            {
                case 1: return info.LocalAxis1;
                case 2: return info.LocalAxis2;
                case 3: return info.LocalAxis3;
                default: return null;
            }
        }

        /// <summary>
        /// L?y t?t c? thông tin
        /// </summary>
        public IEnumerable<ElementInfo> GetAllElements()
        {
            return _elements.Values;
        }

        /// <summary>
        /// ??m s? ph?n t? theo lo?i
        /// </summary>
        public int CountByType(string elementType)
        {
            int count = 0;
            foreach (var elem in _elements.Values)
            {
                if (elem.ElementType.Equals(elementType, StringComparison.OrdinalIgnoreCase))
                    count++;
            }
            return count;
        }

        #endregion

        #region Statistics

        /// <summary>
        /// L?y th?ng kê Inventory
        /// </summary>
        public string GetStatistics()
        {
            int frameCount = CountByType("Frame");
            int areaCount = CountByType("Area");
            int pointCount = CountByType("Point");

            return $"Inventory: {frameCount} Frames, {areaCount} Areas, {pointCount} Points (Total: {_elements.Count})";
        }

        #endregion

        #region Reset

        /// <summary>
        /// Xóa toàn b? d? li?u (dùng khi model thay ??i)
        /// </summary>
        public void Reset()
        {
            _elements.Clear();
            _isBuilt = false;
            Debug.WriteLine("[ModelInventory] Reset completed");
        }

        #endregion
    }
}
