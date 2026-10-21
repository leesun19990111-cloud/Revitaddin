using Autodesk.Revit.DB;

namespace SunnyParameterManager.Bridge;

// 동기화 성공 후(또는 미저장 변경 롤백 후) 실행한다. 다른 사용자 강제 반환은 하지 않는다.
internal static class OwnershipRelease
{
    internal static void Complete(Document doc, Action<string> step)
    {
        step("차용 요소·모든 작업세트 소유권 반환 중");
        using var all = new RelinquishOptions(true);
        using var access = new TransactWithCentralOptions();
        access.SetLockCallback(new NoWait());
        using var released = WorksharingUtils.RelinquishOwnership(doc, all, access);
        if (released == null) throw new InvalidOperationException("Revit이 소유권 반환 결과를 제공하지 않았습니다.");
        step("소유권 반환 후 잔존 여부 검사");
        int elements = 0, worksets = 0;
        var examples = new List<string>();
        // 로컬 캐시 검사는 중앙 반환 호출을 대체하지 않는다. 전체 요소 ID만 순회해 매개변수 재조회/대량 문자열을 피한다.
        using var types = new FilteredElementCollector(doc).WhereElementIsElementType();
        using var instances = new FilteredElementCollector(doc).WhereElementIsNotElementType();
        foreach (var id in types.ToElementIds().Concat(instances.ToElementIds()))
        {
            if (WorksharingUtils.GetCheckoutStatus(doc, id) != CheckoutStatus.OwnedByCurrentUser) continue;
            elements++;
            if (examples.Count < 12) examples.Add("요소 ID " + id.Value);
        }
        // UserWorkset뿐 아니라 뷰·패밀리·프로젝트 표준 등 모든 종류를 검사한다.
        using var collector = new FilteredWorksetCollector(doc);
        foreach (var workset in collector)
        {
            if (!workset.IsEditable) continue;
            worksets++;
            if (examples.Count < 12) examples.Add($"작업세트 {workset.Name} (ID {workset.Id.IntegerValue})");
        }
        if (elements > 0 || worksets > 0)
            throw new InvalidOperationException($"현재 사용자 소유권이 남아 있거나 반환 확인이 필요합니다: 요소 {elements:N0}개 / 작업세트 {worksets:N0}개.\n" + string.Join("\n", examples) +
                "\n작업용 로컬을 보존합니다. 같은 사용자로 열어둔 다른 로컬의 미동기화 작업도 확인하세요. 강제 반환이나 동일 변경 재실행은 하지 마세요.");
    }
    private sealed class NoWait : ICentralLockedCallback { public bool ShouldWaitForLockAvailability() => false; }
}
