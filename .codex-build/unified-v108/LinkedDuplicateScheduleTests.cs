using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using WallSplitter;
partial class Program
{
 static void LinkedDuplicateScheduleRegression()
 {
  TestState.OpenTargets.Clear();
  Parameter P(int id,string value,bool locked=false)=>new(){Id=new(id),Definition=new(){Name="메모",DataType=new("text")},StorageType=StorageType.String,Text=value,IsReadOnly=locked};
  Element E(Document doc,int id,bool locked=false){var e=new FamilyInstance{Id=new(id),Symbol=(FamilySymbol)doc.GetElement(new ElementId(10))!};e.Parameters.Add(P(-10,"같은값",locked));doc.Elements.Add(e);return e;}
  var host=Doc("호스트");var cache=Doc("링크 캐시");var original=Doc("열린 로컬");
  var he=E(host,201);var ce=E(cache,201,true);var oe=E(original,201);
  Link(host,cache,"건축.rvt",500);TestState.OpenTargets["건축.rvt"]=original;
  var view=new ViewSchedule{Id=new(3000),Name="링크 중복 행",Owner=host,Candidates=new(){he},Counts=new(){1,1},LinkedCandidates=new(){{501,new(){ce}}},NativeRows=new(){new(){{"1","같은값"}},new(){{"1","같은값"}}}};
  view.Definition.IncludeLinkedFiles=true;view.Definition.Fields.Add(new(){FieldId=new(1),ParameterId=new(-10),FieldType=ScheduleFieldType.Instance,ColumnHeading="메모"});host.Elements.Add(view);
  var fileKey="param:"+new ElementId(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT);
  var instanceKey="param:"+new ElementId(BuiltInParameter.RVT_LINK_INSTANCE_NAME);
  view.NativeRows[0][fileKey]="건축";view.NativeRows[0][instanceKey]="건축.rvt";
  var app=new UIApplication{ActiveUIDocument=new(){Document=host}};
  ScheduleEditorSnapshot Read()=>ScheduleEditorService.Read(app,view);
  var data=Read();
  Check(data.Rows.All(r=>r.Reason==""&&r.RequiredRows.Count==0)&&!data.Rows[0].Targets["1"].Single().Source.IsHost&&data.Rows[1].Targets["1"].Single().Source.IsHost,"동일 값 호스트/링크 행을 임시 모델 열로 실제 표시 순서에 연결");
  Check(data.Fields[0].Editor==ScheduleEditorKind.Text&&data.Rows[0].Targets["1"].Single().Editable,"읽기 전용 링크 캐시가 아닌 열린 원본의 편집 권한 사용");
  Check(view.Definition.Fields.Count==1&&host.Elements.All(e=>e.Id.Value!=99000),"임시 모델/배치 열은 원본 일람표에 남지 않음");
  ScheduleEditorLogic.Stage(data,data.Fields[0],new(){1},"링크만 수정");
  ScheduleEditorService.ValidateGuards(original,data,"500");ProjectInformationService.Apply(original,data.Pending.Values.ToList(),new(),true);
  Check(oe.Parameters[0].Text=="링크만 수정"&&ce.Parameters[0].Text=="같은값"&&he.Parameters[0].Text=="같은값","링크 원본의 실제 값만 수정·캐시/호스트 보존");
  data=Read();Check(data.Rows[0].Errors.ContainsKey("1")&&data.Rows[1].Errors.Count==0,"원본 미동기화 차이는 해당 링크 셀만 차단");
  Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[0],new(){1},"덮어쓰기"),"열린 원본 기존 작업을 링크의 옛 값으로 덮어쓰지 않음");oe.Parameters[0].Text="같은값";
  original.Elements.Remove(oe);data=Read();Check(data.Rows[0].Errors.ContainsKey("1"),"원본에서 삭제된 요소는 수정 대상 없음 안내");original.Elements.Add(oe);
  // 같은 모델의 완전히 같은 두 행은 전체 집합을 확인하고 두 행을 함께 선택해야만 편집한다.
  var he2=E(host,202);view.Candidates.Add(he2);view.NativeRows.Add(new(){{"1","같은값"}});view.Counts.Add(1);
  data=Read();Check(data.Rows[0].RequiredRows.Count==0&&data.Rows[1].RequiredRows.SequenceEqual(new[]{2,3})&&data.Rows[2].RequiredRows.SequenceEqual(new[]{2,3}),"중복 행이 있어도 정상 링크 행은 개별 편집 유지");
  Check(ReferenceEquals(data.Rows[1].Targets["1"],data.Rows[2].Targets["1"])&&ReferenceEquals(data.Rows[1].RequiredRows,data.Rows[2].RequiredRows),"동일 표시 행은 공동 대상/범위 스냅샷을 공유하여 제곱 반복 조회 방지");
  Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[0],new(){2},"부분"),"구분 불가 두 행 중 하나만 선택하면 차단");Check(data.Pending.Count==0,"중복 일부 선택 실패는 누적 변경 없음");
  ScheduleEditorLogic.Stage(data,data.Fields[0],new(){2,3},"함께 변경");Check(data.Pending.Count==2&&data.Pending.Values.All(r=>r.Source.IsHost),"확인된 중복 행 전체 선택 시 해당 집합만 일괄 변경");
  var w=new ScheduleEditorWindow(data);Field<TextBox>(w,"_range").Text="2";
  Children<System.Windows.Controls.Button>((DependencyObject)w.Content).Single(b=>b.Content?.ToString()=="동일 표시 행 함께 선택").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
  Check(Field<HashSet<int>>(w,"_selected").SetEquals(new[]{2,3}),"동일 표시 행 함께 선택 버튼은 관련 행만 명시적으로 추가");
  Capture(w,"schedule-duplicate-links.png",1180,780);Capture(w,"schedule-duplicate-links-min.png",950,600);w.Close();
  var matches=ScheduleEditorLogic.MatchRows(new[]{"A","A","B"},new[]{"A","A","B"},new[]{"10.001","10.002","ok"},new[]{1,1,1},true);
  Check(matches.Take(2).All(m=>m.Reason.Length>0)&&matches[2].Reason==""&&matches[2].Members.Single()==2,"반올림 모호성은 해당 행만 차단·정상 행 보존");
  matches=ScheduleEditorLogic.MatchRows(new[]{"A","A","B"},new[]{"A","B"},new[]{"a","b"},new[]{1,1,1},true);
  Check(matches.Take(2).All(m=>m.Members.Count==0)&&matches[2].Members.Count==1,"대상 개수 불일치는 공동 편집도 금지");
  matches=ScheduleEditorLogic.MatchRows(new[]{"A","A"},new[]{"A","A"},new[]{"a","a"},new[]{1,1},false);
  Check(matches.All(m=>m.Reason.Length>0),"비개별 그룹의 중복은 임의로 합치지 않음");
  // 링크 배치 정보가 정렬/필터에 있어도 원본 매개변수로 오해하여 전체 차단하지 않는다.
  view.Definition.Fields.Add(new(){FieldId=new(4),FieldType=ScheduleFieldType.RevitLinkType,ParameterId=new(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT),ColumnHeading="링크 파일"});
  view.Definition.Sort.Add(new(){FieldId=new(4)});view.Definition.Filters.Add(new(){FieldId=new(4)});
  view.NativeRows[0]["4"]="건축";data=Read();Check(data.Rows.All(r=>r.Reason=="")&&data.Fields.Single(f=>f.Id=="4").Editor==ScheduleEditorKind.ReadOnly&&data.Fields[0].Editor==ScheduleEditorKind.Text,"링크 파일 정렬/필터는 식별에 사용·메타데이터 열만 읽기 전용");
  // 프로젝트 정보 필드는 행 소유 요소가 아니라 그 모델의 ProjectInfo로 연결한다.
  foreach(var d in new[]{host,cache,original}) {var info=new ProjectInfo{Id=new(1000),Name="정보"};info.Parameters.Add(P(-30,"프로젝트값",d==cache));d.Elements.Add(info);}
  view.Definition.Fields.Add(new(){FieldId=new(5),FieldType=ScheduleFieldType.ProjectInfo,ParameterId=new(-30),ColumnHeading="프로젝트 번호"});foreach(var cell in view.NativeRows)cell["5"]="프로젝트값";
  data=Read();var projectField=data.Fields.Single(f=>f.Id=="5");Check(projectField.Editor==ScheduleEditorKind.Text&&projectField.IsProjectInfo&&data.Rows[0].Targets["5"].Single().UniqueId=="1000","일람표의 프로젝트 정보 필드도 정확한 소속 모델 정보에 연결");
  ScheduleEditorLogic.Stage(data,projectField,new(){1},"LINK-P001");ProjectInformationService.Apply(original,data.Pending.Values.ToList(),new(),true);
  Check(original.ProjectInformation!.Parameters[0].Text=="LINK-P001"&&host.ProjectInformation!.Parameters[0].Text=="프로젝트값","일람표에서 선택한 링크의 프로젝트 정보만 실제 반영");
  original.ProjectInformation.Parameters[0].Text="프로젝트값";
  Parameter ProjectParameter(Document doc,Element element,int id,string name,string value)
  {
   var p=P(id,value);p.Definition.Name=name;element.Parameters.Add(p);
   if(doc.GetElement(new ElementId(id))==null)doc.Elements.Add(new ParameterElement{Id=new(id),Definition=p.Definition});
   return p;
  }
  var hp=ProjectParameter(host,he,1500,"재료명","도장");ProjectParameter(host,he2,1500,"재료명","도장");
  var cp=ProjectParameter(cache,ce,2500,"재료명","도장");var op=ProjectParameter(original,oe,2500,"재료명","도장");
  view.Definition.Fields.Add(new(){FieldId=new(6),FieldType=ScheduleFieldType.Instance,ParameterId=new(1500),ColumnHeading="마감 재료(열 제목)"});foreach(var cell in view.NativeRows)cell["6"]="도장";
  data=Read();var materialName=data.Fields.Single(f=>f.Id=="6");
  Check(materialName.Editor==ScheduleEditorKind.Text&&data.Rows[0].Targets["6"].Single().ProjectValue!.Identity=="id:2500","열 제목이 달라도 실제 정의명·자료형으로 다른 모델 ID 프로젝트 매개변수 후보 연결");
  Throws(()=>ScheduleEditorLogic.Stage(data,materialName,new(){1},"콘크리트"),"일반 프로젝트 매개변수는 연결 승인 전 실제 누적도 차단");
  Check(data.Pending.Count==0,"연결 미승인으로 실패하면 모든 미리보기 보존");
  data.ApprovedMappings.UnionWith(data.Rows[0].ParameterMappings["6"]);ScheduleEditorLogic.Stage(data,materialName,new(){1},"콘크리트");
  ScheduleEditorService.ValidateGuards(original,data,"500");ProjectInformationService.Apply(original,data.Pending.Values.ToList(),new(),true);
  Check(op.Text=="콘크리트"&&cp.Text=="도장"&&hp.Text=="도장","승인한 링크의 실제 프로젝트 매개변수만 저장");op.Text="도장";
  var duplicate=ProjectParameter(cache,ce,2501,"재료명","도장");data=Read();Check(data.Rows[0].Errors.ContainsKey("6"),"동일 이름·자료형 후보가 두 개면 임의 연결 차단");ce.Parameters.Remove(duplicate);
  cp.Definition.DataType=new("other");data=Read();Check(data.Rows[0].Errors.ContainsKey("6"),"이름이 같아도 자료형이 다르면 연결 차단");cp.Definition.DataType=new("text");
  var guid=Guid.Parse("cd5cbbe5-a836-4c40-8494-d437c0736a41");host.Elements.Add(new SharedParameterElement{Id=new(1600),GuidValue=guid});
  foreach(var e in new[]{he,he2,ce,oe}) {var p=P(e==he||e==he2?1600:2600,"업체A");p.IsShared=true;p.GUID=guid;p.Definition.Name="업체명";e.Parameters.Add(p);}
  view.Definition.Fields.Add(new(){FieldId=new(7),FieldType=ScheduleFieldType.Instance,ParameterId=new(1600),ColumnHeading="업체명"});foreach(var cell in view.NativeRows)cell["7"]="업체A";
  data=Read();var company=data.Fields.Single(f=>f.Id=="7");ScheduleEditorLogic.Stage(data,company,new(){1},"업체B");
  Check(data.Pending.Values.Single().ProjectValue!.Identity.StartsWith("guid:")&&!data.Rows[0].ParameterMappings.ContainsKey("7"),"공유 매개변수는 모델별 번호와 무관하게 GUID로 연결·이름 승인 불필요");
  foreach(var e in new[]{he,he2,ce,oe})e.Parameters.Add(P((int)BuiltInParameter.IFC_EXPORT_ELEMENT_AS,""));
  view.Definition.Fields.Add(new(){FieldId=new(8),FieldType=ScheduleFieldType.Instance,ParameterId=new(BuiltInParameter.IFC_EXPORT_ELEMENT_AS),ColumnHeading="다음으로 IFC로 내보내기"});foreach(var cell in view.NativeRows)cell["8"]="";
  data=Read();var ifc=data.Fields.Single(f=>f.Id=="8");
  Check(ifc.IsIfcEntity&&ifc.Editor==ScheduleEditorKind.Text&&ifc.Hint.Contains("IfcWall"),"IFC 내보내기 엔티티 필드는 전용 안내와 문자열 입력 제공");
  ScheduleEditorLogic.Stage(data,ifc,new(){1},"IfcWall");ProjectInformationService.Apply(original,data.Pending.Values.ToList(),new(),true);
  Check(oe.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS)!.AsString()=="IfcWall"&&he.get_Parameter(BuiltInParameter.IFC_EXPORT_ELEMENT_AS)!.AsString()=="","링크 원본에 IFC 엔티티 값 저장·호스트 보존");
  w=new ScheduleEditorWindow(data);Field<ComboBox>(w,"_field").SelectedIndex=data.Fields.IndexOf(ifc);Capture(w,"schedule-ifc-links-min.png",950,600);w.Close();
  TestState.OpenTargets.Clear();
 }
}
