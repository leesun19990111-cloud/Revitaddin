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
        rows = Rows(); rows[0].ParameterKey = KacRulePlanner.IfcTypeKey; rows[0].Name = "IFC로 다음 유형 내보내기";
        check(Plan(rows).Edits.Any(e => e.Original.ParameterKey == KacRulePlanner.IfcTypeKey && e.Value == "IfcColumn"), "실제 Revit 유형 IFC 번역명 연결");
        rows[0].Name = "Export Type to IFC As";
        check(Plan(rows).Edits.Count == 6, "IFC 기본 식별자는 영문 Revit에서도 연결");
        var impostor = Rows()[0]; rows.Add(impostor);
        check(Plan(rows).Edits.Count == 6 && Plan(rows).Edits.All(e => e.Original.ParameterKey != impostor.ParameterKey), "기본 IFC 존재 시 동명 사용자 필드로 오입력 금지");
        rows = Rows(); rows[0].ParameterKey = "builtin:-1019017";
        check(Plan(rows).Edits.Count == 5, "이름이 같아도 미리 정의된 IFC 유형을 엔티티 필드로 오인 금지");
        List<ParameterRow> Mixed()
        {
            var typeRows = Rows(); typeRows.ForEach(r => r.TypeOwnerId = r.OwnerId);
            typeRows.RemoveAll(r => r.Name is "재료명" or "업체명");
            typeRows[0].Name = "IFC로 다음 유형 내보내기"; typeRows[0].ParameterKey = KacRulePlanner.IfcTypeKey;
            foreach (var id in new[] { "instance-1", "instance-2" })
            {
                var items = Rows().Where(r => r.Name is "재료명" or "업체명" or "다음으로 IFC로 내보내기").ToList();
                foreach (var r in items)
                { r.Scope = "인스턴스"; r.OwnerId = r.ElementId = id; r.TypeOwnerId = "owner"; r.OuterMaterial = null; if (r.Name == "다음으로 IFC로 내보내기") r.ParameterKey = KacRulePlanner.IfcInstanceKey; }
                typeRows.AddRange(items);
            }
            return typeRows;
        }
        file = File(); file.Kac!.InstanceFields = ["재료명", "업체명", "다음으로 IFC로 내보내기"];
        rows = Mixed(); plan = Plan(rows, file);
        check(plan.Edits.Count == 10 && plan.Edits.Count(e => e.Original.Scope == "인스턴스") == 6, "실제 유형 기준으로 모든 배치 요소의 세 항목 준비");
        check(plan.Edits.Where(e => e.Field == "재료명").All(e => e.Value == "철근콘크리트" && e.MaterialSource?.Fingerprint == "M1"), "인스턴스 재료명은 유형 외측 근거 사용");
        check(plan.Edits.Count(e => e.Value == "IfcColumn") == 3, "유형 IFC와 인스턴스 IFC를 각각 정확한 필드에 지정");
        check(plan.Notes.Any(n => n.Contains("재료명: 변경 예정 2개")), "항목별 변경 개수 요약");
        check(Plan(Mixed()).Edits.All(e => e.Original.Scope == "유형"), "기존 룰셋 인스턴스 범위 자동 확대 금지");
        rows = Mixed(); rows.Where(r => r.Scope == "인스턴스").ToList().ForEach(r => r.TypeOwnerId = "");
        check(Plan(rows, file).Edits.Count == 4 && Plan(rows, file).Notes.Any(n => n.Contains("다시 조회")), "구버전 조회의 이름 기반 유형 연결 금지");
        rows = Mixed(); rows.Where(r => r.Scope == "인스턴스").ToList().ForEach(r => r.TypeOwnerId = "동명다른유형");
        check(Plan(rows, file).Edits.Count == 4, "동명 다른 유형 식별자 연결 금지");
        rows = Mixed(); rows.Where(r => r.Scope == "인스턴스").ToList().ForEach(r => r.ModelPath = "B");
        check(Plan(rows, file).Edits.Count == 4, "다른 모델의 같은 유형 ID 연결 금지");
        rows = Mixed(); rows.Single(r => r.OwnerId == "instance-1" && r.Name == "재료명").BlockReason = "수식";
        check(Plan(rows, file).Edits.Count == 9, "읽기전용 인스턴스는 보존하고 사유 표시");
        rows = Mixed(); var extra = Rows()[4]; extra.Scope = "프로젝트 정보"; rows.Add(extra);
        check(Plan(rows, file).Edits.Count == 10, "프로젝트 정보의 동명 재료명 제외");
        rows = Mixed(); rows[0].OuterMaterial!.Reason = "일반 유형 다중 재료";
        check(Plan(rows, file).Edits.Count == 8, "외측 불명은 같은 유형 모든 인스턴스 재료명 유지");
        rows = Mixed(); var pendingInstance = EditPlanner.Stage([rows.First(r => r.Scope == "인스턴스" && r.Name == "업체명")], "수동 업체");
        reject(() => RulePlanner.Plan(file, rows, pendingInstance, false), "인스턴스 기존 입력 충돌 전체 보류");
        var actualRow = Mixed().First(r => r.Scope == "인스턴스");
        var expectedRow = JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(actualRow))!;
        actualRow.TypeOwnerId = "동명 교체 유형";
        reject(() => EditPlanner.CheckFresh(expectedRow, actualRow), "조회 후 동명 유형 교체도 실제 ID로 차단");
        file = File(); file.Kac!.InstanceFields = ["임의 항목"];
        reject(() => Plan(Mixed(), file), "승인하지 않은 인스턴스 항목 범위 거부");
        file = File(); file.Kac!.InstanceFields = ["재료명", "재료명"];
        reject(() => Plan(Mixed(), file), "인스턴스 항목 중복 거부");
        var ndFile = File(); ndFile.Kac!.InstanceFields = ["재료명", "업체명", "다음으로 IFC로 내보내기"];
        ndFile.Kac.UnresolvedMaterialValue = "ND";
        MaterialEvidence Missing() => new() { FallbackAllowed = true, Reason = "유형에 지정된 재료 없음" };
        rows = Rows(); rows.ForEach(r => r.OuterMaterial = Missing());
        plan = Plan(rows, ndFile); var ndEdit = plan.Edits.Single(e => e.Field == "재료명");
        check(ndEdit.Value == "ND" && ndEdit.MaterialSource?.FallbackAllowed == true, "미확인 유형 재료명 ND와 재검증 근거");
        check(ndEdit.Reason.Contains("재료 미확인 → ND"), "ND 입력 근거와 사유 표시");
        check(plan.Notes.Any(n => n.Contains("ND 변경 예정 1개")), "ND 별도 요약 개수");
        check(Plan(rows).Edits.All(e => e.Field != "재료명"), "기존 룰셋은 미확인 재료명 유지");
        check(Plan(Rows(), ndFile).Edits.Single(e => e.Field == "재료명").Value == "철근콘크리트", "확인한 재료를 ND로 바꾸지 않기");
        rows = Mixed(); rows.Where(r => r.Scope == "유형").ToList().ForEach(r => r.OuterMaterial = Missing());
        check(Plan(rows, ndFile).Edits.Count(e => e.Field == "재료명" && e.Value == "ND") == 2, "미확인 유형의 모든 배치 요소에 ND");
        rows.ForEach(r => r.TypeName = "일반 유형");
        check(Plan(rows, ndFile).Edits.Count == 0, "ND도 기존 KAC 범위 밖으로 확장 금지");
        rows = Rows(); rows.ForEach(r => r.Scope = "프로젝트 정보");
        check(Plan(rows, ndFile).Edits.Count == 0, "ND 프로젝트 정보 제외");
        rows = Rows(); rows.ForEach(r => r.OuterMaterial = null);
        check(Plan(rows, ndFile).Edits.All(e => e.Field != "재료명"), "조회 누락은 재료 없음으로 취급하지 않기");
        rows.ForEach(r => r.OuterMaterial = new() { Reason = "재료 조회 실패" });
        check(Plan(rows, ndFile).Edits.All(e => e.Field != "재료명"), "재료 API 조회 실패는 ND로 덮어쓰지 않기");
        rows.ForEach(r => r.OuterMaterial = Missing()); rows[4].BlockReason = "수식";
        check(Plan(rows, ndFile).Edits.All(e => e.Field != "재료명"), "ND 실제 읽기전용 유지");
        rows[4].BlockReason = ""; rows[4].Raw = rows[4].Display = "ND";
        check(Plan(rows, ndFile).Edits.All(e => e.Field != "재료명"), "이미 ND는 재입력 제외");
        rows[4].Raw = rows[4].Display = "기존 수동명";
        check(Plan(rows, ndFile).Edits.Single(e => e.Field == "재료명").Before == "기존 수동명", "ND 덮어쓰기의 기존값 표시");
        reject(() => RulePlanner.Plan(ndFile, rows, EditPlanner.Stage([rows[4]], "다른 예정값"), false), "ND와 수동 변경 충돌 보류");
        rows.Add(new() { ModelPath = "A", OwnerId = "owner", Scope = "유형", TypeName = rows[0].TypeName, Name = "재료명", ParameterKey = "id:duplicate" });
        check(Plan(rows, ndFile).Edits.All(e => e.Field != "재료명"), "ND 동명 다중 후보 차단");
        KacRulePlanner.CheckMaterial(ndEdit, Missing()); check(true, "ND 미확인 상태 재검증 성공");
        reject(() => KacRulePlanner.CheckMaterial(ndEdit, new() { Name = "KAC-새 재료", Fingerprint = "new" }), "ND 계획 뒤 실제 재료 발견 시 차단");
        reject(() => KacRulePlanner.CheckMaterial(ndEdit, new() { Reason = "재료 조회 실패" }), "ND 재검증 중 API 실패 차단");
        reject(() => KacRulePlanner.CheckMaterial(ndEdit, null), "ND 재검증 정보 상실 차단");
        reject(() => KacRulePlanner.CheckMaterial(ndEdit, new() { FallbackAllowed = true, Reason = "다른 미확인 상태" }), "ND 미확인 사유 변경 차단");
        var ndRoundTrip = JsonSerializer.Deserialize<ValueEdit>(JsonSerializer.Serialize(ndEdit))!;
        check(ndRoundTrip.MaterialSource!.FallbackAllowed, "ND 근거 작업 저장 왕복");
        ndRoundTrip.Value = "다른값";
        reject(() => KacRulePlanner.CheckMaterial(ndRoundTrip, Missing()), "미확인 근거로 ND 이외 값 쓰기 금지");
        file = File(); file.Kac!.UnresolvedMaterialValue = "임의";
        reject(() => Plan(Rows(), file), "임의 미확인 대체값 거부");
        check(RulePlanner.Parse(JsonSerializer.Serialize(ndFile)).Kac!.UnresolvedMaterialValue == "ND", "새 ND 룰셋 파싱");
        var allFieldsFile = File();
        allFieldsFile.Kac!.InstanceFields = ["다음으로 IFC로 내보내기", "제작정보", "KBIMS-부위코드", "조달청표준공사코드", "재료명", "업체명"];
        List<ParameterRow> AllScopes(bool typeFields)
        {
            var result = Rows(); result.ForEach(r => r.TypeOwnerId = r.OwnerId);
            if (!typeFields) result.RemoveAll(r => r.Name != "다음으로 IFC로 내보내기");
            foreach (var id in new[] { "instance-1", "instance-2" })
            {
                var batch = Rows();
                foreach (var r in batch)
                {
                    r.Scope = "인스턴스"; r.OwnerId = r.ElementId = id; r.TypeOwnerId = "owner"; r.OuterMaterial = null;
                    if (r.Name == "다음으로 IFC로 내보내기") r.ParameterKey = KacRulePlanner.IfcInstanceKey;
                }
                result.AddRange(batch);
            }
            return result;
        }
        check(RulePlanner.Parse(JsonSerializer.Serialize(allFieldsFile)).Kac!.InstanceFields.Count == 6, "6항목 인스턴스 명시 허용");
        rows = AllScopes(false); plan = Plan(rows, allFieldsFile);
        check(plan.Edits.Count == 13, "유형에 없는 업무 필드도 실제 인스턴스에서 입력");
        foreach (var (name, value) in new[] { ("제작정보", "KAC-v1.0"), ("KBIMS-부위코드", "E22"), ("조달청표준공사코드", "AEA110") })
        {
            check(plan.Edits.Count(e => e.Field == name && e.Value == value && e.Original.Scope == "인스턴스") == 2, name + " 같은 유형 모든 요소 적용");
            check(plan.Notes.Any(n => n.Contains(name + ": 변경 예정 2개")), name + " 인스턴스 요약");
        }
        check(Plan(AllScopes(true), allFieldsFile).Edits.Count == 18, "유형과 인스턴스 양쪽에 있으면 각 실제 필드 편집");
        rows = AllScopes(true); rows.RemoveAll(r => r.Scope == "인스턴스" && r.Name is "KBIMS-부위코드" or "조달청표준공사코드");
        plan = Plan(rows, allFieldsFile);
        check(plan.Edits.Count(e => e.Field is "KBIMS-부위코드" or "조달청표준공사코드") == 2 &&
            !plan.Notes.Any(n => n.Contains("/ 인스턴스") && (n.Contains("/ KBIMS-부위코드:") || n.Contains("/ 조달청표준공사코드:"))), "유형 전용 코드의 인스턴스 누락 오안내 방지");
        rows = AllScopes(false); rows.RemoveAll(r => r.Name == "KBIMS-부위코드");
        check(Plan(rows, allFieldsFile).Notes.Any(n => n.Contains("/ KBIMS-부위코드: 확인 필요") && n.Contains("매개변수 0개")), "양쪽 모두 없는 필드는 조용히 생략하지 않기");
        check(Plan(AllScopes(false), ndFile).Edits.All(e => e.Field is not ("제작정보" or "KBIMS-부위코드" or "조달청표준공사코드")), "기존 3항목 룰셋의 인스턴스 범위 보존");
        rows = AllScopes(false); rows.Single(r => r.OwnerId == "instance-1" && r.Name == "KBIMS-부위코드").BlockReason = "수식";
        check(Plan(rows, allFieldsFile).Edits.Count(e => e.Field == "KBIMS-부위코드") == 1, "인스턴스 KBIMS 읽기전용은 우회하지 않기");
        rows = AllScopes(false); var secondCode = JsonSerializer.Deserialize<ParameterRow>(JsonSerializer.Serialize(rows.First(r => r.Name == "KBIMS-부위코드")))!;
        secondCode.ParameterKey = "guid:other"; rows.Add(secondCode);
        check(Plan(rows, allFieldsFile).Edits.Count(e => e.Field == "KBIMS-부위코드") == 1, "인스턴스 동명 KBIMS 후보 중 임의 선택 금지");
        rows = AllScopes(false); rows.Where(r => r.Name == "조달청표준공사코드").ToList().ForEach(r => r.DataType = "autodesk.spec.aec:number-2.0.0");
        check(Plan(rows, allFieldsFile).Edits.All(e => e.Field != "조달청표준공사코드"), "인스턴스 조달청 자료형 불일치 유지");
        rows = AllScopes(false); rows.Where(r => r.Scope == "인스턴스").ToList().ForEach(r => r.TypeOwnerId = "다른 유형");
        check(Plan(rows, allFieldsFile).Edits.Count == 1, "6항목도 실제 소속 유형 불일치 시 차단");
        rows = AllScopes(false); rows.ForEach(r => r.TypeName = "KAC-A-CL9999_미등록");
        check(Plan(rows, allFieldsFile).Edits.All(e => e.Field is not ("KBIMS-부위코드" or "조달청표준공사코드")), "인스턴스라도 미등록 분류코드는 추정 금지");
        var blankMapping = File(); blankMapping.Kac!.InstanceFields = [.. allFieldsFile.Kac.InstanceFields];
        blankMapping.Kac.Classification[0].Kbims = blankMapping.Kac.Classification[0].Procurement = "";
        check(Plan(AllScopes(false), blankMapping).Edits.All(e => e.Field is not ("KBIMS-부위코드" or "조달청표준공사코드")), "빈 분류코드는 인스턴스 기존값도 지우지 않기");
        rows = AllScopes(false); rows.Where(r => r.Name == "KBIMS-부위코드").ToList().ForEach(r => r.Raw = r.Display = "E22");
        check(Plan(rows, allFieldsFile).Edits.All(e => e.Field != "KBIMS-부위코드"), "인스턴스 KBIMS 동일값 제외");
        rows = AllScopes(false); var pendingCode = EditPlanner.Stage([rows.First(r => r.Name == "조달청표준공사코드")], "수동 코드");
        reject(() => RulePlanner.Plan(allFieldsFile, rows, pendingCode, false), "인스턴스 조달청 수동 변경 충돌 보류");
        rows = AllScopes(false); rows.Where(r => r.Scope == "인스턴스").ToList().ForEach(r => r.Scope = "프로젝트 정보");
        check(Plan(rows, allFieldsFile).Edits.Count == 1, "6항목도 프로젝트 정보로 확장 금지");
        // 사용자 원본은 저장소/배포에 넣지 않는다. 환경변수로 지정했을 때만 생성한 실제 룰셋도 평가한다.
        var actualFile = Environment.GetEnvironmentVariable("SUNNY_KAC_RULE_TEST_FILE");
        if (!string.IsNullOrWhiteSpace(actualFile))
        {
            var actual = RulePlanner.Parse(System.IO.File.ReadAllText(actualFile));
            check(Plan(Rows(), actual).Edits.Count == 6, "실제 생성한 룰셋 불러오기와 6항목 적용 계획");
            check(Plan(Rows("KAC-A-WA7140_벽"), actual).Edits.Single(e => e.Field == "조달청표준공사코드").Value == "ANC362", "실제 표 중복 코드 첫 값");
            if (actual.Kac!.UnresolvedMaterialValue == "ND")
            {
                var realRows = Mixed(); realRows.Where(r => r.Scope == "유형").ToList().ForEach(r => r.OuterMaterial = Missing());
                check(Plan(realRows, actual).Edits.Count(e => e.Field == "재료명" && e.Value == "ND") == 2, "전달할 실제 룰셋의 인스턴스 ND 적용");
            }
            var snapshotPath = Environment.GetEnvironmentVariable("SUNNY_KAC_SNAPSHOT_FILE");
            if (!string.IsNullOrWhiteSpace(snapshotPath))
            {
                var snapshot = JsonSerializer.Deserialize<Response>(System.IO.File.ReadAllText(snapshotPath), Protocol.Json)!.Snapshot!;
                var recorded = Plan(snapshot.Rows, actual);
                check(recorded.Edits.Any(e => e.Original.ParameterKey == KacRulePlanner.IfcTypeKey), "실제 조회 기록의 유형 IFC 누락 재현 후 수정 확인");
                check(recorded.Edits.All(e => e.Original.Scope != "인스턴스"), "기존 조회 기록의 소속 유형 ID 부재는 쓰기 차단");
                check(recorded.Notes.Any(n => n.Contains("실제 소속 유형 식별 정보")), "실제 구버전 기록의 새 조회 필요 안내");
                Console.WriteLine($"조회 기록 재평가: 유형 IFC 변경 예정 {recorded.Edits.Count(e => e.Original.ParameterKey == KacRulePlanner.IfcTypeKey)}개. RVT 쓰기 없음.");
            }
        }
    }
}
