using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Utils;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Commands
{
    /// <summary>
    /// Cac lenh gan va xoa kieu phan tu.
    /// Tuan thu ISO/IEC 25010: Functional Suitability, Usability.
    /// </summary>
    public class SetTypeCommands : CommandBase
    {
        [CommandMethod("DTS_SET_TYPE")]
        public void DTS_SET_TYPE()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("Chọn đối tượng để gán type...");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (ids.Count == 0)
                {
                    WriteMessage("Không có đối tượng nào được chọn.");
                    return;
                }

                // Build selectable ElementType list (structural types)
                var allTypes = System.Enum.GetValues(typeof(ElementType)).Cast<ElementType>()
                    .Where(t => t.IsStructuralElement())
                    .ToList();

                // Show menu
                WriteMessage("Chọn loại phần tử để gán cho các đối tượng đã chọn:");
                for (int i = 0; i < allTypes.Count; i++)
                {
                    WriteMessage($" {i + 1}. {GetElementTypeDisplayName(allTypes[i])} ({allTypes[i]})");
                }

                var intOpts = new PromptIntegerOptions("\nNhập số tương ứng (0 để hủy): ")
                {
                    DefaultValue = 0,
                    AllowNone = false,
                    LowerLimit = 0,
                    UpperLimit = allTypes.Count
                };

                var intRes = Ed.GetInteger(intOpts);
                if (intRes.Status != PromptStatus.OK || intRes.Value == 0)
                {
                    WriteMessage("Thao tác bị hủy.");
                    return;
                }

                ElementType chosenType = allTypes[intRes.Value - 1];
                WriteMessage($"Đang gán loại: {GetElementTypeDisplayName(chosenType)} cho {ids.Count} đối tượng...");

                var assignedStats = new Dictionary<ElementType, int>();
                int skippedCountAlready = 0;
                int originProtectedCount = 0;

                UsingTransaction(tr =>
                {
                    foreach (var id in ids)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForWrite);

                        // Protect Story/Origin
                        if (XDataUtils.ReadStoryData(obj) != null)
                        {
                            originProtectedCount++;
                            continue;
                        }

                        // If already has ElementData then skip
                        if (XDataUtils.ReadElementData(obj) != null)
                        {
                            skippedCountAlready++;
                            continue;
                        }

                        // Create instance based on chosen type
                        ElementData newData = CreateElementDataOfType(chosenType);
                        if (newData == null) continue;

                        // Write minimal data (type) to XData
                        XDataUtils.WriteElementData(obj, newData, tr);

                        if (!assignedStats.ContainsKey(newData.ElementType))
                            assignedStats[newData.ElementType] = 0;
                        assignedStats[newData.ElementType]++;
                    }
                });

                // Report
                if (assignedStats.Count > 0)
                {
                    var parts = assignedStats.OrderBy(x => x.Key)
                        .Select(kvp => $"{kvp.Value} {GetElementTypeDisplayName(kvp.Key)}")
                        .ToArray();

                    WriteSuccess($"Đã gán: {string.Join(", ", parts)}.");
                }

                if (skippedCountAlready > 0)
                    WriteMessage($"Bỏ qua: {skippedCountAlready} phần tử (đã có thuộc tính).");

                if (originProtectedCount > 0)
                    WriteMessage($"Bị bỏ qua vì là Origin/Story: {originProtectedCount} đối tượng.");
            });
        }

        [CommandMethod("DTS_CLEAR_TYPE")]
        public void DTS_CLEAR_TYPE()
        {
            ExecuteSafe(() =>
            {
                WriteMessage("Chọn đối tượng để xóa type (hành động này sẽ xóa toàn bộ thuộc tính DTS của phần tử)...");

                var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
                if (ids.Count == 0)
                {
                    WriteMessage("Không có đối tượng nào được chọn.");
                    return;
                }

                // Confirm
                var pko = new PromptKeywordOptions("Xác nhận xóa tất cả DTS data cho các phần tử đã chọn? [Yes/No]: ", "Yes No");
                var pres = Ed.GetKeywords(pko);
                if (pres.Status != PromptStatus.OK || pres.StringResult != "Yes")
                {
                    WriteMessage("Hủy thao tác xóa type.");
                    return;
                }

                int cleared = 0;
                int skippedOrigins = 0;

                UsingTransaction(tr =>
                {
                    foreach (var id in ids)
                    {
                        DBObject obj = tr.GetObject(id, OpenMode.ForWrite);

                        // Protect Story/Origin
                        if (XDataUtils.ReadStoryData(obj) != null)
                        {
                            skippedOrigins++;
                            continue;
                        }

                        if (XDataUtils.HasDtsData(obj))
                        {
                            XDataUtils.ClearElementData(obj, tr);
                            cleared++;
                        }
                    }
                });

                WriteSuccess($"Đã xóa dữ liệu DTS cho {cleared} phần tử.");
                if (skippedOrigins > 0)
                    WriteMessage($"Bỏ qua {skippedOrigins} Origin được bảo vệ.");
            });
        }

        #region Helpers

        private ElementData CreateElementDataOfType(ElementType type)
        {
            switch (type)
            {
                case ElementType.Beam: return new BeamData();
                case ElementType.Column: return new ColumnData();
                case ElementType.Slab: return new SlabData();
                case ElementType.Wall: return new WallData();
                case ElementType.Foundation: return new FoundationData();
                case ElementType.Stair: return new StairData();
                case ElementType.Pile: return new PileData();
                case ElementType.Lintel: return new LintelData();
                case ElementType.Rebar: return new RebarData();
                case ElementType.ShearWall: return new ShearWallData();
                default: return null;
            }
        }

        private string GetElementTypeDisplayName(ElementType type)
        {
            switch (type)
            {
                case ElementType.Beam: return "Dầm";
                case ElementType.Column: return "Cột";
                case ElementType.Slab: return "Sàn";
                case ElementType.Wall: return "Tường";
                case ElementType.Foundation: return "Móng";
                case ElementType.Stair: return "Cầu thang";
                case ElementType.Pile: return "Cọc";
                case ElementType.Lintel: return "Lanh tô";
                case ElementType.Rebar: return "Cốt thép";
                case ElementType.ShearWall: return "Vách";
                case ElementType.StoryOrigin: return "Origin";
                case ElementType.ElementOrigin: return "Element Origin";
                default: return "Khác/Không xác định";
            }
        }

        #endregion
    }
}
