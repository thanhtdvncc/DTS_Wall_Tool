using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using DTS_Engine.Core.Utils;
using System;

namespace DTS_Engine.Commands
{
    /// <summary>
    /// Base class cho tat ca commands.
    /// Cung cap cac phuong thuc tien ich chung cho viec tuong tac voi AutoCAD.
    /// Tuan thu ISO/IEC 25010: Maintainability, Reliability, Usability.
    /// </summary>
    public abstract class CommandBase
    {
        protected Document Doc => AcadUtils.Doc;
        protected Database Db => AcadUtils.Db;
        protected Editor Ed => AcadUtils.Ed;

        #region Message Output Methods

        /// <summary>
        /// Ghi thong bao ra command line.
        /// </summary>
        protected void WriteMessage(string message)
        {
            Ed.WriteMessage($"\n{message}");
        }

        /// <summary>
        /// Ghi thong bao loi ra command line voi dinh dang chuyen nghiep.
        /// </summary>
        protected void WriteError(string message)
        {
            Ed.WriteMessage($"\n>> LOI: {message}");
        }

        /// <summary>
        /// Ghi thong bao thanh cong ra command line voi dinh dang chuyen nghiep.
        /// </summary>
        protected void WriteSuccess(string message)
        {
            Ed.WriteMessage($"\n>> HOÀN TẤT: {message}");
        }

        /// <summary>
        /// Ghi thong bao canh bao ra command line.
        /// </summary>
        protected void WriteWarning(string message)
        {
            Ed.WriteMessage($"\n>> CẢNH BÁO: {message}");
        }

        /// <summary>
        /// Ghi thong bao thong tin ra command line.
        /// </summary>
        protected void WriteInfo(string message)
        {
            Ed.WriteMessage($"\n>> THÔNG TIN: {message}");
        }

        /// <summary>
        /// Ghi thong bao debug (chi hien thi khi DEBUG mode).
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        protected void WriteDebug(string message)
        {
            Ed.WriteMessage($"\n[DEBUG] {message}");
        }

        #endregion

        #region Transaction Methods

        /// <summary>
        /// Thuc hien action trong transaction an toan.
        /// </summary>
        protected void UsingTransaction(Action<Transaction> action)
        {
            AcadUtils.UsingTransaction(action);
        }

        /// <summary>
        /// Thuc hien function trong transaction an toan va tra ve ket qua.
        /// </summary>
        protected T UsingTransaction<T>(Func<Transaction, T> func)
        {
            return AcadUtils.UsingTransaction(func);
        }

        #endregion

        #region Safe Execution Methods

        /// <summary>
        /// Thuc thi mot Action voi co che an toan:
        /// 1. Reset cancel tracking khi bat dau
        /// 2. Catch loi va bao cao
        /// 3. Tu dong Clear Visual neu co loi (optional)
        /// 4. Track cancel neu user Esc (auto-clear sau 2 lan)
        /// </summary>
        /// <param name="action">Action can thuc thi</param>
        /// <param name="clearVisualOnError">Tu dong xoa Transient Graphics khi co loi</param>
        protected void ExecuteSafe(Action action, bool clearVisualOnError = true)
        {
            // Reset cancel tracking khi bat dau lenh moi
            VisualUtils.ResetCancelTracking();

            try
            {
                action();
            }
            catch (Autodesk.AutoCAD.Runtime.Exception acEx)
            {
                // Kiem tra neu la user cancel (Esc)
                if (acEx.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.UserBreak ||
                    acEx.Message.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    HandleUserCancel();
                }
                else
                {
                    WriteError($"AutoCAD Exception: {acEx.Message}");
                    if (clearVisualOnError) VisualUtils.ClearAll();
                }
            }
            catch (OperationCanceledException)
            {
                // User cancel
                HandleUserCancel();
            }
            catch (Exception ex)
            {
                WriteError($"Loi he thong: {ex.Message}");
                if (clearVisualOnError) VisualUtils.ClearAll();
#if DEBUG
                WriteDebug($"Stack trace: {ex.StackTrace}");
#endif
            }
        }

        /// <summary>
        /// Xu ly khi user cancel (Esc).
        /// Track so lan cancel va tu dong clear visual neu can.
        /// </summary>
        private void HandleUserCancel()
        {
            if (VisualUtils.TransientCount > 0)
            {
                bool autoCleared = VisualUtils.TrackCancelAndAutoClear();
                if (autoCleared)
                {
                    WriteMessage("(Đã tự động xóa hiển thị tạm thời sau 5 lần Esc)");
                }
                else
                {
                    WriteMessage("(Nhấn Esc lần nữa để xóa hiển thị tạm thời)");
                }
            }
        }

        /// <summary>
        /// Thuc thi mot Action trong Transaction voi co che an toan.
        /// Dam bao Transaction duoc commit hoac rollback dung cach.
        /// </summary>
        protected void ExecuteSafeInTransaction(Action<Transaction> action, bool clearVisualOnError = true)
        {
            ExecuteSafe(() => UsingTransaction(action), clearVisualOnError);
        }

        /// <summary>
        /// Thuc thi mot lenh voi phan cleanup dam bao chay sau cung (try-finally pattern).
        /// </summary>
        /// <param name="mainAction">Action chinh</param>
        /// <param name="cleanupAction">Action cleanup (luon chay)</param>
        protected void ExecuteWithCleanup(Action mainAction, Action cleanupAction)
        {
            try
            {
                mainAction();
            }
            finally
            {
                try
                {
                    cleanupAction?.Invoke();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }
        }

        #endregion
    }
}