using System.IO;
using SunnyParameterManager.Core;

namespace SunnyParameterManager.Desktop;

public static class RoomEstimateWorkbook
{
    public static string Save(string folder, IEnumerable<RoomEstimate> rows)
    {
        return TypeCodeWorkbook.SaveTable(Path.Combine(folder, "용도_공간분류_임시값_적용결과.xlsx"), "임시값 확인",
            ["모델명", "룸 요소 ID", "실명", "확인 항목", "기존 값", "임시값", "처리 상태", "판정 근거", "모델 경로"],
            [46, 18, 38, 21, 14, 14, 24, 85, 72],
            rows.Distinct().OrderBy(r => r.Model, StringComparer.OrdinalIgnoreCase)
                .ThenBy(r => r.ElementId, StringComparer.Ordinal).ThenBy(r => r.Field, StringComparer.Ordinal)
                .Select(r => new[] { r.Model, r.ElementId, r.RealName, r.Field, r.Before, r.Proposed, r.Status, r.Basis, r.ModelPath }));
    }
}
