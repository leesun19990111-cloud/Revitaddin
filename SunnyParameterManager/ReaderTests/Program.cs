using Autodesk.Revit.DB;
using SunnyParameterManager.Core;
using SunnyParameterManager.Bridge;

int count = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL " + text); Console.WriteLine("PASS " + text); count++; }
var doc = new Document(); var model = new ModelSnapshot { Path = "테스트.rvt" }; var reader = new ParameterReader(doc, model);
var type = new ElementType { Name = "공통 뷰 유형", Category = new() { CategoryType = CategoryType.Internal } }; doc.Elements.Add(type);
foreach (var kind in new[] { ViewType.FloorPlan, ViewType.CeilingPlan, ViewType.Elevation, ViewType.Section, ViewType.Detail, ViewType.DraftingView, ViewType.Legend, ViewType.Schedule, ViewType.ThreeD })
{
 var view = new View { Name = kind + " 실제 이름", ViewType = kind, TypeId = type.Id }; doc.Elements.Add(view);
 var row = reader.ReadAll().Single(r => r.OwnerId == view.UniqueId);
 Check(row.Scope == "뷰" && row.TargetName == view.Name && row.TypeName == type.Name && row.Editable, "실제 리더의 뷰 조회·실제 이름: " + kind);
}
var sheet = new ViewSheet { Name = "평면도 시트", SheetNumber = "A-101" }; doc.Elements.Add(sheet);
var sr = reader.ReadAll().Single(r => r.OwnerId == sheet.UniqueId);
Check(sr.Scope == "시트" && sr.TargetName == "A-101 · 평면도 시트", "시트 번호와 이름 표시");
sheet.Name = "수정한 시트";
Check(reader.Read(sheet, sheet.Parameters[0]).TypeKey == sr.TypeKey, "시트 이름 편집은 유형 변경으로 오인하지 않기");
var excluded = new[] { new View { IsTemplate = true }, new View { ViewType = ViewType.Internal }, new View { ViewType = ViewType.ProjectBrowser }, new View { ViewType = ViewType.SystemBrowser }, new View { ViewType = ViewType.Undefined } };
doc.Elements.AddRange(excluded); doc.Elements.Add(new RevitLinkInstance()); doc.Elements.Add(new RevitLinkType());
Check(reader.ReadAll().All(r => excluded.All(v => v.UniqueId != r.OwnerId)), "템플릿 자체·시스템 뷰 제외");
var normal = new Element { Name = "모델 요소" }; doc.Elements.Add(normal); doc.ProjectInformation = new ProjectInfo();
Check(reader.ReadAll().Any(r => r.OwnerId == normal.UniqueId && r.Scope == "인스턴스") && reader.ReadAll().Any(r => r.Scope == "프로젝트 정보"), "기존 모델·프로젝트 정보 유지");
Check(reader.ReadAll().All(r => doc.Elements.Where(e => e is RevitLinkInstance or RevitLinkType).All(e => e.UniqueId != r.OwnerId)), "링크 자체 수집 제외 유지");
var plan = doc.Elements.OfType<View>().First(); var p = plan.Parameters[0];
p.IsReadOnly = true; Check(!reader.Read(plan, p).Editable, "실제 읽기전용 보호"); p.IsReadOnly = false;
var template = excluded[0]; plan.ViewTemplateId = template.Id; template.TemplateParameters.Add(p.Id);
Check(reader.Read(plan, p).BlockReason.Contains("템플릿"), "템플릿 제어 매개변수 차단");
template.NonControlled.Add(p.Id); Check(reader.Read(plan, p).Editable, "템플릿 비제어 매개변수 편집 가능");
template.NonControlled.Clear(); template.TemplateParameters.Clear(); Check(reader.Read(plan, p).Editable, "템플릿 제어 목록 외 사용자 매개변수 편집 가능");
template.FailTemplateRead = true;
Check(reader.ReadAll().Single(r => r.OwnerId == plan.UniqueId).BlockReason.Contains("제어 여부"), "템플릿 조회 실패도 행을 숨기지 않고 읽기 전용 표시");
template.FailTemplateRead = false;
plan.ViewTemplateId = new(999999); Check(!reader.Read(plan, p).Editable, "확인 불가 템플릿은 안전하게 차단"); plan.ViewTemplateId = ElementId.InvalidElementId;
p.HasValue = false; p.Value = ""; Check(reader.Read(plan, p).ValueLabel == "(없음)" && reader.Read(plan, p).Editable, "빈 뷰 매개변수 표시와 입력 가능");
p.IsShared = true; Check(reader.Read(plan, p).ParameterKey == "guid:" + p.GUID.ToString("D"), "뷰 공유 매개변수 GUID 유지");
Check(ParameterReader.Resolve(plan, reader.Read(plan, p).ParameterKey) == p, "적용 시 같은 뷰의 실제 매개변수 연결");
// 선택하지 않은 값은 AsString/참조 목록/외측 재료조차 읽지 않아야 한다.
var slimDoc = new Document { ProjectInformation = new ProjectInfo() };
var slimType = new ElementType { Name = "KAC-S-CL1210_기둥" }; slimType.Parameters.Clear();
slimType.Parameters.Add(new() { Id = new(1), Definition = new() { Name = "업체명" } });
slimType.Parameters.Add(new() { Id = new(2), Definition = new() { Name = "불필요한 값" }, FailRead = true });
slimDoc.Elements.Add(slimType);
var slimModel = new ModelSnapshot { Path = "A", Title = "A" }; var slimReader = new ParameterReader(slimDoc, slimModel);
var select = new QueryPlan { Scopes = ["유형"], Names = ["업체명"] };
OuterMaterialReader.Calls = 0;
var slim = slimReader.ReadAll(select);
Check(slim.Count == 1 && slim[0].Name == "업체명", "미선택 값 읽기 오류에 접근하지 않고 한 항목만 조회");
Check(OuterMaterialReader.Calls == 0 && slim[0].OuterMaterial == null && slimModel.Catalogs.Count == 0, "일반 룰셋 불필요한 재료·참조 목록 조회 생략");
Check(slimModel.Query == select && slimModel.Owners.Count == 1 && !slimModel.Owners[0].Editable, "조회 범위·비편집 소유자 메타데이터 유지");
slimType.Parameters[0].FailRead = true;
bool failed = false; try { slimReader.ReadAll(select); } catch (InvalidOperationException) { failed = true; }
Check(failed, "선택 필드 조회 실패를 필드 부재로 넘기지 않기"); slimType.Parameters[0].FailRead = false;
slimType.Parameters[0].FailMemory = true;
failed = false; try { slimReader.ReadAll(select); } catch (OutOfMemoryException) { failed = true; }
Check(failed, "선택 조회 OOM도 정상 결과로 넘기지 않기"); slimType.Parameters[0].FailMemory = false;
slimType.Parameters[0].Definition.Name = "없음";
Check(slimReader.ReadAll(select).Count == 0 && slimModel.Owners.Single().OwnerId == slimType.UniqueId, "필드가 없는 유형도 식별 정보 보존");
select.Keys.Add("builtin:-1019015"); slimType.Parameters[0].Id = new(-1019015);
Check(slimReader.ReadAll(select).Single().ParameterKey == KacRulePlanner.IfcTypeKey, "언어와 다른 표시명이어도 실제 IFC 식별자로 조회");
select.IncludeOuterMaterial = true;
slim = slimReader.ReadAll(select);
Check(OuterMaterialReader.Calls == 1 && slimModel.Owners.Single().OuterMaterial?.Fingerprint == "test" && slim[0].OuterMaterial?.Name == "KAC-철근콘크리트", "KAC 외측 재료 근거 유형 캐시와 식별 행 보존");
slimDoc.ProjectInformation.Parameters[0].Definition.Name = "공항코드";
var piOnly = new QueryPlan { Scopes = ["프로젝트 정보"], Names = ["공항코드", "건축물번호"] };
Check(slimReader.ReadAll(piOnly).Single().Scope == "프로젝트 정보" && slimModel.Owners.Count == 1 && OuterMaterialReader.Calls == 1, "프로젝트 정보 전용은 모델 유형과 재료를 조회하지 않음");
var inst = new Element { TypeId = slimType.Id }; inst.Parameters[0].Definition.Name = "업체명"; slimDoc.Elements.Add(inst);
select.Scopes.Add("인스턴스");
Check(slimReader.ReadAll(select).Single(r => r.Scope == "인스턴스").TypeOwnerId == slimType.UniqueId, "선택 조회 실제 인스턴스→유형 연결 보존");
var viewOnly = new View(); viewOnly.Parameters[0].Definition.Name = "업체명"; slimDoc.Elements.Add(viewOnly);
Check(slimReader.ReadAll(select).All(r => r.Scope != "뷰"), "룰셋 범위 밖 뷰 제외·전체 조회는 기존 유지");
slimType.Parameters[1].FailRead = false;
Check(slimReader.ReadAll().Any(r => r.Scope == "뷰") && slimModel.Query == null && slimModel.Owners.Count == 0, "전체 조회 복귀 시 원래 뷰·전체 필드 복원");
var large = new Document();
for (int i = 0; i < 100; i++)
{
    var t = new ElementType { Name = "일반 유형" + i }; t.Parameters.Clear();
    for (int j = 0; j < 100; j++) t.Parameters.Add(new() { Id = new(j + 1), Definition = new() { Name = j == 0 ? "업체명" : "추가" + j } });
    large.Elements.Add(t);
}
var fullModel = new ModelSnapshot(); fullModel.Rows = new ParameterReader(large, fullModel).ReadAll();
var smallModel = new ModelSnapshot(); smallModel.Rows = new ParameterReader(large, smallModel).ReadAll(new() { Scopes = ["유형"], Names = ["업체명"] });
Check(fullModel.Rows.Count == 10000 && smallModel.Rows.Count == 100 && smallModel.Owners.Count == 100, "대량 대역에서 값 생성 10000→100·소유자 100 보존");
var fullBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(fullModel).Length;
var smallBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(smallModel).Length;
Check(smallBytes < fullBytes / 20, $"대량 대역 전송량 감소 {fullBytes:N0}→{smallBytes:N0}바이트 (실제 RVT 속도 측정 아님)");
var generic = new RuleSet { Version = 1, Name = "동일 결과", Rules = [new() { Name = "업체", Match = new() { Scope = "유형", Type = "일반 유형", TypeMatch = "시작" }, Set = [new() { Name = "업체명", Storage = "String", DataType = "text", Value = "ND" }] }] };
var fullPlan = RulePlanner.Plan(generic, fullModel.Rows, [], false);
var slimPlan = RulePlanner.Plan(generic, smallModel.Owners.Concat(smallModel.Rows), [], false);
Check(fullPlan.Edits.Select(e => (e.Original.Key, e.Value)).SequenceEqual(slimPlan.Edits.Select(e => (e.Original.Key, e.Value))), "실제 리더 전체·선택 조회의 일반 룰셋 변경 목록 동일");
var kacRules = new RuleSet { Version = 2, Name = "KAC 검사", Kac = new() { ManufacturerInfo = "KAC-v1.0", Company = "ND", MaterialPolicy = "외측-상단-천장하단-단일재료", UnresolvedPolicy = "기존값유지-확인필요", InstanceFields = ["재료명", "업체명", "다음으로 IFC로 내보내기"], Classification = [new() { Code = "CL1210", Kbims = "E1", Procurement = "AEA110" }], Ifc = [new() { Prefix = "CL", Value = "IfcColumn", Source = "표" }] } };
slimType.Parameters.Clear();
foreach (var name in QueryPlan.FromRules(kacRules).Names)
    slimType.Parameters.Add(new() { Id = new(slimType.Parameters.Count + 10), Definition = new() { Name = name, Spec = new("autodesk.spec:spec.string-2.0.0") } });
inst.Parameters.Clear();
foreach (var name in kacRules.Kac.InstanceFields)
    inst.Parameters.Add(new() { Id = new(inst.Parameters.Count + 100), Definition = new() { Name = name, Spec = new("autodesk.spec:spec.string-2.0.0") } });
var fullKac = new ModelSnapshot { Path = "A", Title = "A" }; fullKac.Rows = new ParameterReader(slimDoc, fullKac).ReadAll();
var slimKac = new ModelSnapshot { Path = "A", Title = "A" }; slimKac.Rows = new ParameterReader(slimDoc, slimKac).ReadAll(QueryPlan.FromRules(kacRules));
fullPlan = RulePlanner.Plan(kacRules, fullKac.Rows, [], false);
slimPlan = RulePlanner.Plan(kacRules, slimKac.Owners.Concat(slimKac.Rows), [], false);
Check(fullPlan.Edits.Count == 9 && fullPlan.Edits.Select(e => (e.Original.Key, e.Value, e.MaterialSource?.Fingerprint)).SequenceEqual(slimPlan.Edits.Select(e => (e.Original.Key, e.Value, e.MaterialSource?.Fingerprint))), "KAC 전체·선택 조회 유형·인스턴스·재료 근거까지 동일");
slimType.Parameters.Clear();
slimKac = new() { Path = "A", Title = "A" }; slimKac.Rows = new ParameterReader(slimDoc, slimKac).ReadAll(QueryPlan.FromRules(kacRules));
slimPlan = RulePlanner.Plan(kacRules, slimKac.Owners.Concat(slimKac.Rows), [], false);
Check(slimPlan.Edits.Any(e => e.Original.Scope == "인스턴스" && e.Field == "재료명" && e.Value == "철근콘크리트"), "유형에 대상 매개변수 없어도 인스턴스 재료명 실제 유형으로 판단");
var auditDoc = new Document();
var standardMaterial = new Material { Name = "KAC-외장" }; var otherMaterial = new Material { Name = "임시 도장" };
auditDoc.Elements.AddRange([standardMaterial, otherMaterial]);
var auditType = new HostObjAttributes { Name = "KAC-A-WA1000" }; auditType.Parameters.Clear();
auditType.Structure!.LayerMaterials.Add(standardMaterial.Id);
auditType.Structure.LayerMaterials.Add(otherMaterial.Id);
auditDoc.Elements.Add(auditType);
var auditInstance = new Element { TypeId = auditType.Id }; auditInstance.Parameters.Clear();
auditInstance.Parameters.Add(new Parameter { Definition = new Definition { Name = "마감 재료", Spec = SpecTypeId.Reference.Material }, StorageType = StorageType.ElementId, MaterialId = otherMaterial.Id });
auditInstance.PaintMaterialIds.Add(otherMaterial.Id);
auditDoc.Elements.Add(auditInstance);
var auditSnapshot = new ModelSnapshot { Path = "검사.rvt", Title = "검사" };
var auditRows = new ParameterReader(auditDoc, auditSnapshot).ReadAll(QueryPlan.FromRules(new RuleSet { Version = 6, Name = "재료", MaterialAudit = true }));
Check(auditRows.Count == 0 && auditSnapshot.MaterialAuditItems != null, "재료 점검은 일반 편집 행을 생성하지 않음");
Check(auditSnapshot.MaterialAuditItems!.Any(x => x.Issue == "인스턴스 재료 매개변수" && x.Slot == "마감 재료"), "인스턴스 재료 슬롯 식별");
Check(auditSnapshot.MaterialAuditItems!.Any(x => x.Issue == "KAC- 이외 재료" && x.Scope == "복합구조 층" && x.Material == "임시 도장"), "복합구조 비표준 재료 보고");
Check(auditSnapshot.MaterialAuditItems!.Any(x => x.Issue == "KAC- 이외 재료" && x.Scope == "페인트"), "페인트 비표준 재료 보고");
Check(auditSnapshot.MaterialAuditItems!.All(x => x.Material != "KAC-외장"), "정상 KAC 재료는 보고 제외");
CleanupTests.Run(Check);
GroupTests.Run(Check);
Console.WriteLine($"TOTAL {count} PASS · 실제 ParameterReader/CleanupWriter의 API 대역 검사; RVT 저장 검증 아님");
