using SunnyParameterManager.Core;
using System.IO;
using System.Text.Json;

internal static class SpaceRuleTests
{
    public static void Run(Action<bool, string> check, Action<Action, string> reject)
    {
        const string fileName = "MWX-15시험건물-SP.rvt";
        ParameterRow Row(string name, string path = fileName) => new() { ModelPath = path, ModelTitle = Path.GetFileNameWithoutExtension(path), OwnerId = "project", Scope = "프로젝트 정보", TypeName = "프로젝트 정보", Name = name, ParameterKey = "id:" + name, DataType = "autodesk.spec:spec.string-2.0.0", Storage = "String", Editor = "텍스트", HasValue = true, Raw = "기존", Display = "기존" };
        RuleSet File() => new() { Version = 3, Name = "시험 공간 규칙", Space = new() { Source = "시험 표", SourceSha256 = new('a',64), Buildings = [new() { Airport = "MWX", Number = "15", Name = "시험건물", ManagementCode = "MWX1D999", Ownership = "1", SourceRow = 5 }] } };
        RulePlan Plan(RuleSet rules, params ParameterRow[] rows) => RulePlanner.Plan(rules, rows, [], false);
        var a = Row("공항코드"); var b = Row("건축물번호"); var file = File();
        check(Plan(file, a, b).Edits.Count == 2, "공간 프로젝트 정보 두 항목");
        check(Plan(file, a, b).Edits.Single(e => e.Field == "건축물번호").Value == "1D999-1", "공항 접두어 제거 후 소유구분 결합");
        check(Plan(file, a, b).Edits.All(e => e.Reason.Contains("5행")), "공간 입력 근거 행 기록");
        check(a.Raw == "기존" && b.Raw == "기존", "공간 미리보기 원본 불변");
        check(RulePlanner.Parse(JsonSerializer.Serialize(file)).Space!.Buildings.Count == 1, "버전3 JSON 왕복");
        check(SpaceRulePlanner.FileKey(@"C:\폴더\MWX-015시험-SP.rvt") == ("MWX", "15"), "공간 원본 경로 및 앞자리 0 처리");
        check(SpaceRulePlanner.FileKey("mwx-15시험-sp.RVT") == ("MWX", "15"), "공간 파일명 대소문자");
        check(SpaceRulePlanner.FileKey("PUS-03-02시험-SP.rvt") == ("PUS", "3-2"), "하위 건물번호 별도 식별");
        check(SpaceRulePlanner.FileKey("MWX-150시험-SP.rvt") == ("MWX", "150"), "15와 150 부분 일치 방지");
        foreach (var invalid in new[] { "MWX-15시험-ME.rvt", "MWX-15시험-SP_사용자.rvt", "MWX-15시험-SP.0001.rvt", "MWX-15시험-SP.rfa", "XXX-15시험-SP.rvt", "접두-MWX-15시험-SP.rvt", "MWX-15-SP.rvt", "MWX-15-1-2시험-SP.rvt", "" })
            check(SpaceRulePlanner.FileKey(invalid) == null, "공간 파일명 미확정 제외 " + invalid);
        var wrong = Row("공항코드", "MWX-15시험-ME.rvt"); wrong.ModelTitle = fileName;
        check(Plan(file, wrong).Edits.Count == 0, "표시 제목 대신 등록 원본 경로 기준");
        const string saPath = "MWX-15시험건물-SA.rvt";
        var saPlan = Plan(file, Row("공항코드", saPath), Row("건축물번호", saPath));
        check(saPlan.Edits.Count == 2 && saPlan.Edits.Single(e => e.Field == "공항코드").Value == "MWX" && saPlan.Edits.Single(e => e.Field == "건축물번호").Value == "1D999-1", "SA도 기존 공간 룰셋으로 두 프로젝트 정보 입력");
        check(Plan(file, a, b, Row("공항코드", saPath), Row("건축물번호", saPath)).Edits.Count == 4, "같은 건물 SP·SA를 모델별 독립 적용");
        check(SpaceRulePlanner.FileKey(@"C:\폴더\cju-004화물터미널-sa.RVT") == ("CJU", "4"), "SA 대소문자·전체 경로·앞자리 0 지원");
        check(SpaceRulePlanner.FileKey("PUS-03-02시험-SA.rvt") == ("PUS", "3-2"), "SA 하위 건물번호 정확 연결");
        foreach (var invalidSa in new[] { "MWX-15시험-SA.0001.rvt", "MWX-15시험-SA_사용자.rvt", "MWX-15시험-SAA.rvt", "MWX-15-SA.rvt" })
            check(SpaceRulePlanner.FileKey(invalidSa) == null, "SA 미확정 파일명 제외 " + invalidSa);
        var missingSa = File(); missingSa.Space!.Buildings[0].Ownership = "";
        check(Plan(missingSa, Row("공항코드", saPath), Row("건축물번호", saPath)).Edits.Single().Field == "공항코드", "SA도 소유구분 없으면 건축물번호 보존");
        var type = Row("공항코드"); type.Scope = "유형"; type.OwnerId = "type";
        var instance = Row("건축물번호"); instance.Scope = "인스턴스"; instance.OwnerId = "instance";
        check(Plan(file, a, b, type, instance).Edits.All(e => e.Original.Scope == "프로젝트 정보"), "동명 유형·인스턴스 변경 금지");
        reject(() => Plan(file, type), "프로젝트 정보 미조회 차단");
        var otherOwner = Row("다른 항목"); otherOwner.OwnerId = "other";
        reject(() => Plan(file, a, b, otherOwner), "프로젝트 정보 소유자 모호성 차단");
        file.Space!.Buildings[0].Ownership = "0";
        check(Plan(file, a, b).Edits.Single(e => e.Field == "건축물번호").Value == "1D999-0", "소유구분 0 보존");
        foreach (var missing in new[] { "", "세관", "1.0", "-1" })
        {
            file.Space.Buildings[0].Ownership = missing;
            var plan = Plan(file, a, b);
            check(plan.Edits.Count == 1 && plan.Edits[0].Field == "공항코드" && plan.Notes.Any(n => n.Contains("확인 필요")) && b.Raw == "기존", "소유구분 미확인 건축물번호 유지 " + missing);
        }
        file = File();
        foreach (var invalid in new[] { "", "PUS1D999", "MWX", "MWX1D999!" })
        {
            file.Space!.Buildings[0].ManagementCode = invalid;
            check(Plan(file, a, b).Edits.All(e => e.Field == "공항코드"), "관리코드 미확인/다른 공항 유지 " + invalid);
        }
        file = File(); file.Space!.Buildings[0].Number = "16";
        check(Plan(file, a, b).Notes.Any(n => n.Contains("엑셀에 건물번호 없음")), "미등록 건물번호 기존값 유지 안내");
        file = File(); var duplicate = File().Space!.Buildings[0]; duplicate.SourceRow = 6; duplicate.Number = "015"; file.Space!.Buildings.Add(duplicate);
        check(Plan(file, a, b).Edits.Count == 1 && Plan(file, a, b).Notes.Any(n => n.Contains("여러 행")), "정규화 후 중복 번호 추정 금지");
        duplicate.SourceRow = 5; reject(() => Plan(file, a, b), "중복 출처 행 차단");
        file = File(); file.Space!.Buildings[0].Ownership = ""; a.Raw = a.Display = "MWX";
        check(Plan(file, a, b).Edits.Count == 0 && Plan(file, a, b).Notes.Any(n => n.Contains("확인 필요")), "변경 0개여도 누락 안내 유지");
        a = Row("공항코드"); file = File();
        reject(() => Plan(file, a), "필수 필드 없으면 전체 보류");
        b.BlockReason = "읽기 전용"; reject(() => Plan(file, a, b), "공간 읽기전용 우회 금지"); b.BlockReason = "";
        b.DataType = "number"; reject(() => Plan(file, a, b), "공간 동명 다른 자료형 차단"); b = Row("건축물번호");
        var duplicateField = Row("건축물번호"); duplicateField.ParameterKey = "id:다름";
        reject(() => Plan(file, a, b, duplicateField), "공간 동명 매개변수 모호성 차단");
        var pending = EditPlanner.Stage([b], "수동");
        reject(() => RulePlanner.Plan(file, [a,b], pending, false), "공간 수동 변경 충돌 전체 보류");
        check(pending[0].Value == "수동", "공간 실패 후 기존 변경 보존");
        check(RulePlanner.Plan(file, [a,b], EditPlanner.Stage([b], "1D999-1"), false).Edits.Count == 2, "공간 동일값 준비 재사용");
        a.Raw = a.Display = "MWX"; b.Raw = b.Display = "1D999-1";
        check(Plan(file, a, b).Edits.Count == 0, "공간 동일값 제외");
        file.SampleOnly = true; reject(() => Plan(file, a, b), "공간 연습 룰셋 실제 파일 차단"); file = File();
        file.Kac = new(); reject(() => Plan(file, a, b), "KAC 공간 프로필 혼합 차단"); file = File();
        file.Version = 1; reject(() => Plan(file, a, b), "v1 공간 프로필 무시 금지");
        file.Version = 2; reject(() => Plan(file, a, b), "v2 공간 프로필 무시 금지"); file = File();
        file.Space!.Scope = "유형"; reject(() => Plan(file, a, b), "공간 범위 확장 금지"); file = File();
        file.Space!.UnresolvedPolicy = "위행상속"; reject(() => Plan(file, a, b), "공간 빈값 상위행 상속 금지"); file = File();
        reject(() => RulePlanner.Parse(JsonSerializer.Serialize(file).Replace("\"Space\":", "\"Spaces\":")), "공간 알 수 없는 속성 차단");
        file.Space!.Buildings[0].NameAliases = ["시험초소A", "시험초소B"];
        var saAlias = "MWX-19-1시험초소A-SA.rvt";
        check(Plan(file, Row("공항코드", saAlias), Row("건축물번호", saAlias)).Edits.Single(e => e.Field == "건축물번호").Value == "1D999-1", "SA 초소 확정 별칭도 동일하게 지원");
        var aliasPath = "MWX-19-1시험초소A-SP.rvt";
        check(Plan(file, Row("공항코드",aliasPath), Row("건축물번호",aliasPath)).Edits.Single(e => e.Field == "건축물번호").Value == "1D999-1", "명시한 별칭은 번호보다 우선");
        aliasPath = "MWX-19-2시험 초소 b-SP.rvt";
        check(Plan(file, Row("공항코드",aliasPath), Row("건축물번호",aliasPath)).Notes.Any(n => n.Contains("확정 별칭")), "확정 별칭 공백·영문 대소문자 및 근거");
        aliasPath = "PUS-19-1시험초소A-SP.rvt";
        check(Plan(file, Row("공항코드",aliasPath), Row("건축물번호",aliasPath)).Edits.Count == 1, "별칭 다른 공항 확대 금지");
        aliasPath = "MWX-19-1시험초소AA-SP.rvt";
        check(Plan(file, Row("공항코드",aliasPath), Row("건축물번호",aliasPath)).Edits.Count == 1, "별칭 부분 일치 금지");
        file.Space.Buildings[0].NameAliases.Add("시험 초소 a");
        reject(() => Plan(file,a,b), "별칭 정규화 중복 차단");

        // ND는 누락된 출처 값에 대한 명시적 선택 정책이며 기존 룰셋의 유지 동작은 바꾸지 않는다.
        file = File(); a = Row("공항코드"); b = Row("건축물번호");
        file.Space!.MissingBuildingNumberValue = "ND";
        check(RulePlanner.Parse(JsonSerializer.Serialize(file)).Space!.MissingBuildingNumberValue == "ND", "ND 정책 JSON 왕복");
        check(Plan(file, a, b).Edits.Single(e => e.Field == "건축물번호").Value == "1D999-1", "ND 정책 정상값 보존");
        file.Space.Buildings[0].Ownership = "0";
        check(Plan(file, a, b).Edits.Single(e => e.Field == "건축물번호").Value == "1D999-0", "ND 정책 소유구분 0은 유효");
        foreach (var suffix in new[] { "SP", "SA" })
        foreach (var blank in new[] { "", " \t" })
        foreach (var kind in new[] { "관리코드", "소유구분", "둘다" })
        {
            file = File(); file.Space!.MissingBuildingNumberValue = "ND";
            if (kind != "소유구분") file.Space.Buildings[0].ManagementCode = blank;
            if (kind != "관리코드") file.Space.Buildings[0].Ownership = blank;
            var path = "MWX-15시험건물-" + suffix + ".rvt";
            var numberRow = Row("건축물번호", path);
            var result = Plan(file, Row("공항코드", path), numberRow);
            check(result.Edits.Count == 2 && result.Edits.Single(e => e.Field == "건축물번호").Value == "ND" &&
                result.Edits.Single(e => e.Field == "건축물번호").Reason.Contains("누락 → ND") &&
                result.Edits.Single(e => e.Field == "공항코드").Value == "MWX" && numberRow.Raw == "기존",
                "ND 누락 대체 및 원본 불변 " + suffix + kind + blank.Length);
        }
        file.Space!.Buildings[0].NameAliases = ["시험초소A"];
        check(Plan(file, Row("공항코드", saAlias), Row("건축물번호", saAlias)).Edits.Single(e => e.Field == "건축물번호").Value == "ND", "확정 별칭 누락도 ND");
        b.Raw = b.Display = "ND";
        check(Plan(file, a, b).Edits.All(e => e.Field != "건축물번호"), "이미 ND면 중복 변경 없음");
        b = Row("건축물번호"); b.BlockReason = "읽기 전용";
        reject(() => Plan(file, a, b), "ND 읽기전용 우회 금지"); b.BlockReason = "";
        reject(() => Plan(file, a), "ND 대상 필드 부재 차단");
        reject(() => Plan(file, a, b, duplicateField), "ND 동명 필드 모호성 차단");
        pending = EditPlanner.Stage([b], "수동");
        reject(() => RulePlanner.Plan(file, [a, b], pending, false), "ND 수동 변경 충돌 차단");
        check(pending.Single().Value == "수동", "ND 충돌 기존 변경 유지");
        file.Space.Buildings[0].Number = "16";
        check(Plan(file, a, b).Edits.Count == 1 && Plan(file, a, b).Notes.Any(n => n.Contains("확인 필요")), "ND 미등록 건물 추정 금지");
        file.Space.Buildings[0].Number = "15";
        duplicate = File().Space!.Buildings[0]; duplicate.SourceRow = 6; file.Space.Buildings.Add(duplicate);
        check(Plan(file, a, b).Edits.Count == 1, "ND 중복 건물 추정 금지");
        foreach (var invalid in new[] { "PUS1D999", "MWX", "MWX1D999!" })
        {
            file = File(); file.Space!.MissingBuildingNumberValue = "ND"; file.Space.Buildings[0].ManagementCode = invalid;
            check(Plan(file, a, b).Edits.Count == 1, "ND 비어있지 않은 관리코드 오류 유지 " + invalid);
        }
        foreach (var invalid in new[] { "세관", "1.0", "-1" })
        {
            file = File(); file.Space!.MissingBuildingNumberValue = "ND"; file.Space.Buildings[0].Ownership = invalid;
            check(Plan(file, a, b).Edits.Count == 1, "ND 비어있지 않은 소유구분 오류 유지 " + invalid);
        }
        foreach (var invalid in new string?[] { null, "nd", " ND ", "임의값" })
        {
            file = File(); file.Space!.MissingBuildingNumberValue = invalid!;
            reject(() => Plan(file, a, b), "ND 이외 대체 정책 거부 " + invalid);
        }

        // 자산번호는 명시적으로 켠 룰셋에서만 조회·입력하며, 빈 값은 ND 정책일 때만 대체한다.
        file = File(); a = Row("공항코드"); b = Row("건축물번호"); var asset = Row("자산번호");
        check(Plan(file, a, b, asset).Edits.All(e => e.Field != "자산번호"), "자산번호 미설정 룰셋은 자산번호 불변");
        file.Space!.Buildings[0].AssetNumber = "11003164";
        reject(() => Plan(file, a, b, asset), "자산번호 입력을 켜지 않은 표의 자산번호 무시 금지");
        file.Space.MapAssetNumber = true;
        check(Plan(file, a, b, asset).Edits.Single(e => e.Field == "자산번호").Value == "11003164", "자산번호 원본 값 입력");
        check(Plan(file, a, b, asset).Edits.Single(e => e.Field == "자산번호").Reason.Contains("5행"), "자산번호 근거 행 기록");
        check(RulePlanner.Parse(JsonSerializer.Serialize(file)).Space!.Buildings[0].AssetNumber == "11003164", "자산번호 JSON 왕복");
        check(QueryPlan.FromRules(file).Names.SequenceEqual(new[] { "공항코드", "건축물번호", "자산번호" }), "자산번호 선택 조회 포함");
        check(QueryPlan.FromRules(File()).Names.Count == 2, "기존 공간 룰셋 조회 범위 유지");
        reject(() => Plan(file, a, b), "자산번호 필드 부재 시 전체 보류");
        asset.BlockReason = "읽기 전용"; reject(() => Plan(file, a, b, asset), "자산번호 읽기전용 우회 금지"); asset = Row("자산번호");
        asset.Raw = asset.Display = "11003164";
        check(Plan(file, a, b, asset).Edits.All(e => e.Field != "자산번호"), "자산번호 동일값 제외");
        asset = Row("자산번호");
        foreach (var invalid in new[] { "11003021\n11003557(증)", "초소 D'TYPE", " 1", "1.0", "-1" })
        {
            file.Space.Buildings[0].AssetNumber = invalid;
            reject(() => Plan(file, a, b, asset), "숫자가 아닌 자산번호 거부 " + invalid);
        }
        file.Space.Buildings[0].AssetNumber = "";
        var keep = Plan(file, a, b, asset);
        check(keep.Edits.All(e => e.Field != "자산번호") && keep.Notes.Any(n => n.Contains("자산번호 누락")), "자산번호 누락 기본은 기존값 유지·확인 필요");
        file.Space.MissingAssetNumberValue = "ND";
        var nd = Plan(file, a, b, asset).Edits.Single(e => e.Field == "자산번호");
        check(nd.Value == "ND" && nd.Reason.Contains("자산번호 누락 → ND") && asset.Raw == "기존", "자산번호 누락 ND 및 원본 불변");
        file.Space.Buildings[0].Number = "16";
        check(Plan(file, a, b, asset).Edits.All(e => e.Field != "자산번호"), "자산번호 미등록 건물 추정 금지");
        file = File(); file.Space!.MissingAssetNumberValue = "ND";
        reject(() => Plan(file, a, b), "자산번호 입력을 켜지 않은 ND 정책 거부");
        foreach (var invalid in new string?[] { null, "nd", "임의" })
        {
            file = File(); file.Space!.MapAssetNumber = true; file.Space.MissingAssetNumberValue = invalid!;
            reject(() => Plan(file, a, b, asset), "자산번호 ND 이외 대체 정책 거부 " + invalid);
        }
        file = File(); file.Space!.MapAssetNumber = true; file.Space.MissingAssetNumberValue = "ND"; file.Space.Buildings[0].NameAliases = ["시험초소A"];
        file.Space.Buildings[0].AssetNumber = "11003172";
        check(Plan(file, Row("공항코드", saAlias), Row("건축물번호", saAlias), Row("자산번호", saAlias)).Edits.Single(e => e.Field == "자산번호").Value == "11003172", "확정 별칭 자산번호");

        // 자산번호 매개변수가 없는 모델은 함께 조회한 모델의 같은 공유 매개변수를 추가한 뒤 입력한다.
        {
            const string guid = "94bccf19-2f58-4de5-b719-c27c099aeb34";
            const string other = "MWX-16시험다른건물-SP.rvt";
            RuleSet AddFile() { var f = File(); f.Space!.MapAssetNumber = true; f.Space.MissingAssetNumberValue = "ND"; f.Space.AddMissingAssetParameter = true;
                f.Space.Buildings[0].AssetNumber = "11003164"; f.Space.Buildings.Add(new() { Airport = "MWX", Number = "16", Name = "시험다른건물", ManagementCode = "MWX1D998", Ownership = "1", AssetNumber = "11000001", SourceRow = 6 }); return f; }
            ParameterRow Source(string path = other, string group = "autodesk.parameter.group:identityData-1.0.0", string key = "guid:" + guid)
            { var r = Row("자산번호", path); r.ParameterKey = key; r.ParameterGroup = group; return r; }
            var addFile = AddFile();
            check(RulePlanner.Parse(JsonSerializer.Serialize(addFile)).Space!.AddMissingAssetParameter, "자산번호 추가 옵션 JSON 왕복");
            var added = Plan(addFile, a, b, Row("공항코드", other), Row("건축물번호", other), Source());
            var add = added.Edits.Single(e => e.Field == "자산번호" && e.Original.ModelPath == fileName);
            check(add.AddParameter != null && add.AddParameter.Guid == guid && add.AddParameter.Group == "autodesk.parameter.group:identityData-1.0.0" &&
                add.Original.ParameterKey == "guid:" + guid && add.Original.OwnerId == "project" && add.Value == "11003164" && add.Reason.Contains("공유 매개변수 추가"),
                "자산번호 없는 모델에 같은 GUID·그룹 공유 매개변수 추가 계획");
            check(added.Edits.Single(e => e.Field == "자산번호" && e.Original.ModelPath == other).AddParameter == null, "자산번호 있는 모델은 일반 입력");
            check(added.Edits.Count(e => e.Original.ModelPath == fileName) == 3, "추가 대상 모델의 공항코드·건축물번호도 함께 적용");
            check(added.Notes.Any(n => n.Contains("프로젝트 정보에 추가")), "매개변수 추가 안내");
            var noSource = Plan(addFile, a, b);
            check(noSource.Edits.Count == 2 && noSource.Edits.All(e => e.Field != "자산번호") && noSource.Notes.Any(n => n.Contains("전송 기준이 없습니다")), "기준 모델 없으면 자산번호만 건너뛰고 나머지 적용");
            var noGroup = Plan(addFile, a, b, Row("공항코드", other), Row("건축물번호", other), Source(group: ""));
            check(noGroup.Edits.All(e => e.AddParameter == null) && noGroup.Notes.Any(n => n.Contains("다시 조회")), "기준 그룹 정보 없으면 추가 보류");
            var third = "MWX-15시험건물-SA.rvt";
            var mixed = Plan(addFile, a, b, Row("공항코드", other), Row("건축물번호", other), Source(), Row("공항코드", third), Row("건축물번호", third), Source(third, "autodesk.parameter.group:text"));
            check(mixed.Edits.All(e => e.AddParameter == null) && mixed.Notes.Any(n => n.Contains("하나로 정할 수 없습니다")), "모델마다 정의가 다르면 추가 보류");
            var local = Plan(addFile, a, b, Row("공항코드", other), Row("건축물번호", other), Source(key: "id:123"));
            check(local.Edits.All(e => e.AddParameter == null) && local.Notes.Any(n => n.Contains("공유 매개변수가 아니라")), "공유가 아닌 기준은 전송 금지");
            var plain = AddFile(); plain.Space!.AddMissingAssetParameter = false;
            reject(() => Plan(plain, a, b, Row("공항코드", other), Row("건축물번호", other), Source()), "추가 옵션 없으면 기존처럼 필드 부재 보류");
            var invalid = AddFile(); invalid.Space!.MapAssetNumber = false; invalid.Space.MissingAssetNumberValue = ""; foreach (var x in invalid.Space.Buildings) x.AssetNumber = "";
            reject(() => Plan(invalid, a, b), "자산번호 입력 없이 추가 옵션 거부");
            var renamed = Row("다른이름", fileName); renamed.ParameterKey = "guid:" + guid;
            reject(() => Plan(addFile, a, b, renamed, Row("공항코드", other), Row("건축물번호", other), Source()), "같은 GUID 다른 이름이면 추가 금지");
            var pendingAdd = EditPlanner.Stage([b], "수동");
            reject(() => RulePlanner.Plan(addFile, [a, b, Row("공항코드", other), Row("건축물번호", other), Source()], pendingAdd, false), "추가 계획도 수동 변경 충돌 보호");
        }

        // 개인 업무 표는 저장소에 넣지 않고 선택한 외부 파일로 별도 검증한다.
        var actualPath = Environment.GetEnvironmentVariable("SUNNY_SPACE_RULE_TEST_FILE");
        if (!string.IsNullOrEmpty(actualPath))
        {
            var actual = RulePlanner.Parse(System.IO.File.ReadAllText(actualPath));
            var count = 0;
            foreach (var group in actual.Space!.Buildings.GroupBy(e => (e.Airport, e.Number)))
            foreach (var suffix in new[] { "SP", "SA" })
            {
                var entry = group.First(); var path = entry.Airport + "-" + entry.Number + "검증건물-" + suffix + ".rvt";
                var rows = actual.Space.MapAssetNumber ? new[] { Row("공항코드", path), Row("건축물번호", path), Row("자산번호", path) } : new[] { Row("공항코드", path), Row("건축물번호", path) };
                var result = Plan(actual, rows);
                var expected = group.Count() == 1 ? SpaceRulePlanner.BuildingValue(entry) : null;
                if (actual.Space.MapAssetNumber)
                {
                    var expectedAsset = group.Count() != 1 ? null : entry.AssetNumber.Length > 0 ? entry.AssetNumber : actual.Space.MissingAssetNumberValue == "ND" ? "ND" : null;
                    if (result.Edits.SingleOrDefault(e => e.Field == "자산번호")?.Value != expectedAsset) throw new Exception("실제 공간 표 자산번호 불일치: " + path);
                    result.Edits.RemoveAll(e => e.Field == "자산번호");
                }
                if (group.Count() == 1 && actual.Space.MissingBuildingNumberValue == "ND" &&
                    (string.IsNullOrWhiteSpace(entry.ManagementCode) || string.IsNullOrWhiteSpace(entry.Ownership))) expected = "ND";
                if (result.Edits.Count != (expected == null ? 1 : 2) || result.Edits.Single(e => e.Field == "공항코드").Value != entry.Airport ||
                    expected != null && result.Edits.Single(e => e.Field == "건축물번호").Value != expected || expected == null && !result.Notes.Any(n => n.Contains("확인 필요")))
                    throw new Exception("실제 공간 표 계획 불일치: " + path);
                count++;
            }
            check(count > 0, "실제 전달 공간 표 모든 건물번호 평가 " + count);
            ParameterRow[] Rows(string path) => actual.Space.MapAssetNumber ? [Row("공항코드", path), Row("건축물번호", path), Row("자산번호", path)] : [Row("공항코드", path), Row("건축물번호", path)];
            var resultExample = Plan(actual, Rows("MWX-15여객터미널-SP.rvt"));
            check(resultExample.Edits.Single(e => e.Field == "건축물번호").Value == "1D001-1", "사용자 확정 공간파일 실제 엑셀 예시");
            if (actual.Space.MapAssetNumber) check(resultExample.Edits.Single(e => e.Field == "자산번호").Value == "11003164", "사용자 확정 공간파일 실제 자산번호 예시");
            foreach (var letter in "ABCDEF")
            {
                var path = "MWX-19-" + (letter-'A'+1) + "초소" + letter + "-SP.rvt";
                var result = Plan(actual, Rows(path));
                check(result.Edits.Single(e => e.Field == "건축물번호").Value == "3B003-1", "실제 초소 별칭 " + letter);
                if (actual.Space.MapAssetNumber) check(result.Edits.Single(e => e.Field == "자산번호").Value == "11003172", "실제 초소 별칭 자산번호 " + letter);
            }
        }
    }
}
