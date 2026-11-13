using SunnyParameterManager.Core;
using System.IO;

namespace SunnyParameterManager.Desktop;

public static class GuardhouseRoomWorkbook
{
    private const string FileName = "초소_룸_이름실명_검토결과.xlsx";

    public static string PathForRule(string? rulePath)
    {
        if (string.IsNullOrWhiteSpace(rulePath) || !File.Exists(rulePath))
            throw new InvalidOperationException("초소 룸 정리 룰셋을 다시 선택하세요. 엑셀은 같은 폴더에 저장합니다.");
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(rulePath))!, FileName);
    }

    public static void Save(string path, IEnumerable<GuardhouseRoomFinding> findings) =>
        TypeCodeWorkbook.SaveTable(path, "이름·실명 검토",
            ["모델 파일", "룸 요소 ID", "기존 번호", "기존 이름", "기존 실명", "확인 항목"],
            [46, 18, 16, 34, 34, 20],
            findings.OrderBy(x => x.Model, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Number, StringComparer.Ordinal)
                .ThenBy(x => x.ElementId, StringComparer.Ordinal)
                .Select(x => new[] { x.Model, x.ElementId, x.Number, x.Name, x.RealName, x.Fields }));
}