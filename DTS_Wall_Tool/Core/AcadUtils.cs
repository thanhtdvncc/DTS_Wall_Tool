using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.Core
{
    // Class tĩnh (Static) chứa các hàm tiện ích dùng chung
    public static class AcadUtils
    {
        // Các thuộc tính (Properties) giúp truy cập nhanh vào AutoCAD
        public static Document Doc => Application.DocumentManager.MdiActiveDocument;
        public static Database Db => Doc.Database;
        public static Editor Ed => Doc.Editor;

        // --- HÀM 1: Quản lý Transaction (Giao dịch) ---
        // Mọi thay đổi bản vẽ (Vẽ, Xóa, Sửa) đều phải nằm trong hàm này để an toàn
        public static void UsingTransaction(Action<Transaction> action)
        {
            using (Transaction tr = Db.TransactionManager.StartTransaction())
            {
                try
                {
                    action(tr); // Thực hiện hành động
                    tr.Commit(); // Lưu thay đổi
                }
                catch (System.Exception ex)
                {
                    tr.Abort(); // Hủy bỏ nếu có lỗi
                    Ed.WriteMessage($"\n[LỖI Transaction]: {ex.Message}");
                }
            }
        }

        // --- HÀM 2: CHỌN ĐỐI TƯỢNG ĐA NĂNG (ĐÃ SỬA LỖI TEXT & LỌC RÁC) ---
        public static List<ObjectId> SelectObjectsOnScreen(string types)
        {
            List<ObjectId> resultIds = new List<ObjectId>();

            // 1. Tạo bộ lọc
            // Lọc theo loại (LINE, CIRCLE...) VÀ KHÔNG thuộc các layer rác của tool
            TypedValue[] filterList = new TypedValue[]
            {
                new TypedValue((int)DxfCode.Start, types),
                new TypedValue((int)DxfCode.Operator, "<NOT"), // Bắt đầu phủ định
                    new TypedValue((int)DxfCode.LayerName, "dts_linkmap,dts_highlight"), // Các layer cần bỏ
                new TypedValue((int)DxfCode.Operator, "NOT>")  // Kết thúc phủ định
            };
            SelectionFilter filter = new SelectionFilter(filterList);

            PromptSelectionOptions opts = new PromptSelectionOptions();
            // Sửa lỗi hiển thị {types} bằng String.Format truyền thống cho chắc chắn
            opts.MessageForAdding = string.Format("\nQuét chọn đối tượng ({0}) [Đã tự lọc bỏ Link/Highlight]: ", types);

            PromptSelectionResult selRes = Ed.GetSelection(opts, filter);

            if (selRes.Status == PromptStatus.OK)
            {
                resultIds.AddRange(selRes.Value.GetObjectIds());
            }

            return resultIds;
        }


        // --- HÀM 3: Đọc tọa độ của một đường Line ---
        // Chuyển đổi từ Line của AutoCAD sang Point2D của chúng ta
        public static void GetLinePoints(ObjectId lineId, Transaction tr, out Point2D startPt, out Point2D endPt)
        {
            // Mở đối tượng để đọc
            Line lineEnt = tr.GetObject(lineId, OpenMode.ForRead) as Line;

            if (lineEnt != null)
            {
                // Lấy điểm đầu và điểm cuối, chuyển sang Point2D
                startPt = new Point2D(lineEnt.StartPoint.X, lineEnt.StartPoint.Y);
                endPt = new Point2D(lineEnt.EndPoint.X, lineEnt.EndPoint.Y);
            }
            else
            {
                startPt = new Point2D(0, 0);
                endPt = new Point2D(0, 0);
            }
        }

        // Hàm tạo Layer mới (nếu chưa có)
        // colorIndex: 1=Đỏ, 2=Vàng, 3=Xanh lá, ...
        public static void CreateLayer(string layerName, short colorIndex)
        {
            UsingTransaction(tr =>
            {
                LayerTable lt = (LayerTable)tr.GetObject(Db.LayerTableId, OpenMode.ForRead);
                if (!lt.Has(layerName))
                {
                    lt.UpgradeOpen(); // Chuyển sang chế độ ghi
                    LayerTableRecord newLayer = new LayerTableRecord
                    {
                        Name = layerName,
                        Color = Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, colorIndex)
                    };

                    lt.Add(newLayer);
                    tr.AddNewlyCreatedDBObject(newLayer, true);
                }
            });
        }

        // --- HÀM 6: Lấy tâm của đối tượng (Line hoặc Circle) ---
        public static Point2D GetEntityCenter(Entity ent)
        {
            if (ent is Line line)
            {
                // Trung điểm đoạn thẳng: (x1+x2)/2
                return new Point2D(
                    (line.StartPoint.X + line.EndPoint.X) / 2.0,
                    (line.StartPoint.Y + line.EndPoint.Y) / 2.0
                );
            }
            else if (ent is Circle circle)
            {
                return new Point2D(circle.Center.X, circle.Center.Y);
            }
            return new Point2D(0, 0);
        }

        // --- HÀM 7: Tìm đối tượng từ mã Handle (Quan trọng cho Link) ---
        public static ObjectId GetObjectIdFromHandle(string handleString)
        {
            if (string.IsNullOrEmpty(handleString)) return ObjectId.Null;

            try
            {
                // Handle trong CAD là số Hex (hệ 16), cần chuyển đổi
                long ln = Convert.ToInt64(handleString, 16);
                Handle hn = new Handle(ln);
                return Db.GetObjectId(false, hn, 0);
            }
            catch
            {
                return ObjectId.Null;
            }
        }

        // --- HÀM 8: Vẽ đường Line (Hỗ trợ layer, màu) ---
        public static void CreateVisualLine(Point2D p1, Point2D p2, string layer, int colorIndex, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            Line l = new Line(new Point3d(p1.X, p1.Y, 0), new Point3d(p2.X, p2.Y, 0));
            l.Layer = layer;
            l.ColorIndex = colorIndex;

            // Mẹo: Nếu muốn vẽ nét đứt, bạn cần load Linetype trước. 
            // Để đơn giản, ta cứ vẽ nét liền màu vàng là đủ nổi bật.

            btr.AppendEntity(l);
            tr.AddNewlyCreatedDBObject(l, true);
        }

        // --- HÀM 9: Xóa sạch đối tượng trên một Layer (Dùng cho lệnh Clear) ---
        public static void ClearLayer(string layerName)
        {
            // Tạo bộ lọc chỉ chọn đối tượng thuộc layer này
            TypedValue[] tvs = new TypedValue[] {
                new TypedValue((int)DxfCode.LayerName, layerName)
            };
            SelectionFilter filter = new SelectionFilter(tvs);

            // Chọn toàn bộ bản vẽ (nhanh hơn bắt người dùng quét)
            PromptSelectionResult sel = Ed.SelectAll(filter);

            if (sel.Status == PromptStatus.OK)
            {
                UsingTransaction(tr =>
                {
                    foreach (ObjectId id in sel.Value.GetObjectIds())
                    {
                        Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
                        if (ent != null) ent.Erase(); // Xóa ngay lập tức
                    }
                });
            }
        }

        // --- HÀM 10: Vẽ vòng tròn Highlight bao quanh đối tượng ---
        public static void CreateHighlightCircle(Point2D center, double radius, string layer, int colorIndex, Transaction tr)
        {
            BlockTable bt = (BlockTable)tr.GetObject(Db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            Circle c = new Circle();
            c.Center = new Point3d(center.X, center.Y, 0);
            c.Radius = radius;
            c.Layer = layer;
            c.ColorIndex = colorIndex;

            btr.AppendEntity(c);
            tr.AddNewlyCreatedDBObject(c, true);
        }










    }
}