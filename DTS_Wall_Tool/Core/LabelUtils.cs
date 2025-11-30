using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using System;
using static DTS_Wall_Tool.Core.Geometry;

namespace DTS_Wall_Tool.Core
{
    public static class LabelUtils
    {
        public static void UpdateLabel(ObjectId wallId, WallData wData, Transaction tr)
        {
            // 1. Nội dung Label
            string content = $"[{wallId.Handle}] {wData.WallType} {wData.LoadPattern}={wData.LoadValue}kN/m";
            foreach (var map in wData.Mappings) content += " " + map.ToString();

            // 2. Vị trí (Tâm tường)
            Entity ent = tr.GetObject(wallId, OpenMode.ForRead) as Entity;
            Point2D center = AcadUtils.GetEntityCenter(ent);
            Point3d insertPt = new Point3d(center.X, center.Y, 0);

            // 3. Vẽ MText
            BlockTable bt = (BlockTable)tr.GetObject(AcadUtils.Db.BlockTableId, OpenMode.ForRead);
            BlockTableRecord btr = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

            MText mtext = new MText();
            mtext.Contents = content;
            mtext.Location = insertPt;
            mtext.TextHeight = 200;
            mtext.Layer = "dts_linkmap";
            mtext.ColorIndex = 2; // Vàng

            btr.AppendEntity(mtext);
            tr.AddNewlyCreatedDBObject(mtext, true);
        }


        // Hàm đổi màu tường: 3 (Xanh) = OK, 1 (Đỏ) = Lỗi/Không tìm thấy dầm
        public static void SetEntityColor(ObjectId id, int colorIndex, Transaction tr)
        {
            Entity ent = tr.GetObject(id, OpenMode.ForWrite) as Entity;
            if (ent != null)
            {
                ent.ColorIndex = colorIndex;
            }
        }


    }
}