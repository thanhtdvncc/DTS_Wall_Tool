using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Smart Naming Engine - Tự động đặt tên dầm theo format: {StoryIndex}{Prefix}{Direction}{Number}{Suffix}
    /// Ví dụ: 3GX12 = Tầng 3 (StartIndex=3) + Dầm chính G + Hướng X + Số 12
    /// </summary>
    public static class NamingEngine
    {
        /// <summary>
        /// Auto-labeling cho danh sách BeamGroup.
        /// </summary>
        public static void AutoLabeling(List<Data.BeamGroup> groups, DtsSettings settings)
        {
            if (groups == null || groups.Count == 0) return;
            if (settings == null) settings = DtsSettings.Instance;

            // 0. Gán Story và lấy Config cho từng group dựa trên cao độ (LevelZ/xBaseZ)
            // Cần đảm bảo StoryName chuẩn theo Config để group sau này
            foreach (var group in groups)
            {
                double z = group.LevelZ; // Lấy từ xBaseZ
                var storyConfig = settings.GetStoryConfig(z);

                if (storyConfig != null)
                {
                    group.StoryName = storyConfig.StoryName;
                }
                else if (string.IsNullOrEmpty(group.StoryName))
                {
                    group.StoryName = "Unknown";
                }
            }

            // 1. Group by StoryName để xử lý từng tầng riêng biệt
            var storyBuckets = groups
                .GroupBy(g => g.StoryName ?? "Unknown")
                .OrderBy(b => GetElevation(b.Key, settings))
                .ToList();

            foreach (var bucket in storyBuckets)
            {
                string storyName = bucket.Key;

                // Lấy lại config của tầng này để lấy StartIndex, Prefix, Suffix
                var storyConfig = settings.StoryConfigs?.FirstOrDefault(s => s.StoryName == storyName);

                // Fallback nếu không tìm thấy config (dù đã gán ở bước 0)
                if (storyConfig == null && settings.StoryConfigs?.Count > 0)
                {
                    storyConfig = settings.StoryConfigs.OrderByDescending(s => s.Elevation).FirstOrDefault();
                }

                // === 1. PREPARE NAMING COMPONENTS ===

                // A. StoryIndex: Lấy từ biến StartIndex trong settings (như bạn yêu cầu)
                // Ví dụ: Setting StartIndex = 3 -> dầm sẽ bắt đầu bằng số 3
                string storyIndexStr = storyConfig?.StartIndex.ToString() ?? "0";

                // B. Suffix
                string suffix = storyConfig?.Suffix ?? "";

                // C. Prefixes
                string girderPrefix = storyConfig?.GirderPrefix ?? "G";
                string beamPrefix = storyConfig?.BeamPrefix ?? "B";

                // === 2. SETUP COUNTERS ===
                // Bộ đếm riêng cho từng hướng và loại dầm (Reset về 1)
                // Key: "{Prefix}_{Direction}" -> Value: Current Number (1, 2, 3...)
                var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                // Map để reuse tên cho các dầm giống hệt nhau (Cùng Signature + Cùng Hướng)
                // Key: "{Signature}_{Direction}" -> Value: Existing Name
                var signatureMap = new Dictionary<string, string>();

                // === 3. SPATIAL SORT (Sắp xếp không gian) ===
                // Ưu tiên Y giảm dần (Trên xuống), sau đó X tăng dần (Trái sang)
                var sortedGroups = bucket
                    .OrderByDescending(g => GetCenterY(g))
                    .ThenBy(g => GetCenterX(g))
                    .ToList();

                foreach (var group in sortedGroups)
                {
                    // Skip nếu user khóa tên
                    if (group.IsNameLocked && !string.IsNullOrEmpty(group.Name))
                        continue;

                    // Update signature (Tiết diện + Thép)
                    group.UpdateSignature();
                    string sig = group.Signature ?? $"RAW_{group.Width}x{group.Height}";

                    // === FIX LOGIC XÁC ĐỊNH GIRDER/BEAM ===

                    // 1. Lấy giới hạn từ Settings (Thay vì hardcode 300)
                    double girderLimit = settings.Naming?.GirderMinWidth ?? 300.0;

                    // 2. Check loại được chỉ định cứng từ XData (Map vào GroupType)
                    string gType = group.GroupType ?? "";
                    bool isExplicitGirder = gType.Equals("Girder", StringComparison.OrdinalIgnoreCase);
                    bool isExplicitBeam = gType.Equals("Beam", StringComparison.OrdinalIgnoreCase);

                    bool isGirder;

                    if (isExplicitGirder)
                    {
                        isGirder = true; // User/XData bảo là Girder -> Là Girder
                    }
                    else if (isExplicitBeam)
                    {
                        isGirder = false; // User/XData bảo là Beam -> Là Beam (Bất chấp bề rộng)
                    }
                    else
                    {
                        // 3. Nếu Auto (không có chỉ định) -> Check theo Section (Width)
                        isGirder = group.Width >= girderLimit;
                    }

                    // Cập nhật lại GroupType chuẩn để dùng sau này
                    group.GroupType = isGirder ? "Girder" : "Beam";

                    string currentPrefix = isGirder ? girderPrefix : beamPrefix;

                    // Xác định hướng (X/Y)
                    string rawDir = group.Direction?.ToUpper() ?? "X";
                    string currentDirection = (rawDir == "Y") ? "Y" : "X";

                    // Key định danh cho nhóm dầm giống nhau
                    // Phải bao gồm cả Direction để dầm ngang (X) không lấy tên của dầm dọc (Y)
                    string groupingKey = $"{sig}_{currentPrefix}_{currentDirection}";

                    // Key cho bộ đếm (VD: "G_X", "B_Y")
                    string counterKey = $"{currentPrefix}_{currentDirection}";

                    // Khởi tạo bộ đếm nếu chưa có (Luôn bắt đầu từ 1)
                    if (!counters.ContainsKey(counterKey))
                    {
                        counters[counterKey] = 1;
                    }

                    // === 4. GÁN TÊN ===
                    if (signatureMap.TryGetValue(groupingKey, out string existingName))
                    {
                        // Reuse tên cũ nếu giống hệt nhau
                        group.Name = existingName;
                    }
                    else
                    {
                        // Tạo tên mới
                        int currentNumber = counters[counterKey];

                        // FORMAT: {StoryIndex}{Prefix}{Direction}{Number}{Suffix}
                        // Ví dụ: 3 + G + X + 12 + "" = 3GX12
                        string newName = $"{storyIndexStr}{currentPrefix}{currentDirection}{currentNumber}{suffix}";

                        group.Name = newName;

                        // Lưu vào map
                        signatureMap[groupingKey] = newName;

                        // Tăng số thứ tự
                        counters[counterKey]++;
                    }
                }
            }
        }

        /// <summary>
        /// Kiểm tra xung đột tên khi user đổi tên thủ công.
        /// </summary>
        public static string CheckConflict(string newName, string newSignature,
            List<BeamGroup> allGroups, string currentGroupName)
        {
            var existing = allGroups.FirstOrDefault(g =>
                g.Name == newName &&
                g.GroupName != currentGroupName);

            if (existing == null) return null;

            if (existing.Signature == newSignature)
                return null;

            return existing.Signature;
        }

        // === HELPERS ===
        private static double GetElevation(string storyName, DtsSettings settings)
        {
            var config = settings.StoryConfigs?.FirstOrDefault(s => s.StoryName == storyName);
            return config?.Elevation ?? 0;
        }

        private static double GetCenterY(Data.BeamGroup group)
        {
            // Use stored geometry center if Spans not available
            if (group.Spans == null || group.Spans.Count == 0)
                return group.GeometryCenterY;

            double sum = 0;
            int count = 0;
            foreach (var span in group.Spans)
            {
                if (span.Segments != null)
                {
                    foreach (var seg in span.Segments)
                    {
                        if (seg.StartPoint != null && seg.StartPoint.Length >= 2)
                        {
                            sum += seg.StartPoint[1];
                            count++;
                        }
                    }
                }
            }
            return count > 0 ? sum / count : group.GeometryCenterY;
        }

        private static double GetCenterX(Data.BeamGroup group)
        {
            // Use stored geometry center if Spans not available
            if (group.Spans == null || group.Spans.Count == 0)
                return group.GeometryCenterX;

            double sum = 0;
            int count = 0;
            foreach (var span in group.Spans)
            {
                if (span.Segments != null)
                {
                    foreach (var seg in span.Segments)
                    {
                        if (seg.StartPoint != null && seg.StartPoint.Length >= 2)
                        {
                            sum += seg.StartPoint[0];
                            count++;
                        }
                    }
                }
            }
            return count > 0 ? sum / count : group.GeometryCenterX;
        }
    }
}
