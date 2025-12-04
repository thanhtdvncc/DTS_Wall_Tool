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
 WriteMessage("=== VẼ MẶT BẰNG TỪ SAP2000 ===");

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
 /// Lọc Frame thuộc tầng (Dầm ở cao độ Z, Cột có đỉnh ở cao độ Z)
 /// </summary>
 private List<SapFrame> FilterFramesForStory(List<SapFrame> allFrames, double storyZ)
 {
 // Increase tolerance and include frames where either endpoint matches the story elevation.
 // This handles sloped beams and avoids missing elements due to small numeric differences.
 double tolerance =200.0; //0.2m default tolerance (was200)

 return allFrames.Where(f =>
 {
 // Dầm: Nằm ngang - accept if average Z OR either endpoint is close to storyZ
 if (!f.IsVertical)
 {
 bool avgMatch = Math.Abs(f.AverageZ - storyZ) <= tolerance;
 bool endMatch = Math.Abs(f.Z1 - storyZ) <= tolerance || Math.Abs(f.Z2 - storyZ) <= tolerance;
 return avgMatch || endMatch;
 }
 // Cột: Đỉnh cột xấp xỉ storyZ (Cột đỡ sàn này)
 else
 {
 double topZ = Math.Max(f.Z1, f.Z2);
 return Math.Abs(topZ - storyZ) <= tolerance;
 }
 }).ToList();
 }

 // ======================= PLOTTING LOGIC =======================

 /// <summary>
 /// Tính toán Extent của Grid và Text Scale
 /// </summary>
 private void CalculateGridParams(List<SapUtils.GridLineRecord> grids,
 out double minX, out double maxX, out double minY, out double maxY,
 out double textHeight)
 {
 // Use case-insensitive checks for orientations
 var xGrids = grids.Where(g => string.Equals(g.Orientation, "X", StringComparison.OrdinalIgnoreCase)).ToList();
 var yGrids = grids.Where(g => string.Equals(g.Orientation, "Y", StringComparison.OrdinalIgnoreCase)).ToList();

 minX = xGrids.Any() ? xGrids.Min(g => g.Coordinate) :0;
 maxX = xGrids.Any() ? xGrids.Max(g => g.Coordinate) :0;
 minY = yGrids.Any() ? yGrids.Min(g => g.Coordinate) :0;
 maxY = yGrids.Any() ? yGrids.Max(g => g.Coordinate) :0;

 double rangeX = maxX - minX;
 double rangeY = maxY - minY;
 double maxRange = Math.Max(rangeX, rangeY);
 if (maxRange <1000) maxRange =5000;

 // FIX1: Giảm khoảng extend xuống còn7.5% (1 nửa so với cũ)
 double extend = maxRange *0.075;

 minX -= extend;
 maxX += extend;
 minY -= extend;
 maxY += extend;

 // FIX2: Dynamic Text Scale
 // Quy tắc:100m (100,000mm) -> Scale1/200 -> Text500mm
 // Formula: Height = maxRange *0.01
 textHeight = Math.Max(250.0, maxRange *0.01);
 }

 private void SetupLayers()
 {
 AcadUtils.CreateLayer(AXIS_LAYER,5); // Blue/Cyan (axis)
 AcadUtils.CreateLayer(FRAME_LAYER,2); // Beam default Yellow
 AcadUtils.CreateLayer(POINT_LAYER,3); // Point/Column Green
 AcadUtils.CreateLayer(ORIGIN_LAYER,1); // Red
 AcadUtils.CreateLayer(LABEL_LAYER,254); // Gray
 }

 /// <summary>
 /// Vẽ mặt bằng2D đơn
 /// </summary>
 private void PlotSingleStory2D(SapUtils.GridStoryItem story)
 {
 WriteMessage($"\nVẽ mặt bằng {story.Name} (Z={story.Coordinate})...");

 PromptPointOptions ptOpt = new PromptPointOptions($"\nChọn vị trí đặt mặt bằng {story.Name}: ");
 PromptPointResult ptRes = Ed.GetPoint(ptOpt);
 if (ptRes.Status != PromptStatus.OK) return;

 Point2D insertPoint = new Point2D(ptRes.Value.X, ptRes.Value.Y);

 var allGrids = SapUtils.GetGridLines();
 var allFrames = SapUtils.GetAllFramesGeometry();
 var framesAtStory = FilterFramesForStory(allFrames, story.Coordinate);
 var allPoints = SapUtils.GetAllPoints();
 var pointsAtStory = allPoints.Where(p => Math.Abs(p.Z - story.Coordinate) <=200.0).ToList();

 SetupLayers();

 UsingTransaction(tr =>
 {
 var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
 var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

 // Tạo Origin trước -> lấy handle
 string originHandle = CreateOriginForStory(btr, tr, insertPoint, story);

 // Vẽ grid
 PlotGridLines(btr, tr, allGrids, insertPoint,0);

 // Vẽ frame (và auto link vào origin)
 PlotFramesAt(btr, tr, framesAtStory, insertPoint, story.Coordinate, originHandle, is3D: false);

 // Vẽ điểm thuộc tầng (2D)
 foreach (var pt in pointsAtStory)
 {
 Point3d p2d = new Point3d(pt.X + insertPoint.X, pt.Y + insertPoint.Y,0);
 PlotPoint(btr, tr, p2d);
 }

 });

 WriteSuccess($"Vẽ xong {story.Name}");
 }

 /// <summary>
 /// Vẽ tất cả mặt bằng2D
 /// </summary>
 private void PlotAllStories2D(List<SapUtils.GridStoryItem> stories)
 {
 var allGrids = SapUtils.GetGridLines();
 var allFrames = SapUtils.GetAllFramesGeometry();
 var allPoints = SapUtils.GetAllPoints();
 var yGrids = allGrids.Where(g => string.Equals(g.Orientation, "Y", StringComparison.OrdinalIgnoreCase)).ToList();
 if (yGrids.Count ==0) { WriteError("Không có trục Y"); return; }

 // Tính khoảng cách
 double maxY = yGrids.Max(g => g.Coordinate);
 double minY = yGrids.Min(g => g.Coordinate);
 double rangeY = maxY - minY;
 double spacing = Math.Max(20000.0, rangeY *2.5);

 WriteMessage($"\nChọn điểm gốc để vẽ {stories.Count} mặt bằng");
 PromptPointOptions ptOpt = new PromptPointOptions("Chọn điểm gốc: ");
 PromptPointResult ptRes = Ed.GetPoint(ptOpt);
 if (ptRes.Status != PromptStatus.OK) return;

 Point2D baseInsert = new Point2D(ptRes.Value.X, ptRes.Value.Y);

 SetupLayers();

 var sorted = stories.OrderBy(s => s.Coordinate).ToList();
 UsingTransaction(tr =>
 {
 var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
 var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

 for (int i =0; i < sorted.Count; i++)
 {
 var st = sorted[i];
 Point2D insert = new Point2D(baseInsert.X, baseInsert.Y + i * spacing);

 // Tạo origin
 string originHandle = CreateOriginForStory(btr, tr, insert, st);

 // Vẽ grid & frame
 PlotGridLines(btr, tr, allGrids, insert,0);
 var framesAt = FilterFramesForStory(allFrames, st.Coordinate);
 PlotFramesAt(btr, tr, framesAt, insert, st.Coordinate, originHandle, is3D: false);

 // Vẽ Point cho từng mặt bằng
 var pointsAt = allPoints.Where(p => Math.Abs(p.Z - st.Coordinate) <=200.0).ToList();
 foreach (var pt in pointsAt)
 {
 Point3d p2d = new Point3d(pt.X + insert.X, pt.Y + insert.Y,0);
 PlotPoint(btr, tr, p2d);
 }
 }
 });
 WriteSuccess("Vẽ tất cả mặt bằng2D hoàn thành");
 }

 /// <summary>
 /// Vẽ tất cả mặt bằng3D
 /// </summary>
 private void PlotAll3D(List<SapUtils.GridStoryItem> stories)
 {
 WriteMessage("\nChế độ3D - Vẽ toàn bộ mô hình3D");

 PromptPointOptions ptOpt = new PromptPointOptions("\nChọn vị trí đặt mô hình3D: ");
 PromptPointResult ptRes = Ed.GetPoint(ptOpt);
 if (ptRes.Status != PromptStatus.OK) return;

 Point2D baseInsertPoint = new Point2D(ptRes.Value.X, ptRes.Value.Y);

 //1. Lấy dữ liệu toàn cục
 WriteMessage("Đang đọc dữ liệu từ SAP...");
 var allGrids = SapUtils.GetGridLines();
 var allFrames = SapUtils.GetAllFramesGeometry();

 // NEW: Lấy tất cả Points
 var allPoints = SapUtils.GetAllPoints();
 WriteMessage($" -> Frames: {allFrames.Count}, Points: {allPoints.Count}");

 var sortedStories = stories.OrderBy(s => s.Coordinate).ToList();

 // Tạo Layer
 SetupLayers();

 UsingTransaction(tr =>
 {
 var bt = tr.GetObject(Db.BlockTableId, OpenMode.ForRead) as BlockTable;
 var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite) as BlockTableRecord;

 //2. Vẽ Grid ở tầng thấp nhất
 if (sortedStories.Count >0)
 {
 PlotGridLines(btr, tr, allGrids, baseInsertPoint, sortedStories[0].Coordinate);
 }

 //3. Vẽ Frames & Origin theo tầng
 foreach (var story in sortedStories)
 {
 WriteMessage($" Vẽ {story.Name} tại Z={story.Coordinate}...");

 // Origin
 string originHandle = CreateOriginForStory3D(btr, tr, baseInsertPoint, story);

 // Frames thuộc tầng
 var framesAtStory = FilterFramesForStory(allFrames, story.Coordinate);
 PlotFramesAt3D(btr, tr, framesAtStory, baseInsertPoint, story.Coordinate, originHandle);
 }

 //4. Vẽ POINTS (Toàn bộ points trong vùng Z của các tầng được chọn)
 WriteMessage(" Vẽ Points...");
 double minZ = sortedStories.Min(s => s.Coordinate) -500;
 double maxZ = sortedStories.Max(s => s.Coordinate) +4000; // Bao gồm cả đỉnh cột tầng trên cùng

 var pointsToDraw = allPoints.Where(p => p.Z >= minZ && p.Z <= maxZ).ToList();

 foreach (var pt in pointsToDraw)
 {
 Point3d p3d = new Point3d(pt.X + baseInsertPoint.X, pt.Y + baseInsertPoint.Y, pt.Z);
 PlotPoint3D(btr, tr, p3d);
 }

 });

 WriteSuccess("Vẽ mô hình3D hoàn thành");
 }

 // ======================= LOW LEVEL PLOTTING =======================

 private void PlotGridLines(BlockTableRecord btr, Transaction tr, List<SapUtils.GridLineRecord> grids, Point2D offset, double z)
 {
 if (grids == null || grids.Count ==0) return;

 CalculateGridParams(grids, out double minX, out double maxX, out double minY, out double maxY, out double textH);

 var xGrids = grids.Where(g => string.Equals(g.Orientation, "X", StringComparison.OrdinalIgnoreCase)).ToList();
 var yGrids = grids.Where(g => string.Equals(g.Orientation, "Y", StringComparison.OrdinalIgnoreCase)).ToList();

 // Vẽ trục X (Dọc)
 foreach (var x in xGrids)
 {
 Point3d p1 = new Point3d(x.Coordinate + offset.X, minY + offset.Y, z);
 Point3d p2 = new Point3d(x.Coordinate + offset.X, maxY + offset.Y, z);
 
 var line = new Line(p1, p2) { Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);

 // FIX2: Text to hơn và màu7
 PlotAxisLabel(btr, tr, x.Name, new Point3d(p1.X, maxY + offset.Y + textH *0.5, z), textH);
 PlotAxisLabel(btr, tr, x.Name, new Point3d(p1.X, minY + offset.Y - textH *0.5, z), textH);
 }

 // Vẽ trục Y (Ngang)
 foreach (var y in yGrids)
 {
 Point3d p1 = new Point3d(minX + offset.X, y.Coordinate + offset.Y, z);
 Point3d p2 = new Point3d(maxX + offset.X, y.Coordinate + offset.Y, z);

 var line = new Line(p1, p2) { Layer = AXIS_LAYER, ColorIndex =5 };
 btr.AppendEntity(line); tr.AddNewlyCreatedDBObject(line, true);

 PlotAxisLabel(btr, tr, y.Name, new Point3d(minX + offset.X - textH, p1.Y, z), textH);
 PlotAxisLabel(btr, tr, y.Name, new Point3d(maxX + offset.X + textH, p1.Y, z), textH);
 }
 }

 private void PlotAxisLabel(BlockTableRecord btr, Transaction tr, string name, Point3d position, double height)
 {
 if (string.IsNullOrWhiteSpace(name)) return;
 MText m = new MText 
 { 
 Contents = name, 
 Location = position, 
 TextHeight = height, 
 Layer = AXIS_LAYER, 
 ColorIndex =7, // FIX2: White/Black
 Attachment = AttachmentPoint.MiddleCenter
 };
 btr.AppendEntity(m); tr.AddNewlyCreatedDBObject(m, true);
 }

 private void PlotFramesAt(BlockTableRecord btr, Transaction tr, List<SapFrame> frames, 
 Point2D offset, double storyZ, string originHandle, bool is3D)
 {
 if (frames == null) return;

 foreach (var f in frames)
 {
 Point3d p1, p2;

 if (is3D)
 {
 // Tọa độ3D thật
 p1 = new Point3d(f.StartPt.X + offset.X, f.StartPt.Y + offset.Y, f.Z1);
 p2 = new Point3d(f.EndPt.X + offset.X, f.EndPt.Y + offset.Y, f.Z2);
 }
 else
 {
 // Chiếu xuống mặt bằng2D
 p1 = new Point3d(f.StartPt.X + offset.X, f.StartPt.Y + offset.Y,0);
 p2 = new Point3d(f.EndPt.X + offset.X, f.EndPt.Y + offset.Y,0);
 }

 // Nếu là cột trong chế độ2D, vẽ điểm hoặc skip
 if (f.IsVertical && !is3D)
 {
 // Vẽ cột dạng Point/Circle trên mặt bằng (hiển thị và gán XData để link)
 Circle col = new Circle { Center = p1, Radius =100, Layer = POINT_LAYER, ColorIndex =3 };
 ObjectId colId = btr.AppendEntity(col);
 tr.AddNewlyCreatedDBObject(col, true);

 // Gán XData cho marker point để có thể liên kết (Origin/Link)
 var colData = new ColumnData { SectionName = f.Section, Material = "Concrete", BaseZ = Math.Min(f.Z1, f.Z2) };
 DBObject colObj = tr.GetObject(colId, OpenMode.ForWrite);
 XDataUtils.WriteElementData(colObj, colData, tr);

 if (!string.IsNullOrEmpty(originHandle))
 {
 AddChildToOrigin(originHandle, colId.Handle.ToString(), tr);
 }

 continue;
 }
 
 // Vẽ Line cho Dầm (hoặc Cột3D)
 // Logic màu: Beam =2 (Yellow), Column =3 (Green)
 var line = new Line(p1, p2) { Layer = FRAME_LAYER, ColorIndex = f.IsBeam ?2 :3 };
 ObjectId id = btr.AppendEntity(line); 
 tr.AddNewlyCreatedDBObject(line, true);

 // FIX4: Phân loại Data
 ElementData elemData;
 if (f.IsBeam)
 {
 elemData = new BeamData { SectionName = f.Section, Length = f.Length2D, BeamType = "Main" };
 }
 else
 {
 elemData = new ColumnData { SectionName = f.Section, Material = "Concrete" };
 }

 elemData.BaseZ = Math.Min(f.Z1, f.Z2);
 elemData.Height = Math.Abs(f.Z2 - f.Z1);

 // Link Origin
 if (!string.IsNullOrEmpty(originHandle)) elemData.OriginHandle = originHandle;

 XDataUtils.WriteElementData(line, elemData, tr);

 try { LabelUtils.RefreshEntityLabel(id, tr); } catch { }

 if (!string.IsNullOrEmpty(originHandle))
 {
 AddChildToOrigin(originHandle, id.Handle.ToString(), tr);
 }

 // Vẽ điểm node (3D only)
 if (is3D)
 {
 PlotPoint3D(btr, tr, p1);
 PlotPoint3D(btr, tr, p2);
 }
 }
 }

 private void PlotPoint3D(BlockTableRecord btr, Transaction tr, Point3d pt)
 {
 Circle circle = new Circle
 {
 Center = pt,
 Radius =50,
 Layer = POINT_LAYER,
 ColorIndex =3,
 Normal = new Vector3d(0,0,1) // Luôn nằm ngang
 };
 btr.AppendEntity(circle);
 tr.AddNewlyCreatedDBObject(circle, true);
 }

 // Plot frames for3D mode (do not create points here)
 private void PlotFramesAt3D(BlockTableRecord btr, Transaction tr, List<SapFrame> frames,
 Point2D offset, double elevation, string originHandle)
 {
 if (frames == null) return;

 foreach (var frame in frames)
 {
 Point3d pt1 = new Point3d(frame.StartPt.X + offset.X, frame.StartPt.Y + offset.Y, frame.Z1);
 Point3d pt2 = new Point3d(frame.EndPt.X + offset.X, frame.EndPt.Y + offset.Y, frame.Z2);

 Line line = new Line(pt1, pt2);
 line.Layer = FRAME_LAYER;
 line.ColorIndex = frame.IsBeam ?2 :3;

 ObjectId lineId = btr.AppendEntity(line);
 tr.AddNewlyCreatedDBObject(line, true);

 // Gán Data
 ElementData elemData;
 if (frame.IsBeam)
 elemData = new BeamData { SectionName = frame.Section, Length = frame.Length2D, BeamType = "Main" };
 else
 elemData = new ColumnData { SectionName = frame.Section, Material = "Concrete" };

 elemData.BaseZ = frame.Z1; //3D dùng Z thực
 elemData.Height = Math.Abs(frame.Z2 - frame.Z1);
 if (!string.IsNullOrEmpty(originHandle)) elemData.OriginHandle = originHandle;

 XDataUtils.WriteElementData(tr.GetObject(lineId, OpenMode.ForRead), elemData, tr);

 // Label3D (Sử dụng LabelPlotter mới)
 try { LabelUtils.RefreshEntityLabel(lineId, tr); } catch { }

 if (!string.IsNullOrEmpty(originHandle))
 AddChildToOrigin(originHandle, lineId.Handle.ToString(), tr);
 }
 }

 private void PlotPoint(BlockTableRecord btr, Transaction tr, Point3d pt)
 {
 Circle circle = new Circle
 {
 Center = pt,
 Radius =50,
 Layer = POINT_LAYER,
 ColorIndex =3
 };
 btr.AppendEntity(circle);
 tr.AddNewlyCreatedDBObject(circle, true);
 }

 private string CreateOriginForStory(BlockTableRecord btr, Transaction tr, Point2D insertPoint, SapUtils.GridStoryItem story)
 {
 return CreateOriginCommon(btr, tr, new Point3d(insertPoint.X, insertPoint.Y,0), story);
 }

 private string CreateOriginForStory3D(BlockTableRecord btr, Transaction tr, Point2D insertPoint, SapUtils.GridStoryItem story)
 {
 return CreateOriginCommon(btr, tr, new Point3d(insertPoint.X, insertPoint.Y, story.Coordinate), story);
 }

 private string CreateOriginCommon(BlockTableRecord btr, Transaction tr, Point3d center, SapUtils.GridStoryItem story)
 {
 //1. Vẽ vòng tròn
 Circle c = new Circle { Center = center, Radius =500, Layer = ORIGIN_LAYER, ColorIndex =1 };
 ObjectId id = btr.AppendEntity(c); 
 tr.AddNewlyCreatedDBObject(c, true);

 //2. Gán XData
 var sd = new StoryData { StoryName = story.Name, Elevation = story.Coordinate, StoryHeight =3300, OffsetX = center.X, OffsetY = center.Y };
 DBObject obj = tr.GetObject(id, OpenMode.ForRead);
 XDataUtils.WriteStoryData(obj, sd, tr);

 // FIX3: Thêm Label text dưới Origin
 MText lbl = new MText
 {
 Contents = $"{story.Name}\nZ={story.Coordinate}",
 Location = new Point3d(center.X, center.Y -600, center.Z), // Offset xuống dưới600mm
 TextHeight =250,
 Layer = ORIGIN_LAYER,
 ColorIndex =0,
 Attachment = AttachmentPoint.TopCenter
 };
 btr.AppendEntity(lbl);
 tr.AddNewlyCreatedDBObject(lbl, true);

 return id.Handle.ToString();
 }

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
 }
 #endregion
 }
}