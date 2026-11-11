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
    private static readonly string[] Headers =
    ["모델", "모델 경로", "이름", "Room 요소 ID (이름 없음/ND)", "번호", "실명", "문제 필드", "현재 값", "점검 결과"];

    public static string PathForRule(string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("용도구분+공간정보엑셀검토 룰셋 파일을 다시 선택하세요. 엑셀은 같은 폴더에 저장됩니다.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, ReportFileName);
    }

    public static void Save(string path, IEnumerable<ModelSnapshot> models, IEnumerable<RoomSpaceFinding> findings)
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
                    WriteSheet(zip, number, findingList.Where(f => string.Equals(f.ModelPath, model.Path, StringComparison.OrdinalIgnoreCase)));
                }
                contentTypes.Append("</Types>"); workbook.Append("</sheets></workbook>"); relationships.Append("</Relationships>");
                Write(zip, "[Content_Types].xml", contentTypes.ToString());
                Write(zip, "_rels/.rels", "<?xml version=\"1.0\" encoding=\"utf-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>");
                Write(zip, "xl/workbook.xml", workbook.ToString());
                Write(zip, "xl/_rels/workbook.xml.rels", relationships.ToString());
            }
            File.Move(temporary, fullPath, true);
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

    private static void WriteSheet(ZipArchive zip, int sheetNumber, IEnumerable<RoomSpaceFinding> findings)
    {
        using var stream = zip.CreateEntry($"xl/worksheets/sheet{sheetNumber}.xml", CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        foreach (var (width, index) in new[] { 36, 70, 34, 27, 18, 34, 19, 28, 35 }.Select((width, index) => (width, index)))
            writer.Write($"<col min=\"{index + 1}\" max=\"{index + 1}\" width=\"{width}\" customWidth=\"1\"/>");
        writer.Write("</cols><sheetData>");
        Row(writer, 1, Headers);
        var number = 1;
        foreach (var item in findings)
        {
            var idWhenNameMissing = item.RevitName.Length == 0 || string.Equals(item.RevitName, "ND", StringComparison.OrdinalIgnoreCase)
                ? item.ElementId : "";
            Row(writer, ++number, [item.Model, item.ModelPath, item.RevitName, idWhenNameMissing,
                item.Number, item.RoomName, item.Field, item.CurrentValue, item.Issue]);
        }
        writer.Write($"</sheetData><autoFilter ref=\"A1:I{number}\"/></worksheet>");
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
