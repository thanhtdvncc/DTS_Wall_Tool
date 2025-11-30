// File này cung cấp type aliases để code cũ vẫn hoạt động
// Đặt trong Core/ hoặc gốc project

// Re-export types từ Primitives vào namespace Core
using Point2D = DTS_Wall_Tool.Core.Primitives.Point2D;
using LineSegment2D = DTS_Wall_Tool.Core.Primitives.LineSegment2D;
using BoundingBox = DTS_Wall_Tool.Core.Primitives.BoundingBox;
// Fixed: ProjectionResult should alias GeometryResults (two-double ctor), not PointProjectionResult
using ProjectionResult = DTS_Wall_Tool.Core.Primitives.GeometryResults;
using OverlapResult = DTS_Wall_Tool.Core.Primitives.OverlapResult;

// Re-export Data types
using WallData = DTS_Wall_Tool.Core.Data.WallData;
using StoryData = DTS_Wall_Tool.Core.Data.StoryData;
using MappingRecord = DTS_Wall_Tool.Core.Data.MappingRecord;
using SapFrame = DTS_Wall_Tool.Core.Data.SapFrame;