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
                    string sig = group.Signature ?? "";

                    // Xác định loại (Girder/Beam)
                    bool isGirder = (group.GroupType ?? "").Equals("Girder", StringComparison.OrdinalIgnoreCase)
                                    || group.Width >= 300;

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

        /// <summary>
        /// Auto-labeling cho từng dầm riêng lẻ (không phải Group).
        /// Hỗ trợ name locking - các dầm đã khóa sẽ giữ nguyên tên và reserve số.
        /// </summary>
        public static void AutoLabelBeams(List<BeamData> beams, DtsSettings settings)
        {
            if (beams == null || beams.Count == 0) return;
            if (settings == null) settings = DtsSettings.Instance;

            // 0. Gán Story cho từng dầm
            foreach (var beam in beams)
            {
                if (beam.BaseZ == null) continue;
                var storyConfig = settings.GetStoryConfig(beam.BaseZ.Value);
                if (storyConfig != null)
                {
                    beam.StoryName = storyConfig.StoryName;
                }
            }

            // 1. Group by Story
            var storyBuckets = beams
                .Where(b => !string.IsNullOrEmpty(b.StoryName))
                .GroupBy(b => b.StoryName)
                .OrderBy(g => GetElevation(g.Key, settings))
                .ToList();

            foreach (var bucket in storyBuckets)
            {
                string storyName = bucket.Key;
                var storyConfig = settings.StoryConfigs?.FirstOrDefault(s => s.StoryName == storyName);
                if (storyConfig == null) continue;

                string storyIndexStr = storyConfig.StartIndex.ToString();
                string suffix = storyConfig.Suffix ?? "";
                string girderPrefix = storyConfig.GirderPrefix ?? "G";
                string beamPrefix = storyConfig.BeamPrefix ?? "B";

                // === PHASE 1: Collect locked names ===
                var lockedMap = new Dictionary<string, string>(); // Signature -> Name
                var reservedNumbers = new Dictionary<string, HashSet<int>>(); // CounterKey -> Numbers

                foreach (var beam in bucket.Where(b => b.SectionLabelLocked))
                {
                    if (string.IsNullOrEmpty(beam.SectionLabel)) continue;

                    // Tính Signature và Type
                    string sig = GetBeamSignature(beam);
                    bool isGirder = (beam.SupportI == 1 && beam.SupportJ == 1);
                    string prefix = isGirder ? girderPrefix : beamPrefix;
                    string direction = GetDirection(beam);
                    string key = $"{sig}_{prefix}_{direction}";

                    // Lưu locked name
                    if (!lockedMap.ContainsKey(key))
                        lockedMap[key] = beam.SectionLabel;

                    // Extract số từ tên và reserve
                    int number = ExtractNumber(beam.SectionLabel);
                    if (number > 0)
                    {
                        string counterKey = $"{prefix}_{direction}";
                        if (!reservedNumbers.ContainsKey(counterKey))
                            reservedNumbers[counterKey] = new HashSet<int>();
                        reservedNumbers[counterKey].Add(number);
                    }
                }

                // === PHASE 2: Sort và gán tên cho unlocked beams ===
                var unlockedBeams = bucket.Where(b => !b.SectionLabelLocked).ToList();
                unlockedBeams = SortBeams(unlockedBeams, settings.Naming.SortCorner, settings.Naming.SortDirection);

                var counters = new Dictionary<string, int>();
                var assignedNames = new Dictionary<string, string>(); // Key -> Name (để reuse)

                foreach (var beam in unlockedBeams)
                {
                    // Validation: Phải có Support data
                    // (sẽ báo lỗi ở RebarCommands, ở đây skip)
                    if (!beam.BaseZ.HasValue) continue;

                    // Xác định Type và Direction
                    bool isGirder = (beam.SupportI == 1 && beam.SupportJ == 1);
                    string prefix = isGirder ? girderPrefix : beamPrefix;
                    string direction = GetDirection(beam);
                    string counterKey = $"{prefix}_{direction}";

                    // Tính Signature
                    string sig = GetBeamSignature(beam);
                    string key = $"{sig}_{prefix}_{direction}";

                    // Nếu có locked name cho key này -> reuse
                    if (lockedMap.TryGetValue(key, out string lockedName))
                    {
                        beam.SectionLabel = lockedName;
                        continue;
                    }

                    // Nếu đã có tên cho key này (từ beam trước) -> reuse
                    if (assignedNames.TryGetValue(key, out string existingName))
                    {
                        beam.SectionLabel = existingName;
                        continue;
                    }

                    // Tạo tên mới
                    if (!counters.ContainsKey(counterKey))
                        counters[counterKey] = 1;

                    int number = GetNextAvailableNumber(counterKey, counters, reservedNumbers);
                    string newName = $"{storyIndexStr}{prefix}{direction}{number}{suffix}";

                    beam.SectionLabel = newName;
                    assignedNames[key] = newName;
                    counters[counterKey] = number + 1;
                }
            }
        }

        /// <summary>
        /// Tính Signature từ Width x Height + OptUser + Support Type.
        /// CRITICAL: Bao gồm Support để tránh gộp nhầm Girder/Beam!
        /// </summary>
        private static string GetBeamSignature(BeamData beam)
        {
            double w = beam.Width ?? 0;
            double h = beam.Depth ?? 0;
            // OptUser lưu trong XData, cần đọc từ dict (sẽ được populate bởi RebarCommands)
            // Tạm thời dùng placeholder nếu chưa có
            string optUser = beam.OptUser ?? "";

            // CRITICAL FIX: Thêm Support type vào signature
            // Girder (I=1,J=1) khác hoàn toàn với Beam (I=0 hoặc J=0)
            string supportType = (beam.SupportI == 1 && beam.SupportJ == 1) ? "G" : "B";

            return $"{(int)w}x{(int)h}|{optUser}|{supportType}";
        }

        /// <summary>
        /// Xác định Direction (X/Y) từ góc dầm.
        /// </summary>
        private static string GetDirection(BeamData beam)
        {
            // Nếu không có geometry data → fallback "X"
            if (beam.StartPoint == null || beam.EndPoint == null ||
                beam.StartPoint.Length < 2 || beam.EndPoint.Length < 2)
                return "X";

            // Tính delta X và delta Y
            double dx = Math.Abs(beam.EndPoint[0] - beam.StartPoint[0]);
            double dy = Math.Abs(beam.EndPoint[1] - beam.StartPoint[1]);

            // Dầm nào dài hơn theo X → Direction = X
            // Dầm nào dài hơn theo Y → Direction = Y
            return (dx >= dy) ? "X" : "Y";
        }

        /// <summary>
        /// Extract số từ SectionLabel (VD: "2GY3" -> 3).
        /// </summary>
        private static int ExtractNumber(string sectionLabel)
        {
            if (string.IsNullOrEmpty(sectionLabel)) return 0;

            // Tìm vị trí số cuối cùng
            int i = sectionLabel.Length - 1;
            while (i >= 0 && char.IsDigit(sectionLabel[i]))
                i--;

            if (i == sectionLabel.Length - 1) return 0; // Không có số

            string numStr = sectionLabel.Substring(i + 1);
            if (int.TryParse(numStr, out int num))
                return num;

            return 0;
        }

        /// <summary>
        /// Lấy số tiếp theo available (skip reserved numbers).
        /// </summary>
        private static int GetNextAvailableNumber(string counterKey, Dictionary<string, int> counters, Dictionary<string, HashSet<int>> reserved)
        {
            int current = counters.ContainsKey(counterKey) ? counters[counterKey] : 1;

            if (!reserved.ContainsKey(counterKey))
                return current;

            var reservedSet = reserved[counterKey];
            while (reservedSet.Contains(current))
                current++;

            return current;
        }

        /// <summary>
        /// Sort beams theo SortCorner và SortDirection.
        /// </summary>
        private static List<BeamData> SortBeams(List<BeamData> beams, int sortCorner, int sortDirection)
        {
            // SortCorner: 0=TL, 1=TR, 2=BL, 3=BR
            // SortDirection: 0=X first (horizontal), 1=Y first (vertical)

            IOrderedEnumerable<BeamData> sorted;

            switch (sortCorner)
            {
                case 0: // Top-Left: Y desc, X asc
                    sorted = beams.OrderByDescending(b => b.CenterY).ThenBy(b => b.CenterX);
                    break;
                case 1: // Top-Right: Y desc, X desc
                    sorted = beams.OrderByDescending(b => b.CenterY).ThenByDescending(b => b.CenterX);
                    break;
                case 2: // Bottom-Left: Y asc, X asc
                    sorted = beams.OrderBy(b => b.CenterY).ThenBy(b => b.CenterX);
                    break;
                case 3: // Bottom-Right: Y asc, X desc
                    sorted = beams.OrderBy(b => b.CenterY).ThenByDescending(b => b.CenterX);
                    break;
                default: // Fallback to TL
                    sorted = beams.OrderByDescending(b => b.CenterY).ThenBy(b => b.CenterX);
                    break;
            }

            return sorted.ToList();
        }
    }
}
