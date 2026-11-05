using SunnyParameterManager.Core;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace SunnyParameterManager.Desktop;

// 선택 모델당 한 행으로 활성 공유대지의 프로젝트 기준점 값을 기록한다.
public static class ProjectBasePointWorkbook
{
    public const string ReportFileName = "프로젝트기준점_공유대지_점검결과.xlsx";
    private static readonly string[] Headers =
    [
        "모델 파일", "모델 경로", "활성 공유대지", "북/남 (mm)", "동/서 (mm)",
        "높이 (mm)", "진북각도 (°)", "상태"
    ];

    public static string PathForRule(string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("프로젝트 기준점 점검 룰셋 파일을 다시 선택하세요. 엑셀은 그 룰셋과 같은 폴더에 저장됩니다.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, ReportFileName);
    }

    public static void Save(string path, IEnumerable<ModelSnapshot> models)
    {
        var rows = models.ToList();
        if (rows.Any(m => m.ProjectBasePointAudit == null))
            throw new InvalidOperationException("프로젝트 기준점을 읽지 못한 모델이 있습니다. 해당 파일을 다시 조회하세요.");
        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "." + Path.GetFileName(fullPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            WriteWorkbook(temporary, rows);
            File.Move(temporary, fullPath, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void WriteWorkbook(string path, List<ModelSnapshot> rows)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Write(zip, "[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>");
        Write(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
        Write(zip, "xl/workbook.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"기준점 점검\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
        Write(zip, "xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>");
        // 숫자는 실제 숫자 셀로 유지하고 표시 자리수만 고정한다.
        Write(zip, "xl/styles.xml", "<?xml version=\"1.0\" encoding=\"utf-8\"?><styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><fonts count=\"1\"><font><sz val=\"11\"/><name val=\"맑은 고딕\"/></font></fonts><fills count=\"1\"><fill><patternFill patternType=\"none\"/></fill></fills><borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders><cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs><cellXfs count=\"3\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/><xf numFmtId=\"3\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/><xf numFmtId=\"2\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>");
        using var sheetStream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
        using var xml = new StreamWriter(sheetStream, new UTF8Encoding(false));
        xml.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        var widths = new[] { 38, 70, 28, 18, 18, 18, 20, 52 };
        for (var i = 0; i < widths.Length; i++) xml.Write($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i]}\" customWidth=\"1\"/>");
        xml.Write("</cols><sheetData><row r=\"1\">");
        for (var c = 0; c < Headers.Length; c++) TextCell(xml, 1, c, Headers[c]);
        xml.Write("</row>");
        for (var i = 0; i < rows.Count; i++)
        {
            var model = rows[i];
            var audit = model.ProjectBasePointAudit!;
            var row = i + 2;
            xml.Write($"<row r=\"{row}\">");
            TextCell(xml, row, 0, Path.GetFileName(model.Path));
            TextCell(xml, row, 1, model.Path);
            TextCell(xml, row, 2, audit.SiteName);
            NumberCell(xml, row, 3, audit.NorthSouthMillimeters, 1);
            NumberCell(xml, row, 4, audit.EastWestMillimeters, 1);
            NumberCell(xml, row, 5, audit.ElevationMillimeters, 1);
            NumberCell(xml, row, 6, audit.TrueNorthDegrees, 2);
            TextCell(xml, row, 7, audit.Status);
            xml.Write("</row>");
        }
        xml.Write("</sheetData>");
        if (rows.Count > 0) xml.Write($"<autoFilter ref=\"A1:H{rows.Count + 1}\"/>");
        xml.Write("</worksheet>");
    }

    private static void TextCell(StreamWriter xml, int row, int col, string? value)
    {
        var safe = new string((value ?? "").Where(XmlConvert.IsXmlChar).Take(32767).ToArray());
        xml.Write($"<c r=\"{(char)('A' + col)}{row}\" t=\"inlineStr\"><is><t xml:space=\"preserve\">");
        xml.Write(SecurityElement.Escape(safe));
        xml.Write("</t></is></c>");
    }

    private static void NumberCell(StreamWriter xml, int row, int col, double? value, int style)
    {
        if (value is not { } number || !double.IsFinite(number)) return;
        xml.Write($"<c r=\"{(char)('A' + col)}{row}\" s=\"{style}\"><v>{number.ToString("0.##", CultureInfo.InvariantCulture)}</v></c>");
    }

    private static void Write(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }
}
