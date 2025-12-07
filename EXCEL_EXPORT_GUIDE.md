# 📊 EXCEL EXPORT - HƯỚNG DẪN CÀI ĐẶT

## Giới thiệu

Tính năng xuất Excel đang được phát triển. Hiện tại có 2 thư viện C# tốt nhất để tạo file Excel:

---

## ✅ OPTION 1: ClosedXML (KHUYẾN NGHỊ)

### Ưu điểm:
- ✅ **MIT License** - Hoàn toàn miễn phí, không giới hạn thương mại
- ✅ API đơn giản, dễ sử dụng
- ✅ Hỗ trợ tốt cho .NET Framework 4.8
- ✅ Tạo file .xlsx chuẩn (Excel 2007+)

### Cài đặt:
```powershell
Install-Package ClosedXML
```

### Code mẫu:
```csharp
using ClosedXML.Excel;

public void ExportToExcel(AuditReport report, string filePath)
{
    using (var workbook = new XLWorkbook())
    {
        var worksheet = workbook.Worksheets.Add("Audit Report");
        
        // Header
        worksheet.Cell("A1").Value = "SAP2000 LOAD AUDIT REPORT";
        worksheet.Cell("A1").Style.Font.Bold = true;
        worksheet.Cell("A1").Style.Font.FontSize = 16;
        
        // Data
        int row = 3;
        worksheet.Cell(row, 1).Value = "Model:";
        worksheet.Cell(row, 2).Value = report.ModelName;
        row++;
        
        worksheet.Cell(row, 1).Value = "Load Pattern:";
        worksheet.Cell(row, 2).Value = report.LoadPattern;
        row += 2;
        
        // Table headers
        worksheet.Cell(row, 1).Value = "Grid Location";
        worksheet.Cell(row, 2).Value = "Calculator";
        worksheet.Cell(row, 3).Value = "Type";
        worksheet.Cell(row, 4).Value = "Unit Load";
        worksheet.Cell(row, 5).Value = "Force";
        worksheet.Cell(row, 6).Value = "Direction";
        worksheet.Cell(row, 7).Value = "Elements";
        
        // Style header row
        var headerRange = worksheet.Range(row, 1, row, 7);
        headerRange.Style.Font.Bold = true;
        headerRange.Style.Fill.BackgroundColor = XLColor.LightGray;
        headerRange.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
        
        row++;
        
        // Data rows
        foreach (var story in report.Stories)
        {
            foreach (var loadType in story.LoadTypes)
            {
                foreach (var entry in loadType.Entries)
                {
                    worksheet.Cell(row, 1).Value = entry.GridLocation;
                    worksheet.Cell(row, 2).Value = entry.Explanation;
                    worksheet.Cell(row, 3).Value = entry.QuantityUnit;
                    worksheet.Cell(row, 4).Value = entry.UnitLoad;
                    worksheet.Cell(row, 5).Value = entry.TotalForce;
                    worksheet.Cell(row, 6).Value = entry.Direction;
                    worksheet.Cell(row, 7).Value = string.Join(", ", entry.ElementList);
                    row++;
                }
            }
        }
        
        // Auto-fit columns
        worksheet.Columns().AdjustToContents();
        
        workbook.SaveAs(filePath);
    }
}
```

---

## ⚠️ OPTION 2: EPPlus

### Ưu điểm:
- ✅ Hiệu năng cao
- ✅ API mạnh mẽ, nhiều tính năng nâng cao

### Nhược điểm:
- ⚠️ Từ phiên bản 5.0+: **Polyform Noncommercial License**
- ⚠️ Cần mua license thương mại nếu dùng cho dự án có thu phí
- ⚠️ EPPlus 4.5.3.3 (phiên bản cuối cùng LGPL) đã cũ, ít tính năng

### Cài đặt:
```powershell
# EPPlus 5+ (cần license thương mại)
Install-Package EPPlus

# EPPlus 4.5.3.3 (LGPL - miễn phí nhưng cũ)
Install-Package EPPlus -Version 4.5.3.3
```

### Code mẫu (EPPlus 5+):
```csharp
using OfficeOpenXml;

public void ExportToExcel(AuditReport report, string filePath)
{
    ExcelPackage.LicenseContext = LicenseContext.NonCommercial; // Hoặc Commercial
    
    using (var package = new ExcelPackage())
    {
        var worksheet = package.Workbook.Worksheets.Add("Audit Report");
        
        // Header
        worksheet.Cells["A1"].Value = "SAP2000 LOAD AUDIT REPORT";
        worksheet.Cells["A1"].Style.Font.Bold = true;
        worksheet.Cells["A1"].Style.Font.Size = 16;
        
        // Data (tương tự ClosedXML)
        int row = 3;
        worksheet.Cells[row, 1].Value = "Model:";
        worksheet.Cells[row, 2].Value = report.ModelName;
        // ... (tương tự như trên)
        
        // Save
        FileInfo fileInfo = new FileInfo(filePath);
        package.SaveAs(fileInfo);
    }
}
```

---

## 🎯 KHUYẾN NGHỊ

**Sử dụng ClosedXML** vì:
1. Hoàn toàn miễn phí (MIT License)
2. API đơn giản hơn EPPlus
3. Đủ tính năng cho báo cáo audit
4. Không lo vấn đề license

---

## 📦 Tích hợp vào DTS Engine

### Bước 1: Thêm reference
```xml
<!-- Thêm vào DTS_Engine.csproj -->
<PackageReference Include="ClosedXML" Version="0.102.1" />
```

### Bước 2: Tạo class ExcelExporter
```csharp
// DTS_Engine\Core\Utils\ExcelExporter.cs
using ClosedXML.Excel;
using DTS_Engine.Core.Data;

namespace DTS_Engine.Core.Utils
{
    public static class ExcelExporter
    {
        public static void ExportAuditReport(AuditReport report, string filePath, 
            string targetUnit = "kN", string language = "English")
        {
            // Implementation như mẫu trên
        }
    }
}
```

### Bước 3: Sử dụng trong AuditCommands
```csharp
if (exportExcel)
{
    string fileName = $"DTS_Audit_{safeModel}_{selectedPattern}.xlsx";
    filePath = Path.Combine(tempFolder, fileName);
    ExcelExporter.ExportAuditReport(report, filePath, selectedUnit, selectedLang);
}
```

---

## 🚀 Tính năng Excel nâng cao

Có thể mở rộng:
- ✅ Conditional formatting (tô màu theo giá trị)
- ✅ Charts (biểu đồ tải trọng)
- ✅ Multiple sheets (mỗi tầng 1 sheet)
- ✅ Data validation (dropdown lists)
- ✅ Formulas (SUM, AVERAGE tự động)

---

## 📞 Hỗ trợ

- ClosedXML Docs: https://docs.closedxml.io/
- ClosedXML GitHub: https://github.com/ClosedXML/ClosedXML
- EPPlus Docs: https://epplussoftware.com/docs/

