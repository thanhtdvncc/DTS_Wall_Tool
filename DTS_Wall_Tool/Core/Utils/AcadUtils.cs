using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using DTS_Wall_Tool.Core.Primitives;
using System;
using System.Collections.Generic;

namespace DTS_Wall_Tool.Core.Utils
{
    /// <summary>
    /// Tiện ích làm việc với AutoCAD
    /// </summary>
    public static class AcadUtils
    {
        #region AutoCAD Access Properties

        public static Document Doc => Application.DocumentManager.MdiActiveDocument;
        public static Database Db => Doc.Database;
        public static Editor Ed => Doc.Editor;

        #endregion

        #region Transaction Management

        /// <summary>
        /// Thực hiện action trong transaction an toàn
        /// </summary>
        public static void UsingTransaction(Action<Transaction> action)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                try
                {
                    action(tr);
                    tr.Commit();
                }
                catch (System.Exception ex)
                {
                    tr.Abort();
                    Ed.WriteMessage($"\n[LỖI Transaction]: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Thực hiện action trong transaction và trả về kết quả
        /// </summary>
        public static T UsingTransaction<T>(Func<Transaction, T> func)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                try
                {
                    T result = func(tr);
                    tr.Commit();
                    return result;
                }
                catch (System.Exception ex)
                {
                    tr.Abort();
                    Ed.WriteMessage($"\n[LỖI Transaction]: {ex.Message}");
                    return default(T);
                }
            }
        }

        #endregion

        #region Selection

        /// <summary>
        /// Chọn đối tượng trên màn hình theo loại
        /// </summary>
        public static List<ObjectId> SelectObjectsOnScreen(string types)
        {
            List<ObjectId> resultIds = new List<ObjectId>();

            TypedValue[] filterList = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, types),
                new TypedValue((int)DxfCode. Operator, "<NOT"),
                new TypedValue((int)DxfCode.LayerName, "dts_linkmap,dts_highlight,dts_temp"),
                new TypedValue((int)DxfCode. Operator, "NOT>")
            };
            SelectionFilter filter = new SelectionFilter(filterList);

            PromptSelectionOptions opts = new PromptSelectionOptions();
            opts.MessageForAdding = string.Format("\nChọn đối tượng ({0}): ", types);

            PromptSelectionResult selRes = Ed.GetSelection(opts, filter);

            if (selRes.Status == PromptStatus.OK)
            {
                resultIds.AddRange(selRes.Value.GetObjectIds());
            }

            return resultIds;
        }

        /// <summary>
        /// Chọn tất cả đối tượng theo loại trong bản vẽ
        /// </summary>
        public static List<ObjectId> SelectAll(string types)
        {
            List<ObjectId> resultIds = new List<ObjectId>();

            TypedValue[] filterList = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, types)
            };
            SelectionFilter filter = new SelectionFilter(filterList);

            PromptSelectionResult selRes = Ed.SelectAll(filter);

            if (selRes.Status == PromptStatus.OK)
            {
                resultIds.AddRange(selRes.Value.GetObjectIds());
            }

            return resultIds;
        }

        #endregion

        #region Geometry Conversion

        /// <summary>
        /// Lấy tọa độ điểm đầu và cuối của Line
        /// </summary>
        public static void GetLinePoints(ObjectId lineId, Transaction tr, out Point2D startPt, out Point2D endPt)
        {
            Line lineEnt = tr.GetObject(lineId, OpenMode.ForRead) as Line;

            if (lineEnt != null)
            {
                startPt = new Point2D(lineEnt.StartPoint.X, lineEnt.StartPoint.Y);
                endPt = new Point2D(lineEnt.EndPoint.X, lineEnt.EndPoint.Y);
            }
            else
            {
                startPt = Point2D.Origin;
                endPt = Point2D.Origin;
            }
        }

        /// <summary>
        /// Lấy LineSegment2D từ Line entity
        /// </summary>
        public static LineSegment2D GetLineSegment(Line lineEnt)
        {
            return new LineSegment2D(
                new Point2D(lineEnt.StartPoint.X, lineEnt.StartPoint.Y),
                new Point2D(lineEnt.EndPoint.X, lineEnt.EndPoint.Y)
            );
        }

        /// <summary>
        /// Lấy tâm của entity
        /// </summary>
        public static Point2D GetEntityCenter(Entity ent)
        {
            if (ent is Line line)
            {
                return new Point2D(
                    (line.StartPoint.X + line.EndPoint.X) / 2.0,
                    (line.StartPoint.Y + line.EndPoint.Y) / 2.0
                );
            }
            else if (ent is Circle circle)
            {
                return new Point2D(circle.Center.X, circle.Center.Y);
            }
            else if (ent is Polyline pline)
            {
                var ext = pline.GeometricExtents;
                return new Point2D(
                    (ext.MinPoint.X + ext.MaxPoint.X) / 2.0,
                    (ext.MinPoint.Y + ext.MaxPoint.Y) / 2.0
                );
            }
            return Point2D.Origin;
        }

        /// <summary>
        /// Chuyển Point2D sang Point3d
        /// </summary>
        public static Point3d ToPoint3d(Point2D pt, double z = 0)
        {
            return new Point3d(pt.X, pt.Y, z);
        }

        /// <summary>
        /// Chuyển Point3d sang Point2D
        /// </summary>
        public static Point2D ToPoint2D(Point3d pt)
        {
            return new Point2D(pt.X, pt.Y);
        }

        #endregion

        #region Handle Operations

        /// <summary>
        /// Lấy ObjectId từ Handle string
        /// </summary>
        public static ObjectId GetObjectIdFromHandle(string handleString)
        {
            if (string.IsNullOrEmpty(handleString)) return ObjectId.Null;

            try
            {
                long ln = Convert.ToInt64(handleString, 16);
                Handle hn = new Handle(ln);
                return Db.GetObjectId(false, hn, 0);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        /// <summary>
        /// Lấy Handle string từ ObjectId
        /// </summary>
        public static string GetHandleString(ObjectId id)
        {
            if (id == ObjectId.Null) return "";
            return id.Handle.ToString();
        }

        #endregion

        #region Layer Management

        /// <summary>
        /// Tạo layer mới nếu chưa tồn tại
        /// </summary>
        public static void CreateLayer(string layerName, short colorIndex)
        {
            UsingTransaction(tr =>
            {
                LayerTable lt = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen();
                    LayerTableRecord newLayer = new LayerTableRecord
                    {
                        Name = layerName,
                        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(
                            Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
                    };

                    lt.Add(newLayer);
                    tr.AddNewlyCreatedDBObject(newLayer, true);
                }
            });
        }

        /// <summary>
        /// Xóa tất cả đối tượng trên layer
        /// </summary>
        public static void ClearLayer(string layerName)
        {
            TypedValue[] tvs = new TypedValue[] {
                new TypedValue((int)DxfCode.LayerName, layerName)
            };
            SelectionFilter filter = new SelectionFilter(tvs);

            PromptSelectionResult sel = Ed.SelectAll(filter);

            if (sel.Status == PromptStatus.OK)
            {
                UsingTransaction(tr =>
                {
                    foreach (ObjectId id in sel.Value.GetObjectIds())
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null) ent.Erase();
                    }
                });
            }
        }

        #endregion

        #region Drawing Helpers

        /// <summary>
        /// Vẽ đường Line
        /// </summary>
        public static ObjectId CreateLine(Point2D p1, Point2D p2, string layer, int colorIndex, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            Line l = new Line(ToPoint3d(p1), ToPoint3d(p2));
            l.Layer = layer;
            l.ColorIndex = colorIndex;

            ObjectId id = btr.AppendEntity(l);
            tr.AddNewlyCreatedDBObject(l, true);
            return id;
        }

        /// <summary>
        /// Vẽ đường Line từ LineSegment2D
        /// </summary>
        public static ObjectId CreateLine(LineSegment2D segment, string layer, int colorIndex, Transaction tr)
        {
            return CreateLine(segment.Start, segment.End, layer, colorIndex, tr);
        }

        /// <summary>
        /// Vẽ vòng tròn
        /// </summary>
        public static ObjectId CreateCircle(Point2D center, double radius, string layer, int colorIndex, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            Circle c = new Circle();
            c.Center = ToPoint3d(center);
            c.Radius = radius;
            c.Layer = layer;
            c.ColorIndex = colorIndex;

            ObjectId id = btr.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);
            return id;
        }

        /// <summary>
        /// Vẽ MText
        /// </summary>
        public static ObjectId CreateMText(Point2D position, string content, double textHeight,
            string layer, int colorIndex, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            MText mtext = new MText();
            mtext.Contents = content;
            mtext.Location = ToPoint3d(position);
            mtext.TextHeight = textHeight;
            mtext.Layer = layer;
            mtext.ColorIndex = colorIndex;

            ObjectId id = btr.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
            return id;
        }

        /// <summary>
        /// Alias cho CreateLine (backward compatibility)
        /// </summary>
        public static void CreateVisualLine(Point2D p1, Point2D p2, string layer, int colorIndex, Transaction tr)
        {
            CreateLine(p1, p2, layer, colorIndex, tr);
        }

        /// <summary>
        /// Alias cho CreateCircle (backward compatibility)
        /// </summary>
        public static void CreateHighlightCircle(Point2D center, double radius, string layer, int colorIndex, Transaction tr)
        {
            CreateCircle(center, radius, layer, colorIndex, tr);
        }

        #endregion

        #region Entity Properties

        /// <summary>
        /// Đổi màu entity
        /// </summary>
        public static void SetEntityColor(ObjectId id, int colorIndex, Transaction tr)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
            if (ent != null)
            {
                ent.ColorIndex = colorIndex;
            }
        }

        /// <summary>
        /// Lấy chiều dài của Line
        /// </summary>
        public static double GetLineLength(ObjectId lineId, Transaction tr)
        {
            Line line = tr.GetObject(lineId, OpenMode.ForRead) as Line;
            return line?.Length ?? 0;
        }

        #endregion

        /// <summary>
        /// Tìm Origin Circle trên layer dts_origin
        /// </summary>
        public static Point2D? FindOriginCircle()
        {
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null) return null;

                using (var tr = doc.TransactionManager.StartTransaction())
                {
                    var bt = tr.GetObject(doc.Database.BlockTableId, OpenMode.ForRead) as BlockTable;
                    var btr = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead) as BlockTableRecord;

                    foreach (ObjectId objId in btr)
                    {
                        var ent = tr.GetObject(objId, OpenMode.ForRead) as Entity;
                        if (ent == null) continue;

                        // Check if circle on dts_origin layer
                        if (ent is Circle circle &&
                            circle.Layer.Equals("dts_origin", System.StringComparison.OrdinalIgnoreCase))
                        {
                            return new Point2D(circle.Center.X, circle.Center.Y);
                        }
                    }
                    tr.Commit();
                }
            }
            catch { }

            return null;
        }











    }

}