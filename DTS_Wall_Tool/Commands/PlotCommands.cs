using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Engines;
using DTS_Wall_Tool.Core.Primitives;
using DTS_Wall_Tool.Core.Utils;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Commands
{
    /// <summary>
    /// Các lệnh vẽ mô hình từ SAP2000 vào AutoCAD
    /// </summary>
    public class PlotCommands : CommandBase
    {
        private const string AXIS_LAYER = "dts_axis";
        private const string FRAME_LAYER = "dts_frame";
        private const string POINT_LAYER = "dts_point";
        private const string ORIGIN_LAYER = "dts_origin";
        private const string LABEL_LAYER = "dts_frame_label";

        /// <summary>
        /// Vẽ mặt bằng từ SAP2000 (2D/3D)
        /// </summary>
        [CommandMethod("DTS_PLOT_FROM_SAP")]
        public void DTS_PLOT_FROM_SAP()
        {
            WriteMessage("=== VẼ MặT BẰNG TỪ SAP2000 ===");

            if (!EnsureSapConnection()) return;

            // Bước1: Lấy danh sách tầng từ SAP
            var stories = SapUtils.GetStories();
            if (stories.Count ==0)
            {
                WriteError("Không tìm thấy tầng trong SAP2000");
                return;
            }

            WriteMessage($"Tìm thấy {stories.Count} tầng");

            // Bước2: Hiển thị menu chọn tầng
            var selectedStories = SelectStories(stories);
            if (selectedStories.Count ==0)
            {
                WriteMessage("Không chọn tầng nào.");
                return;
            }

            // Bước3: Nếu chọn "All", hỏi chế độ2D/3D
            bool is3D = false;
            if (selectedStories.Count >1)
            {
                is3D = PromptFor3DMode();
            }

            // Bước4: Plot mặt bằng
            if (is3D)
            {
                PlotAll3D(selectedStories);
            }
            else
            {
                if (selectedStories.Count ==1)
                {
                    // Single2D: pick location
                    PlotSingleStory2D(selectedStories[0]);
                }
                else
                {
                    // All2D: auto layout
                    PlotAllStories2D(selectedStories);
                }
            }

            WriteSuccess("Hoàn thành!");
        }

        #region Helper Methods

        private bool EnsureSapConnection()
        {
            if (!SapUtils.IsConnected)
            {
                if (!SapUtils.Connect(out string msg))
                {
                    WriteError(msg);
                    return false;
                }
            }
            return true;
        }

        /// <summary>
        /// Hiển thị menu chọn tầng
        /// </summary>
        private List<SapUtils.GridStoryItem> SelectStories(List<SapUtils.GridStoryItem> stories)
        {
            var zStories = stories.Where(s => s.IsElevation).OrderBy(s => s.Coordinate).ToList();

            if (zStories.Count ==0)
            {
                WriteError("Không tìm thấy tầng (Z) trong danh sách");
                return new List<SapUtils.GridStoryItem>();
            }

            WriteMessage("\nChọn tầng để vẽ:");
            for (int i =0; i < zStories.Count; i++)
            {
                WriteMessage($" {i +1}. {zStories[i].Name} (Z={zStories[i].Coordinate})");
            }
            WriteMessage($" {zStories.Count +1}. All (tất cả)");
            WriteMessage($"0. Cancel");

            PromptIntegerOptions opt = new PromptIntegerOptions("\nChọn số: ");
            opt.LowerLimit =0;
            opt.UpperLimit = zStories.Count +1;
            opt.DefaultValue =0;

            PromptIntegerResult res = Ed.GetInteger(opt);
            if (res.Status != PromptStatus.OK) return new List<SapUtils.GridStoryItem>();

            if (res.Value ==0) return new List<SapUtils.GridStoryItem>();

            var result = new List<SapUtils.GridStoryItem>();
            if (res.Value == zStories.Count +1)
            {
                result.AddRange(zStories); // All
            }
            else
            {
                result.Add(zStories[res.Value -1]); // Single
            }

            return result;
        }

        /// <summary>
        /// Hỏi chế độ2D hay3D
        /// </summary>
        private bool PromptFor3DMode()
        {
            PromptKeywordOptions opt = new PromptKeywordOptions("\nChế độ [2D/3D] <2D>: ");
            opt.Keywords.Add("2D");
            opt.Keywords.Add("3D");
            opt.Keywords.Default = "2D";

            PromptResult res = Ed.GetKeywords(opt);
            if (res.Status != PromptStatus.OK) return false;

            return res.StringResult == "3D";
        }

        /// <summary>
        /// Vẽ mặt bằng2D đơn
        /// </summary>
        private void PlotSingleStory2D(SapUtils.GridStoryItem story)
        {
            WriteMessage($"\nVẽ mặt bằng {story.Name} (Z={story.Coordinate})...");

            // Bước1: Pick điểm đặt gốc trong CAD
            PromptPointOptions ptOpt = new PromptPointOptions($"\nChọn vị trí đặt mặt bằng {story.Name}: ");
            PromptPointResult ptRes = Ed.GetPoint(ptOpt);
            if (ptRes.Status != PromptStatus.OK) return;

            Point2D insertPoint = new Point2D(ptRes.Value.X, ptRes.Value.Y);

            // Bước2: Lấy dữ liệu grid và frame từ SAP
            var allGrids = SapUtils.GetGridLines();
            var allFrames = SapUtils.GetAllFramesGeometry();

            // Lọc frame cùng tầng
            double storyElev = story.Coordinate;
            var framesAtStory = allFrames
 .Where(f => Math.Abs(f.AverageZ - storyElev) <=200)
                .ToList();

            // Bước3: Tạo layer
            AcadUtils.CreateLayer(AXIS_LAYER,5); // Cyan/Blue
            AcadUtils.CreateLayer(FRAME_LAYER,2); // Yellow
            AcadUtils.CreateLayer(POINT_LAYER,3); // Green
            AcadUtils.CreateLayer(ORIGIN_LAYER,1);
            AcadUtils.CreateLayer(LABEL_LAYER,254);

            // Bước4: Vẽ
            UsingTransaction(tr =>
 {
                var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                // Tạo Origin trước -> lấy handle
                string originHandle = CreateOriginForStory(btr, tr, insertPoint, story);

                // Vẽ grid
                PlotGridLines(btr, tr, allGrids, insertPoint);

                // Vẽ frame (và auto link vào origin)
                PlotFramesAt(btr, tr, framesAtStory, insertPoint, story.Coordinate, originHandle);

                // Origin đã tạo trước đó
 });

            WriteSuccess($"Vẽ xong {story.Name}");
        }

        /// <summary>
        /// Vẽ tất cả mặt bằng3D
        /// </summary>
        private void PlotAll3D(List<SapUtils.GridStoryItem> stories)
        {
            WriteMessage("\nChế độ3D - Vẽ toàn bộ mô hình3D");

            // Hỏi user chọn vị trí plot
            PromptPointOptions ptOpt = new PromptPointOptions("\nChọn vị trí đặt mô hình3D: ");
            PromptPointResult ptRes = Ed.GetPoint(ptOpt);
            if (ptRes.Status != PromptStatus.OK) return;

            Point2D baseInsertPoint = new Point2D(ptRes.Value.X, ptRes.Value.Y);

            var allGrids = SapUtils.GetGridLines();
            var allFrames = SapUtils.GetAllFramesGeometry();

            var sortedStories = stories.OrderBy(s => s.Coordinate).ToList();

            // Ensure layers created BEFORE drawing to avoid KeyNotFound
            AcadUtils.CreateLayer(AXIS_LAYER,5);
            AcadUtils.CreateLayer(FRAME_LAYER,2);
            AcadUtils.CreateLayer(POINT_LAYER,3);
            AcadUtils.CreateLayer(ORIGIN_LAYER,1);
            AcadUtils.CreateLayer(LABEL_LAYER,254);

            UsingTransaction(tr =>
 {
                var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

                for (int i =0; i < sortedStories.Count; i++)
 {
                    var story = sortedStories[i];

                    // Vị trí XY của mặt bằng tại vị trí pick
                    Point2D insertPoint = baseInsertPoint;

                    // Lấy frame tại tầng này
                    var framesAtStory = allFrames
 .Where(f => Math.Abs(f.AverageZ - story.Coordinate) <=200)
                .ToList();

                    WriteMessage($" Vẽ {story.Name} tại Z={story.Coordinate}...");

                    // TẠO ORIGIN TRƯỚC để lấy Handle
                    string originHandle = CreateOriginForStory3D(btr, tr, insertPoint, story);

                    // Vẽ frame với tọa độ Z đúng (3D) và auto link
                    PlotFramesAt3D(btr, tr, framesAtStory, insertPoint, story.Coordinate, originHandle);

                    if (framesAtStory.Count ==0)
 {
                        WriteMessage($" - Chú ý: Không có frame nào ở tầng {story.Name} (Z={story.Coordinate}).");
 }
                }

                // Vẽ lưới trục chỉ ở mặt bằng đầu tiên (tầng thấp nhất) với extent thống nhất
                var firstStory = sortedStories.First();
                PlotGridLines3D(btr, tr, allGrids, baseInsertPoint, firstStory.Coordinate);
 });

            WriteSuccess("Vẽ mô hình3D hoàn thành");
        }

        /// <summary>
        /// Vẽ grid tại một cao độ Z cụ thể (3D)
        /// </summary>
        private void PlotGridLines3D(BlockTableRecord btr, Transaction tr,
 List<SapUtils.GridLineRecord> grids, Point2D offset, double elevation)
 {
 if (grids == null || grids.Count ==0) return;

 var xGrids = grids.Where(g => g.Orientation == "X").OrderBy(g => g.Coordinate).ToList();
 var yGrids = grids.Where(g => g.Orientation == "Y").OrderBy(g => g.Coordinate).ToList();

 if (xGrids.Count ==0 || yGrids.Count ==0) return;

 // Use unified extents
 CalculateGridExtents(grids, out double extMinX, out double extMaxX, out double extMinY, out double extMaxY);

 // Vẽ X-axis
 foreach (var xGrid in xGrids)
 {
 Point3d pt1 = new Point3d(xGrid.Coordinate + offset.X, extMinY + offset.Y, elevation);
 Point3d pt2 = new Point3d(xGrid.Coordinate + offset.X, extMaxY + offset.Y, elevation);

 Line line = new Line(pt1, pt2) { Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(line);
 tr.AddNewlyCreatedDBObject(line, true);

 // Vẽ tên trục ở cả2 đầu (trên và dưới)
 PlotAxisLabel(btr, tr, xGrid.Name, new Point3d(pt1.X, extMaxY + offset.Y +100, elevation));
 PlotAxisLabel(btr, tr, xGrid.Name, new Point3d(pt1.X, extMinY + offset.Y -100, elevation));
 }

 // Vẽ Y-axis
 foreach (var yGrid in yGrids)
 {
 Point3d pt1 = new Point3d(extMinX + offset.X, yGrid.Coordinate + offset.Y, elevation);
 Point3d pt2 = new Point3d(extMaxX + offset.X, yGrid.Coordinate + offset.Y, elevation);

 Line line = new Line(pt1, pt2) { Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(line);
 tr.AddNewlyCreatedDBObject(line, true);

 // Vẽ tên trục ở cả2 đầu (trái và phải)
 PlotAxisLabel(btr, tr, yGrid.Name, new Point3d(extMinX + offset.X -200, pt1.Y, elevation));
 PlotAxisLabel(btr, tr, yGrid.Name, new Point3d(extMaxX + offset.X +100, pt1.Y, elevation));
 }
 }

 /// <summary>
 /// Vẽ frame với tọa độ3D (Z từ frame chính)
 /// </summary>
 private void PlotFramesAt3D(BlockTableRecord btr, Transaction tr, List<SapFrame> frames, Point2D offset, double elevation, string originHandle)
 {
 foreach (var frame in frames)
 {
 // Vẽ frame từ Z1 tới Z2 (3D)
 Point3d pt1 = new Point3d(frame.StartPt.X + offset.X, frame.StartPt.Y + offset.Y, frame.Z1);
 Point3d pt2 = new Point3d(frame.EndPt.X + offset.X, frame.EndPt.Y + offset.Y, frame.Z2);

 Line line = new Line(pt1, pt2);
 line.Layer = FRAME_LAYER;
 line.ColorIndex = frame.IsBeam ?2 :5; // Yellow beam, Cyan column

 ObjectId lineId = btr.AppendEntity(line);
 tr.AddNewlyCreatedDBObject(line, true);

 var frameData = new BeamData
 {
 SectionName = frame.Section,
 Length = frame.Length2D,
 BeamType = frame.IsBeam ? "Main" : "Column"
 };
 frameData.BaseZ = frame.Z1;
 frameData.Height = Math.Abs(frame.Z2 - frame.Z1);

 // Auto link into origin if provided
 if (!string.IsNullOrEmpty(originHandle)) frameData.OriginHandle = originHandle;

 XDataUtils.WriteElementData(tr.GetObject(lineId, OpenMode.ForRead), frameData, tr);

 // Try to label (LabelUtils updated to use3D overload)
 try { LabelUtils.RefreshEntityLabel(lineId, tr); } catch { }

 // Add child handle to origin
 if (!string.IsNullOrEmpty(originHandle)) AddChildToOrigin(originHandle, lineId.Handle.ToString(), tr);

 PlotPoint3D(btr, tr, pt1);
 PlotPoint3D(btr, tr, pt2);
 }
 }

 /// <summary>
 /// Vẽ point3D (Circle nhỏ)
 /// </summary>
 private void PlotPoint3D(BlockTableRecord btr, Transaction tr, Point3d pt)
 {
 Circle circle = new Circle
 {
 Center = pt,
 Radius =50,
 Layer = POINT_LAYER,
 ColorIndex =3 // Green
 };

 btr.AppendEntity(circle);
 tr.AddNewlyCreatedDBObject(circle, true);
 }

 /// <summary>
 /// Tạo Origin tại3D (với cao độ Z) và trả về Handle
 /// </summary>
 private string CreateOriginForStory3D(BlockTableRecord btr, Transaction tr,
 Point2D insertPoint, SapUtils.GridStoryItem story)
 {
 AcadUtils.CreateLayer(ORIGIN_LAYER,1);

 Circle circle = new Circle
 {
 Center = new Point3d(insertPoint.X, insertPoint.Y, story.Coordinate),
 Radius =500,
 Layer = ORIGIN_LAYER,
 ColorIndex =1 // Red
 };

 ObjectId circleId = btr.AppendEntity(circle);
 tr.AddNewlyCreatedDBObject(circle, true);

 var storyData = new StoryData
 {
 StoryName = story.Name,
 Elevation = story.Coordinate,
 StoryHeight =3300,
 OffsetX = insertPoint.X,
 OffsetY = insertPoint.Y
 };

 DBObject circleObj = tr.GetObject(circleId, OpenMode.ForRead);
 XDataUtils.WriteStoryData(circleObj, storyData, tr);
 return circleId.Handle.ToString();
 }

 /// <summary>
 /// Vẽ point (Circle nhỏ) -2D mode
 /// </summary>
 private void PlotPoint(BlockTableRecord btr, Transaction tr, Point3d pt)
 {
 Circle circle = new Circle
 {
 Center = pt,
 Radius =50, //50mm circle
 Layer = POINT_LAYER,
 ColorIndex =3 // Green
 };

 btr.AppendEntity(circle);
 tr.AddNewlyCreatedDBObject(circle, true);
 }

 /// <summary>
 /// Vẽ grid2D tại offset (dùng extent lớn nhất và nhãn ở hai đầu)
 /// </summary>
 private void PlotGridLines(BlockTableRecord btr, Transaction tr, List<SapUtils.GridLineRecord> grids, Point2D offset)
 {
 if (grids == null || grids.Count ==0) return;

 var xGrids = grids.Where(g => g.Orientation == "X").OrderBy(g => g.Coordinate).ToList();
 var yGrids = grids.Where(g => g.Orientation == "Y").OrderBy(g => g.Coordinate).ToList();
 if (xGrids.Count ==0 || yGrids.Count ==0) return;

 // Use unified extents
 CalculateGridExtents(grids, out double extMinX, out double extMaxX, out double extMinY, out double extMaxY);

 // X axes (vertical grid lines in plan)
 foreach (var x in xGrids)
 {
 Point3d p1 = new Point3d(x.Coordinate + offset.X, extMinY + offset.Y,0);
 Point3d p2 = new Point3d(x.Coordinate + offset.X, extMaxY + offset.Y,0);
 var line = new Line(p1, p2) { Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);

 // labels both ends
 PlotAxisLabel(btr, tr, x.Name, new Point3d(p1.X, extMaxY + offset.Y +100,0));
 PlotAxisLabel(btr, tr, x.Name, new Point3d(p1.X, extMinY + offset.Y -100,0));
 }

 // Y axes (horizontal grid lines in plan)
 foreach (var y in yGrids)
 {
 Point3d p1 = new Point3d(extMinX + offset.X, y.Coordinate + offset.Y,0);
 Point3d p2 = new Point3d(extMaxX + offset.X, y.Coordinate + offset.Y,0);
 var line = new Line(p1, p2) { Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);

 PlotAxisLabel(btr, tr, y.Name, new Point3d(extMinX + offset.X -200, p1.Y,0));
 PlotAxisLabel(btr, tr, y.Name, new Point3d(extMaxX + offset.X +100, p1.Y,0));
 }
 }

 /// <summary>
 /// Vẽ tên trục (MText)
 /// </summary>
 private void PlotAxisLabel(BlockTableRecord btr, Transaction tr, string name, Point3d position)
 {
 if (string.IsNullOrWhiteSpace(name)) return;
 MText m = new MText { Contents = name, Location = position, TextHeight =120, Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(m); tr.AddNewlyCreatedDBObject(m, true);
 }

 /// <summary>
 /// Vẽ frame2D (plan) ở offset
 /// </summary>
 private void PlotFramesAt(BlockTableRecord btr, Transaction tr, List<SapFrame> frames, Point2D offset, double elevation, string originHandle)
 {
 if (frames == null) return;
 foreach (var f in frames)
 {
 Point3d p1 = new Point3d(f.StartPt.X + offset.X, f.StartPt.Y + offset.Y,0);
 Point3d p2 = new Point3d(f.EndPt.X + offset.X, f.EndPt.Y + offset.Y,0);
 var line = new Line(p1, p2) { Layer = FRAME_LAYER, ColorIndex = f.IsBeam ?2 :5 };
 ObjectId id = btr.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);

 var fd = new BeamData { SectionName = f.Section, Length = f.Length2D, BeamType = f.IsBeam ? "Main" : "Column" };
 fd.BaseZ = elevation; fd.Height = f.IsBeam ? null : (double?)3300;
 if (!string.IsNullOrEmpty(originHandle)) fd.OriginHandle = originHandle;
 XDataUtils.WriteElementData(tr.GetObject(id, OpenMode.ForRead), fd, tr);

 // Use centralized LabelUtils to render labels for created element
 try
 {
 LabelUtils.RefreshEntityLabel(id, tr);
 }
 catch { }

 // Add to children of origin
 if (!string.IsNullOrEmpty(originHandle))
 {
 AddChildToOrigin(originHandle, id.Handle.ToString(), tr);
 }
 }
 }

 // Helper để thêm con vào Origin
 private void AddChildToOrigin(string originHandle, string childHandle, Transaction tr)
 {
 ObjectId originId = AcadUtils.GetObjectIdFromHandle(originHandle);
 if (originId == ObjectId.Null) return;

 DBObject originObj = tr.GetObject(originId, OpenMode.ForWrite);
 var story = XDataUtils.ReadStoryData(originObj);
 if (story != null)
 {
 if (story.ChildHandles == null) story.ChildHandles = new List<string>();
 if (!story.ChildHandles.Contains(childHandle))
 {
 story.ChildHandles.Add(childHandle);
 XDataUtils.WriteStoryData(originObj, story, tr);
 }
 }
 else
 {
 // Maybe origin is an element origin
 var elem = XDataUtils.ReadElementData(originObj);
 if (elem != null)
 {
 if (elem.ChildHandles == null) elem.ChildHandles = new List<string>();
 if (!elem.ChildHandles.Contains(childHandle))
 {
 elem.ChildHandles.Add(childHandle);
 XDataUtils.WriteElementData(originObj, elem, tr);
 }
 }
 }
 }

 // FIX: Calculate unified extents for grids
 private void CalculateGridExtents(List<SapUtils.GridLineRecord> grids,
 out double extMinX, out double extMaxX, out double extMinY, out double extMaxY)
 {
 var xGrids = grids.Where(g => g.Orientation == "X").ToList();
 var yGrids = grids.Where(g => g.Orientation == "Y").ToList();

 double minX = xGrids.Any() ? xGrids.Min(g => g.Coordinate) :0;
 double maxX = xGrids.Any() ? xGrids.Max(g => g.Coordinate) :0;
 double minY = yGrids.Any() ? yGrids.Min(g => g.Coordinate) :0;
 double maxY = yGrids.Any() ? yGrids.Max(g => g.Coordinate) :0;

 double rangeX = maxX - minX;
 double rangeY = maxY - minY;
 double maxRange = Math.Max(rangeX, rangeY);
 if (maxRange <1000) maxRange =5000;

 double extend = maxRange *0.15; //15% range

 extMinX = minX - extend;
 extMaxX = maxX + extend;
 extMinY = minY - extend;
 extMaxY = maxY + extend;
 }

 /// <summary>
 /// Tạo Origin cho mặt bằng2D - trả về handle
 /// </summary>
 private string CreateOriginForStory(BlockTableRecord btr, Transaction tr, Point2D insertPoint, SapUtils.GridStoryItem story)
 {
 AcadUtils.CreateLayer(ORIGIN_LAYER,1);
 Circle c = new Circle { Center = new Point3d(insertPoint.X, insertPoint.Y,0), Radius =500, Layer = ORIGIN_LAYER, ColorIndex =1 };
 ObjectId id = btr.AppendEntity(c); tr.AddNewlyCreatedDBObject(c, true);
 var sd = new StoryData { StoryName = story.Name, Elevation = story.Coordinate, StoryHeight =3300, OffsetX = insertPoint.X, OffsetY = insertPoint.Y };
 DBObject obj = tr.GetObject(id, OpenMode.ForRead);
 XDataUtils.WriteStoryData(obj, sd, tr);
 return id.Handle.ToString();
 }

 /// <summary>
 /// Vẽ tất cả mặt bằng2D với spacing =2/3 Y-range
 /// </summary>
 private void PlotAllStories2D(List<SapUtils.GridStoryItem> stories)
 {
 var allGrids = SapUtils.GetGridLines();
 var allFrames = SapUtils.GetAllFramesGeometry();
 var yGrids = allGrids.Where(g => g.Orientation == "Y").OrderBy(g => g.Coordinate).ToList();
 if (yGrids.Count ==0) { WriteError("Không có trục Y"); return; }

 // Compute spacing based on Y-range with larger multiplier
 double maxY = yGrids.Max(g => g.Coordinate);
 double minY = yGrids.Min(g => g.Coordinate);
 double rangeY = maxY - minY;
 double spacing = Math.Max(20000.0, rangeY *2.5);

 // Prompt user for base insertion point for All-2D layout
 WriteMessage($"\nChọn điểm gốc để vẽ bố cục {stories.Count} mặt bằng (All2D). Spacing = {spacing:0}");
 PromptPointOptions ptOpt = new PromptPointOptions("Chọn điểm gốc: ");
 PromptPointResult ptRes = Ed.GetPoint(ptOpt);
 if (ptRes.Status != PromptStatus.OK)
 {
 WriteMessage("Bỏ qua vẽ All2D (không chọn điểm).");
 return;
 }
 Point2D baseInsert = new Point2D(ptRes.Value.X, ptRes.Value.Y);

 // Ensure layers exist
 AcadUtils.CreateLayer(AXIS_LAYER,5);
 AcadUtils.CreateLayer(FRAME_LAYER,2);
 AcadUtils.CreateLayer(POINT_LAYER,3);
 AcadUtils.CreateLayer(ORIGIN_LAYER,1);
 AcadUtils.CreateLayer(LABEL_LAYER,254);

 var sorted = stories.OrderBy(s => s.Coordinate).ToList();
 UsingTransaction(tr =>
 {
 var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
 var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

 for (int i =0; i < sorted.Count; i++)
 {
 var st = sorted[i];
 // Arrange along Y from baseInsert
 Point2D insert = new Point2D(baseInsert.X, baseInsert.Y + i * spacing);

 // Create origin first
 string originHandle = CreateOriginForStory(btr, tr, insert, st);

 var framesAt = allFrames.Where(f => Math.Abs(f.AverageZ - st.Coordinate) <=200).ToList();
 PlotGridLines(btr, tr, allGrids, insert);
 PlotFramesAt(btr, tr, framesAt, insert, st.Coordinate, originHandle);
 }
 });
 WriteSuccess("Vẽ tất cả mặt bằng2D hoàn thành");
 }

 #endregion
 }
}