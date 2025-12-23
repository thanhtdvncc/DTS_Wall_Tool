using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;

namespace DTS_Engine.Core.Engines
{
    /// <summary>
    /// NOD-based Registry Engine - "Sổ Hộ Khẩu" trung tâm cho DTS.
    /// 
    /// ARCHITECTURE:
    /// - Sử dụng Named Object Dictionary (NOD) làm persistent database
    /// - Flexible schema cho phép mở rộng nhiều loại registry (BeamGroups, RebarHosting, Labels...)
    /// - Dual-write: Song song với XData để đảm bảo backward compatibility
    /// 
    /// NOD STRUCTURE:
    /// DTS_REGISTRY (root dictionary)
    /// ├── BEAM_GROUPS (sub-dictionary for beam groups)
    /// ├── REBAR_HOSTING (future: rebar-to-beam associations)
    /// ├── LABEL_LINKS (future: label-to-element associations)
    /// └── METADATA (registry metadata and version info)
    /// 
    /// USAGE:
    /// - RegisterEntry/LookupEntry cho generic data
    /// - RegisterBeamGroup/LookupBeamGroup cho beam-specific operations
    /// </summary>
    public static class RegistryEngine
    {
        #region Constants - Registry Keys

        /// <summary>Root dictionary name in NOD</summary>
        private const string REGISTRY_ROOT = "DTS_REGISTRY";

        /// <summary>Sub-dictionary for beam groups</summary>
        public const string CATEGORY_BEAM_GROUPS = "BEAM_GROUPS";

        /// <summary>Sub-dictionary for rebar hosting (future)</summary>
        public const string CATEGORY_REBAR_HOSTING = "REBAR_HOSTING";

        /// <summary>Sub-dictionary for label links (future)</summary>
        public const string CATEGORY_LABEL_LINKS = "LABEL_LINKS";

        /// <summary>Metadata entry</summary>
        private const string KEY_METADATA = "METADATA";

        /// <summary>Current schema version</summary>
        private const string SCHEMA_VERSION = "1.0";

        #endregion

        #region Data Classes

        /// <summary>
        /// Generic registry entry - base class for all stored data
        /// </summary>
        public class RegistryEntry
        {
            public string Key { get; set; }
            public string Category { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime ModifiedAt { get; set; }
            public Dictionary<string, object> Data { get; set; } = new Dictionary<string, object>();
        }

        /// <summary>
        /// Beam Group registry info - structured data for beam groups
        /// </summary>
        public class BeamGroupRegistryInfo
        {
            public string MotherHandle { get; set; }
            public string GroupName { get; set; }
            public string Name { get; set; } // NamingEngine label (e.g., "3GX12")
            public string GroupType { get; set; } // "Girder" or "Beam"
            public string Direction { get; set; } // "X" or "Y"
            public string AxisName { get; set; }
            public double LevelZ { get; set; }
            public double Width { get; set; }
            public double Height { get; set; }
            public List<string> ChildHandles { get; set; } = new List<string>();
            public DateTime CreatedAt { get; set; }
            public DateTime ModifiedAt { get; set; }

            /// <summary>
            /// Check if this group contains a specific handle (as mother or child)
            /// </summary>
            public bool ContainsMember(string handle)
            {
                if (MotherHandle == handle) return true;
                return ChildHandles?.Contains(handle) ?? false;
            }

            /// <summary>
            /// Get all member handles (mother + children)
            /// </summary>
            public List<string> GetAllMembers()
            {
                var result = new List<string> { MotherHandle };
                if (ChildHandles != null) result.AddRange(ChildHandles);
                return result;
            }
        }

        #endregion

        #region Core NOD Helpers

        /// <summary>
        /// Get or create the root DTS_REGISTRY dictionary
        /// </summary>
        private static DBDictionary EnsureRootDictionary(Transaction tr, OpenMode mode = OpenMode.ForWrite)
        {
            var db = HostApplicationServices.WorkingDatabase;
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);

            if (nod.Contains(REGISTRY_ROOT))
            {
                return (DBDictionary)tr.GetObject(nod.GetAt(REGISTRY_ROOT), mode);
            }

            // Create new root dictionary
            var registry = new DBDictionary();
            nod.SetAt(REGISTRY_ROOT, registry);
            tr.AddNewlyCreatedDBObject(registry, true);

            // Create metadata entry
            CreateMetadata(registry, tr);

            return registry;
        }

        /// <summary>
        /// Get or create a category sub-dictionary (e.g., BEAM_GROUPS)
        /// </summary>
        private static DBDictionary EnsureCategoryDictionary(string category, Transaction tr)
        {
            var root = EnsureRootDictionary(tr);

            if (root.Contains(category))
            {
                return (DBDictionary)tr.GetObject(root.GetAt(category), OpenMode.ForWrite);
            }

            // Create new category dictionary
            var catDict = new DBDictionary();
            root.SetAt(category, catDict);
            tr.AddNewlyCreatedDBObject(catDict, true);

            return catDict;
        }

        /// <summary>
        /// Get category dictionary if exists, returns null otherwise
        /// </summary>
        private static DBDictionary GetCategoryDictionary(string category, Transaction tr)
        {
            var db = HostApplicationServices.WorkingDatabase;
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);

            if (!nod.Contains(REGISTRY_ROOT)) return null;

            var root = (DBDictionary)tr.GetObject(nod.GetAt(REGISTRY_ROOT), OpenMode.ForRead);
            if (!root.Contains(category)) return null;

            return (DBDictionary)tr.GetObject(root.GetAt(category), OpenMode.ForRead);
        }

        /// <summary>
        /// Create metadata entry in registry root
        /// </summary>
        private static void CreateMetadata(DBDictionary root, Transaction tr)
        {
            var rb = new ResultBuffer(
                new TypedValue((int)DxfCode.Text, SCHEMA_VERSION),
                new TypedValue((int)DxfCode.Text, DateTime.Now.ToString("o"))
            );

            var xRec = new Xrecord { Data = rb };
            root.SetAt(KEY_METADATA, xRec);
            tr.AddNewlyCreatedDBObject(xRec, true);
        }

        /// <summary>
        /// Check if a handle exists as a valid object in the database
        /// </summary>
        public static bool IsValidHandle(string handle, Transaction tr)
        {
            if (string.IsNullOrEmpty(handle)) return false;

            try
            {
                var objId = Utils.AcadUtils.GetObjectIdFromHandle(handle);
                if (objId.IsNull || objId.IsErased) return false;

                var obj = tr.GetObject(objId, OpenMode.ForRead, false);
                return obj != null && !obj.IsErased;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region BeamGroup Registration

        /// <summary>
        /// Register a beam group in the NOD registry.
        /// Called after creating group in LinkCommands to persist to NOD.
        /// </summary>
        public static void RegisterBeamGroup(
            string motherHandle,
            string groupName,
            string name, // NamingEngine label
            string groupType,
            string direction,
            string axisName,
            double levelZ,
            double width,
            double height,
            List<string> childHandles,
            Transaction tr)
        {
            if (string.IsNullOrEmpty(motherHandle)) return;

            var catDict = EnsureCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);

            // Build Xrecord data
            var rb = new ResultBuffer();
            rb.Add(new TypedValue((int)DxfCode.Text, SCHEMA_VERSION));       // 0: Version
            rb.Add(new TypedValue((int)DxfCode.Text, groupName ?? ""));      // 1: GroupName
            rb.Add(new TypedValue((int)DxfCode.Text, name ?? ""));           // 2: Name (NamingEngine)
            rb.Add(new TypedValue((int)DxfCode.Text, groupType ?? "Beam"));  // 3: GroupType
            rb.Add(new TypedValue((int)DxfCode.Text, direction ?? "X"));     // 4: Direction
            rb.Add(new TypedValue((int)DxfCode.Text, axisName ?? ""));       // 5: AxisName
            rb.Add(new TypedValue((int)DxfCode.Real, levelZ));               // 6: LevelZ
            rb.Add(new TypedValue((int)DxfCode.Real, width));                // 7: Width
            rb.Add(new TypedValue((int)DxfCode.Real, height));               // 8: Height
            rb.Add(new TypedValue((int)DxfCode.Text, DateTime.Now.ToString("o"))); // 9: CreatedAt
            rb.Add(new TypedValue((int)DxfCode.Text, DateTime.Now.ToString("o"))); // 10: ModifiedAt
            rb.Add(new TypedValue((int)DxfCode.Int32, childHandles?.Count ?? 0));  // 11: ChildCount

            // Add child handles
            if (childHandles != null)
            {
                foreach (var child in childHandles)
                {
                    rb.Add(new TypedValue((int)DxfCode.Handle, child));
                }
            }

            // Remove existing entry if present
            if (catDict.Contains(motherHandle))
            {
                var oldRec = tr.GetObject(catDict.GetAt(motherHandle), OpenMode.ForWrite);
                oldRec.Erase();
            }

            // Create new entry
            var xRec = new Xrecord { Data = rb };
            catDict.SetAt(motherHandle, xRec);
            tr.AddNewlyCreatedDBObject(xRec, true);
        }

        /// <summary>
        /// Lookup a beam group by mother handle
        /// </summary>
        public static BeamGroupRegistryInfo LookupBeamGroup(string motherHandle, Transaction tr)
        {
            if (string.IsNullOrEmpty(motherHandle)) return null;

            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict == null || !catDict.Contains(motherHandle)) return null;

            var xRec = (Xrecord)tr.GetObject(catDict.GetAt(motherHandle), OpenMode.ForRead);
            return ParseBeamGroupXrecord(motherHandle, xRec);
        }

        /// <summary>
        /// Find beam group containing a specific handle (mother or child)
        /// </summary>
        public static BeamGroupRegistryInfo FindBeamGroupByMember(string handle, Transaction tr)
        {
            if (string.IsNullOrEmpty(handle)) return null;

            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict == null) return null;

            // First check if handle is a mother
            if (catDict.Contains(handle))
            {
                return LookupBeamGroup(handle, tr);
            }

            // Search all groups for this handle as a child
            foreach (var entry in catDict)
            {
                var xRec = (Xrecord)tr.GetObject(entry.Value, OpenMode.ForRead);
                var info = ParseBeamGroupXrecord(entry.Key, xRec);
                if (info != null && info.ChildHandles.Contains(handle))
                {
                    return info;
                }
            }

            return null;
        }

        /// <summary>
        /// Get all registered beam groups
        /// </summary>
        public static List<BeamGroupRegistryInfo> GetAllBeamGroups(Transaction tr)
        {
            var result = new List<BeamGroupRegistryInfo>();

            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict == null) return result;

            foreach (var entry in catDict)
            {
                try
                {
                    var xRec = (Xrecord)tr.GetObject(entry.Value, OpenMode.ForRead);
                    var info = ParseBeamGroupXrecord(entry.Key, xRec);
                    if (info != null) result.Add(info);
                }
                catch { /* Skip invalid entries */ }
            }

            return result;
        }

        /// <summary>
        /// Parse Xrecord data into BeamGroupRegistryInfo
        /// </summary>
        private static BeamGroupRegistryInfo ParseBeamGroupXrecord(string motherHandle, Xrecord xRec)
        {
            if (xRec?.Data == null) return null;

            var data = xRec.Data.AsArray();
            if (data.Length < 12) return null; // Minimum required fields

            try
            {
                var info = new BeamGroupRegistryInfo
                {
                    MotherHandle = motherHandle,
                    GroupName = data[1].Value?.ToString() ?? "",
                    Name = data[2].Value?.ToString() ?? "",
                    GroupType = data[3].Value?.ToString() ?? "Beam",
                    Direction = data[4].Value?.ToString() ?? "X",
                    AxisName = data[5].Value?.ToString() ?? "",
                    LevelZ = Convert.ToDouble(data[6].Value),
                    Width = Convert.ToDouble(data[7].Value),
                    Height = Convert.ToDouble(data[8].Value),
                    ChildHandles = new List<string>()
                };

                // Parse timestamps
                if (DateTime.TryParse(data[9].Value?.ToString(), out var created))
                    info.CreatedAt = created;
                if (DateTime.TryParse(data[10].Value?.ToString(), out var modified))
                    info.ModifiedAt = modified;

                // Parse child count and handles
                int childCount = Convert.ToInt32(data[11].Value);
                for (int i = 12; i < 12 + childCount && i < data.Length; i++)
                {
                    var childHandle = data[i].Value?.ToString();
                    if (!string.IsNullOrEmpty(childHandle))
                        info.ChildHandles.Add(childHandle);
                }

                return info;
            }
            catch
            {
                return null;
            }
        }

        #endregion

        #region BeamGroup Updates

        /// <summary>
        /// Update beam group name (after NamingEngine runs)
        /// </summary>
        public static void UpdateBeamGroupName(string motherHandle, string newGroupName, string newName, Transaction tr)
        {
            var info = LookupBeamGroup(motherHandle, tr);
            if (info == null) return;

            // Re-register with updated names
            RegisterBeamGroup(
                motherHandle,
                newGroupName,
                newName,
                info.GroupType,
                info.Direction,
                info.AxisName,
                info.LevelZ,
                info.Width,
                info.Height,
                info.ChildHandles,
                tr);
        }

        /// <summary>
        /// Add a child to an existing beam group
        /// </summary>
        public static void AddChildToBeamGroup(string motherHandle, string childHandle, Transaction tr)
        {
            var info = LookupBeamGroup(motherHandle, tr);
            if (info == null) return;

            if (!info.ChildHandles.Contains(childHandle))
            {
                info.ChildHandles.Add(childHandle);
                RegisterBeamGroup(
                    motherHandle,
                    info.GroupName,
                    info.Name,
                    info.GroupType,
                    info.Direction,
                    info.AxisName,
                    info.LevelZ,
                    info.Width,
                    info.Height,
                    info.ChildHandles,
                    tr);
            }
        }

        /// <summary>
        /// Remove a child from an existing beam group
        /// </summary>
        public static void RemoveChildFromBeamGroup(string motherHandle, string childHandle, Transaction tr)
        {
            var info = LookupBeamGroup(motherHandle, tr);
            if (info == null) return;

            if (info.ChildHandles.Remove(childHandle))
            {
                RegisterBeamGroup(
                    motherHandle,
                    info.GroupName,
                    info.Name,
                    info.GroupType,
                    info.Direction,
                    info.AxisName,
                    info.LevelZ,
                    info.Width,
                    info.Height,
                    info.ChildHandles,
                    tr);
            }
        }

        /// <summary>
        /// Unregister (delete) a beam group from registry
        /// </summary>
        public static void UnregisterBeamGroup(string motherHandle, Transaction tr)
        {
            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict == null) return;

            // Need write access to remove
            catDict = (DBDictionary)tr.GetObject(catDict.ObjectId, OpenMode.ForWrite);

            if (catDict.Contains(motherHandle))
            {
                var xRec = tr.GetObject(catDict.GetAt(motherHandle), OpenMode.ForWrite);
                xRec.Erase();
            }
        }

        #endregion

        #region Mother Election (Self-Healing)

        /// <summary>
        /// Elect a new mother when the current mother is deleted.
        /// Returns the new mother handle, or null if no valid candidates.
        /// 
        /// ELECTION LOGIC:
        /// 1. Get current group info
        /// 2. Find first valid child (exists in drawing)
        /// 3. Promote that child to mother
        /// 4. Update registry with new mother
        /// 5. Caller must update XData on remaining children
        /// </summary>
        public static string ElectNewMother(string oldMotherHandle, Transaction tr)
        {
            var info = LookupBeamGroup(oldMotherHandle, tr);
            if (info == null) return null;

            // Find first valid child
            string newMother = null;
            var validChildren = new List<string>();

            foreach (var childHandle in info.ChildHandles)
            {
                if (IsValidHandle(childHandle, tr))
                {
                    if (newMother == null)
                    {
                        newMother = childHandle; // First valid = new mother
                    }
                    else
                    {
                        validChildren.Add(childHandle);
                    }
                }
            }

            if (newMother == null)
            {
                // No valid candidates - unregister the group
                UnregisterBeamGroup(oldMotherHandle, tr);
                return null;
            }

            // Unregister old entry
            UnregisterBeamGroup(oldMotherHandle, tr);

            // Register new entry with elected mother
            RegisterBeamGroup(
                newMother,
                info.GroupName,
                info.Name,
                info.GroupType,
                info.Direction,
                info.AxisName,
                info.LevelZ,
                info.Width,
                info.Height,
                validChildren,
                tr);

            return newMother;
        }

        /// <summary>
        /// Determine the role of a handle within a beam group
        /// </summary>
        public static string GetRoleInGroup(string handle, Transaction tr)
        {
            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict == null) return null;

            // Check if it's a mother
            if (catDict.Contains(handle))
                return "Mother";

            // Check if it's a child
            var group = FindBeamGroupByMember(handle, tr);
            if (group != null && group.ChildHandles.Contains(handle))
                return "Child";

            return null;
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Get registry statistics
        /// </summary>
        public static (int BeamGroupCount, DateTime? LastModified) GetRegistryStats(Transaction tr)
        {
            int beamGroupCount = 0;
            DateTime? lastModified = null;

            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict != null)
            {
                beamGroupCount = catDict.Count;
            }

            return (beamGroupCount, lastModified);
        }

        /// <summary>
        /// Validate and clean up registry by removing entries with invalid handles
        /// </summary>
        public static int CleanupInvalidEntries(Transaction tr)
        {
            int removedCount = 0;

            var catDict = GetCategoryDictionary(CATEGORY_BEAM_GROUPS, tr);
            if (catDict == null) return 0;

            catDict = (DBDictionary)tr.GetObject(catDict.ObjectId, OpenMode.ForWrite);

            var toRemove = new List<string>();

            foreach (var entry in catDict)
            {
                if (!IsValidHandle(entry.Key, tr))
                {
                    toRemove.Add(entry.Key);
                }
            }

            foreach (var key in toRemove)
            {
                var xRec = tr.GetObject(catDict.GetAt(key), OpenMode.ForWrite);
                xRec.Erase();
                removedCount++;
            }

            return removedCount;
        }

        /// <summary>
        /// Debug: Write registry contents to command line
        /// </summary>
        public static void DumpRegistry(Transaction tr)
        {
            var ed = Application.DocumentManager.MdiActiveDocument?.Editor;
            if (ed == null) return;

            var groups = GetAllBeamGroups(tr);
            ed.WriteMessage($"\n=== DTS Registry Dump ===");
            ed.WriteMessage($"\n  Beam Groups: {groups.Count}");

            foreach (var g in groups)
            {
                ed.WriteMessage($"\n  [{g.MotherHandle}] {g.GroupName}");
                ed.WriteMessage($"\n    Type={g.GroupType}, Dir={g.Direction}, Axis={g.AxisName}");
                ed.WriteMessage($"\n    Children: {string.Join(", ", g.ChildHandles)}");
            }
        }

        #endregion
    }
}
