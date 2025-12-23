using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Runtime;
using DTS_Engine.Core.Data;
using DTS_Engine.Core.Engines;
using DTS_Engine.Core.Utils;
using System;
using System.Collections.Generic;

[assembly: ExtensionApplication(typeof(DTS_Engine.DtsInitializer))]

namespace DTS_Engine
{
    /// <summary>
    /// DtsInitializer - Khởi tạo plugin khi DLL được load
    /// - Tự động hiển thị Dashboard Mini-Toolbar
    /// - Đăng ký Self-Healing events cho beam groups
    /// </summary>
    public class DtsInitializer : IExtensionApplication
    {
        /// <summary>
        /// Flag để tránh xử lý lặp trong Undo/Redo
        /// </summary>
        private static bool _processingObjectErased = false;

        /// <summary>
        /// Được gọi khi AutoCAD load DLL
        /// </summary>
        public void Initialize()
        {
            // Đợi AutoCAD sẵn sàng hoàn toàn trước khi hiện palette
            Application.Idle += OnApplicationIdle;

            // Register DocumentManager events for self-healing
            Application.DocumentManager.DocumentCreated += OnDocumentCreated;
            Application.DocumentManager.DocumentToBeDestroyed += OnDocumentToBeDestroyed;

            // Register for active document if exists
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                RegisterDatabaseEvents(doc.Database);
            }
        }

        private void OnApplicationIdle(object sender, System.EventArgs e)
        {
            // Chỉ chạy một lần
            Application.Idle -= OnApplicationIdle;

            try
            {
                // Hiện Dashboard Mini-Toolbar tự động
                DTS_Engine.UI.Forms.DashboardPalette.ShowPalette();

                // Thông báo trong command line
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                {
                    doc.Editor.WriteMessage("\n[DTS Engine] Đã khởi tạo thành công. Dashboard Mini-Toolbar đã sẵn sàng.\n");
                    doc.Editor.WriteMessage("[DTS Engine] Self-Healing enabled - beam groups sẽ tự phục hồi khi xóa.\n");
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"DTS Init Error: {ex.Message}");
            }
        }

        #region Document Events

        private void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != null)
            {
                RegisterDatabaseEvents(e.Document.Database);
            }
        }

        private void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document != null)
            {
                UnregisterDatabaseEvents(e.Document.Database);
            }
        }

        private void RegisterDatabaseEvents(Database db)
        {
            if (db == null) return;

            try
            {
                // Register ObjectErased for self-healing
                db.ObjectErased += OnObjectErased;
            }
            catch { /* Silent - không ảnh hưởng workflow chính */ }
        }

        private void UnregisterDatabaseEvents(Database db)
        {
            if (db == null) return;

            try
            {
                db.ObjectErased -= OnObjectErased;
            }
            catch { /* Silent */ }
        }

        #endregion

        #region Self-Healing: ObjectErased Handler

        /// <summary>
        /// Xử lý khi đối tượng bị xóa - Self-Healing cho beam groups.
        /// Khi Mother bị xóa, tự động bầu Mother mới từ Children.
        /// </summary>
        private void OnObjectErased(object sender, ObjectErasedEventArgs e)
        {
            // Chỉ xử lý khi thực sự xóa (không phải Undo)
            if (!e.Erased) return;

            // Tránh recursive processing
            if (_processingObjectErased) return;

            try
            {
                _processingObjectErased = true;

                // Get handle của object bị xóa
                string deletedHandle = e.DBObject.Handle.ToString();

                // Queue xử lý sau khi transaction hiện tại commit
                // (ObjectErased được gọi trong transaction đang chạy)
                Application.Idle += (s, args) =>
                {
                    Application.Idle -= (EventHandler)((s2, args2) => { });
                    ProcessDeletedBeam(e.DBObject.Database, deletedHandle);
                };
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DTS Self-Healing] Error: {ex.Message}");
            }
            finally
            {
                _processingObjectErased = false;
            }
        }

        /// <summary>
        /// Xử lý sau khi đối tượng đã thực sự bị xóa
        /// </summary>
        private void ProcessDeletedBeam(Database db, string deletedHandle)
        {
            if (db == null || string.IsNullOrEmpty(deletedHandle)) return;

            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc == null || doc.Database != db) return;

                using (var docLock = doc.LockDocument())
                using (var tr = db.TransactionManager.StartTransaction())
                {
                    // Kiểm tra xem handle bị xóa có phải là Mother trong Registry không
                    var regInfo = RegistryEngine.LookupBeamGroup(deletedHandle, tr);

                    if (regInfo != null)
                    {
                        // ĐÂY LÀ MOTHER - Cần bầu mẹ mới!
                        doc.Editor.WriteMessage($"\n[DTS Self-Healing] Phát hiện Mother beam {deletedHandle} bị xóa.");

                        // Elect new mother
                        string newMother = RegistryEngine.ElectNewMother(deletedHandle, tr);

                        if (newMother != null)
                        {
                            // Update XData của new mother để trở thành mother
                            UpdateNewMotherXData(newMother, regInfo, tr);

                            // Update XData của children còn lại để trỏ về new mother
                            UpdateChildrenToNewMother(newMother, regInfo.ChildHandles, deletedHandle, tr);

                            doc.Editor.WriteMessage($"\n[DTS Self-Healing] ✅ Đã bầu {newMother} làm Mother mới.");
                        }
                        else
                        {
                            doc.Editor.WriteMessage($"\n[DTS Self-Healing] ⚠ Không còn con hợp lệ - group đã giải tán.");
                        }
                    }
                    else
                    {
                        // Check if it's a child - just remove from parent's list
                        var parentGroup = RegistryEngine.FindBeamGroupByMember(deletedHandle, tr);
                        if (parentGroup != null)
                        {
                            RegistryEngine.RemoveChildFromBeamGroup(parentGroup.MotherHandle, deletedHandle, tr);
                            RemoveChildFromMotherXData(parentGroup.MotherHandle, deletedHandle, tr);
                        }
                    }

                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DTS Self-Healing] ProcessDeletedBeam Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Cập nhật XData của mother mới (clear OriginHandle, keep ChildHandles)
        /// </summary>
        private void UpdateNewMotherXData(string newMotherHandle, RegistryEngine.BeamGroupRegistryInfo regInfo, Transaction tr)
        {
            try
            {
                var objId = AcadUtils.GetObjectIdFromHandle(newMotherHandle);
                if (objId.IsNull || objId.IsErased) return;

                var obj = tr.GetObject(objId, OpenMode.ForWrite);
                var elemData = XDataUtils.ReadElementData(obj);

                if (elemData != null)
                {
                    // Clear OriginHandle - đây giờ là mother
                    elemData.OriginHandle = null;

                    // Set ChildHandles (các con còn lại, loại bỏ chính nó)
                    elemData.ChildHandles = new List<string>(regInfo.ChildHandles);
                    elemData.ChildHandles.Remove(newMotherHandle);

                    // Copy group info nếu là BeamData
                    if (elemData is BeamData beamData)
                    {
                        beamData.GroupLabel = regInfo.Name;
                        beamData.GroupType = regInfo.GroupType;
                    }

                    XDataUtils.WriteElementData(obj, elemData, tr);
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Self-Healing] UpdateNewMotherXData Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Cập nhật OriginHandle của các children để trỏ về mother mới
        /// </summary>
        private void UpdateChildrenToNewMother(string newMotherHandle, List<string> childHandles, string excludeHandle, Transaction tr)
        {
            if (childHandles == null) return;

            foreach (var childHandle in childHandles)
            {
                // Skip new mother và handle đã xóa
                if (childHandle == newMotherHandle || childHandle == excludeHandle) continue;

                try
                {
                    var objId = AcadUtils.GetObjectIdFromHandle(childHandle);
                    if (objId.IsNull || objId.IsErased) continue;

                    var obj = tr.GetObject(objId, OpenMode.ForWrite);
                    var elemData = XDataUtils.ReadElementData(obj);

                    if (elemData != null)
                    {
                        elemData.OriginHandle = newMotherHandle;
                        XDataUtils.WriteElementData(obj, elemData, tr);
                    }
                }
                catch { /* Skip invalid children */ }
            }
        }

        /// <summary>
        /// Xóa child khỏi ChildHandles của mother
        /// </summary>
        private void RemoveChildFromMotherXData(string motherHandle, string childToRemove, Transaction tr)
        {
            try
            {
                var objId = AcadUtils.GetObjectIdFromHandle(motherHandle);
                if (objId.IsNull || objId.IsErased) return;

                var obj = tr.GetObject(objId, OpenMode.ForWrite);
                var elemData = XDataUtils.ReadElementData(obj);

                if (elemData?.ChildHandles != null && elemData.ChildHandles.Remove(childToRemove))
                {
                    XDataUtils.WriteElementData(obj, elemData, tr);
                }
            }
            catch { /* Silent */ }
        }

        #endregion

        /// <summary>
        /// Được gọi khi AutoCAD unload DLL (thường là khi đóng AutoCAD)
        /// </summary>
        public void Terminate()
        {
            // Cleanup events
            try
            {
                Application.DocumentManager.DocumentCreated -= OnDocumentCreated;
                Application.DocumentManager.DocumentToBeDestroyed -= OnDocumentToBeDestroyed;
            }
            catch { }

            // Cleanup palette
            DTS_Engine.UI.Forms.DashboardPalette.ClosePalette();
        }
    }
}
