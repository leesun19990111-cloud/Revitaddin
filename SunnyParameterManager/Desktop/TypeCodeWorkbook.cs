using SunnyParameterManager.Core;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace SunnyParameterManager.Desktop;

public static class TypeCodeWorkbook
{
    public const string ReportFileName = "KAC_세자리_유형코드_점검결과.xlsx";
    private static readonly string[] Headers = ["모델 파일", "카테고리", "패밀리", "유형 요소 ID", "현재 유형명", "변경 후 유형명", "처리 상태"];

    public static string PathForRule(string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("유형 코드 보정 룰셋 파일을 다시 선택하세요. 엑셀은 같은 폴더에 저장됩니다.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, ReportFileName);
    }

    public static string Save(string path, IEnumerable<TypeCodeFinding> findings) =>
        SaveTable(path, "유형 코드 점검", Headers, [35, 20, 27, 18, 45, 45, 42],
            findings.Select(item => new[] { item.Model, item.Category, item.Family, item.ElementId, item.CurrentName, item.ProposedName, item.Status }));

    internal static string SaveTable(string path, string sheetName, string[] headers, int[] widths, IEnumerable<string[]> rows)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                Write(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>");
                Write(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Write(zip, "xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"utf-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"{SecurityElement.Escape(sheetName)}\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Write(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
                foreach (var (width, index) in widths.Select((width, index) => (width, index)))
                    writer.Write($"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{width}\" customWidth=\"1\"/>");
                writer.Write("</cols><sheetData>");
                Row(writer, 1, headers);
                var rowNumber = 1;
                foreach (var row in rows)
                    Row(writer, ++rowNumber, row);
                writer.Write("</sheetData>");
                if (rowNumber > 1) writer.Write($"<autoFilter ref=\"A1:{(char)('A' + headers.Length - 1)}{rowNumber}\"/>");
                writer.Write("</worksheet>");
            }
            return WorkbookOutput.Publish(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Row(StreamWriter writer, int number, IReadOnlyList<string> values)
    {
        writer.Write($"<row r=\"{number}\">");
        for (var i = 0; i < values.Count; i++)
        {
            var safe = new string((values[i] ?? "").Where(XmlConvert.IsXmlChar).Take(32767).ToArray());
            writer.Write($"<c r=\"{(char)('A' + i)}{number}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(safe)}</t></is></c>");
        }
        writer.Write("</row>");
    }

    private static void Write(ZipArchive zip, string name, string contents)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }
}
