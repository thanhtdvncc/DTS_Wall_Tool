namespace DTS_Wall_Tool.Core.Data
{
    /// <summary>
    /// Định danh loại phần tử xây dựng
    /// </summary>
    public enum ElementType
    {
        Unknown = 0,
        Beam = 1,       // Dầm (Line/Polyline)
        Column = 2,     // Cột (Polyline/Block)
        Slab = 3,       // Sàn (Polyline)
        Wall = 4,       // Tường (Line)
        Foundation = 5, // Móng (Polyline)
        Stair = 6,      // Cầu thang (Polyline)
        Pile = 7,       // Cọc (Line)
        Lintel = 8,     // Lanh tô (Line)
        Rebar = 9,      // Cốt Thép (Line/Polyline)
        StoryOrigin = 99, // Điểm gốc tầng
        ElementOrigin = 100 // Điểm gốc phần tử
    }
}