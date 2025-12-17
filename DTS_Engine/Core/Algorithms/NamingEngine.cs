using System;
using System.Collections.Generic;
using System.Linq;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Algorithms
{
    /// <summary>
    /// Smart Naming Engine - Tự động đặt tên dầm theo tầng và nhóm giống nhau.
    /// Dầm có cùng Signature (Tiết diện + Thép) sẽ dùng chung tên.
    /// </summary>
    public static class NamingEngine
    {
        /// <summary>
        /// Auto-labeling cho danh sách BeamGroup.
        /// Thuật toán:
        /// 1. Phân loại theo Story (Tầng)
        /// 2. Sắp xếp không gian: Y desc → X asc (Trên xuống, Trái sang)
        /// 3. Gán tên: Dùng SignatureMap để reuse tên cho dầm giống nhau
        /// </summary>
        public static void AutoLabeling(List<Data.BeamGroup> groups, DtsSettings settings)
        {
            if (groups == null || groups.Count == 0) return;
            if (settings == null) settings = DtsSettings.Instance;

            // 1. Group by Story
            var storyBuckets = groups
                .GroupBy(g => g.StoryName ?? "Unknown")
                .OrderBy(b => GetElevation(b.Key, settings))
                .ToList();

            foreach (var bucket in storyBuckets)
            {
                string storyName = bucket.Key;
                var storyConfig = settings.StoryConfigs?.FirstOrDefault(s => s.StoryName == storyName);

                // Fallback nếu không tìm thấy config
                if (storyConfig == null && settings.StoryConfigs?.Count > 0)
                {
                    // Dùng tầng cao nhất và cảnh báo
                    storyConfig = settings.StoryConfigs.OrderByDescending(s => s.Elevation).FirstOrDefault();
                    System.Diagnostics.Debug.WriteLine($"[NamingEngine] WARNING: Story '{storyName}' not in StoryConfigs. Using fallback: {storyConfig?.StoryName}");
                }

                // Lấy prefix/suffix từ config (KHÔNG HARDCODE)
                string prefix = storyConfig?.BeamPrefix ?? "";
                string suffix = storyConfig?.Suffix ?? "";
                int counter = storyConfig?.StartIndex ?? 1;

                // 2. Spatial Sort: Y desc, X asc
                var sortedGroups = bucket
                    .OrderByDescending(g => GetCenterY(g))
                    .ThenBy(g => GetCenterX(g))
                    .ToList();

                // 3. Signature Map: Key=Signature, Value=Name
                var signatureMap = new Dictionary<string, string>();

                foreach (var group in sortedGroups)
                {
                    // Skip if user locked name
                    if (group.IsNameLocked && !string.IsNullOrEmpty(group.Name))
                        continue;

                    // Update signature
                    group.UpdateSignature();
                    string sig = group.Signature ?? "";

                    // Check map
                    if (signatureMap.TryGetValue(sig, out string existingName))
                    {
                        // Reuse existing name (same structure)
                        group.Name = existingName;
                    }
                    else
                    {
                        // Generate new name
                        string newName = $"{prefix}{counter}{suffix}";
                        group.Name = newName;
                        signatureMap[sig] = newName;
                        counter++;
                    }
                }
            }
        }

        /// <summary>
        /// Kiểm tra xung đột tên khi user đổi tên thủ công.
        /// </summary>
        /// <returns>
        /// null = OK, no conflict
        /// string = Signature của group đang dùng tên này (nếu khác signature → warning)
        /// </returns>
        public static string CheckConflict(string newName, string newSignature,
            List<BeamGroup> allGroups, string currentGroupName)
        {
            var existing = allGroups.FirstOrDefault(g =>
                g.Name == newName &&
                g.GroupName != currentGroupName);

            if (existing == null) return null; // No conflict

            // Check if same signature (OK to merge) or different (warning)
            if (existing.Signature == newSignature)
                return null; // Same structure, can share name

            return existing.Signature; // Different structure - return for warning
        }

        // === HELPERS ===
        private static double GetElevation(string storyName, DtsSettings settings)
        {
            var config = settings.StoryConfigs?.FirstOrDefault(s => s.StoryName == storyName);
            return config?.Elevation ?? 0;
        }

        private static double GetCenterY(Data.BeamGroup group)
        {
            if (group.Spans == null || group.Spans.Count == 0) return 0;
            // Lấy Y trung bình từ các span
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
                            sum += seg.StartPoint[1]; // Y
                            count++;
                        }
                    }
                }
            }
            return count > 0 ? sum / count : 0;
        }

        private static double GetCenterX(Data.BeamGroup group)
        {
            if (group.Spans == null || group.Spans.Count == 0) return 0;
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
                            sum += seg.StartPoint[0]; // X
                            count++;
                        }
                    }
                }
            }
            return count > 0 ? sum / count : 0;
        }
    }
}
