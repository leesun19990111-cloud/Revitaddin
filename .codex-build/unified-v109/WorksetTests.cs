using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows;
using System.Windows.Controls;
using WallSplitter;
namespace Autodesk.Revit.DB {
 public record WorksetId(int IntegerValue);
 public enum WorksetKind { UserWorkset, ViewWorkset }
 public class Workset { public WorksetId Id=new(1);public Guid UniqueId=Guid.NewGuid();public string Name="";public WorksetKind Kind; }
 public partial class Document {public bool IsWorkshared;public List<Workset> Worksets=new();public WorksetTable GetWorksetTable()=>new(this);}
 public class WorksetTable(Document doc) {public Workset? GetWorkset(WorksetId id)=>doc.Worksets.SingleOrDefault(w=>w.Id==id);}
 public class FilteredWorksetCollector(Document doc):IEnumerable<Workset>,IDisposable {
  IEnumerable<Workset> rows=doc.Worksets;public FilteredWorksetCollector OfKind(WorksetKind kind){rows=rows.Where(w=>w.Kind==kind);return this;}
  public IEnumerator<Workset> GetEnumerator()=>rows.GetEnumerator();System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()=>GetEnumerator();public void Dispose(){}
 }
}
partial class Program {
 static void WorksetRegression(){
  var doc=Doc("작업세트 테스트");doc.IsWorkshared=true;
  var first=new Workset{Id=new(1),Name="건축"};var next=new Workset{Id=new(2),Name="구조"};
  doc.Worksets.AddRange(new[]{first,next,new Workset{Id=new(3),Name="뷰",Kind=WorksetKind.ViewWorkset}});
  var a=new FamilyInstance{Id=new(201),Symbol=(FamilySymbol)doc.GetElement(new ElementId(10))!};
  var b=new FamilyInstance{Id=new(202),Symbol=a.Symbol};doc.Elements.AddRange(new Element[]{a,b});
  Parameter P(bool locked=false)=>new(){Id=new(BuiltInParameter.ELEM_PARTITION_PARAM),Definition=new(){Name="작업 세트",DataType=new("enum")},StorageType=StorageType.Integer,Number=1,IsReadOnly=locked};
  a.Parameters.Add(P());b.Parameters.Add(P(true));
  var view=new ViewSchedule{Id=new(3900),Owner=doc,Name="작업세트·혼합 권한",Candidates=new(){a,b},Counts=new(){1,1},NativeRows=new(){new(){{"1","건축"},{"2","A"}},new(){{"1","건축"},{"2","B"}}}};
  a.Parameters.Add(new(){Id=new(-5),StorageType=StorageType.String,Definition=new(){Name="마크",DataType=new("text")},Text="A"});
  b.Parameters.Add(new(){Id=new(-5),StorageType=StorageType.String,Definition=a.Parameters[1].Definition,Text="B"});
  view.Definition.Fields.Add(new(){FieldId=new(1),ParameterId=new(BuiltInParameter.ELEM_PARTITION_PARAM),FieldType=ScheduleFieldType.Instance,ColumnHeading="작업 세트"});
  view.Definition.Fields.Add(new(){FieldId=new(2),ParameterId=new(-5),FieldType=ScheduleFieldType.Instance,ColumnHeading="마크"});doc.Elements.Add(view);
  ScheduleEditorSnapshot Read()=>ScheduleEditorService.Read(new(){ActiveUIDocument=new(){Document=doc}},view);
  var data=Read();var field=data.Fields[0];var target=data.Rows[0].Targets["1"].Single();
  Check(field.Editor==ScheduleEditorKind.Reference&&target.ProjectValue!.ReferenceKind=="작업세트","작업세트 정수 전용 설정을 모델별 선택기로 제공");
  Check(target.ProjectValue!.ReferenceChoices.Count==2&&!target.ProjectValue.ReferenceChoices.ContainsKey(doc.Worksets[2].UniqueId.ToString("D")),"사용자 작업세트만 후보·시스템 작업세트 제외");
  Check(data.Rows[1].Targets["1"].Single().Editable==false&&field.Reason=="","실제 읽기 전용 행이 정상 작업세트 행 편집을 막지 않음");
  Check(ScheduleEditorLogic.EditableRows(data,field).SetEquals(new[]{1}),"편집 가능한 행만 선택·읽기 전용 제외");
  var choices=new Dictionary<string,string>{{"host",next.UniqueId.ToString("D")}};
  Throws(()=>ScheduleEditorLogic.Stage(data,field,new(){1},"2"),"작업세트 숫자 직접 입력 차단");
  Throws(()=>ScheduleEditorLogic.StageReferences(data,field,new(){1,2},choices),"잠긴 행을 포함해 적용하면 전체 미리보기 취소");
  Check(data.Pending.Count==0,"혼합 권한 선택 실패 후 미리보기 보존");
  ScheduleEditorLogic.StageReferences(data,field,new(){1},choices);
  Check(target.Working=="구조"&&a.Parameters[0].Number==1,"작업세트 선택은 미리보기만 변경");
  var w=new ScheduleEditorWindow(data);Children<System.Windows.Controls.Button>((DependencyObject)w.Content).Single(x=>x.Content?.ToString()=="편집 가능 행만 선택").RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
  Check(Field<TextBox>(w,"_range").Text=="1"&&Field<TextBlock>(w,"_editorHint").Text.Contains("1행 / 2행"),"편집 가능 행 버튼과 행 수 안내 실제 WPF 연결");
  Capture(w,"schedule-workset.png",1180,780);Capture(w,"schedule-workset-min.png",950,600);w.Close();
  next.Name="변경됨";Throws(()=>ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),new(),true),"조회 후 작업세트 이름 변경 차단");next.Name="구조";
  var savedGuid=next.UniqueId;next.UniqueId=Guid.NewGuid();Throws(()=>ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),new(),true),"이름과 숫자 ID가 같아도 다른 GUID 작업세트 차단");next.UniqueId=savedGuid;
  a.Parameters[0].FailSet=true;Throws(()=>ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),new(),true),"Revit이 소속 변경 거부하면 예외 전달·롤백");a.Parameters[0].FailSet=false;
  var logs=new List<ChangeLogEntry>();ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),logs,true);
  Check(a.Parameters[0].Number==2&&b.Parameters[0].Number==1&&logs.Single().NewValue=="구조","선택 요소 소속만 실제 정수 Set·로그는 작업세트 이름");
  var source=new UnifiedSource{Key="다른모델",Name="다른모델"};var other=Doc("다른모델");other.IsWorkshared=true;other.Worksets.Add(new(){Id=new(2),Name="구조"});
  var foreign=ProjectInformationService.Snapshot(other,a,a.Parameters[0],source);ScheduleEditorParameters.ConfigureReference(other,a.Parameters[0],foreign,new());
  Check(!foreign.ProjectValue!.ReferenceChoices.ContainsKey(savedGuid.ToString("D")),"모델별 같은 숫자 ID를 공유 GUID처럼 사용하지 않음");
  Check(ScheduleEditorParameters.RawKey(a.Parameters[0],"host")!=ScheduleEditorParameters.RawKey(a.Parameters[0],"link"),"작업세트 식별 원시 키 모델별 분리");
  other.IsLinked=true;var linked=ProjectInformationService.Snapshot(other,a,a.Parameters[0],source);
  Check(!linked.Editable&&linked.ProjectValue!.ReadOnlyReason.Contains("원본 로컬"),"링크 캐시는 영구 읽기 전용 대신 원본 준비 필요 표시");
  data=Read();data.Rows[0].RequiredRows=new(){1,2};Check(ScheduleEditorLogic.EditableRows(data,data.Fields[0]).Count==0,"공동 대상 중 잠긴 행이 있으면 부분 자동 선택하지 않음");
 }
}
