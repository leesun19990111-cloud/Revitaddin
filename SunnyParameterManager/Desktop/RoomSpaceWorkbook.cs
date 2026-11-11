using SunnyParameterManager.Core;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace SunnyParameterManager.Desktop;

public static class RoomSpaceWorkbook
{
    public const string ReportFileName = "용도구분+공간정보엑셀검토_점검결과.xlsx";
    private static readonly string[] Headers = ["모델", "모델 경로", "Room 요소 ID", "실명", "문제 필드", "현재 값", "점검 결과"];

    public static string PathForRule(string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("용도구분+공간정보엑셀검토 룰셋 파일을 다시 선택하세요. 엑셀은 같은 폴더에 저장됩니다.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, ReportFileName);
    }

    public static void Save(string path, IEnumerable<RoomSpaceFinding> findings)
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
                Write(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"공간정보 점검\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
                Write(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
                using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
                foreach (var (width, index) in new[] { 36, 70, 20, 35, 20, 28, 35 }.Select((width, index) => (width, index)))
                    writer.Write($"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{width}\" customWidth=\"1\"/>");
                writer.Write("</cols><sheetData>");
                Row(writer, 1, Headers);
                var number = 1;
                foreach (var item in findings)
                    Row(writer, ++number, [item.Model, item.ModelPath, item.ElementId, item.RoomName, item.Field, item.CurrentValue, item.Issue]);
                writer.Write("</sheetData>");
                if (number > 1) writer.Write($"<autoFilter ref=\"A1:G{number}\"/>");
                writer.Write("</worksheet>");
            }
            File.Move(temporary, fullPath, true);
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
