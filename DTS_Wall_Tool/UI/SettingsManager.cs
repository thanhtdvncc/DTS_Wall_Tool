using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace DTS_Wall_Tool.UI
{
    /// <summary>
    /// Quản lý lưu/đọc cài đặt người dùng
    /// Tuân thủ ISO 25010: Recoverability, User error protection
    /// </summary>
    public static class SettingsManager
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DTS_Wall_Tool");

        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.json");

        private static Dictionary<string, object> _settings = new Dictionary<string, object>();

        #region Initialization

        static SettingsManager()
        {
            EnsureSettingsFolder();
            Load();
        }

        private static void EnsureSettingsFolder()
        {
            if (!Directory.Exists(SettingsFolder))
            {
                Directory.CreateDirectory(SettingsFolder);
            }
        }

        #endregion

        #region Get/Set Methods

        public static T Get<T>(string key, T defaultValue = default)
        {
            if (_settings.ContainsKey(key))
            {
                try
                {
                    var value = _settings[key];
                    if (value is T typedValue)
                        return typedValue;

                    // Chuyển đổi kiểu nếu cần
                    return (T)Convert.ChangeType(value, typeof(T));
                }
                catch
                {
                    return defaultValue;
                }
            }
            return defaultValue;
        }

        public static void Set<T>(string key, T value)
        {
            _settings[key] = value;
        }

        public static bool HasKey(string key)
        {
            return _settings.ContainsKey(key);
        }

        public static void Remove(string key)
        {
            if (_settings.ContainsKey(key))
                _settings.Remove(key);
        }

        #endregion

        #region Load/Save

        public static void Load()
        {
            try
            {
                if (File.Exists(SettingsFile))
                {
                    string json = File.ReadAllText(SettingsFile);
                    var serializer = new JavaScriptSerializer();
                    _settings = serializer.Deserialize<Dictionary<string, object>>(json)
                        ?? new Dictionary<string, object>();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading settings: {ex.Message}");
                _settings = new Dictionary<string, object>();
            }
        }

        public static void Save()
        {
            try
            {
                EnsureSettingsFolder();
                var serializer = new JavaScriptSerializer();
                string json = serializer.Serialize(_settings);
                File.WriteAllText(SettingsFile, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving settings: {ex.Message}");
            }
        }

        #endregion

        #region Predefined Keys

        public static class Keys
        {
            // Wall Line Gen Tab
            public const string WallThickness = "WallLineGen. WallThickness";
            public const string LayerDetect = "WallLineGen.LayerDetect";
            public const string AngleTolerance = "WallLineGen.AngleTolerance";
            public const string DoorWidths = "WallLineGen.DoorWidths";
            public const string ColumnWidths = "WallLineGen. ColumnWidths";
            public const string ExtendCoeff = "WallLineGen.ExtendCoeff";
            public const string AutoExtend = "WallLineGen.AutoExtend";
            public const string WallThkTolerance = "WallLineGen.WallThkTolerance";
            public const string AutoJoinGap = "WallLineGen.AutoJoinGap";
            public const string AxisSnap = "WallLineGen.AxisSnap";
            public const string BreakAtGrid = "WallLineGen.BreakAtGrid";
            public const string ExtendOnGrid = "WallLineGen.ExtendOnGrid";

            // Load Assignment Tab
            public const string LastElevation = "LoadAssignment.LastElevation";
            public const string LastHeight = "LoadAssignment.LastHeight";
            public const string LastOriginX = "LoadAssignment.LastOriginX";
            public const string LastOriginY = "LoadAssignment.LastOriginY";

            // Auto Load Tab
            public const string LoadMethod = "AutoLoad.LoadMethod";
            public const string LoadFactor = "AutoLoad.LoadFactor";
            public const string AutoDeductBeam = "AutoLoad.AutoDeductBeam";
            public const string ParapetHeight = "AutoLoad.ParapetHeight";
            public const string RoofWallHeight = "AutoLoad. RoofWallHeight";
            public const string FireWallFactor = "AutoLoad.FireWallFactor";
        }

        #endregion
    }
}