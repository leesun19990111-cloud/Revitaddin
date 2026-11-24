using System.Reflection;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;
using Button=System.Windows.Controls.Button;
class Program {
 static int count;
 static void Check(bool ok,string s){if(!ok)throw new Exception(s);Console.WriteLine("PASS "+s);count++;}
 static T Field<T>(object o,string n)=>(T)o.GetType().GetField(n,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(o)!;
 static void Call(object o,string n)=>o.GetType().GetMethod(n,BindingFlags.NonPublic|BindingFlags.Instance|BindingFlags.DeclaredOnly)!.Invoke(o,null);
 static IEnumerable<T> Children<T>(DependencyObject o) where T:DependencyObject {
  if(o is T t)yield return t;
  for(int i=0;i<VisualTreeHelper.GetChildrenCount(o);i++)foreach(var x in Children<T>(VisualTreeHelper.GetChild(o,i)))yield return x;
 }
 static void Click(UnifiedEditWindow w,string text)=>Children<Button>(w.Content as DependencyObject ?? w).First(b=>b.Content?.ToString()==text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 static Document Doc(string name,int children=2) {
  var d=new Document{Title=name,PathName=name+".rvt"};
  var f=new Family{Id=new(1),Name="문_자동문"};d.Elements.Add(f);
  for(int i=0;i<children;i++){var type=new FamilySymbol{Id=new(i+10),Name="유형_"+i,Family=f,FamilyName=f.Name};d.Elements.Add(type);f.Symbols.Add(type.Id);}
  d.Elements.Add(new Material{Id=new(100),Name="콘크리트"});
  d.Elements.Add(new Material{Id=new(101),Name="금속"});
  return d;
 }
 static void Link(Document host,Document? linked,string path,int n,bool nested=false){
  var type=new RevitLinkType{Id=new(n),Name=path,Path=path,IsNestedLink=nested};host.Elements.Add(type);
  host.Elements.Add(new RevitLinkInstance{Id=new(n+1),Name=path,Type=type.Id,Link=linked});
 }
 static void Capture(Window w,string path,int width=1240,int height=800){
  w.Width=width;w.Height=height;
  var root=(FrameworkElement)w.Content;
  root.Measure(new Size(width-28,height-28));root.Arrange(new Rect(0,0,width-28,height-28));root.UpdateLayout();
  var bitmap=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);bitmap.Render(root);
  var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var file=File.Create(path);encoder.Save(file);
 }
 static void Throws(Action action,string message){bool thrown=false;try{action();}catch{thrown=true;}Check(thrown,message);}
 static void Batch(bool approve,string fail){
  TestState.Reset();TestState.Approve=approve;TestState.FailLink=fail;
  var host=Doc("현재");Link(host,Doc("A"),"A",500);Link(host,Doc("B"),"B",600);
  Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
   var w=Application.Current.Windows.OfType<UnifiedEditWindow>().Single();
   foreach(var row in Field<List<UnifiedSource>>(w,"_sources").SelectMany(s=>s.Rows).Where(r=>r.IsFamily))row.Working="변경";
   w.DialogResult=true;
  }));
  var result=UnifiedEditService.Execute(new UIApplication{ActiveUIDocument=new(){Document=host}},false,()=>Result.Cancelled);
  Check(TestState.Confirmations==1,"모델 전체 최종 확인 한 번");
  if(!approve)Check(TestState.LinkCalls==0&&host.GetElement(new ElementId(1))!.Name=="문_자동문","최종 취소는 모든 모델 미변경");
  else if(fail.Length>0){
   Check(result==Result.Succeeded,"앞 모델 동기화 후 중단은 외부 명령 자동 취소를 요청하지 않음");
   Check(TestState.Events.SequenceEqual(new[]{"A"}),"중간 실패면 성공한 링크만 반영, 현재 모델 미처리");
   Check(TestState.Report.Contains("미처리")&&TestState.Report.Contains("동기화 완료")&&TestState.Report.Contains("실패"),"성공·실패·미처리 동시 보고");
  }else Check(result==Result.Succeeded&&TestState.Events.SequenceEqual(new[]{"A","B","현재"}),"모든 링크 순차 적용 후 현재 모델 마지막 반영");
 }
 [STAThread] static void Main(){
  new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  UiRegression();
  ProjectInformationRegression();
  ScheduleRegression();
  ScheduleSelectionRegression();
  var host=Doc("현재 프로젝트",245);var a=Doc("구조");var b=Doc("건축");
  Link(host,a,"구조.rvt",500);Link(host,a,"구조.rvt",600);Link(host,b,"건축.rvt",700);Link(host,null,"미로드.rvt",800);Link(host,Doc("중첩"),"중첩.rvt",900,true);
  var sources=UnifiedEditService.Collect(host,false);
  var opened=Doc("열린 구조");opened.GetElement(new ElementId(1))!.Name="미동기화 이름";
  TestState.OpenTargets["구조.rvt"]=opened;
  var openSources=UnifiedEditService.Collect(host,false,new UIApplication{ActiveUIDocument=new(){Document=host}});
  var openSource=openSources.Single(s=>s.Path=="구조.rvt");
  Check(openSource.Rows.Single(r=>r.IsFamily).Original=="미동기화 이름"&&openSource.OpenDocumentPath==opened.PathName,"링크 캐시 대신 열린 원본의 미동기화 이름과 경로 수집");
  Check(a.GetElement(new ElementId(1))!.Name=="문_자동문","열린 원본 조회는 링크 캐시를 수정하지 않음");
  opened.GetElement(new ElementId(100))!.Name="열린 재료";
  var openMaterials=UnifiedEditService.Collect(host,true,new UIApplication{ActiveUIDocument=new(){Document=host}});
  Check(openMaterials.Single(s=>s.Path=="구조.rvt").Materials["100"]=="열린 재료","재료 선택 목록도 열린 원본에서 수집");
  TestState.OpenTargets.Clear();
  Check(sources.Count==5,"현재+링크 통합, 반복 배치된 동일 파일 중복 제거");
  Check(sources.Count(s=>!s.Editable)==2,"미로드와 중첩 모델의 사유 보존");
  var parents=sources.Where(s=>s.Editable).Select(s=>s.Rows.Single(r=>r.IsFamily)).ToList();
  Check(parents.Select(r=>r.Key).Distinct().Count()==3&&parents.All(r=>r.UniqueId=="1"),"동일 UniqueId라도 모델 키로 분리");
  Check(sources[0].Rows.Count(r=>r.ParentId=="1")==245,"패밀리 하위 모든 유형 연결");
  var w=new UnifiedEditWindow(sources,false);
  Check(Field<List<UnifiedRow>>(w,"_display").Count==4,"처음 모든 모델의 패밀리가 같은 목록에 표시");
  Click(w,"전체 선택");Check(parents.All(r=>r.Selected)&&!sources[0].Rows.Any(r=>r.ParentId=="1"&&r.Selected),"전체 선택은 접힌 유형 제외");
  parents[0].Expanded=true;Call(w,"Refresh");
  Check(Field<List<UnifiedRow>>(w,"_display").Count==249,"패밀리 펼침 및 200개 페이징");
  Capture(w,"expanded-before-edit.png");
  var childrenCheck=Children<CheckBox>((DependencyObject)w.Content).Single(c=>c.Content?.ToString()?.StartsWith("하위 유형만")==true);
  parents[0].Selected=false;childrenCheck.RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
  Check(sources[0].Rows.Where(r=>r.ParentId=="1").All(r=>r.Selected)&&!parents[0].Selected,"하위 전체 선택은 부모 제외·페이지 밖 포함");
  parents[0].Expanded=false;Call(w,"Refresh");
  Check(sources[0].Rows.Count(r=>r.ParentId=="1"&&r.Selected)==245,"접어도 유형 선택 유지");
  Field<TextBox>(w,"_first").Text="유형";Field<TextBox>(w,"_second").Text="수정";Call(w,"Stage");
  Check(sources[0].Rows.Count(r=>r.ParentId=="1"&&r.Working.StartsWith("수정"))==245,"접힌 선택 유형에도 일괄 변경");
  Check(host.GetElement(new ElementId(10))!.Name=="유형_0","창 안 편집은 실제 문서 미변경");
  Check(a.GetElement(new ElementId(10))!.Name=="유형_0","같은 ID의 다른 링크 요소 미변경");
  parents[0].Expanded=true;Call(w,"Refresh");Capture(w,"unified-namer.png");
  Capture(w,"unified-namer-min.png",1040,620);
  Field<ComboBox>(w,"_model").SelectedIndex=2;
  Check(Field<List<UnifiedRow>>(w,"_display").All(r=>r.Source.Key==sources[1].Key),"모델 필터가 소속 기준으로 분리");
  Check(sources[0].Rows.Count(r=>r.Changed)==245,"필터 변경 후 누적 편집 유지");
  w.Close();
  Check(UnifiedEditLogic.Rename("ABC",0,"B","x")=="AxC","치환");
  Check(UnifiedEditLogic.Rename("ABC",1,"2","x")=="AxBC","위치 삽입");
  Check(UnifiedEditLogic.Rename("A-B-C",2,"-","")=="B-C-A","구분자 자리바꾸기");
  Check(UnifiedEditLogic.Rename("ABCDE",3,"2","4")=="AE","구간 삭제");
  for(int mode=0;mode<6;mode++)Check(UnifiedEditLogic.Matches("ABC","",mode),"빈 검색어 전부 표시 "+mode);
  var edit=sources[1].Rows.Single(r=>r.IsFamily);edit.Working="새 이름";
  UnifiedEditService.Apply(a,new(){edit},false,false,new());
  Check(a.GetElement(new ElementId(1))!.Name=="새 이름"&&host.GetElement(new ElementId(1))!.Name=="문_자동문","적용 문서 안에서만 UniqueId 재해석");
  Throws(()=>UnifiedEditService.Apply(a,new(){edit},false,false,new()),"조회 이후 이름 변경은 재적용 차단");
  a.Elements.RemoveAll(e=>e.UniqueId==edit.UniqueId);Throws(()=>UnifiedEditService.Apply(a,new(){edit},false,false,new()),"삭제된 요소는 전체 사전 검증 실패");

  host=Doc("현재");a=Doc("구조");Link(host,a,"구조.rvt",500);
  foreach(var d in new[]{host,a}){
   var type=(ElementType)d.GetElement(new ElementId(10))!;
   type.Parameters.Add(new Parameter{Id=new(55),Value=new(100)});
   d.Elements.Add(new FamilyInstance{Id=new(200),Symbol=(FamilySymbol)type,Parameters=new(){new Parameter{Id=new(56),Value=new(100)}}});
   d.Elements.Add(new FamilyInstance{Id=new(201),Symbol=(FamilySymbol)type,Parameters=new(){new Parameter{Id=new(56),Value=new(101)}}});
  }
  sources=UnifiedEditService.Collect(host,true);var rows=sources.SelectMany(s=>s.Rows).ToList();
  Check(rows.Count==4&&rows.Count(r=>r.IsInstance)==2,"모델별 유형 슬롯·인스턴스 그룹 분리");
  Check(rows.Where(r=>r.IsInstance).All(r=>r.Original=="<다양함>"&&r.OriginalMaterials.Count==2),"모델별 인스턴스 다양함 표시");
  Check(UnifiedEditLogic.MatchesMaterials(rows.First(r=>r.IsInstance),"금속",0)&&!UnifiedEditLogic.MatchesMaterials(rows.First(r=>r.IsInstance),"금속",1),"다양함 내부 검색 및 부정 검색");
  foreach(var row in rows)row.Selected=true;
  var choices=new Dictionary<string,string>{{sources[0].Key,"101"}};
  Check(!UnifiedEditLogic.StageMaterials(rows,choices,out _)&&rows.All(r=>!r.Changed),"한 모델의 재료 선택 누락 시 창 안 적용도 전체 취소");
  choices[sources[1].Key]="100";Check(UnifiedEditLogic.StageMaterials(rows,choices,out _),"모델별 명시적 재료 선택 일괄 적용");
  Check(rows.Where(r=>r.Source.IsHost).All(r=>r.NewMaterial=="101")&&rows.Where(r=>!r.Source.IsHost).All(r=>r.NewMaterial=="100"),"모델별 지정 재료 혼선 없음");
  w=new UnifiedEditWindow(sources,true);Capture(w,"unified-material.png");Capture(w,"unified-material-min.png",1040,620);w.Close();
  TestState.Reset();
  var materialRow=rows.First(r=>r.Source.IsHost&&r.IsInstance);
  host.GetElement(new ElementId(200))!.Parameters[0].Value=new(101);
  Throws(()=>UnifiedEditService.Apply(host,new(){materialRow},true,false,new()),"조회 후 기존 재료 변경은 덮어쓰기 차단");
  Check(TestState.ApplyCalls==0,"재료 검증 실패는 쓰기 함수 호출 이전");
  host.GetElement(new ElementId(200))!.Parameters[0].Value=new(100);
  host.Elements.Add(new FamilyInstance{Id=new(202),Symbol=(FamilySymbol)host.GetElement(new ElementId(10))!,Parameters=new(){new Parameter{Id=new(56),Value=new(100)}}});
  Throws(()=>UnifiedEditService.Apply(host,new(){materialRow},true,false,new()),"인스턴스 범위 증가도 자동 확장하지 않고 차단");
  host.Elements.RemoveAll(e=>e.Id.Value==202);
  Check(UnifiedEditService.Apply(host,new(){materialRow},true,false,new())==Result.Succeeded&&host.GetElement(new ElementId(200))!.Parameters[0].Value==new ElementId(101),"검증된 인스턴스 그룹 전체 재료 반영");
  Batch(false,"");Batch(true,"");Batch(true,"B");
  Console.WriteLine("ALL "+count+" UNIFIED TESTS PASSED");
 }
 static object? Invoke(object o,string n,params object[] args)=>o.GetType().GetMethod(n,BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(o,args);
 static void ProjectInformationRegression(){
  var doc=Doc("프로젝트 정보");var info=new ProjectInfo{Id=new(1000),Name="정보"};doc.Elements.Add(info);
  Parameter Param(int id,string name,string text,bool readOnly=false)=>new(){Id=new(id),Definition=new(){Name=name,DataType=new("text")},StorageType=StorageType.String,Text=text,IsReadOnly=readOnly};
  info.Parameters.Add(Param(-1,"프로젝트 이름","Sunny 병원"));info.Parameters.Add(Param(-2,"프로젝트 번호",""));
  info.Parameters.Add(Param(-3,"읽기 전용","보존",true));info.Parameters.Add(Param(70,"중복 이름","A"));info.Parameters.Add(Param(71,"중복 이름","B"));
  info.Parameters.Add(new(){Id=new(72),Definition=new(){Name="예 아니요",DataType=SpecTypeId.Boolean.YesNo},StorageType=StorageType.Integer,Number=1});
  info.Parameters.Add(new(){Id=new(73),Definition=new(){Name="정수",DataType=SpecTypeId.Int.Integer},StorageType=StorageType.Integer,Number=0});
  var source=new UnifiedSource{Key="host",IsHost=true,Name="현재 프로젝트"};source.Rows.AddRange(ProjectInformationService.Read(doc,source));
  var rows=source.Rows;Check(rows.Count==7&&rows.Select(r=>r.Key).Distinct().Count()==7,"모든 정보 매개변수·동명 항목 고유 키 보존");
  var blank=rows.Single(r=>r.Family=="프로젝트 번호");Check(blank.Original==""&&blank.Display(blank.Original)=="없음"&&!blank.Changed,"없음은 표시만 하고 원시 빈 값 보존");
  var readonlyRow=rows.Single(r=>r.Family=="읽기 전용");Check(!readonlyRow.Editable,"읽기 전용 항목도 표시하지만 편집 금지");
  var w=new UnifiedEditWindow(new(){source},false);Field<ComboBox>(w,"_category").SelectedIndex=8;
  Check(Field<List<UnifiedRow>>(w,"_display").Count==7,"통합 NAMER 프로젝트 정보 카테고리");Click(w,"전체 선택");Check(!readonlyRow.Selected&&blank.Selected,"전체 선택은 읽기 전용 제외");
  Field<TextBox>(w,"_search").Text="없음";Check(Field<List<UnifiedRow>>(w,"_display").Single()==blank,"빈 항목 없음 검색");
  Field<ComboBox>(w,"_mode").SelectedIndex=4;Field<TextBox>(w,"_first").Text="P-100";Call(w,"Stage");Check(blank.Working=="P-100"&&info.Parameters[1].Text=="","값 전체 지정은 창 안에만 누적");
  Field<TextBox>(w,"_search").Clear();Capture(w,"project-info.png");Capture(w,"project-info-min.png",1040,620);w.Close();
  var logs=new List<ChangeLogEntry>();ProjectInformationService.Apply(doc,new(){blank},logs);Check(info.Parameters[1].Text=="P-100"&&logs.Count==1,"빈 프로젝트 정보 작성·커밋 후 기록");
  Throws(()=>ProjectInformationService.Apply(doc,new(){blank},new()),"조회 이후 프로젝트 정보 변경 감지");
  rows=ProjectInformationService.Read(doc,source);var name=rows.Single(r=>r.Family=="프로젝트 이름");name.Working="";ProjectInformationService.Apply(doc,new(){name},new());Check(info.Parameters[0].Text=="","텍스트 삭제는 없음 문자열을 저장하지 않음");
  var b=rows.Single(r=>r.Family=="예 아니요");b.Working="maybe";Check(ProjectInformationService.InputError(b)!=null,"잘못된 예 아니요 입력 차단");b.Working="아니요";ProjectInformationService.Apply(doc,new(){b},new());Check(info.Parameters[5].Number==0,"예 아니요는 정수형으로 저장");
  var integer=rows.Single(r=>r.Family=="정수");integer.Working="";Check(ProjectInformationService.InputError(integer)!=null,"숫자 항목 빈 값 차단");
  rows=ProjectInformationService.Read(doc,source);var duplicates=rows.Where(r=>r.Family=="중복 이름").ToList();duplicates[0].Working="changed";ProjectInformationService.Apply(doc,new(){duplicates[0]},new());Check(info.Parameters.Count(p=>p.Text=="changed")==1&&info.Parameters[4].Text=="B","동명 매개변수 중 정확한 ID 하나만 변경");
  rows=ProjectInformationService.Read(doc,source);name=rows.Single(r=>r.Family=="프로젝트 이름");name.Working="rollback";blank=rows.Single(r=>r.Family=="프로젝트 번호");blank.Working="failure";info.Parameters[1].FailSet=true;logs.Clear();
  Throws(()=>ProjectInformationService.Apply(doc,new(){name,blank},logs),"여러 프로젝트 정보 중 실패 시 예외");Check(info.Parameters[0].Text==""&&logs.Count==0,"중간 실패는 앞 변경도 롤백하고 로그 없음");info.Parameters[1].FailSet=false;
  var old=new NamerWindow(doc,new());old.CategoryProjectInformationRadio.IsChecked=true;
  var panel=Field<ProjectInformationPanel>(old,"_projectPanel");Check(panel.Visibility==Visibility.Visible,"현재 모델 전용 NAMER에도 별도 프로젝트 정보 카테고리");
  var root=(FrameworkElement)old.Content;((System.Windows.Controls.Grid)root).Background=Theme.WindowBackground;root.Measure(new Size(752,680));root.Arrange(new Rect(0,0,752,680));root.UpdateLayout();
  var bitmap=new RenderTargetBitmap(780,708,96,96,PixelFormats.Pbgra32);bitmap.Render(root);var enc=new PngBitmapEncoder();enc.Frames.Add(BitmapFrame.Create(bitmap));using(var stream=File.Create("project-info-current.png"))enc.Save(stream);
  old.CategoryFamilyRadio.IsChecked=true;Check(panel.Visibility==Visibility.Collapsed,"기존 패밀리 분류로 되돌아가기");old.Close();
  source.Materials["m1"]="콘크리트 C30";source.Materials["m2"]="Concrete C40";var other=new UnifiedSource{Key="other",Name="링크"};other.Materials["m1"]="철근";
  w=new UnifiedEditWindow(new(){source,other},true);var pickers=Field<StackPanel>(w,"_materialChoices");var lines=pickers.Children.OfType<WrapPanel>().ToList();
  var search=lines[0].Children.OfType<TextBox>().Single();var combo=lines[0].Children.OfType<ComboBox>().Single();var otherCombo=lines[1].Children.OfType<ComboBox>().Single();otherCombo.SelectedIndex=0;
  search.Text="cOnCr";Check(combo.Items.Count==1&&combo.Items[0].ToString()=="Concrete C40"&&combo.SelectedIndex==-1,"재료 이름 부분 검색·대소문자 무시·자동 지정 없음");
  combo.SelectedIndex=0;search.Text="con";Check(combo.SelectedIndex==0,"검색 결과에 남은 명시적 선택 유지");search.Text="없음";Check(combo.Items.Count==0&&!Field<Dictionary<string,string>>(w,"_choices").ContainsKey("host"),"검색에서 제외된 재료의 숨은 지정 제거");
  Check(Field<Dictionary<string,string>>(w,"_choices")["other"]=="m1","한 모델 재료 검색은 다른 모델 선택 보존");search.Text="콘크";Check(combo.Items.Count==1,"한국어 재료 부분 검색");Capture(w,"material-search.png");Capture(w,"material-search-min.png",1040,620);w.Close();
 }
 static void UiRegression(){
  var host=Doc("현재",245);Link(host,Doc("링크"),"링크.rvt",500);
  var sources=UnifiedEditService.Collect(host,false);var w=new UnifiedEditWindow(sources,false);
  var first=sources[0].Rows.Single(r=>r.IsFamily);first.Expanded=true;Call(w,"Refresh");Capture(w,"ui-before.png");
  var display=Field<List<UnifiedRow>>(w,"_display");
  Invoke(w,"BeginDrag",display[1],false);Invoke(w,"ContinueDrag",display[4]);Invoke(w,"ContinueDrag",display[2]);Call(w,"EndDrag");
  Check(display.Skip(1).Take(4).All(r=>r.Selected)&&!first.Selected,"드래그 건너뛴 행 포함·되돌아가도 선택 유지·부모 독립");
  Invoke(w,"BeginDrag",display[6],true);Call(w,"EndDrag");
  Check(display.Skip(1).Take(6).All(r=>r.Selected),"Shift 클릭 범위 선택");
  Invoke(w,"BeginDrag",display[1],false);Invoke(w,"ContinueDrag",display[6]);Call(w,"EndDrag");
  Check(display.Skip(1).Take(6).All(r=>!r.Selected),"드래그 해제는 고정된 해제 상태");
  display[1].Selected=true;Call(w,"RefreshPreview");
  var firstBox=Field<TextBox>(w,"_first");var secondBox=Field<TextBox>(w,"_second");firstBox.Text="유형";secondBox.Text="새 이름";
  Check((string)Invoke(w,"PreviewName",display[1])! == display[1].Working.Replace("유형","새 이름")&&!display[1].Changed,"입력 즉시 미리보기·누적값은 미변경");
  Check((string)Invoke(w,"PreviewName",display[2])! == display[2].Working,"미선택 항목은 입력 미리보기 제외");
  var grids=Children<System.Windows.Controls.Grid>((DependencyObject)w.Content).Where(g=>g.Tag!=null).ToList();
  var rowGrid=grids[1];var visual=rowGrid.Tag;
  var preview=Field<TextBlock>(visual,"Preview");Check(preview.Text.Contains("새 이름"),"TextChanged가 실제 행 미리보기 갱신");
  var header=Field<System.Windows.Controls.Grid>(w,"_header");double previous=header.ColumnDefinitions[3].ActualWidth;
  header.ColumnDefinitions[3].Width=new GridLength(previous+40);Call(w,"SyncColumns");
  Check(Math.Abs(rowGrid.ColumnDefinitions[3].Width.Value-header.ColumnDefinitions[3].ActualWidth)<0.1,"헤더 열 너비를 실제 행에 동기화");
  Check(Children<GridSplitter>(header).Count()==3,"소속 모델·패밀리·이름 경계 조절 핸들 제공");
  first.Selected=false;
  grids[0].RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonDownEvent});
  Check(first.Selected&&Field<bool>(w,"_dragging"),"실제 행 MouseDown 이벤트가 선택·드래그 시작에 연결");
  Field<StackPanel>(w,"_items").RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
  Check(!Field<bool>(w,"_dragging"),"MouseUp 이벤트로 드래그 종료");first.Selected=false;
  header.ColumnDefinitions[2].Width=new GridLength(180);header.UpdateLayout();
  Children<GridSplitter>(header).First().RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(20,0){RoutedEvent=System.Windows.Controls.Primitives.Thumb.DragDeltaEvent});
  Check(Math.Abs(rowGrid.ColumnDefinitions[2].Width.Value-header.ColumnDefinitions[2].ActualWidth)<0.1,"GridSplitter DragDelta 이벤트가 행 너비 동기화에 연결");
  Invoke(w,"BeginInline",visual);var box=Field<TextBox>(visual,"Editor");box.Text="수동 이름";Invoke(w,"CommitInline",true);
  Check(display[1].Working=="수동 이름"&&display[1].Changed,"직접 편집 확정은 해당 행만 누적");
  Invoke(w,"BeginInline",visual);Field<TextBox>(visual,"Editor").Text="취소";Invoke(w,"CommitInline",false);
  Check(display[1].Working=="수동 이름","직접 편집 취소 보존");
  firstBox.Text="수동";secondBox.Text="확정";Call(w,"Stage");
  Check(display[1].Working=="확정 이름"&&firstBox.Text==""&&!display[1].Selected,"창 안 적용 후 입력/선택 초기화·중복 변환 방지");
  display[1].Selected=true;firstBox.Text="확정";secondBox.Text="미저장";Capture(w,"live-preview.png");
  Check(display[1].Working=="확정 이름"&&(string)Invoke(w,"PreviewName",display[1])! == "미저장 이름","누적 변경과 입력 중 예시 이름 분리");
  display[1].Selected=false;firstBox.Clear();secondBox.Clear();
  var limit=w.GetType().GetField("_limit",BindingFlags.NonPublic|BindingFlags.Instance)!;limit.SetValue(w,400);Call(w,"Render");Capture(w,"ui-restored.png");
  var scroll=Field<ScrollViewer>(w,"_scroll");scroll.ScrollToVerticalOffset(500);scroll.UpdateLayout();double offset=scroll.VerticalOffset;Call(w,"Stage");scroll.UpdateLayout();
  Check((int)limit.GetValue(w)! == 400&&Math.Abs(scroll.VerticalOffset-offset)<1,"적용 후 펼친 페이지·스크롤 위치 유지");
  Capture(w,"ui-restored-min.png",1040,620);w.Close();
 }
 static void ScheduleSelectionRegression(){
  var data=new ScheduleEditorSnapshot{Name="범위 선택 검증"};
  data.Fields.Add(new(){Id="text",Name="주석"});data.Fields.Add(new(){Id="other",Name="두 번째 필드"});
  for(int n=1;n<=260;n++)data.Rows.Add(new(){Number=n,Models=n%2==0?"링크 모델":"현재 모델",ElementCount=1,Cells={{"text","기존 값 "+n},{"other","다른 값"}}});
  var w=new ScheduleEditorWindow(data){ShowActivated=false,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.Manual,Left=-10000,Top=-10000};
  w.Show();Capture(w,"selection.png",1180,780);
  var range=Field<TextBox>(w,"_range");var selected=Field<HashSet<int>>(w,"_selected");var rows=Field<StackPanel>(w,"_rows");
  var scroll=Field<ScrollViewer>(w,"_scroll");var checks=Field<Dictionary<int,CheckBox>>(w,"_checks");
  var firstVisual=rows.Children[0];
  void Begin(int n,bool shift=false)=>Invoke(w,"BeginDrag",n,shift);
  void Move(int n)=>Invoke(w,"ContinueDrag",n);
  void End()=>Call(w,"EndDrag");
  void ClickButton(string text)=>Children<Button>((DependencyObject)w.Content).First(b=>b.Content?.ToString()==text).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
  Begin(3);Move(8);Move(5);End();
  Check(selected.SetEquals(Enumerable.Range(3,6)),"일람표 드래그 건너뛰기·되짚기 고정 선택");
  Check(range.Text=="3-8"&&checks.Where(p=>p.Value.IsChecked==true).Select(p=>p.Key).ToHashSet().SetEquals(selected),"선택→압축 행 범위·체크 시각 동기화");
  Check(ReferenceEquals(firstVisual,rows.Children[0]),"선택 중 행 트리 재생성 없음");
  Begin(5);Move(7);Move(6);End();Check(selected.SetEquals(new[]{3,4,8}),"체크된 행에서 드래그하면 고정 해제");
  Begin(10,true);End();Check(selected.SetEquals(Enumerable.Range(3,8)),"Shift 클릭은 마지막 클릭 행부터 범위 선택");
  Begin(4,true);End();Check(selected.SetEquals(new[]{3}),"체크된 행 Shift 클릭은 범위 해제");
  range.Text="2,4-6,250-260";Check(selected.Count==15&&checks[2].IsChecked==true&&!checks.ContainsKey(250),"직접 입력은 페이지 밖까지 선택");
  Check(ReferenceEquals(firstVisual,rows.Children[0])&&Field<int>(w,"_anchor")==0,"직접 입력은 행 보존·이전 Shift 기준 초기화");
  Begin(8,true);End();Check(selected.Count==16&&!selected.Contains(7),"기준 없는 Shift는 클릭 행만 선택");
  range.Text="1-";Check(selected.Count==0&&checks.Values.All(c=>c.IsChecked==false)&&Field<string>(w,"_rangeError").Length>0,"잘못된 입력은 옛 선택을 숨겨 유지하지 않음");
  ClickButton("미리보기에 적용");Check(data.Pending.Count==0&&Field<TextBlock>(w,"_status").Text.StartsWith("적용하지 않았습니다"),"잘못된 행 입력으로 적용 차단");
  Begin(12);End();Check(range.Text=="12"&&Field<string>(w,"_rangeError")=="","행 클릭으로 잘못된 입력을 유효한 선택으로 교체");
  ClickButton("200행 더 보기");Check(Field<List<System.Windows.Controls.Grid>>(w,"_grids").Count==260&&selected.SetEquals(new[]{12}),"더 보기 이후 기존 선택 유지");
  Begin(240,true);End();Check(selected.SetEquals(Enumerable.Range(12,229)),"Shift 범위는 200행 페이지 경계를 포함");
  Field<ComboBox>(w,"_field").SelectedIndex=1;Check(selected.Count==229&&Field<Dictionary<int,CheckBox>>(w,"_checks")[220].IsChecked==true,"필드 전환 후 범위·페이지 유지");
  ClickButton("전체 선택");Check(selected.Count==260&&range.Text=="1-260","전체 선택은 모든 데이터 행 포함");
  ClickButton("선택 해제");Check(selected.Count==0&&range.Text=="","전체 해제와 범위 입력 동기화");
  // 실제 창의 라우팅 이벤트 연결을 검증한다. OS 마우스 입력/실제 Revit 검증은 아니다.
  var grid=Field<List<System.Windows.Controls.Grid>>(w,"_grids")[1];
  var down=new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=System.Windows.Input.Mouse.MouseDownEvent};
  grid.Children.OfType<TextBlock>().First().RaiseEvent(down);
  Check(selected.SetEquals(new[]{2})&&down.Handled,"행의 텍스트 셀 MouseDown 버블링이 한 번만 선택");
  rows.RaiseEvent(new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0,System.Windows.Input.MouseButton.Left){RoutedEvent=UIElement.MouseLeftButtonUpEvent});
  Check(!Field<bool>(w,"_dragging")&&!Field<DispatcherTimer>(w,"_dragTimer").IsEnabled,"MouseUp은 드래그·타이머 종료");
  checks=Field<Dictionary<int,CheckBox>>(w,"_checks");checks[2].IsChecked=false;checks[2].RaiseEvent(new RoutedEventArgs(CheckBox.ClickEvent));
  Check(selected.Count==0&&checks[2].IsChecked==false,"키보드/접근성 체크 클릭은 이중 반전 없이 해제");
  Begin(5);checks[5].RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.LostMouseCaptureEvent});
  Check(Field<bool>(w,"_dragging"),"자식 컨트롤 캡처 상실은 드래그 취소하지 않음");
  rows.RaiseEvent(new System.Windows.Input.MouseEventArgs(System.Windows.Input.Mouse.PrimaryDevice,0){RoutedEvent=System.Windows.Input.Mouse.LostMouseCaptureEvent});
  Check(!Field<bool>(w,"_dragging"),"목록 자체 캡처 상실은 드래그 종료");
  Begin(6);Invoke(w,"UpdateDrag",new System.Windows.Point(10,30),false);Check(!Field<bool>(w,"_dragging"),"창 밖에서 버튼을 놓은 경우도 드래그 종료");
  Begin(7);range.Text="20-22";Check(!Field<bool>(w,"_dragging")&&selected.SetEquals(new[]{20,21,22}),"직접 입력이 진행 중인 드래그를 종료");
  Begin(8);Field<ComboBox>(w,"_field").SelectedIndex=0;Check(!Field<bool>(w,"_dragging"),"필드 전환은 진행 중인 드래그 종료");
  Capture(w,"selection-min.png",950,600);scroll.ScrollToVerticalOffset(0);scroll.UpdateLayout();range.Clear();Begin(1);
  var targetGrid=Field<List<System.Windows.Controls.Grid>>(w,"_grids")[4];
  var point=targetGrid.TranslatePoint(new System.Windows.Point(30,targetGrid.ActualHeight/2),scroll);
  Invoke(w,"UpdateDrag",point,true);Check(selected.SetEquals(Enumerable.Range(1,5)),"실제 행 히트테스트로 중간 행까지 선택");
  double offset=scroll.VerticalOffset;Invoke(w,"UpdateDrag",new System.Windows.Point(30,scroll.ViewportHeight-2),true);scroll.UpdateLayout();
  Check(scroll.VerticalOffset>offset,"아래 끝 드래그 자동 스크롤");
  offset=scroll.VerticalOffset;Invoke(w,"UpdateDrag",new System.Windows.Point(30,1),true);scroll.UpdateLayout();Check(scroll.VerticalOffset<offset,"위 끝 드래그 자동 스크롤");End();
  range.Text="5-12";scroll.ScrollToVerticalOffset(600);scroll.UpdateLayout();offset=scroll.VerticalOffset;Call(w,"Render");scroll.UpdateLayout();
  Check(Math.Abs(scroll.VerticalOffset-offset)<1&&selected.Count==8&&Field<int>(w,"_limit")==400,"다시 그리기 후 페이지·스크롤·선택 보존");
  Check(data.Pending.Count==0&&data.Rows[0].Cells["text"]=="기존 값 1","선택 조작만으로 미리보기/원본 값 변경 없음");
  Begin(3);w.Close();Check(!Field<bool>(w,"_dragging")&&!Field<DispatcherTimer>(w,"_dragTimer").IsEnabled,"창 닫기 후 드래그·타이머 정리");
 }
 static void ScheduleRegression(){
  Check(ScheduleEditorLogic.ParseRows("1-3,2,5",5).SetEquals(new[]{1,2,3,5}),"일람표 행 범위·중복 제거");
  foreach(string bad in new[]{"","0","3-2","1-6","a","1,","1--2"})Throws(()=>ScheduleEditorLogic.ParseRows(bad,5),"잘못된 행 입력 차단: "+bad);
  Check(ScheduleEditorLogic.Key(new[]{"a","bc"})!=ScheduleEditorLogic.Key(new[]{"ab","c"}),"행 식별값 구분자 충돌 방지");
  var mapped=ScheduleEditorLogic.Match(new[]{"B","A"},new[]{"A","A","B"},false);Check(mapped[0].SequenceEqual(new[]{2})&&mapped[1].SequenceEqual(new[]{0,1}),"원래 표시 순서로 묶인 행의 전체 요소 연결");
  Throws(()=>ScheduleEditorLogic.Match(new[]{"A","A"},new[]{"A","A"},true),"중복 개별 행은 순서로 추측하지 않고 차단");
  Throws(()=>ScheduleEditorLogic.Match(new[]{"A"},new[]{"A","B"},false),"필터 대상과 표시 행 불일치 차단");
  Throws(()=>ScheduleEditorLogic.Match(new[]{"A"},new[]{"A","A"},true),"개별 행 한 개에 여러 요소 연결 차단");
  var doc=Doc("일람표 현재");var type=(FamilySymbol)doc.GetElement(new ElementId(10))!;
  type.Parameters.Add(new(){Id=new(-12),Definition=new(){Name="유형 비용",DataType=new("number")},StorageType=StorageType.Double,Double=50});
  Parameter TextParam(int id,string name,string text)=>new(){Id=new(id),Definition=new(){Name=name,DataType=new("text")},StorageType=StorageType.String,Text=text};
  var instances=new List<Element>();for(int i=0;i<3;i++){var e=new FamilyInstance{Id=new(201+i),Symbol=type};e.Parameters.Add(TextParam(-10,"정렬 마크",i<2?"A":"B"));e.Parameters.Add(TextParam(-11,"메모","기존"));doc.Elements.Add(e);instances.Add(e);}
  var view=new ViewSchedule{Id=new(3000),Name="문 목록",Owner=doc,Candidates=instances,Counts=new(){2,1},NativeRows=new(){new(){{"1","A"},{"2","기존"},{"3","50"}},new(){{"1","B"},{"2","기존"},{"3","50"}}}};
  view.Definition.IsItemized=false;view.Definition.Fields=new(){new(){FieldId=new(1),ParameterId=new(-10),FieldType=ScheduleFieldType.Instance,ColumnHeading="마크",IsHidden=true},new(){FieldId=new(2),ParameterId=new(-11),FieldType=ScheduleFieldType.Instance,ColumnHeading="메모"},new(){FieldId=new(3),ParameterId=new(-12),FieldType=ScheduleFieldType.ElementType,ColumnHeading="유형 비용"}};
  view.Definition.Sort.Add(new(){FieldId=new(1)});doc.Elements.Add(view);var app=new UIApplication{ActiveUIDocument=new(){Document=doc}};
  var data=ScheduleEditorService.Read(app,view);Check(data.Rows.Count==2&&data.Rows[0].ElementCount==2&&data.Fields.Count==2,"숨은 정렬 필드 유지·묶인 행 요소 수 검증");
  Check(doc.Elements.All(e=>e.Id.Value!=99000)&&view.Definition.ShowHeaders&&view.Definition.Sort[0].ShowHeader&&view.Definition.Fields[0].IsHidden,"임시 복제는 롤백·원본 일람표 설정 보존");
  var field=data.Fields[0];ScheduleEditorLogic.Stage(data,field,new(){1},"일괄 입력");Check(data.Pending.Count==2&&data.Rows[0].Targets[field.Id].All(r=>r.Working=="일괄 입력")&&data.Rows[1].Targets[field.Id].Single().Working=="기존","묶인 행 전체 수정·다른 행 미변경");
  Check(instances.All(e=>e.Parameters[1].Text=="기존"),"일람표 미리보기는 실제 매개변수 미변경");
  ScheduleEditorLogic.Stage(data,data.Fields[1],new(){1},"75");Check(data.Pending.Count==3&&data.Rows[1].Targets["3"].Single().Working=="75","동일 유형은 한 번만 누적하고 다른 행에 영향 표시");
  var before=data.Pending.Count;Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[1],new(){1,2},""),"숫자 빈 값 일괄 지정 차단");Check(data.Pending.Count==before&&data.Rows[0].Targets["3"].Single().Working=="75","잘못된 지정은 기존 미리보기 전체 보존");
  var w=new ScheduleEditorWindow(data);Field<TextBox>(w,"_range").Text="1";Capture(w,"schedule-editor.png",1180,780);Field<ComboBox>(w,"_field").SelectedIndex=1;Capture(w,"schedule-editor-min.png",950,600);w.Close();
  ScheduleEditorService.ValidateGuards(doc,data,"host");var logs=new List<ChangeLogEntry>();ProjectInformationService.Apply(doc,data.Pending.Values.ToList(),logs,true);
  Check(instances.Take(2).All(e=>e.Parameters[1].Text=="일괄 입력")&&instances[2].Parameters[1].Text=="기존"&&type.Parameters[0].Double==75&&logs.All(l=>l.Kind==ChangeKind.ScheduleParameter),"일람표 값 실제 대상에 반영·별도 변경 기록");
  instances[0].Parameters[0].Text="바뀐 마크";Throws(()=>ScheduleEditorService.ValidateGuards(doc,data,"host"),"조회 후 정렬/그룹 값 변경은 실제 쓰기 전 차단");instances[0].Parameters[0].Text="A";
  view.Counts[0]=1;Throws(()=>ScheduleEditorService.Read(app,view),"Revit 행 개수와 요소 개수 불일치 차단");Check(doc.Elements.All(e=>e.Id.Value!=99000),"조회 실패 시에도 임시 복제 제거");view.Counts[0]=2;
  var linked=Doc("링크 중앙");var linkedType=(FamilySymbol)linked.GetElement(new ElementId(10))!;linkedType.Parameters.Add(new(){Id=new(-12),Definition=new(){Name="유형 비용",DataType=new("number")},StorageType=StorageType.Double,Double=100});
  var le=new FamilyInstance{Id=new(201),Symbol=linkedType};le.Parameters.Add(TextParam(-10,"정렬 마크","C"));le.Parameters.Add(TextParam(-11,"메모","링크 기존"));linked.Elements.Add(le);Link(doc,linked,"링크.rvt",500);
  view.Definition.IncludeLinkedFiles=true;view.LinkedCandidates[501]=new(){le};view.NativeRows.Add(new(){{"1","C"},{"2","링크 기존"},{"3","100"}});view.Counts.Add(1);
  data=ScheduleEditorService.Read(app,view);Check(data.Rows.Count==3&&data.Rows[2].Models.Contains("링크")&&data.Rows[2].Targets["2"].Single().Source.Key!="host","링크 일람표 행을 실제 소속 모델로 분리");
  ScheduleEditorLogic.Stage(data,data.Fields[0],new(){3},"링크 수정");ScheduleEditorService.ValidateGuards(linked,data,data.Pending.Values.First().Source.Key);ProjectInformationService.Apply(linked,data.Pending.Values.ToList(),new(),true);
  Check(le.Parameters[1].Text=="링크 수정"&&instances[0].Parameters[1].Text=="일괄 입력","같은 ElementId여도 링크 문서의 값만 변경");
  data.Fields[0].Reason="계산식";Throws(()=>ScheduleEditorLogic.Stage(data,data.Fields[0],new(){1},"x"),"읽기 전용 계산식 수정 차단");
 }
}
