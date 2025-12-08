using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using NetTopologySuite.Geometries;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// Stage 2: Geometry Processing - NTS-based shape analysis and decomposition.
    /// 
    /// CRITICAL DESIGN DECISION:
    /// - Quantity (numeric): Actual area in m² or length in m - used for force calculation
    /// - Explanation (text): Formula string like "4×5 + 2×3" - DISPLAY ONLY
    /// - These are calculated INDEPENDENTLY to avoid NTS failures affecting force calculations
    /// 
    /// ISO/IEC 25010: Reliability - if NTS fails, we fallback to simple area calculation
    /// </summary>
    public class GeometryProcessor
    {
        #region Constants

        private const double MIN_AREA_THRESHOLD_M2 = 0.01; // 100 cm²

        #endregion

        #region Fields

        private readonly GeometryFactory _geometryFactory;

        #endregion

        #region Constructor

        public GeometryProcessor()
        {
            _geometryFactory = new GeometryFactory(new PrecisionModel(1000)); // mm precision
        }

        #endregion

        #region Public API

        /// <summary>
        /// Process area geometry and return quantity + explanation.
        /// Quantity is always calculated from actual geometry, not from NTS decomposition.
        /// </summary>
        public GeometryResult ProcessAreaGeometry(SapArea area)
        {
            if (area == null || area.BoundaryPoints == null || area.BoundaryPoints.Count < 3)
            {
                return new GeometryResult
                {
                    Quantity = 0,
                    QuantityUnit = "m²",
                    Explanation = "(invalid geometry)",
                    GridLocation = "Unknown"
                };
            }

            // CRITICAL: Calculate actual area from boundary points directly
            double areaM2 = area.Area / 1_000_000.0; // mm² to m²

            // Calculate bounding box for grid location
            var bbox = area.BoundingBox;

            // Try NTS decomposition for explanation only
            string explanation = GenerateAreaExplanation(area);

            return new GeometryResult
            {
                Quantity = areaM2,
                QuantityUnit = "m²",
                Explanation = explanation,
                MinX = bbox.MinX,
                MaxX = bbox.MaxX,
                MinY = bbox.MinY,
                MaxY = bbox.MaxY
            };
        }

        /// <summary>
        /// Process frame geometry and return quantity + explanation.
        /// </summary>
        public GeometryResult ProcessFrameGeometry(SapFrame frame, double distStart = 0, double distEnd = 1, bool isRelative = true)
        {
            if (frame == null)
            {
                return new GeometryResult
                {
                    Quantity = 0,
                    QuantityUnit = "m",
                    Explanation = "(invalid frame)"
                };
            }

            // Calculate covered length
            double fullLengthMm = frame.Length2D;
            double startMm, endMm;

            if (isRelative)
            {
                startMm = fullLengthMm * distStart;
                endMm = fullLengthMm * distEnd;
            }
            else
            {
                startMm = distStart;
                endMm = distEnd;
            }

            double coveredLengthM = Math.Abs(endMm - startMm) / 1000.0;

            // Create explanation
            string explanation = FormatFrameExplanation(coveredLengthM, fullLengthMm / 1000.0, distStart, distEnd);

            return new GeometryResult
            {
                Quantity = coveredLengthM,
                QuantityUnit = "m",
                Explanation = explanation,
                MinX = Math.Min(frame.StartPt.X, frame.EndPt.X),
                MaxX = Math.Max(frame.StartPt.X, frame.EndPt.X),
                MinY = Math.Min(frame.StartPt.Y, frame.EndPt.Y),
                MaxY = Math.Max(frame.StartPt.Y, frame.EndPt.Y)
            };
        }

        /// <summary>
        /// Process multiple areas and union them to get combined quantity + explanation.
        /// </summary>
        public GeometryResult ProcessMultipleAreas(IEnumerable<SapArea> areas)
        {
            var areaList = areas?.ToList();
            if (areaList == null || areaList.Count == 0)
            {
                return new GeometryResult { Quantity = 0, QuantityUnit = "m²", Explanation = "(no areas)" };
            }

            if (areaList.Count == 1)
            {
                return ProcessAreaGeometry(areaList[0]);
            }

            // Calculate total area (simple sum, no union for reliability)
            double totalAreaM2 = areaList.Sum(a => a.Area / 1_000_000.0);

            // Calculate combined bounding box
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var area in areaList)
            {
                var bbox = area.BoundingBox;
                minX = Math.Min(minX, bbox.MinX);
                maxX = Math.Max(maxX, bbox.MaxX);
                minY = Math.Min(minY, bbox.MinY);
                maxY = Math.Max(maxY, bbox.MaxY);
            }

            // Generate explanation with individual areas
            string explanation = GenerateMultiAreaExplanation(areaList);

            return new GeometryResult
            {
                Quantity = totalAreaM2,
                QuantityUnit = "m²",
                Explanation = explanation,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        /// <summary>
        /// Process multiple frames and sum their lengths.
        /// </summary>
        public GeometryResult ProcessMultipleFrames(IEnumerable<FrameLoadInfo> frames)
        {
            var frameList = frames?.ToList();
            if (frameList == null || frameList.Count == 0)
            {
                return new GeometryResult { Quantity = 0, QuantityUnit = "m", Explanation = "(no frames)" };
            }

            double totalLengthM = frameList.Sum(f => f.CoveredLengthM);

            // Calculate combined bounding box
            double minX = double.MaxValue, minY = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue;

            foreach (var f in frameList)
            {
                if (f.Frame == null) continue;
                minX = Math.Min(minX, Math.Min(f.Frame.StartPt.X, f.Frame.EndPt.X));
                maxX = Math.Max(maxX, Math.Max(f.Frame.StartPt.X, f.Frame.EndPt.X));
                minY = Math.Min(minY, Math.Min(f.Frame.StartPt.Y, f.Frame.EndPt.Y));
                maxY = Math.Max(maxY, Math.Max(f.Frame.StartPt.Y, f.Frame.EndPt.Y));
            }

            // Generate explanation
            string explanation = GenerateMultiFrameExplanation(frameList);

            return new GeometryResult
            {
                Quantity = totalLengthM,
                QuantityUnit = "m",
                Explanation = explanation,
                MinX = minX,
                MaxX = maxX,
                MinY = minY,
                MaxY = maxY
            };
        }

        #endregion

        #region Private Methods - Area Explanation

        private string GenerateAreaExplanation(SapArea area)
        {
            try
            {
                var polygon = CreateNtsPolygon(area.BoundaryPoints);
                if (polygon == null || !polygon.IsValid)
                {
                    return FormatSimpleAreaExplanation(area);
                }

                // Check if it's a rectangle
                if (area.IsRectangle)
                {
                    var env = polygon.EnvelopeInternal;
                    double w = (env.MaxX - env.MinX) / 1000.0;
                    double h = (env.MaxY - env.MinY) / 1000.0;
                    return $"{w:0.##}×{h:0.##}";
                }

                // Try decomposition
                var rectangles = DecomposePolygon(polygon);
                if (rectangles.Count > 0 && rectangles.Count <= 5)
                {
                    return FormatRectangleList(rectangles);
                }

                // Fallback to simple bounding box
                return FormatSimpleAreaExplanation(area);
            }
            catch
            {
                return FormatSimpleAreaExplanation(area);
            }
        }

        private string FormatSimpleAreaExplanation(SapArea area)
        {
            var bbox = area.BoundingBox;
            double w = (bbox.MaxX - bbox.MinX) / 1000.0;
            double h = (bbox.MaxY - bbox.MinY) / 1000.0;
            double areaM2 = area.Area / 1_000_000.0;
            return $"~{w:0.#}×{h:0.#} ({areaM2:0.##}m²)";
        }

        private string GenerateMultiAreaExplanation(List<SapArea> areas)
        {
            if (areas.Count <= 3)
            {
                var parts = areas.Select(a =>
                {
                    var bbox = a.BoundingBox;
                    double w = (bbox.MaxX - bbox.MinX) / 1000.0;
                    double h = (bbox.MaxY - bbox.MinY) / 1000.0;
                    return $"{w:0.#}×{h:0.#}";
                });
                return string.Join(" + ", parts);
            }

            double totalM2 = areas.Sum(a => a.Area / 1_000_000.0);
            return $"{areas.Count}ea ({totalM2:0.##}m²)";
        }

        private List<RectangleInfo> DecomposePolygon(Polygon polygon)
        {
            var result = new List<RectangleInfo>();

            try
            {
                var envelope = polygon.EnvelopeInternal;

                // Simple case: if polygon fills most of its envelope, treat as single rectangle
                double polyArea = polygon.Area;
                double envArea = envelope.Area;
                if (polyArea / envArea > 0.95)
                {
                    result.Add(new RectangleInfo
                    {
                        Width = (envelope.MaxX - envelope.MinX) / 1000.0,
                        Height = (envelope.MaxY - envelope.MinY) / 1000.0
                    });
                    return result;
                }

                // Try grid-based decomposition
                result = TryGridDecomposition(polygon);
            }
            catch
            {
                // Return empty list on failure
            }

            return result;
        }

        private List<RectangleInfo> TryGridDecomposition(Polygon polygon)
        {
            var result = new List<RectangleInfo>();
            var envelope = polygon.EnvelopeInternal;

            // Create grid
            double cellSize = 500; // 500mm cells
            int cols = (int)Math.Ceiling((envelope.MaxX - envelope.MinX) / cellSize);
            int rows = (int)Math.Ceiling((envelope.MaxY - envelope.MinY) / cellSize);

            if (cols > 100 || rows > 100)
            {
                // Too complex, skip
                return result;
            }

            // Find rectangles using simple scan
            var usedCells = new bool[rows, cols];
            var geometryFactory = new GeometryFactory();

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    double x = envelope.MinX + c * cellSize + cellSize / 2;
                    double y = envelope.MinY + r * cellSize + cellSize / 2;
                    var point = geometryFactory.CreatePoint(new Coordinate(x, y));

                    if (polygon.Contains(point))
                    {
                        usedCells[r, c] = true;
                    }
                }
            }

            // Find connected rectangular regions (simplified)
            int rectCount = 0;
            for (int r = 0; r < rows && rectCount < 10; r++)
            {
                for (int c = 0; c < cols && rectCount < 10; c++)
                {
                    if (usedCells[r, c])
                    {
                        // Find extent of rectangle starting here
                        int maxC = c;
                        while (maxC < cols - 1 && usedCells[r, maxC + 1]) maxC++;

                        int maxR = r;
                        bool canExtend = true;
                        while (canExtend && maxR < rows - 1)
                        {
                            for (int cc = c; cc <= maxC; cc++)
                            {
                                if (!usedCells[maxR + 1, cc])
                                {
                                    canExtend = false;
                                    break;
                                }
                            }
                            if (canExtend) maxR++;
                        }

                        // Mark cells as used
                        for (int rr = r; rr <= maxR; rr++)
                        {
                            for (int cc = c; cc <= maxC; cc++)
                            {
                                usedCells[rr, cc] = false;
                            }
                        }

                        double w = (maxC - c + 1) * cellSize / 1000.0;
                        double h = (maxR - r + 1) * cellSize / 1000.0;

                        if (w * h > MIN_AREA_THRESHOLD_M2)
                        {
                            result.Add(new RectangleInfo { Width = w, Height = h });
                            rectCount++;
                        }
                    }
                }
            }

            return result;
        }

        private string FormatRectangleList(List<RectangleInfo> rectangles)
        {
            if (rectangles.Count == 0) return "";
            if (rectangles.Count == 1)
            {
                var r = rectangles[0];
                return $"{r.Width:0.##}×{r.Height:0.##}";
            }

            var parts = rectangles.Select(r => $"{r.Width:0.#}×{r.Height:0.#}");
            return string.Join("+", parts);
        }

        #endregion

        #region Private Methods - Frame Explanation

        private string FormatFrameExplanation(double coveredLengthM, double fullLengthM, double distStart, double distEnd)
        {
            if (Math.Abs(distStart) < 0.001 && Math.Abs(distEnd - 1.0) < 0.001)
            {
                // Full length
                return $"L={coveredLengthM:0.##}m";
            }

            // Partial
            return $"L={coveredLengthM:0.##}m ({distStart:0%}-{distEnd:0%})";
        }

        private string GenerateMultiFrameExplanation(List<FrameLoadInfo> frames)
        {
            double totalM = frames.Sum(f => f.CoveredLengthM);

            if (frames.Count <= 3)
            {
                var parts = frames.Select(f => $"{f.CoveredLengthM:0.##}");
                return string.Join("+", parts) + "m";
            }

            return $"{frames.Count}ea ({totalM:0.##}m)";
        }

        #endregion

        #region Private Methods - NTS Helpers

        private Polygon CreateNtsPolygon(List<Point2D> pts)
        {
            if (pts == null || pts.Count < 3) return null;

            try
            {
                // Ensure closed ring
                var coords = new List<Coordinate>();
                foreach (var p in pts)
                {
                    coords.Add(new Coordinate(p.X, p.Y));
                }
                if (coords[0].X != coords[coords.Count - 1].X ||
                    coords[0].Y != coords[coords.Count - 1].Y)
                {
                    coords.Add(new Coordinate(pts[0].X, pts[0].Y));
                }

                var ring = _geometryFactory.CreateLinearRing(coords.ToArray());
                return _geometryFactory.CreatePolygon(ring);
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region Nested Types

        private class RectangleInfo
        {
            public double Width { get; set; }
            public double Height { get; set; }
        }

        #endregion
    }

    /// <summary>
    /// Result of geometry processing - contains both quantity (for calculation) and explanation (for display).
    /// </summary>
    public struct GeometryResult
    {
        /// <summary>
        /// Numeric quantity - area in m² or length in m. Used for force calculation.
        /// </summary>
        public double Quantity { get; set; }

        /// <summary>
        /// Unit of quantity: "m²" or "m"
        /// </summary>
        public string QuantityUnit { get; set; }

        /// <summary>
        /// Text explanation like "4×5" or "L=3.5m" - DISPLAY ONLY
        /// </summary>
        public string Explanation { get; set; }

        /// <summary>
        /// Bounding box for grid location calculation
        /// </summary>
        public double MinX { get; set; }
        public double MaxX { get; set; }
        public double MinY { get; set; }
        public double MaxY { get; set; }

        /// <summary>
        /// Grid location string (populated externally by LoadEnricher)
        /// </summary>
        public string GridLocation { get; set; }
    }

    /// <summary>
    /// Frame with load coverage information
    /// </summary>
    public class FrameLoadInfo
    {
        public SapFrame Frame { get; set; }
        public double CoveredLengthM { get; set; }
        public double DistStart { get; set; }
        public double DistEnd { get; set; }
        public RawSapLoad Load { get; set; }
    }
}
