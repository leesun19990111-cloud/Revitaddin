using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;
partial class Program {
 static void SharedOwnerRegression(){
  TestState.OpenTargets.Clear();
  var host=Doc("유형 소유 검증");var cache=Doc("링크 캐시");cache.IsLinked=true;var open=Doc("열린 로컬");
  var guid=Guid.Parse("1160800e-68ed-4493-aacf-bd9982b15708");
  host.Elements.Add(new SharedParameterElement{Id=new(1900),GuidValue=guid,Definition=new(){Name="제작정보",DataType=new("text")}});
  Parameter P(int id,string text,bool locked=false)=>new(){Id=new(id),IsShared=true,GUID=guid,Definition=new(){Name="제작정보",DataType=new("text")},StorageType=StorageType.String,Text=text,IsReadOnly=locked};
  FamilyInstance E(Document d,int id,string value){var e=new FamilyInstance{Id=new(id),Symbol=(FamilySymbol)d.GetElement(new ElementId(10))!};d.Elements.Add(e);e.Symbol.Parameters.Add(P(1900,value,d.IsLinked));e.GuidProxies[guid]=P(1900,"",true);return e;}
  var a=E(host,201,"현재 유형");var b=E(cache,201,"링크 유형");var original=E(open,201,"링크 유형");
  Link(host,cache,"유형링크.rvt",500);TestState.OpenTargets["유형링크.rvt"]=open;
  string file="param:"+new ElementId(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT);
  var view=new ViewSchedule{Id=new(3901),Owner=host,Name="공유 유형 매개변수",Candidates=new(){a},LinkedCandidates=new(){{501,new(){b}}},Counts=new(){1,1},NativeRows=new(){new(){{"1","현재 유형"}},new(){{"1","링크 유형"},{file,"유형링크"}}}};
  view.Definition.IncludeLinkedFiles=true;view.Definition.Fields.Add(new(){FieldId=new(1),ParameterId=new(1900),FieldType=ScheduleFieldType.Instance,ColumnHeading="제작정보"});host.Elements.Add(view);
  ScheduleEditorSnapshot Read()=>ScheduleEditorService.Read(new(){ActiveUIDocument=new(){Document=host}},view);
  var data=Read();var field=data.Fields[0];
  Check(data.Rows.All(r=>r.Reason==""&&r.Errors.Count==0)&&field.Editor==ScheduleEditorKind.Text&&field.IsType,"공유 유형 필드가 Instance여도 읽기 전용 대리값 대신 실제 유형으로 연결");
  Check(data.Rows.All(r=>r.Targets["1"].Single().UniqueId=="10")&&data.Rows[1].Targets["1"].Single().Editable,"현재/링크의 같은 유형 ID도 각각 열린 원본의 소유 매개변수 조회");
  ScheduleEditorLogic.Stage(data,field,new(){2},"변경 유형");ScheduleEditorService.ValidateGuards(open,data,"500");var logs=new List<ChangeLogEntry>();ProjectInformationService.Apply(open,data.Pending.Values.ToList(),logs,true);
  Check(original.Symbol.Parameters.Single().Text=="변경 유형"&&b.Symbol.Parameters.Single().Text=="링크 유형"&&a.Symbol.Parameters.Single().Text=="현재 유형","선택한 링크 원본 유형만 수정·캐시/현재/대리값 보존");original.Symbol.Parameters.Single().Text="링크 유형";
  foreach(var e in new[]{a,b,original}){e.GuidProxies[guid].IsReadOnly=false;e.GuidProxies[guid].HasValue=false;}
  Check(Read().Rows.All(r=>r.Errors.Count==0)&&Read().Fields[0].IsType,"쓰기 가능처럼 보이는 빈 대리값도 실제 소유 집합으로 제외");
  a.Parameters.Add(P(1900,"실제 인스턴스",true));
  data=Read();Check(data.Rows.Any(r=>r.Errors.Values.Any(v=>v.Contains("유일하지"))),"동일 GUID가 실제 인스턴스·유형 양쪽에 있으면 임의 소유 범위 선택 차단");
  a.Symbol.Parameters.Clear();view.NativeRows[0]["1"]="실제 인스턴스";
  data=Read();Check(!data.Rows[0].Targets["1"].Single().Editable&&data.Rows[1].Targets["1"].Single().Editable,"실제 소유 인스턴스 읽기 전용은 풀지 않고 다른 링크 정상 행 보존");
  a.Parameters[0].IsReadOnly=false;data=Read();Check(data.Rows[0].Targets["1"].Single().UniqueId==a.UniqueId,"실제 인스턴스 공유 매개변수는 인스턴스로 유지");
  a.Parameters[0].GUID=Guid.NewGuid();a.GuidProxies[guid].IsReadOnly=true;
  data=Read();Check(data.Rows.Any(r=>r.Errors.Values.Any(v=>v.Contains("공유 GUID"))),"같은 이름이지만 GUID가 다른 값에 쓰지 않음·진단에 정확한 식별자 제공");
  Check(data.Diagnostics.Any(s=>s.Contains("필드 상태:")),"실제 진단에 필드별 편집 가능 행 수와 제한 사유 추가");
  TestState.OpenTargets.Clear();
 }
}
