using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using SunnyParameterManager.Bridge;
using SunnyParameterManager.Core;

internal static class StairNameTests
{
    internal static void Run(Action<bool, string> check)
    {
        State.Reset();
        var doc = State.Doc;
        var type = new StairsRunType { Name = "KAC-S-ST1110_ND-T150" };
        doc.Types[type.UniqueId] = type;
        var edit = new ValueEdit { Original = new() { OwnerId = type.UniqueId, TypeOwnerId = type.UniqueId, TypeName = type.Name, Family = type.FamilyName } };
        var expected = StairTypeNames.Capture(doc, [edit, edit]);
        check(expected.Count == 1 && expected[0].Name == type.Name, "계단진행 이름 보존 계획은 실제 유형별 하나");
        check(StairTypeNames.Restore(doc, expected) == 0 && !State.Events.Any(), "이름이 같으면 추가 트랜잭션 없음");
        type.Name = "KAC-S-ST1110_ND-T1";
        check(StairTypeNames.Restore(doc, expected) == 1 && type.Name == edit.Original.TypeName, "재생성된 계단진행 이름만 원래 이름으로 보존");
        edit.KacCleanup = true; edit.Original.ParameterKey = CleanupPlanner.NameKey; edit.Value = "KAC-S-ST1110_ND-T200";
        expected = StairTypeNames.Capture(doc, [edit]);
        check(expected.Single().Name == edit.Value && StairTypeNames.Restore(doc, expected) == 1 && type.Name == edit.Value, "유형 정리 명시 승인은 이전 이름이 아니라 승인한 이름 보존");
        edit.Original.TypeName = type.Name;
        bool Refused(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        type.CanBeRenamed = false; type.Name = "예상 밖";
        check(Refused(() => StairTypeNames.Restore(doc, expected)) && type.Name == "예상 밖", "실제 이름 변경 불가는 강제 해제하지 않음");
        type.CanBeRenamed = true; type.Id = new(99);
        check(Refused(() => StairTypeNames.Restore(doc, expected)), "유형 요소 ID 변경 시 복원 금지");
        type.Id = new(10); type.FamilyName = "다른 패밀리";
        check(Refused(() => StairTypeNames.Restore(doc, expected)), "패밀리 변경 시 복원 금지");
        type.FamilyName = expected[0].Family; type.Category!.Id = new(1);
        check(Refused(() => StairTypeNames.Restore(doc, expected)), "카테고리 변경 시 복원 금지");
        doc.Types[type.UniqueId] = new ElementType();
        check(Refused(() => StairTypeNames.Restore(doc, expected)), "같은 키라도 다른 시스템 유형으로 대체 시 차단");
        check(StairTypeNames.Capture(doc, [edit]).Count == 0, "계단진행 이외 유형의 이름 변화는 보정하지 않음");
        doc.Types[type.UniqueId] = type;
        check(Refused(() => StairTypeNames.Capture(doc, [edit])), "조회 당시 계단진행 이름 불일치 사전 차단");
        type.Category.Id = new(expected[0].Category); type.Name = "달라진 이름"; State.FailCommit = true;
        check(Refused(() => StairTypeNames.Restore(doc, expected)), "이름 보존 트랜잭션 확정 실패 전파·자동 반복 없음");
    }
}
