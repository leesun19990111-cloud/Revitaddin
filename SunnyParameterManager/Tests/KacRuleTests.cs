using SunnyParameterManager.Core;
using System.Text.Json;

internal static class KacRuleTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        RuleSet File() => new() { Version = 2, Name = "분류표 검사", Kac = new()
        {
            ManufacturerInfo = "KAC-v1.0", Company = "ND", MaterialPolicy = "외측-상단-천장하단-단일재료", UnresolvedPolicy = "기존값유지-확인필요",
            Classification = [new() { Code = "CL1210", Kbims = "E22", Procurement = "AEA110", KbimsSource = "시트!I100", ProcurementSource = "시트!H100" }],
            Ifc = [new() { Prefix = "CL", Value = "IfcColumn", Source = "표!E6" }, new() { Prefix = "FA1", Value = "IfcShadingDevice", Source = "표!E29" }, new() { Prefix = "FA2", Value = "IfcFurniture", Source = "표!E30" }]
        }};
        List<ParameterRow> Rows(string type = "KAC-S-CL1210_기둥", string path = "A") =>
            new[] { "다음으로 IFC로 내보내기", "제작정보", "KBIMS-부위코드", "조달청표준공사코드", "재료명", "업체명" }.Select((name, i) => new ParameterRow
            {
                ModelPath = path, ModelTitle = path, OwnerId = "owner", TypeName = type, Scope = "유형", Name = name, ParameterKey = "id:" + i,
                DataType = "autodesk.spec:spec.string-2.0.0", Storage = "String", Editor = "텍스트", Raw = "기존", Display = "기존", HasValue = true,
                OuterMaterial = new() { Name = "KAC-철근콘크리트", Basis = "유형 단일 재료", Fingerprint = "M1" }
            }).ToList();
        RulePlan Plan(List<ParameterRow> rows, RuleSet? file = null) => RulePlanner.Plan(file ?? File(), rows, [], false);
        var file = RulePlanner.Parse(JsonSerializer.Serialize(File()));
        var rows = Rows(); var plan = Plan(rows, file);
        check(plan.Edits.Count == 6 && plan.MatchedOwners == 1, "KAC 6항목 미리보기");
        check(plan.Edits.Single(e => e.Field == "조달청표준공사코드").Value == "AEA110", "KAC 정확한 4자리 코드");
        check(plan.Edits.Single(e => e.Field == "KBIMS-부위코드").Value == "E22", "KAC 부위분류 코드");
        check(plan.Edits.Single(e => e.Field == "제작정보").Value == "KAC-v1.0" && plan.Edits.Single(e => e.Field == "업체명").Value == "ND", "KAC 고정값");
        check(plan.Edits.Single(e => e.Field == "재료명").Value == "철근콘크리트", "재료명 맨앞 KAC 접두어 제거");
        check(plan.Edits.Single(e => e.Field == "재료명").MaterialSource?.Fingerprint == "M1", "재료 입력의 원본 근거 보존");
        check(plan.Edits.Single(e => e.Field == "조달청표준공사코드").Reason.Contains("시트!H100"), "매핑 출처 표시");
        check(rows.All(r => r.Raw == "기존"), "KAC 미리보기는 조회값 불변");
        check(Plan(Rows("KAC-A-CL1210_기둥")).Edits.Count == 6, "건축과 구조 코드 지원");
        check(Plan([.. rows, .. Rows(path: "B")]).Edits.Count == 12, "KAC 파일별 동일 유형 격리");
        foreach (var name in new[] { "KAC-M-CL1210_기둥", "KAC-S-CL12101", "KAC-S-CL121", "앞_KAC-S-CL1210", "CL1210" })
            check(Plan(Rows(name)).Edits.Count == 0, "미지정 공정/코드 형식 제외 " + name);
        check(Plan(Rows("KAC-S-CL1210")).Edits.Count == 6, "유형명 코드로 끝나는 경우");
        check(Plan(Rows("KAC-A-FA1100_차양")).Edits.Any(e => e.Value == "IfcShadingDevice") && Plan(Rows("KAC-A-FA2100_사인")).Edits.Any(e => e.Value == "IfcFurniture"), "FA 숫자 분류별 IFC 구분");
        check(Plan(Rows("KAC-A-CL9999_미등록")).Edits.Count == 4, "미등록 세부코드는 분류값 유지");
        rows = Rows(); rows[0].OuterMaterial!.Reason = "다중 재료 외측 불명";
        plan = Plan(rows); check(plan.Edits.Count == 5 && plan.Notes.Any(n => n.Contains("다중 재료")), "외측 불명 기존 재료명 유지와 안내");
        rows = Rows(); rows.ForEach(r => r.OuterMaterial = null);
        check(Plan(rows).Edits.Count == 5 && Plan(rows).Notes.Any(n => n.Contains("다시 조회")), "이전 조회 기록 재료 자동 추정 금지");
        rows = Rows(); rows[0].OuterMaterial!.Name = "도료-KAC-백색";
        check(Plan(rows).Edits.Single(e => e.Field == "재료명").Value == "도료-KAC-백색", "중간 KAC 문자열 유지");
        rows = Rows(); rows[0].OuterMaterial!.Name = "KAC-";
        check(Plan(rows).Edits.Count == 5, "접두어만 있는 재료명 자동 지우기 금지");
        rows = Rows(); rows[1].BlockReason = "수식";
        check(Plan(rows).Edits.Count == 5, "읽기전용은 유지하고 나머지 검토");
        rows = Rows(); rows[1].Storage = "Integer";
        check(Plan(rows).Edits.Count == 5, "KAC 문자 아닌 자료형 건너뛰기");
        rows = Rows(); rows[1].DataType = "autodesk.spec.aec:number-2.0.0";
        check(Plan(rows).Edits.Count == 5, "자료형 불일치 차단");
        rows = Rows(); rows[1].DataType = "autodesk.spec.aec:multilineText-2.0.0";
        check(Plan(rows).Edits.Count == 6, "실제 Revit 여러 줄 텍스트 자료형 지원");
        rows = Rows(); rows[1].DataType = "autodesk.spec:string.text-2.0.0";
        check(Plan(rows).Edits.Count == 5, "존재하지 않는 추정 텍스트 자료형 거부");
        rows = Rows(); rows[1].DataType = "autodesk.spec.string:url-2.0.0";
        check(Plan(rows).Edits.Count == 5, "URL을 텍스트 업무 항목으로 오인하지 않기");
        rows = Rows(); var duplicate = Rows()[1]; duplicate.ParameterKey = "guid:다른매개변수"; rows.Add(duplicate);
        check(Plan(rows).Edits.Count == 5, "동명 매개변수 임의 선택 금지");
        rows = Rows(); rows.ForEach(r => r.Scope = "인스턴스"); check(Plan(rows).Edits.Count == 0, "유형 규칙 인스턴스 오적용 금지");
        rows = Rows(); rows[1].Raw = rows[1].Display = "KAC-v1.0";
        check(Plan(rows).Edits.Count == 5, "KAC 동일값 제외");
        rows = Rows(); var pending = EditPlanner.Stage([rows[1]], "수동값");
        reject(() => RulePlanner.Plan(File(), rows, pending, false), "KAC 수동 변경 충돌 전체 보류");
        check(pending.Single().Value == "수동값", "충돌 기존 변경 보존");
        file = File(); file.Kac!.Classification.Add(file.Kac.Classification[0]); reject(() => Plan(Rows(), file), "확정표의 중복코드는 거부");
        file = File(); file.Kac!.MaterialPolicy = "임의"; reject(() => Plan(Rows(), file), "임의 재료 정책 거부");
        file = File(); file.Kac!.Scope = "인스턴스"; reject(() => Plan(Rows(), file), "KAC 범위 무단 확장 거부");
        reject(() => RulePlanner.Parse(JsonSerializer.Serialize(File()).Replace("\"Company\":", "\"Typo\":")), "KAC 알 수 없는 속성 거부");
        var materialEdit = Plan(Rows()).Edits.Single(e => e.Field == "재료명");
        KacRulePlanner.CheckMaterial(materialEdit, materialEdit.MaterialSource); check(true, "재료 최신성 일치");
        reject(() => KacRulePlanner.CheckMaterial(materialEdit, new() { Name = "KAC-철근콘크리트", Basis = "유형 단일 재료", Fingerprint = "다른층" }), "외측층 변경 차단");
        reject(() => KacRulePlanner.CheckMaterial(materialEdit, null), "재료 근거 상실 차단");
        reject(() => KacRulePlanner.CheckMaterial(materialEdit, new() { Name = "KAC-다른이름", Basis = "유형 단일 재료", Fingerprint = "M1" }), "재료 이름 변경 차단");
        var roundTrip = JsonSerializer.Deserialize<ValueEdit>(JsonSerializer.Serialize(materialEdit))!;
        check(roundTrip.MaterialSource!.Fingerprint == "M1", "작업 저장 시 재료 근거 보존");
        // 사용자 원본은 저장소/배포에 넣지 않는다. 환경변수로 지정했을 때만 생성한 실제 룰셋도 평가한다.
        var actualFile = Environment.GetEnvironmentVariable("SUNNY_KAC_RULE_TEST_FILE");
        if (!string.IsNullOrWhiteSpace(actualFile))
        {
            var actual = RulePlanner.Parse(System.IO.File.ReadAllText(actualFile));
            check(Plan(Rows(), actual).Edits.Count == 6, "실제 생성한 룰셋 불러오기와 6항목 적용 계획");
            check(Plan(Rows("KAC-A-WA7140_벽"), actual).Edits.Single(e => e.Field == "조달청표준공사코드").Value == "ANC362", "실제 표 중복 코드 첫 값");
        }
    }
}
