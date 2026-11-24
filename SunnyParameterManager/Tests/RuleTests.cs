using SunnyParameterManager.Core;
using System.Text.Json;

internal static class RuleTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        ParameterRow Row(string path = "A", string owner = "1") => new() { ModelPath = path, ModelTitle = path, OwnerId = owner, Scope = "유형", TypeName = "벽_100", Category = "벽", Family = "기본 벽", Name = "업체명", DataType = "text", Storage = "String", Editor = "텍스트", ParameterKey = "id:123", Raw = "기존", Display = "기존", HasValue = true };
        RuleSet File(string value = "새 업체") => new() { Version = 1, Name = "검사 규칙", Rules = [new() { Name = "벽 업체", Match = new() { Scope = "유형", Type = "벽_", TypeMatch = "시작" }, Set = [new() { Name = "업체명", DataType = "text", Storage = "String", Value = value }] }] };
        RulePlan Plan(RuleSet rules, params ParameterRow[] rows) => RulePlanner.Plan(rules, rows, [], false);
        var a = Row(); var b = Row("B");
        var file = File();
        check(Plan(file, a, b).Edits.Count == 2, "룰셋 여러 모델 미리보기");
        check(a.Raw == "기존", "룰셋 원본 값 보존");
        check(Plan(file, a).Edits[0].Reason.Contains("검사 규칙"), "룰셋 입력 근거 기록");
        check(RulePlanner.Parse(JsonSerializer.Serialize(file)).Rules.Count == 1, "룰셋 JSON 왕복");
        reject(() => RulePlanner.Parse(JsonSerializer.Serialize(file).Replace("\"Type\":", "\"Typo\":")), "알 수 없는 조건 묵시적 무시 금지");
        reject(() => RulePlanner.Parse(JsonSerializer.Serialize(file).Replace("\"Version\":1", "\"Version\":1,\"Version\":1")), "중복 JSON 속성 거부");
        reject(() => RulePlanner.Parse("{"), "잘못된 JSON 거부");
        reject(() => RulePlanner.Parse("null"), "null 룰셋 거부");
        reject(() => RulePlanner.Parse("{\"Version\":1,\"Name\":\"x\",\"Rules\":[null]}"), "null 규칙 거부");
        file.Version = 2; reject(() => Plan(file, a), "지원하지 않는 룰셋 버전 차단"); file.Version = 1;
        file.Rules[0].Match.Type = null; reject(() => Plan(file, a), "유형 조건 누락 전체 적용 차단"); file = File();
        file.Rules[0].Match.TypeMatch = "정규식"; reject(() => Plan(file, a), "임의 비교/실행 방식 차단"); file = File();
        file.Rules[0].Match.Model = ""; reject(() => Plan(file, a), "빈 모델 조건 거부"); file = File();
        file.Rules[0].Set[0].Value = null; reject(() => Plan(file, a), "입력값 누락 자동 지우기 금지"); file = File();
        check(Plan(File(""), a).Edits[0].Value == "", "명시적 빈 문자열 지우기 지원");
        check(Plan(File("기존"), a).Edits.Count == 0, "룰셋 동일값 제외");
        file.Rules[0].Match.Type = "없는 유형";
        check(Plan(file, a).Notes.Any(n => n.Contains("대상 없음")) && Plan(file, a).Edits.Count == 0, "조건 불일치 설명"); file = File();
        file.Rules[0].Match.Model = "A"; check(Plan(file, a, b).Edits.Count == 1, "파일 조건 격리"); file = File();
        file.Rules[0].Match.Type = "벽_100"; file.Rules[0].Match.TypeMatch = "일치"; check(Plan(file, a).Edits.Count == 1, "유형 완전 일치");
        file.Rules[0].Match.Type = "_10"; file.Rules[0].Match.TypeMatch = "포함"; check(Plan(file, a).Edits.Count == 1, "유형 포함 조건"); file = File();
        file.Rules[0].Match.Category = "바닥"; check(Plan(file, a).Edits.Count == 0, "카테고리 격리"); file = File();
        file.Rules[0].Match.Family = "다른 패밀리"; check(Plan(file, a).Edits.Count == 0, "패밀리 격리"); file = File();
        b.Name = "다른 항목"; reject(() => Plan(file, a, b), "일부 모델 필드 누락 전체 보류"); b = Row("B");
        b.DataType = "number"; reject(() => Plan(file, a, b), "동명 다른 자료형 차단"); b = Row("B");
        b.Storage = "Integer"; reject(() => Plan(file, a, b), "다른 저장형식 차단"); b = Row("B");
        b.BlockReason = "수식"; reject(() => Plan(file, a, b), "읽기전용 우회 금지"); b = Row("B");
        var duplicate = Row(); duplicate.ParameterKey = "id:999"; reject(() => Plan(file, a, duplicate), "같은 소유자의 동명 매개변수 모호성 차단");
        file.Rules[0].Set[0].ParameterKey = "id:123"; reject(() => Plan(file, a), "모델별 매개변수 ID 사용 금지");
        var guid = "guid:1ebd4cb8-d66f-41f7-8bab-5bb4c0e30149";
        file.Rules[0].Set[0].ParameterKey = guid; a.ParameterKey = guid; check(Plan(file, a).Edits.Count == 1, "공유 GUID 식별");
        reject(() => Plan(file, b), "지정한 GUID 대신 이름 추정 금지");
        file.Rules[0].Set[0].ParameterKey = "builtin:-123"; a.ParameterKey = "builtin:-123"; check(Plan(file, a).Edits.Count == 1, "기본 매개변수 식별");
        file = File(); a = Row(); var second = File("충돌").Rules[0]; second.Name = "다른 규칙"; file.Rules.Add(second);
        reject(() => Plan(file, a), "서로 다른 규칙 값 충돌 전체 보류");
        second.Set[0].Value = "새 업체"; check(Plan(file, a).Edits.Count == 1, "동일한 규칙 결과 중복 제거");
        second.Name = "벽 업체"; reject(() => Plan(file, a), "중복 규칙 이름 차단");
        file = File();
        var pending = EditPlanner.Stage([a], "수동");
        reject(() => RulePlanner.Plan(file, [a], pending, false), "수동 변경을 룰셋이 덮어쓰기 금지");
        check(pending[0].Value == "수동", "룰셋 실패 시 기존 변경 유지");
        check(RulePlanner.Plan(file, [a], EditPlanner.Stage([a], "새 업체"), false).Edits.Count == 1, "동일값 룰셋 재불러오기 허용");
        file.SampleOnly = true; reject(() => Plan(file, a), "연습용 룰셋 실제 모델 차단");
        check(RulePlanner.Plan(file, [a], [], true).Edits.Count == 1, "연습용 룰셋 샘플 허용");
        file = File("작업세트2"); a.Editor = "선택"; a.Storage = "ElementId"; file.Rules[0].Set[0].Storage = "ElementId"; a.Choices = [new("target-unique-key", "작업세트2")];
        check(Plan(file, a).Edits[0].ReferenceKey == "target-unique-key", "룰셋 선택값은 대상 파일의 식별자 사용");
        a.Choices.Clear(); reject(() => Plan(file, a), "없는 참조값 차단");
        file = File(); a = Row(); a.Scope = "인스턴스"; check(Plan(file, a).Edits.Count == 0, "유형 룰셋 인스턴스 오적용 금지");
        file.Rules[0].Match.Scope = "인스턴스"; check(Plan(file, a).Edits.Count == 1, "명시적 인스턴스 규칙 지원");
        a.Scope = "프로젝트 정보"; file.Rules[0].Match.Scope = "프로젝트 정보"; file.Rules[0].Match.Type = null;
        check(Plan(file, a).Edits.Count == 1, "프로젝트 정보 규칙 지원");
    }
}
