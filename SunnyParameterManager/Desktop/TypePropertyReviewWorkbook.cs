using SunnyParameterManager.Core;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SunnyParameterManager.Desktop;

public static class TypePropertyReviewWorkbook
{
    public const string SummarySheetName = TypePropertyReviewSettings.DefaultSummaryName;
    private const int TypeNameColumn = 3;
    private const int FirstPropertyColumn = 6;
    public static (string Path, int IssueCount) Save(string? rulePath, IReadOnlyList<ModelSnapshot> models, TypePropertyReviewProfile profile)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("유형속성검토 룰셋 파일을 다시 선택하세요. 엑셀은 같은 폴더에 저장됩니다.");
        if (models.Count == 0) throw new InvalidOperationException("유형속성을 점검할 모델이 없습니다.");

        var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, $"{DateTime.Now:yyMMdd}_유형속성검토_점검결과.xlsx");
        var review = new TypePropertyReview(profile);
        var settings = review.Settings;
        var sheetNames = RoomReviewWorkbook.SheetNames(models, settings.ShowSummary ? settings.SummarySheetName : null);
        List<string> allNames = settings.ShowSummary ? [settings.SummarySheetName, .. sheetNames] : sheetNames;
        var modelCounts = new int[models.Count][];
        var path = RoomReviewWorkbook.SaveStyled(target, allNames, zip =>
        {
            // 모델 시트를 쓰면서 강조 셀을 집계한다. 집계 시트는 ZIP에 마지막으로 쓰지만 엑셀에서는 첫 시트다.
            for (var i = 0; i < models.Count; i++)
                modelCounts[i] = WriteModelSheet(zip, i + (settings.ShowSummary ? 2 : 1), models[i], review);
            if (settings.ShowSummary) WriteSummarySheet(zip, models, sheetNames, modelCounts, settings.Columns);
        });
        return (path, modelCounts.Sum(counts => counts.Sum()));
    }

    private static int[] WriteModelSheet(ZipArchive zip, int index, ModelSnapshot model, TypePropertyReview review)
    {
        var columns = review.Settings.Columns;
        string[] headers = ["모델명", "카테고리", "패밀리", "유형명", "유형 ID", "배치 수", .. columns.Select(c => c.Name), "점검 사유"];
        var counts = new int[1 + columns.Count];
        using var stream = zip.CreateEntry($"xl/worksheets/sheet{index}.xml", CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        StartSheet(writer, [42, 18, 28, 46, 16, 12, .. columns.Select(c => c.Width), 110]);
        RoomReviewWorkbook.Row(writer, 1, headers, header: true);
        var number = 1;
        foreach (var row in review.Inspect(model))
        {
            var type = row.Type;
            string[] values = [number == 1 ? model.Title : "", type.Category, type.Family, type.Name, type.ElementId,
                type.InstanceCount.ToString(CultureInfo.InvariantCulture), .. row.Cells.Select(c => c.Value), ""];
            var red = new bool[headers.Length];
            var reasons = new List<string>();
            if (row.NameIssue.Length != 0)
            {
                red[TypeNameColumn] = true;
                counts[0]++;
                reasons.Add("유형명 · " + row.NameIssue);
            }
            for (var i = 0; i < row.Cells.Count; i++)
            {
                var issue = row.Cells[i].Issue;
                if (issue.Length == 0) continue;
                red[FirstPropertyColumn + i] = true;
                counts[i + 1]++;
                reasons.Add($"{columns[i].Name} · {issue}");
            }
            values[^1] = string.Join(" | ", reasons);
            RoomReviewWorkbook.Row(writer, ++number, values, missingRequired: red, numericColumn: 5);
        }
        writer.Write($"</sheetData><autoFilter ref=\"A1:{RoomReviewWorkbook.ColumnName(headers.Length - 1)}{number}\"/></worksheet>");
        return counts;
    }

    private static void WriteSummarySheet(ZipArchive zip, IReadOnlyList<ModelSnapshot> models,
        IReadOnlyList<string> sheetNames, IReadOnlyList<int[]> counts, IReadOnlyList<TypePropertyReviewColumn> columns)
    {
        using var stream = zip.CreateEntry("xl/worksheets/sheet1.xml", CompressionLevel.Optimal).Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        string[] headers = ["모델명", "유형명", .. columns.Select(c => c.Name), "문제 셀 합계"];
        StartSheet(writer, [55, 13, .. columns.Select(c => c.SummaryWidth), 18], freezeModelColumn: true);
        RoomReviewWorkbook.Row(writer, 1, headers, header: true);

        var repeatedTitles = models.GroupBy(model => model.Title, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var total = new int[1 + columns.Count];
        for (var i = 0; i < models.Count; i++)
        {
            var title = models[i].Title;
            if (title.Length == 0) title = sheetNames[i];
            else if (repeatedTitles.Contains(title)) title += $" [{sheetNames[i]}]";
            WriteSummaryRow(writer, i + 2, title, counts[i]);
            for (var field = 0; field < total.Length; field++) total[field] += counts[i][field];
        }
        WriteSummaryRow(writer, models.Count + 2, "전체 합계", total, totalRow: true);
        writer.Write($"</sheetData><autoFilter ref=\"A1:{RoomReviewWorkbook.ColumnName(headers.Length - 1)}{models.Count + 1}\"/></worksheet>");
    }

    private static void WriteSummaryRow(StreamWriter writer, int number, string label, int[] counts, bool totalRow = false)
    {
        var sum = counts.Sum();
        string[] values = [label, .. counts.Select(count => count.ToString(CultureInfo.InvariantCulture)), sum.ToString(CultureInfo.InvariantCulture)];
        bool[] problems = [false, .. counts.Select(count => count > 0), sum > 0];
        RoomReviewWorkbook.Row(writer, number, values, problems, header: totalRow, numericColumn: 1, numericColumnCount: values.Length - 1);
    }

    private static void StartSheet(StreamWriter writer, int[] widths, bool freezeModelColumn = false)
    {
        writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\">");
        writer.Write(freezeModelColumn
            ? "<pane xSplit=\"1\" ySplit=\"1\" topLeftCell=\"B2\" activePane=\"bottomRight\" state=\"frozen\"/>"
            : "<pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/>");
        writer.Write("</sheetView></sheetViews><cols>");
        for (var i = 0; i < widths.Length; i++)
            writer.Write($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{widths[i]}\" customWidth=\"1\"/>");
        writer.Write("</cols><sheetData>");
    }
}
