using SunnyParameterManager.Core;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Text;
using System.Xml;

namespace SunnyParameterManager.Desktop;

public static class RoomReviewWorkbook
{
    public const string ReportFileName = "룸_검토_점검결과.xlsx";
    public static readonly string[] Headers =
    ["모델명", "용도", "지상지하", "층", "설명", "번호", "실번호", "기존실번호", "이름", "실명",
        "자산번호", "LOD", "공간분류코드", "구역", "실ID", "실내재료마감-걸레받이",
        "실내재료마감-바닥", "실내재료마감-벽", "실내재료마감-천장", "작성기준"];

    public static string PathForRule(string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("룸 검토 룰셋 파일을 다시 선택하세요. 엑셀은 같은 폴더에 저장됩니다.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, ReportFileName);
    }

    public static string Save(string path, IEnumerable<ModelSnapshot> models, IEnumerable<RoomSpaceFinding> findings)
    {
        var modelList = models.ToList();
        if (modelList.Count == 0) throw new InvalidOperationException("공간정보를 점검할 모델이 없습니다.");
        var findingList = findings.ToList();
        var names = SheetNames(modelList);
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temporary))
            using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
            {
                var contentTypes = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>");
                var workbook = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>");
                var relationships = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">");
                for (var i = 0; i < modelList.Count; i++)
                {
                    var number = i + 1;
                    contentTypes.Append($"<Override PartName=\"/xl/worksheets/sheet{number}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>");
                    workbook.Append($"<sheet name=\"{SecurityElement.Escape(names[i])}\" sheetId=\"{number}\" r:id=\"rId{number}\"/>");
                    relationships.Append($"<Relationship Id=\"rId{number}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{number}.xml\"/>");
                    var model = modelList[i];
                    WriteSheet(zip, number, model, findingList.Where(f => string.Equals(f.ModelPath, model.Path, StringComparison.OrdinalIgnoreCase)));
                }
                contentTypes.Append("<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>");
                relationships.Append($"<Relationship Id=\"rId{modelList.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
                contentTypes.Append("</Types>"); workbook.Append("</sheets></workbook>"); relationships.Append("</Relationships>");
                Write(zip, "[Content_Types].xml", contentTypes.ToString());
                Write(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Write(zip, "xl/workbook.xml", workbook.ToString());
                Write(zip, "xl/_rels/workbook.xml.rels", relationships.ToString());
                Write(zip, "xl/styles.xml", Styles);
            }
            return WorkbookOutput.Publish(temporary, fullPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static List<string> SheetNames(IReadOnlyList<ModelSnapshot> models)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new List<string>(models.Count);
        for (var i = 0; i < models.Count; i++)
        {
            var source = Path.GetFileNameWithoutExtension(models[i].Path);
            if (string.IsNullOrWhiteSpace(source)) source = models[i].Title;
            var clean = new string((source ?? "").Where(c => XmlConvert.IsXmlChar(c) && "[]:*?/\\".IndexOf(c) < 0).ToArray()).Trim(' ', '\'');
            if (clean.Length == 0) clean = $"모델 {i + 1}";
            var stem = clean[..Math.Min(31, clean.Length)];
            var name = stem;
            for (var suffixNumber = 2; !used.Add(name); suffixNumber++)
            {
                var suffix = $" ({suffixNumber})";
                name = stem[..Math.Min(31 - suffix.Length, stem.Length)] + suffix;
            }
            names.Add(name);
        }
        return names;
    }

    private static void WriteSheet(ZipArchive zip, int sheetNumber, ModelSnapshot model, IEnumerable<RoomSpaceFinding> findings)
    {
        var problems = findings.Select(f => (f.ElementId, f.Field)).ToHashSet();
        var rooms = model.Owners.Concat(model.Rows)
            .Where(r => r.Scope == "인스턴스" && r.CategoryKey == RoomUsePlanner.RoomCategory)
            .GroupBy(r => r.OwnerId, StringComparer.Ordinal)
            .Select(g => g.ToList())
            .OrderBy(r => SortValue(r, "번호"), StringComparer.Ordinal)
            .ThenBy(r => SortValue(r, "실번호"), StringComparer.Ordinal)
            .ThenBy(r => r[0].OwnerId, StringComparer.Ordinal);
        using var stream = zip.CreateEntry($"xl/worksheets/sheet{sheetNumber}.xml", CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        foreach (var (width, index) in new[] { 38, 14, 16, 11, 30, 16, 16, 19, 30, 30, 18, 12, 22, 18, 18, 28, 28, 28, 28, 18 }.Select((width, index) => (width, index)))
            writer.Write($"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{width}\" customWidth=\"1\"/>");
        writer.Write("</cols><sheetData>");
        Row(writer, 1, Headers, null, true);
        var number = 1;
        foreach (var rows in rooms)
        {
            var identity = rows.FirstOrDefault(r => r.Name.Length == 0) ?? rows[0];
            var elementId = rows.Select(r => r.ElementId).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? rows[0].OwnerId;
            var values = new string[Headers.Length];
            values[0] = number == 1 ? model.Title : "";
            values[8] = identity.ElementName.Trim();
            var styles = new bool[Headers.Length];
            for (var i = 0; i < Headers.Length; i++)
            {
                var field = i == 7 ? "기존 실번호" : Headers[i];
                styles[i] = problems.Contains((elementId, field));
                if (i is 0 or 8) continue;
                var matches = rows.Where(r => r.Name == field).ToList();
                values[i] = matches.Count switch
                {
                    0 => "",
                    1 => Value(matches[0]),
                    _ => "동명 매개변수 확인 필요"
                };
            }
            Row(writer, ++number, values, styles);
        }
        writer.Write($"</sheetData><autoFilter ref=\"A1:T{number}\"/></worksheet>");
    }

    private static string SortValue(IReadOnlyList<ParameterRow> rows, string field)
    {
        var matches = rows.Where(r => r.Name == field).Take(2).ToList();
        var value = matches.Count == 1 ? Value(matches[0]) : "";
        return value.Length == 0 ? "\uFFFF" : value;
    }

    private static string Value(ParameterRow row) => row.HasValue
        ? (row.Display.Length > 0 ? row.Display : row.Raw).Trim() : "";

    private static void Row(StreamWriter writer, int number, IReadOnlyList<string> values, bool[]? problems = null, bool header = false)
    {
        writer.Write($"<row r=\"{number}\" ht=\"{(header ? 28 : 21)}\" customHeight=\"1\">");
        for (var i = 0; i < values.Count; i++)
        {
            var safe = new string((values[i] ?? "").Where(XmlConvert.IsXmlChar).Take(32767).ToArray());
            var styleNumber = header ? 2 : problems?[i] == true ? 1 : i == 0 && safe.Length > 0 ? 4 : number % 2 == 0 ? 3 : 0;
            var style = $" s=\"{styleNumber}\"";
            writer.Write($"<c r=\"{(char)('A' + i)}{number}\"{style} t=\"inlineStr\"><is><t xml:space=\"preserve\">{SecurityElement.Escape(safe)}</t></is></c>");
        }
        writer.Write("</row>");
    }

    // 본문은 작은 글자와 연초록 행 교차색을 쓰고, 확인이 필요한 셀은 붉게 강조한다.
    private const string Styles = """
        <?xml version="1.0" encoding="utf-8"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="3"><font><sz val="9"/><color rgb="FF244036"/></font><font><b/><sz val="11"/><color rgb="FFFFFFFF"/></font><font><b/><sz val="9"/><color rgb="FF20543B"/></font></fonts>
          <fills count="5"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill><fill><patternFill patternType="solid"><fgColor rgb="FFFFE8DE"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FF256B4A"/><bgColor indexed="64"/></patternFill></fill><fill><patternFill patternType="solid"><fgColor rgb="FFEAF5EE"/><bgColor indexed="64"/></patternFill></fill></fills>
          <borders count="1"><border/></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="5"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/><xf numFmtId="0" fontId="0" fillId="2" borderId="0" xfId="0" applyFill="1"/><xf numFmtId="0" fontId="1" fillId="3" borderId="0" xfId="0" applyFont="1" applyFill="1"><alignment vertical="center"/></xf><xf numFmtId="0" fontId="0" fillId="4" borderId="0" xfId="0" applyFill="1"/><xf numFmtId="0" fontId="2" fillId="4" borderId="0" xfId="0" applyFont="1" applyFill="1"/></cellXfs>
        </styleSheet>
        """;

    private static void Write(ZipArchive zip, string name, string contents)
    {
        using var stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(contents);
    }
}
