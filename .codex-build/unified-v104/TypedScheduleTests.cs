using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using WallSplitter;
partial class Program {
 static void TypedScheduleRegression(){
  var doc=Doc("형식 테스트");var type=(FamilySymbol)doc.GetElement(new ElementId(10))!;
  var a=new FamilyInstance{Id=new(201),Symbol=type};var b=new FamilyInstance{Id=new(202),Symbol=type};doc.Elements.AddRange(new Element[]{a,b,new Level{Id=new(800),Name="1층"},new Level{Id=new(801),Name="2층"}});
  Parameter P(int id,string spec,StorageType storage,double number=0)=>new(){Id=new(id),Definition=new(){Name="항목"+id,DataType=new(spec)},StorageType=storage,Double=number,Number=(int)number};
  a.Parameters.Add(P(-10,"number",StorageType.Double,20));b.Parameters.Add(P(-10,"number",StorageType.Double,10));
  var view=new ViewSchedule{Id=new(3000),Name="숫자 필드 일람표",Owner=doc,Candidates=new(){a,b},Counts=new(){1,1},NativeRows=new(){new(){{"1","10"}},new(){{"1","20"}}}};
  view.Definition.Fields.Add(new(){FieldId=new(1),ParameterId=new(-10),FieldType=ScheduleFieldType.Instance,ColumnHeading="길이"});doc.Elements.Add(view);
  var app=new UIApplication{ActiveUIDocument=new(){Document=doc}};
  ScheduleEditorSnapshot Read()=>ScheduleEditorService.Read(app,view);
  var data=Read();Check(data.Rows.All(r=>r.Reason=="")&&data.Rows[0].Targets["1"].Single().UniqueId==b.UniqueId,"텍스트 없는 숫자 일람표를 실제 표시 순서로 연결");
  Check(data.Fields[0].Editor==ScheduleEditorKind.Number&&data.Fields[0].Hint.Contains("mm"),"숫자 입력기와 모델 단위 안내");
  view.Definition.Fields[0].Format=new(){UseDefault=false,Digits=2};view.NativeRows[0]["1"]="10.00";view.NativeRows[1]["1"]="20.00";
  Check(Read().Rows.All(r=>r.Reason==""),"일람표 필드의 사용자 지정 숫자 서식 반영");
  a.Parameters[0].Double=10.001;b.Parameters[0].Double=10.002;view.NativeRows=new(){new(){{"1","10.00"}}};view.Counts=new(){2};view.Definition.IsItemized=false;view.Definition.Sort.Add(new(){FieldId=new(1)});
  data=Read();Check(data.Rows[0].Reason.Contains("원시값")&&data.Rows[0].Targets["1"].Count==0,"반올림으로 겹친 다른 원시값은 창을 열되 오편집 차단");
  a.Parameters[0].Double=b.Parameters[0].Double=10;data=Read();Check(data.Rows[0].ElementCount==2&&data.Rows[0].Targets["1"].Count==2,"동일 숫자로 묶인 행의 전체 요소 확인");
  ScheduleEditorLogic.Stage(data,data.Fields[0],new(){1},"30");Check(data.Pending.Count==2,"숫자 그룹 행 전체 미리보기 지정");
  view.Definition.IsItemized=true;view.Definition.Sort.Clear();a.Parameters[0].Double=20;b.Parameters[0].Double=10;view.Counts=new(){1,1};view.NativeRows=new(){new(){{"1","10.00"}},new(){{"1","20.00"}}};
  void Add(int id,int param,string heading,Parameter pa,Parameter pb,string av,string bv){a.Parameters.Add(pa);b.Parameters.Add(pb);view.Definition.Fields.Add(new(){FieldId=new(id),ParameterId=new(param),FieldType=ScheduleFieldType.Instance,ColumnHeading=heading});view.NativeRows[0][id.ToString()]=bv;view.NativeRows[1][id.ToString()]=av;}
  Add(2,-11,"사용 여부",P(-11,"bool",StorageType.Integer,0),P(-11,"bool",StorageType.Integer,1),"아니요","예");
  Add(3,-12,"정수",P(-12,"int",StorageType.Integer,2),P(-12,"int",StorageType.Integer,3),"2","3");
  var ma=P(-13,"material",StorageType.ElementId);ma.Value=new(100);var mb=P(-13,"material",StorageType.ElementId);mb.Value=new(100);Add(4,-13,"재료",ma,mb,"콘크리트","콘크리트");
  var la=P(-20,"level",StorageType.ElementId);la.Value=new(800);var lb=P(-20,"level",StorageType.ElementId);lb.Value=new(800);Add(5,-20,"레벨",la,lb,"1층","1층");
  Add(6,-21,"전용 설정",P(-21,"enum",StorageType.Integer),P(-21,"enum",StorageType.Integer),"0","0");
  view.Definition.Fields.Add(new(){FieldId=new(7),IsCalculatedField=true,ColumnHeading="계산값"});foreach(var row in view.NativeRows)row["7"]="42";
  data=Read();Check(data.Rows.All(r=>r.Reason==""),"예/아니요 특수 표시와 계산식이 있어도 검증된 숫자 식별로 편집 가능");
  Check(data.Fields.Select(f=>f.Editor).SequenceEqual(new[]{ScheduleEditorKind.Number,ScheduleEditorKind.Boolean,ScheduleEditorKind.Integer,ScheduleEditorKind.Reference,ScheduleEditorKind.Reference,ScheduleEditorKind.ReadOnly,ScheduleEditorKind.ReadOnly}),"숫자·불리언·정수·재료·레벨·전용 설정·계산식 입력기 분류");
  ScheduleEditorLogic.Stage(data,data.Fields[1],new(){1,2},"아니요");Check(data.Pending.Count==1,"예/아니요는 실제 바뀌는 값만 누적");
  Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[2],new(){1,2},"2.5"),"정수 필드 소수 입력 차단");Check(data.Pending.Count==1,"정수 오류 뒤 미리보기 보존");
  Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[3],new(){1},"금속"),"재료는 이름 텍스트로 임의 지정 불가");
  ScheduleEditorLogic.StageReferences(data,data.Fields[3],new(){1,2},new(){{"host","101"}});Check(data.Pending.Count==3&&data.Rows.All(r=>r.Targets["4"].Single().Working=="금속"),"재료 선택은 해당 모델의 UniqueId로 누적");
  ScheduleEditorLogic.StageReferences(data,data.Fields[4],new(){1},new(){{"host","801"}});Check(data.Pending.Count==4,"레벨 선택을 별도 누적");
  var w=new ScheduleEditorWindow(data);var combo=Field<ComboBox>(w,"_field");combo.SelectedIndex=1;
  Check(Field<ComboBox>(w,"_boolean").Visibility==System.Windows.Visibility.Visible&&Field<TextBox>(w,"_value").Visibility==System.Windows.Visibility.Collapsed,"불리언 선택 시 텍스트 대신 예/아니요 목록 표시");
  Field<ComboBox>(w,"_boolean").SelectedIndex=1;combo.SelectedIndex=2;Check(Field<TextBox>(w,"_value").Visibility==System.Windows.Visibility.Visible&&Field<TextBox>(w,"_value").Text=="","필드 전환 시 이전 입력값 이월 방지");
  combo.SelectedIndex=3;Check(Children<ComboBox>(Field<StackPanel>(w,"_referenceEditors")).Count()==1,"재료 필드에 모델별 검색·선택 UI 표시");Capture(w,"schedule-typed-material.png",1180,780);
  combo.SelectedIndex=4;Capture(w,"schedule-typed-level-min.png",950,600);combo.SelectedIndex=6;Check(!Field<System.Windows.Controls.Button>(w,"_stage").IsEnabled,"계산식 선택 시 지정 버튼 비활성");w.Close();
  var logs=new List<ChangeLogEntry>();ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),logs,true);
  Check(mb.Value.Value==101&&ma.Value.Value==101&&lb.Value.Value==801&&la.Value.Value==800&&b.Parameters[1].Number==0,"각 저장형에 맞는 실제 쓰기와 선택 밖 레벨 보존");
  Check(logs.Count==4&&logs.All(l=>l.Kind==ChangeKind.ScheduleParameter),"형식별 변경은 커밋 후 일람표 로그 기록");
  foreach(var row in view.NativeRows){row["4"]="금속";row["2"]="아니요";}view.NativeRows[0]["5"]="2층";
  data=Read();ScheduleEditorLogic.StageReferences(data,data.Fields[3],new(){1,2},new(){{"host","100"}});doc.GetElement(new ElementId(100))!.Name="조회 후 변경";
  Throws(()=>ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),new(),true),"선택 후 참조 이름 변경은 저장 전 차단");Check(ma.Value.Value==101&&mb.Value.Value==101,"참조 검증 실패 시 실제 값 보존");doc.GetElement(new ElementId(100))!.Name="콘크리트";
  data=Read();ScheduleEditorLogic.StageReferences(data,data.Fields[3],new(){1,2},new(){{"host","100"}});ma.FailSet=true;logs.Clear();Throws(()=>ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),logs,true),"두 번째 참조 쓰기 실패 시 전체 트랜잭션 취소");Check(ma.Value.Value==101&&mb.Value.Value==101&&logs.Count==0,"부분 참조 쓰기 및 로그 롤백");ma.FailSet=false;
  var original=ProjectInformationService.Snapshot(doc,a,ma,new UnifiedSource{Key="host"});Check(!original.Editable,"일람표에서 명시적으로 활성화하지 않은 참조는 기존 읽기 전용 유지");
  view.Definition.IsItemized=false;view.Definition.Sort.Add(new(){FieldId=new(5)});data=Read();Check(data.Rows.All(r=>r.Reason=="")&&data.Rows[0].Targets["5"].Single().UniqueId==b.UniqueId,"레벨 기준 그룹을 직접 참조값과 개수로 연결");
  view.Definition.Sort.Clear();view.Definition.IsItemized=true;
  view.Definition.Fields.ForEach(f=>f.IsHidden=f.FieldId.IntegerValue!=7);data=Read();Check(data.Rows.Count==2&&data.Rows.All(r=>r.Reason.Length>0)&&data.Fields.Single().Editor==ScheduleEditorKind.ReadOnly,"계산식만 있는 일람표도 표시 전용 창으로 조회");
  Check(doc.Elements.All(e=>e.Id.Value!=99000),"모든 새 조회 경로에서 임시 일람표 제거");
  // 서로 다른 문서에서 같은 이름을 자동 매핑하지 않는다.
  var x=new UnifiedSource{Key="x",Name="링크 X"};var y=new UnifiedSource{Key="y",Name="링크 Y"};
  UnifiedRow Ref(UnifiedSource s,string uid)=>new(){Source=s,UniqueId="1",Category=-1,Original="기존",Working="기존",ProjectValue=new(){Identity="ref",ReferenceKind="재료",OriginalReference="old",SelectedReference="old",ReferenceChoices=new(){{uid,"동명 재료"},{"old","기존"}}}};
  var rx=Ref(x,"x-new");var ry=Ref(y,"y-new");var mixed=new ScheduleEditorSnapshot();var mf=new ScheduleEditorField{Id="ref"};mixed.Fields.Add(mf);var mr=new ScheduleEditorRow{Number=1};mr.Targets["ref"]=new(){rx,ry};mixed.Rows.Add(mr);
  Throws(()=>ScheduleEditorLogic.StageReferences(mixed,mf,new(){1},new(){{"x","x-new"}}),"링크별 선택 누락은 전체 지정 차단");Check(mixed.Pending.Count==0&&rx.Working=="기존","모델별 선택 누락에 부분 미리보기 없음");
  ScheduleEditorLogic.StageReferences(mixed,mf,new(){1},new(){{"x","x-new"},{"y","y-new"}});Check(rx.ProjectValue!.SelectedReference=="x-new"&&ry.ProjectValue!.SelectedReference=="y-new","동명 재료라도 각 모델의 선택 ID 유지");
  ScheduleEditorLogic.StageReferences(mixed,mf,new(){1},new(){{"x","old"},{"y","old"}});Check(mixed.Pending.Count==0,"원래 참조 선택으로 돌아오면 변경 취소");
 }
}
