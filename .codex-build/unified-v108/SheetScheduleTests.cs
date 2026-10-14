using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using System.Windows.Controls;
using WallSplitter;
partial class Program {
 static void SheetScheduleRegression(){
  TestState.Reset();
  var host=Doc("호스트");var a=Doc("건축");var b=Doc("구조");Link(host,a,"건축.rvt",500);Link(host,b,"구조.rvt",600);
  Parameter Text(BuiltInParameter id,string value)=>new(){Id=new(id),StorageType=StorageType.String,Definition=new(){Name=id.ToString(),DataType=new("text")},Text=value};
  ViewSheet Sheet(Document d,int id,string number,string name){var s=new ViewSheet{Id=new(id),Name=name};s.VirtualParameters[BuiltInParameter.SHEET_NUMBER]=Text(BuiltInParameter.SHEET_NUMBER,number);s.Parameters.Add(Text(BuiltInParameter.SHEET_NAME,name));d.Elements.Add(s);return s;}
  var hs=Sheet(host,201,"A01-001","표지");var sa=Sheet(a,201,"A01-001","표지");var sb=Sheet(b,201,"A01-001","표지");
  var ha=Sheet(host,202,"A01-002","목록");var aa=Sheet(a,202,"A01-002","목록");var ba=Sheet(b,202,"A01-002","목록");
  var view=new ViewSchedule{Id=new(3000),Owner=host,Name="00_도면목록표(건축)"};host.Elements.Add(view);view.Definition.CategoryId=new(BuiltInCategory.OST_Sheets);view.Definition.IncludeLinkedFiles=true;
  view.Definition.Fields=new(){new(){FieldId=new(1),ParameterId=new(BuiltInParameter.SHEET_NUMBER),FieldType=ScheduleFieldType.Instance,ColumnHeading="도면번호"},new(){FieldId=new(2),ParameterId=new(BuiltInParameter.SHEET_NAME),FieldType=ScheduleFieldType.Instance,ColumnHeading="도면명"}};view.Definition.Sort.Add(new(){FieldId=new(1)});
  Dictionary<string,string> Row(string n,string title,string file)=>new(){{"1",n},{"2",title},{"param:"+(int)BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT,file},{"param:"+(int)BuiltInParameter.SHEET_NUMBER,n}};
  view.NativeRows=new(){Row("A01-001","표지","구조"),Row("A01-001","표지",""),Row("A01-001","표지","건축"),Row("A01-002","목록","건축"),Row("A01-002","목록","구조"),Row("A01-002","목록","")};view.Counts=Enumerable.Repeat(1,6).ToList();
  // 종전의 그래픽 collector가 시트 아닌 요소/링크 래퍼를 반환하는 환경을 재현한다.
  view.Candidates=host.Elements.OfType<RevitLinkInstance>().Cast<Element>().ToList();view.LinkedCandidates[501]=new(){new Element{Id=new(999)}};view.LinkedCandidates[601]=new(){new Element{Id=new(999)}};
  var app=new UIApplication{ActiveUIDocument=new(){Document=host}};ScheduleEditorSnapshot Read()=>ScheduleEditorService.Read(app,view);
  var data=Read();Check(data.Rows.Count==6&&data.Rows.All(r=>r.Reason==""),"링크 시트 목록은 그래픽 collector의 잘못된 후보에 영향받지 않음");
  Check(data.Fields.All(f=>f.Editor==ScheduleEditorKind.Text),"링크 포함 시트 번호·이름 필드는 편집 가능");
  Check(data.Rows.Take(3).Select(r=>r.Models).SequenceEqual(new[]{"[링크] 구조.rvt","[현재] 호스트","[링크] 건축.rvt"}),"같은 시트 번호·같은 ElementId라도 표의 원본 파일로 소속 구분");
  Check(data.Rows[0].Targets["1"].Single().Key!=data.Rows[1].Targets["1"].Single().Key,"다른 모델의 동일 ID/번호는 별도 수정 키");
  Check(view.Definition.Fields.Count==2&&view.Definition.ShowHeaders&&host.Elements.All(e=>e.Id.Value!=99000),"식별 보조 열은 원본 일람표에 남지 않음");
  ScheduleEditorLogic.Stage(data,data.Fields[1],new(){1},"구조 표지 수정");var pending=data.Pending.Values.ToList();ScheduleEditorService.ValidateGuards(b,data,pending[0].Source.Key);ProjectInformationService.Apply(b,pending,new(),true);
  Check(sb.Parameters[0].Text=="구조 표지 수정"&&sa.Parameters[0].Text=="표지"&&hs.Parameters[0].Text=="표지","선택한 링크 시트만 수정하고 다른 모델의 동명 시트 보존");sb.Parameters[0].Text="표지";
  data=Read();ScheduleEditorLogic.Stage(data,data.Fields[0],new(){3},"A01-009");ProjectInformationService.Apply(a,data.Pending.Values.ToList(),new(),true);
  Check(sa.SheetNumber=="A01-009"&&hs.SheetNumber=="A01-001"&&sb.SheetNumber=="A01-001","Parameters 열거에 없는 기본 시트 번호도 전용 접근자로 저장");sa.VirtualParameters[BuiltInParameter.SHEET_NUMBER].Text="A01-001";
  data=Read();var w=new ScheduleEditorWindow(data);Field<System.Windows.Controls.TextBox>(w,"_range").Text="1-3";Capture(w,"schedule-linked-sheets.png",1180,780);w.Close();
  view.NativeRows.Add(Row("없는 번호","없음","건축"));view.Counts.Add(1);data=Read();Check(data.Rows.Last().Reason.Length>0&&data.Rows.Take(6).All(r=>r.Reason=="")&&data.Fields[0].Editor==ScheduleEditorKind.Text,"찾지 못한 한 행만 차단하고 정상 시트 편집 유지");
  Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[0],new(){7},"x"),"불명확한 행 강제 편집 금지");view.NativeRows.RemoveAt(6);view.Counts.RemoveAt(6);
  var conflict=Doc("다른 건축");Sheet(conflict,201,"A01-001","표지");Link(host,conflict,"다른폴더/건축.rvt",700);((RevitLinkType)host.GetElement(new ElementId(700))!).Name="건축.rvt";
  data=Read();Check(data.Rows[2].Reason.Length>0&&data.Rows[0].Reason==""&&data.Rows[1].Reason=="","경로가 다른 동명 파일은 순서로 추측하지 않고 해당 행만 차단");host.Elements.RemoveAll(e=>e.Id.Value==700||e.Id.Value==701);
  Link(host,a,"건축.rvt",800);data=Read();Check(data.Rows[2].Reason==""&&data.Rows[2].ElementCount==1,"같은 원본의 반복 링크 배치는 실제 시트 한 개로 중복 제거");host.Elements.RemoveAll(e=>e.Id.Value==800||e.Id.Value==801);
  var open=Doc("열린 건축");var os=Sheet(open,201,"A01-001","표지");Sheet(open,202,"A01-002","목록");TestState.OpenTargets["건축.rvt"]=open;
  os.Parameters[0].Text="미동기화 이름";data=Read();Check(data.Rows[2].Errors.ContainsKey("2")&&data.Rows[2].Targets["2"].Count==0,"열린 원본의 미동기화 값과 표가 다르면 해당 필드 덮어쓰기 차단");
  Check(data.Rows[2].Targets["1"].Single().Original=="A01-001","열린 원본의 다른 정상 필드는 계속 조회");TestState.OpenTargets.Clear();
  data=Read();ha.VirtualParameters[BuiltInParameter.SHEET_NUMBER].Text="외부 변경";Throws(()=>ScheduleEditorService.ValidateGuards(host,data,"host"),"조회 후 시트 번호 변경은 쓰기 직전 차단");ha.VirtualParameters[BuiltInParameter.SHEET_NUMBER].Text="A01-002";
  // 링크 포함을 끈 상태에서 비어 있는 링크 정보 열이 있어도 일반 시트 필드를 잠그지 않는다.
  view.Definition.IncludeLinkedFiles=false;view.NativeRows=new(){Row("A01-001","표지",""),Row("A01-002","목록","")};view.Counts=new(){1,1};view.Definition.Fields.Add(new(){FieldId=new(10),ParameterId=new(BuiltInParameter.RVT_LINK_FILE_NAME_WITHOUT_EXT),FieldType=ScheduleFieldType.RevitLinkType,ColumnHeading="RVT 링크: 파일 이름"});
  foreach(var row in view.NativeRows)row["10"]="";data=Read();Check(data.Rows.All(r=>r.Reason=="")&&data.Fields.Take(2).All(f=>f.Editor==ScheduleEditorKind.Text)&&data.Fields[2].Editor==ScheduleEditorKind.ReadOnly,"링크 해제+빈 링크 파일 열은 그 열만 읽기 전용·시트 번호와 이름 편집 허용");
  w=new ScheduleEditorWindow(data);Field<ComboBox>(w,"_field").SelectedIndex=2;Check(!Field<System.Windows.Controls.Button>(w,"_stage").IsEnabled,"표시용 링크 정보 열만 지정 버튼 비활성");Field<ComboBox>(w,"_field").SelectedIndex=0;Check(Field<System.Windows.Controls.Button>(w,"_stage").IsEnabled,"도면번호로 전환하면 즉시 편집 입력 활성");Capture(w,"schedule-host-sheets-min.png",950,600);w.Close();
  view.Definition.Fields[0].IsHidden=true;data=Read();Check(data.Rows.All(r=>r.Reason=="")&&view.Definition.Fields[0].IsHidden,"원본 시트 번호 열이 숨겨져도 임시 식별 후 원본 숨김 보존");view.Definition.Fields[0].IsHidden=false;
  // 그룹 전체 수정도 실제 대상 수가 맞는 경우만 허용한다.
  view.Definition.IsItemized=false;view.Definition.Sort.Clear();view.Definition.Sort.Add(new(){FieldId=new(2)});hs.Parameters[0].Text=ha.Parameters[0].Text="그룹";view.NativeRows=new(){Row("","그룹","")};view.NativeRows[0]["10"]="";view.Counts=new(){2};data=Read();
  Check(data.Rows[0].ElementCount==2&&data.Rows[0].Targets["2"].Count==2&&data.Rows[0].Reason=="","묶인 시트 행은 정렬 기준·실제 개수 확인 후 전체 연결");
  ScheduleEditorLogic.Stage(data,data.Fields[1],new(){1},"그룹 수정");Check(data.Pending.Count==2,"묶인 시트 두 개 전체 미리보기 지정");
  view.Counts[0]=1;data=Read();Check(data.Rows[0].Reason.Length>0,"묶인 시트 개수 불일치 차단");view.Counts[0]=2;
  Check(host.Elements.All(e=>e.Id.Value!=99000),"성공·오류·부분 읽기 전용 경로 모두 임시 시트 목록 제거");
 }
}
