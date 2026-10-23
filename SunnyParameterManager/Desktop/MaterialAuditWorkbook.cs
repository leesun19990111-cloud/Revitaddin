using SunnyParameterManager.Core;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace SunnyParameterManager.Desktop;

// 읽기 전용 재료 점검 결과를 외부 라이브러리 없이 표준 XLSX로 기록한다.
public static class MaterialAuditWorkbook
{
    private static readonly string[] Headers =
    [
        "모델 파일", "카테고리", "패밀리", "유형명", "유형 요소 ID", "문제", "확인 범위",
        "재료 매개변수·층", "재료 이름", "배치 요소 수", "예시 요소 ID", "상세"
    ];

    public static void Save(string path, IEnumerable<MaterialAuditItem> items)
    {
        var rows = items.ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Write(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Write(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"재료 점검\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Write(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        Write(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"2\"><font><sz val=\"11\"/><name val=\"맑은 고딕\"/></font><font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"맑은 고딕\"/></font></fonts><fills count=\"2\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF285C85\"/><bgColor indexed=\"64\"/></patternFill></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"0\" fontId=\"1\" fillId=\"1\" borderId=\"0\" xfId=\"0\" applyFill=\"1\" applyFont=\"1\"/></cellXfs></styleSheet>");
        using var sheetStream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
        using var xml = new StreamWriter(sheetStream, new UTF8Encoding(false));
        xml.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        var widths = new[] { 35, 19, 28, 38, 16, 24, 21, 28, 30, 17, 18, 55 };
        for (var i = 0; i < widths.Length; i++) xml.Write($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i]}\" customWidth=\"1\"/>");
        xml.Write("</cols><sheetData><row r=\"1\">");
        for (var c = 0; c < Headers.Length; c++) Cell(xml, 1, c, Headers[c], true);
        xml.Write("</row>");
        for (var i = 0; i < rows.Count; i++)
        {
            var item = rows[i];
            var values = new[] { item.Model, item.Category, item.Family, item.TypeName, item.TypeId, item.Issue,
                item.Scope, item.Slot, item.Material, item.ElementCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                item.ExampleElementId, item.Detail };
            xml.Write($"<row r=\"{i + 2}\">");
            for (var c = 0; c < values.Length; c++) Cell(xml, i + 2, c, values[c], false);
            xml.Write("</row>");
        }
        xml.Write("</sheetData>");
        if (rows.Count > 0) xml.Write($"<autoFilter ref=\"A1:L{rows.Count + 1}\"/>");
        xml.Write("</worksheet>");
    }

    private static void Cell(StreamWriter xml, int row, int column, string? value, bool header)
    {
        var safe = new string((value ?? "").Where(XmlConvert.IsXmlChar).Take(32767).ToArray());
        var address = $"{(char)('A' + column)}{row}";
        if (!header && column == 9 && int.TryParse(safe, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count))
        {
            xml.Write($"<c r=\"{address}\"><v>{count}</v></c>");
            return;
        }
        xml.Write($"<c r=\"{address}\" t=\"inlineStr\"{(header ? " s=\"1\"" : "")}><is><t xml:space=\"preserve\">");
        xml.Write(SecurityElement.Escape(safe));
        xml.Write("</t></is></c>");
    }

    private static void Write(ZipArchive zip, string name, string contents)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }
}
