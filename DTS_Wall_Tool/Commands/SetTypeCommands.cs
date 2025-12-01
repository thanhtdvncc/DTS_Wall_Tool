using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using DTS_Wall_Tool.Core.Data;
using DTS_Wall_Tool.Core.Utils;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Wall_Tool.Commands
{
 public class SetTypeCommands : CommandBase
 {
 [CommandMethod("DTS_SET_TYPE")]
 public void DTS_SET_TYPE()
 {
 WriteMessage("Ch?n ??i t??ng ?? set type...");

 var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
 if (ids.Count ==0)
 {
 WriteMessage("Không có ??i t??ng nào ???c ch?n.");
 return;
 }

 // Build selectable ElementType list (structural types)
 var allTypes = System.Enum.GetValues(typeof(ElementType)).Cast<ElementType>()
 .Where(t => t.IsStructuralElement())
 .ToList();

 // Show menu
 WriteMessage("Ch?n lo?i ph?n t? ?? gán cho các ??i t??ng ?ã ch?n:");
 for (int i =0; i < allTypes.Count; i++)
 {
 WriteMessage($" {i +1}. {GetElementTypeDisplayName(allTypes[i])} ({allTypes[i]})");
 }

 var intOpts = new Autodesk.AutoCAD.EditorInput.PromptIntegerOptions("\nNh?p s? t??ng ?ng (0 ?? h?y): ")
 {
 DefaultValue =0,
 AllowNone = false,
 LowerLimit =0,
 UpperLimit = allTypes.Count
 };

 var intRes = Ed.GetInteger(intOpts);
 if (intRes.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK)
 {
 WriteMessage("H?y thao tác.");
 return;
 }

 int selIndex = intRes.Value;
 if (selIndex ==0)
 {
 WriteMessage("H?y thao tác.");
 return;
 }

 ElementType chosenType = allTypes[selIndex -1];
 WriteMessage($"?ang gán lo?i: {GetElementTypeDisplayName(chosenType)} cho {ids.Count} ??i t??ng...");

 var assignedStats = new Dictionary<ElementType, int>();
 int skippedCountAlready =0;
 int originProtectedCount =0;
 int undeterminedCount =0; // not used here but kept

 UsingTransaction(tr =>
 {
 foreach (var id in ids)
 {
 DBObject obj = tr.GetObject(id, OpenMode.ForWrite);

 // Protect Story/Origin
 var story = XDataUtils.ReadStoryData(obj);
 if (story != null)
 {
 originProtectedCount++;
 continue;
 }

 // If already has ElementData then skip
 var existing = XDataUtils.ReadElementData(obj);
 if (existing != null)
 {
 skippedCountAlready++;
 continue;
 }

 // Create instance based on chosen type
 ElementData newData = CreateElementDataOfType(chosenType);
 if (newData == null)
 {
 undeterminedCount++;
 continue;
 }

 // Write minimal data (type) to XData
 XDataUtils.WriteElementData(obj, newData, tr);

 if (!assignedStats.ContainsKey(newData.ElementType)) assignedStats[newData.ElementType] =0;
 assignedStats[newData.ElementType]++;
 }
 });

 // Report
 if (assignedStats.Count >0)
 {
 var parts = assignedStats.OrderBy(x => x.Key)
 .Select(kvp => $"{kvp.Value} {GetElementTypeDisplayName(kvp.Key)}")
 .ToArray();

 WriteSuccess($"?ã gán: {string.Join(", ", parts)}.");
 }

 if (skippedCountAlready >0)
 {
 WriteMessage($"B? qua: {skippedCountAlready} ph?n t? (?ã có thu?c tính).");
 }

 if (originProtectedCount >0)
 {
 WriteMessage($"B?o v?: {originProtectedCount} ??i t??ng Origin/Story (không th? gán type).");
 }

 if (undeterminedCount >0)
 {
 WriteMessage($"Không xác ??nh lo?i cho {undeterminedCount} ph?n t?. Hãy ??t th? công ho?c ki?m tra layer.");
 }
 }

 [CommandMethod("DTS_CLEAR_TYPE")]
 public void DTS_CLEAR_TYPE()
 {
 WriteMessage("Ch?n ??i t??ng ?? xóa type (hành ??ng này s? xóa toàn b? thu?c tính DTS c?a ph?n t?)...");

 var ids = AcadUtils.SelectObjectsOnScreen("LINE,LWPOLYLINE,POLYLINE,CIRCLE");
 if (ids.Count ==0)
 {
 WriteMessage("Không có ??i t??ng nào ???c ch?n.");
 return;
 }

 // Confirm
 var pko = new Autodesk.AutoCAD.EditorInput.PromptKeywordOptions("Xác nh?n xóa t?t c? DTS data cho các ph?n t? ?ã ch?n? [Yes/No]: ", "Yes No");
 var pres = Ed.GetKeywords(pko);
 if (pres.Status != Autodesk.AutoCAD.EditorInput.PromptStatus.OK || pres.StringResult != "Yes")
 {
 WriteMessage("H?y thao tác xóa type.");
 return;
 }

 int cleared =0;
 int skippedOrigins =0;

 UsingTransaction(tr =>
 {
 foreach (var id in ids)
 {
 DBObject obj = tr.GetObject(id, OpenMode.ForWrite);

 // Protect Story/Origin
 var story = XDataUtils.ReadStoryData(obj);
 if (story != null)
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

 WriteSuccess($"?ã xóa d? li?u DTS cho {cleared} ph?n t?.");
 if (skippedOrigins >0) WriteMessage($"B? qua {skippedOrigins} Origin ???c b?o v?.");
 }

 private ElementData CreateElementDataOfType(ElementType type)
 {
 switch (type)
 {
 case ElementType.Beam: return new BeamData();
 case ElementType.Column: return new ColumnData();
 case ElementType.Slab: return new SlabData();
 case ElementType.Wall: return new WallData();
 case ElementType.Foundation: return new WallData();
 case ElementType.Stair: return new WallData();
 case ElementType.Pile: return new WallData();
 case ElementType.Lintel: return new WallData();
 case ElementType.Rebar: return new WallData();
 case ElementType.ShearWall: return new WallData();
 default: return null;
 }
 }

 private string GetElementTypeDisplayName(ElementType type)
 {
 switch (type)
 {
 case ElementType.Beam: return "D?m";
 case ElementType.Column: return "C?t";
 case ElementType.Slab: return "Sàn";
 case ElementType.Wall: return "T??ng";
 case ElementType.Foundation: return "Móng";
 case ElementType.Stair: return "C?u thang";
 case ElementType.Pile: return "C?c";
 case ElementType.Lintel: return "Lanh tô";
 case ElementType.Rebar: return "C?t thép";
 case ElementType.ShearWall: return "Vách";
 case ElementType.StoryOrigin: return "Origin";
 case ElementType.ElementOrigin: return "Element Origin";
 default: return "Khác/Không xác ??nh";
 }
 }
 }
}
