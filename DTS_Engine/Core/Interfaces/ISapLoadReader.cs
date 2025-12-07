using DTS_Engine.Core.Data;
using System.Collections.Generic;

namespace DTS_Engine.Core.Interfaces
{
    /// <summary>
    /// Interface tr?u t??ng hóa vi?c ??c t?i tr?ng t? ngu?n d? li?u.
    /// Tuân th? Dependency Inversion Principle (SOLID).
    /// 
    /// RATIONALE:
    /// - AuditEngine không ph? thu?c vào SAP2000 API c? th?
    /// - Có th? thay ??i ngu?n d? li?u (SAP, Excel, SQL) mà không s?a Engine
    /// - D? dàng Mock/Stub trong Unit Test
    /// 
    /// RESPONSIBILITY:
    /// - ??c t?t c? lo?i t?i tr?ng (Frame, Area, Point) t? ngu?n d? li?u
    /// - Tr? v? danh sách RawSapLoad chu?n hóa (Data Contract)
    /// - X? lý fallback và error handling n?i b?
    /// </summary>
    public interface ISapLoadReader
    {
        /// <summary>
        /// ??c t?t c? t?i tr?ng t? ngu?n d? li?u theo Load Pattern.
        /// 
        /// SPECIFICATIONS:
        /// - Input: patternFilter (null = l?y t?t c?)
        /// - Output: List<RawSapLoad> v?i Vector ?ã ???c tính toán (DirectionX/Y/Z)
        /// - Behavior: T? ??ng fallback Table -> API khi c?n
        /// - Performance: Cache geometry ?? t?i ?u
        /// 
        /// POSTCONDITIONS:
        /// - M?i RawSapLoad ph?i có DirectionX/Y/Z h?p l?
        /// - ElementZ ph?i ???c ?i?n cho vi?c phân t?ng
        /// - Value1 ?ã ???c convert sang ??n v? chu?n (kN/m, kN/m², kN)
        /// </summary>
        /// <param name="patternFilter">Tên Load Pattern c?n l?c (null = t?t c?)</param>
        /// <returns>Danh sách t?i tr?ng chu?n hóa</returns>
        List<RawSapLoad> ReadAllLoads(string patternFilter);
    }
}
