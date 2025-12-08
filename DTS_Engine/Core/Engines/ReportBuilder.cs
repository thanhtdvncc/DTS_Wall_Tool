using DTS_Engine.Core.Data;
using DTS_Engine.Core.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// Stage 4: Report Building - Transforms grouped data into AuditReport structure.
    /// 
    /// CRITICAL FORMULA:
    /// Force_Vector = Quantity × UnitLoad × Direction_Vector
    /// where:
    ///   - Quantity: Area (m²) or Length (m) from GeometryProcessor
    ///   - UnitLoad: kN/m² or kN/m from load data
    ///   - Direction_Vector: (Fx, Fy, Fz) normalized direction
    /// 
    /// ISO/IEC 25010: Accuracy - All calculations are explicit and verifiable
    /// </summary>
    public class ReportBuilder
    {
        #region Dependencies

        private readonly LoadEnricher _enricher;
        private readonly GeometryProcessor _geometryProcessor;

        #endregion

        #region Constructor

        public ReportBuilder(LoadEnricher enricher, GeometryProcessor geometryProcessor)
        {
            _enricher = enricher ?? throw new ArgumentNullException(nameof(enricher));
            _geometryProcessor = geometryProcessor ?? throw new ArgumentNullException(nameof(geometryProcessor));
        }

        #endregion

        #region Public API

        /// <summary>
        /// Build AuditReport from grouped load data.
        /// </summary>
        public AuditReport BuildReport(List<StoryBucket> storyBuckets, string loadPattern, string modelName)
        {
            var report = new AuditReport
            {
                LoadPattern = loadPattern,
                ModelName = modelName,
                AuditDate = DateTime.Now,
                Stories = new List<AuditStoryGroup>()
            };

            double totalFx = 0, totalFy = 0, totalFz = 0;

            foreach (var storyBucket in storyBuckets.OrderByDescending(s => s.Elevation))
            {
                var storyGroup = BuildStoryGroup(storyBucket);
                report.Stories.Add(storyGroup);

                // Accumulate totals from story
                totalFx += storyGroup.LoadTypes.Sum(lt => lt.SubTotalFx);
                totalFy += storyGroup.LoadTypes.Sum(lt => lt.SubTotalFy);
                totalFz += storyGroup.LoadTypes.Sum(lt => lt.SubTotalFz);
            }

            report.CalculatedFx = totalFx;
            report.CalculatedFy = totalFy;
            report.CalculatedFz = totalFz;

            return report;
        }

        #endregion

        #region Private Methods - Story Processing

        private AuditStoryGroup BuildStoryGroup(StoryBucket bucket)
        {
            var storyGroup = new AuditStoryGroup
            {
                StoryName = bucket.StoryName,
                Elevation = bucket.Elevation,
                LoadTypes = new List<AuditLoadTypeGroup>()
            };

            foreach (var loadTypeBucket in bucket.LoadTypeBuckets)
            {
                var loadTypeGroup = BuildLoadTypeGroup(loadTypeBucket);
                storyGroup.LoadTypes.Add(loadTypeGroup);
            }

            return storyGroup;
        }

        private AuditLoadTypeGroup BuildLoadTypeGroup(LoadTypeBucket bucket)
        {
            var loadTypeGroup = new AuditLoadTypeGroup
            {
                LoadTypeName = bucket.DisplayName,
                Entries = new List<AuditEntry>()
            };

            double subFx = 0, subFy = 0, subFz = 0;

            foreach (var locationBucket in bucket.LocationBuckets)
            {
                // Process geometry and calculate forces
                var entry = BuildAuditEntry(locationBucket, bucket.LoadType);
                loadTypeGroup.Entries.Add(entry);

                // Accumulate subtotals
                subFx += entry.ForceX;
                subFy += entry.ForceY;
                subFz += entry.ForceZ;
            }

            loadTypeGroup.SubTotalFx = subFx;
            loadTypeGroup.SubTotalFy = subFy;
            loadTypeGroup.SubTotalFz = subFz;

            return loadTypeGroup;
        }

        #endregion

        #region Private Methods - Entry Building

        private AuditEntry BuildAuditEntry(LocationBucket bucket, string loadType)
        {
            // Step 1: Calculate geometry (Quantity + Explanation)
            GeometryResult geomResult;

            switch (loadType)
            {
                case "Area":
                    geomResult = ProcessAreaBucket(bucket);
                    break;

                case "Frame":
                    geomResult = ProcessFrameBucket(bucket);
                    break;

                case "Point":
                    geomResult = ProcessPointBucket(bucket);
                    break;

                default:
                    geomResult = new GeometryResult
                    {
                        Quantity = bucket.Loads.Count,
                        QuantityUnit = "pcs",
                        Explanation = $"{bucket.Loads.Count} items"
                    };
                    break;
            }

            // Step 2: Recalculate grid location from ACTUAL geometry bounds (CRITICAL FIX)
            // This ensures merged elements get accurate location instead of using first load's location
            string gridLocation = bucket.GridLocation;
            if (geomResult.MaxX > geomResult.MinX || geomResult.MaxY > geomResult.MinY)
            {
                // Use geometry bounds to calculate accurate grid location
                gridLocation = _enricher.GetGridRangeForBoundingBox(
                    geomResult.MinX, geomResult.MaxX,
                    geomResult.MinY, geomResult.MaxY);
            }
            else if (string.IsNullOrEmpty(gridLocation) || gridLocation == "Unknown" || gridLocation == "No Grid")
            {
                // Fallback to first load's pre-calculated location if geometry bounds unavailable
                gridLocation = bucket.GridLocation ?? "Unknown";
            }

            // Step 3: Calculate force vector
            // Formula: Force = Quantity × UnitLoad × Direction
            double quantity = geomResult.Quantity;
            double unitLoad = bucket.UnitLoad;

            // Get normalized direction from bucket's accumulated vectors
            double dirMagnitude = Math.Sqrt(
                bucket.VectorFx * bucket.VectorFx +
                bucket.VectorFy * bucket.VectorFy +
                bucket.VectorFz * bucket.VectorFz);

            double dirX = 0, dirY = 0, dirZ = -1; // Default gravity
            if (dirMagnitude > 1e-6)
            {
                dirX = bucket.VectorFx / dirMagnitude;
                dirY = bucket.VectorFy / dirMagnitude;
                dirZ = bucket.VectorFz / dirMagnitude;
            }

            // Calculate force components
            double forceX = quantity * unitLoad * dirX;
            double forceY = quantity * unitLoad * dirY;
            double forceZ = quantity * unitLoad * dirZ;
            double totalForce = Math.Sqrt(forceX * forceX + forceY * forceY + forceZ * forceZ);

            // Step 4: Build entry
            var entry = new AuditEntry
            {
                GridLocation = gridLocation,
                Explanation = geomResult.Explanation,
                Quantity = quantity,
                QuantityUnit = geomResult.QuantityUnit,
                UnitLoad = unitLoad,
                UnitLoadString = FormatUnitLoad(unitLoad, loadType),
                Direction = bucket.Direction,
                DirectionSign = bucket.DirectionSign,
                TotalForce = totalForce,
                ForceX = forceX,
                ForceY = forceY,
                ForceZ = forceZ,
                ElementList = bucket.Elements.ToList()
            };

            // Store calculated values back to bucket
            bucket.Quantity = quantity;
            bucket.QuantityUnit = geomResult.QuantityUnit;
            bucket.Explanation = geomResult.Explanation;
            bucket.TotalFx = forceX;
            bucket.TotalFy = forceY;
            bucket.TotalFz = forceZ;

            return entry;
        }

        #endregion

        #region Private Methods - Load Type Processing

        private GeometryResult ProcessAreaBucket(LocationBucket bucket)
        {
            var areas = new List<SapArea>();
            foreach (var load in bucket.Loads)
            {
                var area = _enricher.GetAreaGeometry(load.ElementName);
                if (area != null && !areas.Any(a => a.Name == area.Name))
                {
                    areas.Add(area);
                }
            }

            if (areas.Count == 0)
            {
                return new GeometryResult
                {
                    Quantity = 0,
                    QuantityUnit = "m²",
                    Explanation = "(no geometry)"
                };
            }

            return _geometryProcessor.ProcessMultipleAreas(areas);
        }

        private GeometryResult ProcessFrameBucket(LocationBucket bucket)
        {
            var frameInfos = new List<FrameLoadInfo>();

            foreach (var load in bucket.Loads)
            {
                var frame = _enricher.GetFrameGeometry(load.ElementName);
                if (frame == null) continue;

                // Calculate covered length
                double distStart = load.DistStart;
                double distEnd = load.DistEnd;
                bool isRelative = load.IsRelative;

                double fullLengthMm = frame.Length2D;
                double startMm, endMm;

                if (isRelative)
                {
                    startMm = fullLengthMm * distStart;
                    endMm = fullLengthMm * (distEnd > 0 ? distEnd : 1.0);
                }
                else
                {
                    startMm = distStart;
                    endMm = distEnd > 0 ? distEnd : fullLengthMm;
                }

                double coveredLengthM = Math.Abs(endMm - startMm) / 1000.0;

                // Check for duplicates
                if (!frameInfos.Any(f => f.Frame.Name == frame.Name))
                {
                    frameInfos.Add(new FrameLoadInfo
                    {
                        Frame = frame,
                        CoveredLengthM = coveredLengthM,
                        DistStart = distStart,
                        DistEnd = distEnd,
                        Load = load
                    });
                }
            }

            if (frameInfos.Count == 0)
            {
                return new GeometryResult
                {
                    Quantity = 0,
                    QuantityUnit = "m",
                    Explanation = "(no geometry)"
                };
            }

            return _geometryProcessor.ProcessMultipleFrames(frameInfos);
        }

        private GeometryResult ProcessPointBucket(LocationBucket bucket)
        {
            int count = bucket.Loads.Select(l => l.ElementName).Distinct().Count();

            // For point loads, quantity is just count (force is already in kN)
            // But we need to treat it differently - UnitLoad IS the force per point

            return new GeometryResult
            {
                Quantity = count,
                QuantityUnit = "pcs",
                Explanation = count == 1 ? "1 point" : $"{count} points"
            };
        }

        #endregion

        #region Private Methods - Formatting

        private string FormatUnitLoad(double value, string loadType)
        {
            switch (loadType)
            {
                case "Area":
                    return $"{value:0.##} kN/m²";
                case "Frame":
                    return $"{value:0.##} kN/m";
                case "Point":
                    return $"{value:0.##} kN";
                default:
                    return $"{value:0.##}";
            }
        }

        #endregion
    }
}
