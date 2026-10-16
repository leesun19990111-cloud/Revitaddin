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
Console.WriteLine($"TOTAL {count} PASS · 실제 ParameterReader의 API 대역 검사; RVT 저장 검증 아님");
