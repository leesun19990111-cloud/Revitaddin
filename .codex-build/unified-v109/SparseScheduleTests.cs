using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using WallSplitter;
partial class Program
{
 static void SparseScheduleRegression()
 {
  TestState.OpenTargets.Clear();
  var host=Doc("호스트");var linked=Doc("연결 캐시");var original=Doc("연결 로컬");
  var guid=Guid.Parse("7bf1a940-0123-4321-bcde-012345678910");
  host.Elements.Add(new SharedParameterElement{Id=new(1800),GuidValue=guid});
  Parameter P(string text)=>new(){Id=new(1800),IsShared=true,GUID=guid,Definition=new(){Name="제작정보",DataType=new("text")},StorageType=StorageType.String,Text=text};
  Element E(Document d,int id,string? value){var e=new FamilyInstance{Id=new(id),Symbol=(FamilySymbol)d.GetElement(new ElementId(10))!};if(value!=null)e.Parameters.Add(P(value));d.Elements.Add(e);return e;}
  var hostMissing=E(host,201,null);var cacheValue=E(linked,201,"KAC-v2.0");var liveValue=E(original,201,"KAC-v2.0");
  var cacheMissing=E(linked,202,null);var liveMissing=E(original,202,null);
  Link(host,linked,"구조.rvt",500);TestState.OpenTargets["구조.rvt"]=original;
  string file="param:"+new ElementId(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT),placement="param:"+new ElementId(BuiltInParameter.RVT_LINK_INSTANCE_NAME);
  var view=new ViewSchedule{Id=new(3800),Owner=host,Name="다중카테고리 빈 셀",Candidates=new(){hostMissing},LinkedCandidates=new(){{501,new(){cacheValue,cacheMissing}}},Counts=new(){1,1,1},
   NativeRows=new(){new(){{"1","KAC-v2.0"},{file,"구조"},{placement,"구조.rvt"}},new(){{"1",""},{file,"구조"},{placement,"구조.rvt"}},new(){{"1",""}}}};
  view.Definition.IncludeLinkedFiles=true;view.Definition.Fields.Add(new(){FieldId=new(1),ParameterId=new(1800),ColumnHeading="제작정보",FieldType=ScheduleFieldType.Instance});host.Elements.Add(view);
  var app=new UIApplication{ActiveUIDocument=new(){Document=host}};ScheduleEditorSnapshot Read()=>ScheduleEditorService.Read(app,view);
  var data=Read();
  Check(data.Rows.All(r=>r.Reason.Length==0&&r.Models!="소속 확인 불가"),"일부 카테고리에 없는 공유 필드와 빈 셀도 행 소속 연결");
  Check(data.Fields[0].Editor==ScheduleEditorKind.Text&&data.Rows[1].Errors.ContainsKey("1")&&data.Rows[2].Errors.ContainsKey("1"),"매개변수 없는 셀만 제한·다른 링크 행 편집 유지");
  ScheduleEditorLogic.Stage(data,data.Fields[0],new(){1},"변경");Check(data.Pending.Count==1,"빈 셀이 섞인 표에서 선택한 링크 행만 미리보기");
  var successWindow=new ScheduleEditorWindow(data);Field<TextBox>(successWindow,"_range").Text="1";Capture(successWindow,"schedule-sparse-linked-min.png",950,600);successWindow.Close();
  ScheduleEditorService.ValidateGuards(original,data,"500");ProjectInformationService.Apply(original,data.Pending.Values.ToList(),new(),true);
  Check(liveValue.Parameters[0].Text=="변경"&&cacheValue.Parameters[0].Text=="KAC-v2.0"&&hostMissing.Parameters.Count==0,"값은 열린 원본에만 반영·없는 매개변수 자동 생성 없음");liveValue.Parameters[0].Text="KAC-v2.0";
  liveMissing.Parameters.Add(P("새로 생김"));Throws(()=>ScheduleEditorService.ValidateGuards(original,data,"500"),"조회 당시 없던 식별 매개변수가 생기면 최종 쓰기 차단");liveMissing.Parameters.Clear();
  view.Definition.Sort.Add(new(){FieldId=new(1)});view.Definition.Filters.Add(new(){FieldId=new(1)});
  data=Read();Check(data.Rows.All(r=>r.Reason.Length==0),"직접 필터/정렬 필드 부재는 재검증 가능한 없음 상태로 보관");
  view.Definition.Sort.Clear();view.Definition.Filters.Clear();
  var extra=E(linked,203,"표 밖 후보");view.LinkedCandidates[501].Add(extra);
  data=Read();Check(data.Rows.All(r=>r.Reason.Length==0)&&data.Rows[0].Targets["1"].Count==1,"표 밖 다른 키 후보 때문에 정상 표시 열 전체를 버리지 않음");
  ScheduleEditorService.ValidateGuards(original,data,"500");Check(data.Guards.All(g=>g.ElementId!="203"),"표와 연결되지 않은 외부 후보는 적용 검증 대상으로도 확장하지 않음");
  extra.Parameters[0].Text="KAC-v2.0";data=Read();Check(data.Rows[0].Reason.Length>0&&data.Rows[0].Targets["1"].Count==0,"표 밖 후보가 같은 키면 개수 불일치로 차단·임의 선택 금지");
  Check(data.Rows[2].Reason.Length==0,"한 링크의 개수 충돌이 다른 모델의 정상 행 소속을 지우지 않음");
  view.LinkedCandidates[501].Remove(extra);
  // 모든 직접 필드의 표시가 API 값과 달라도, 검증된 파일/배치 열은 마지막까지 검사한다.
  view.NativeRows[0]["1"]="별도 표시";data=Read();
  Check(data.Rows.All(r=>r.Reason.Length==0)&&data.Rows[0].RequiredRows.SequenceEqual(new[]{1,2}),"직접 열 제외 후에도 보조 링크 열로 정확한 공동 대상 집합 연결");
  Check(data.Diagnostics.Any(s=>s.Contains("식별 제외"))&&data.Diagnostics.Any(s=>s.Contains("표 데이터 3행")),"진단에 실제 행/후보 수 및 열 제외 이유 기록");
  view.NativeRows[0][file]="일치하지않는파일";view.NativeRows[0][placement]="일치하지않는배치";data=Read();
  Check(data.Rows.All(r=>r.Reason.Length>0)&&data.Fields[0].ConnectionFailed&&data.Fields[0].ToString().Contains("요소 연결 필요"),"전체 연결 실패를 실제 읽기 전용과 구분하여 표시");
  var w=new ScheduleEditorWindow(data);Field<TextBox>(w,"_range").Text="1-3";
  Children<System.Windows.Controls.Button>((DependencyObject)w.Content).Single(b=>b.Content?.ToString()=="프로젝트 매개변수 연결 확인").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
  Check(Field<TextBlock>(w,"_status").Text.Contains("연결 승인 단계까지 도달하지 못했습니다")&&!Field<TextBlock>(w,"_status").Text.Contains("승인이 필요하지"),"미연결 행의 승인 버튼은 승인 불필요라고 오안내하지 않음");
  Capture(w,"schedule-connection-failed-min.png",950,600);
  w.Show();
  var diagnostic=(Window)w.GetType().GetMethod("CreateDiagnosticsWindow",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!.Invoke(w,null)!;
  Check(Children<TextBox>((DependencyObject)diagnostic.Content).Single().IsReadOnly,"행 연결 진단은 읽기 전용 복사 가능한 텍스트");Capture(diagnostic,"schedule-diagnostics.png",820,580);diagnostic.Close();w.Close();
  view.NativeRows[0]["1"]="KAC-v2.0";view.NativeRows[0][file]="구조";view.NativeRows[0][placement]="구조.rvt";
  // 없는 매개변수와 존재하지만 비어 있는 매개변수를 임의로 같은 대상이라 하지 않는다.
  cacheValue.Parameters[0].Text="";liveValue.Parameters[0].Text="";view.NativeRows[0]["1"]="";data=Read();
  Check(data.Rows[0].Reason.Contains("원시값")&&data.Rows[1].Reason.Contains("원시값")&&data.Rows[2].Reason.Length==0,"빈 문자열과 매개변수 부재가 같은 표시 키이면 공동 수정 차단");
  Check(view.Definition.Fields.Count==1&&host.Elements.All(e=>e.Id.Value!=99000),"진단/실패 경로 모두 임시 열·복제 뷰 원본에 남기지 않음");
  TestState.OpenTargets.Clear();
 }
}
