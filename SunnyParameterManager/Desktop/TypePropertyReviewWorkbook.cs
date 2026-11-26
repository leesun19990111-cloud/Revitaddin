using SunnyParameterManager.Core;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace SunnyParameterManager.Desktop;

public static class TypePropertyReviewWorkbook
{
    private static readonly string[] Headers =
        ["모델명", "카테고리", "패밀리", "유형명", "유형 ID", "배치 수", .. TypePropertyReview.Fields, "점검 사유"];
    private static readonly int[] Widths = [42, 18, 28, 46, 16, 12, 30, 26, 14, 18, 17, 16, 20, 21, 25, 26, 110];

    public static (string Path, int IssueCount) Save(string? rulePath, IReadOnlyList<ModelSnapshot> models, TypePropertyReviewProfile profile)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("유형속성검토 룰셋 파일을 다시 선택하세요. 엑셀은 같은 폴더에 저장됩니다.");
        if (models.Count == 0) throw new InvalidOperationException("유형속성을 점검할 모델이 없습니다.");

        var target = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, $"{DateTime.Now:yyMMdd}_유형속성검토_점검결과.xlsx");
        var review = new TypePropertyReview(profile);
        var issues = 0;
        var path = RoomReviewWorkbook.SaveStyled(target, RoomReviewWorkbook.SheetNames(models), (zip, index) =>
        {
            var model = models[index - 1];
            using var stream = zip.CreateEntry($"xl/worksheets/sheet{index}.xml", CompressionLevel.Optimal).Open();
            using var writer = new StreamWriter(stream, new UTF8Encoding(false));
            writer.Write("<?xml version=\"1.0\" encoding=\"utf-8\"?><worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
            for (var i = 0; i < Widths.Length; i++)
                writer.Write($"<col min=\"{i + 1}\" max=\"{i + 1}\" width=\"{Widths[i]}\" customWidth=\"1\"/>");
            writer.Write("</cols><sheetData>");
            RoomReviewWorkbook.Row(writer, 1, Headers, header: true);
            var number = 1;
            foreach (var row in review.Inspect(model))
            {
                var type = row.Type;
                var reason = row.Cells.Select((c, i) => (c.Issue, Field: TypePropertyReview.Fields[i]))
                    .Where(x => x.Issue.Length != 0).Select(x => $"{x.Field} · {x.Issue}").ToList();
                issues += reason.Count;
                string[] values = [number == 1 ? model.Title : "", type?.Category ?? "", type?.Family ?? "",
                    type?.Name ?? "검토 대상 KAC 유형 없음", type?.ElementId ?? "", type?.InstanceCount.ToString() ?? "",
                    .. row.Cells.Select(c => c.Value), string.Join(" | ", reason)];
                bool[] red = [false, false, false, false, false, false, .. row.Cells.Select(c => c.Issue.Length != 0), false];
                RoomReviewWorkbook.Row(writer, ++number, values, missingRequired: red, numericColumn: type == null ? -1 : 5);
            }
            writer.Write($"</sheetData><autoFilter ref=\"A1:Q{number}\"/></worksheet>");
        });
        return (path, issues);
    }
}
