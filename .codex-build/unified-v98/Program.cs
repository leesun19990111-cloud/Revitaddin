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
 static void Capture(UnifiedEditWindow w,string path,int width=1240,int height=800){
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
   Check(TestState.Events.SequenceEqual(new[]{"A"}),"중간 실패면 성공한 링크만 반영, 현재 모델 미처리");
   Check(TestState.Report.Contains("미처리")&&TestState.Report.Contains("동기화 완료")&&TestState.Report.Contains("실패"),"성공·실패·미처리 동시 보고");
  }else Check(result==Result.Succeeded&&TestState.Events.SequenceEqual(new[]{"A","B","현재"}),"모든 링크 순차 적용 후 현재 모델 마지막 반영");
 }
 [STAThread] static void Main(){
  new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
  UiRegression();
  var host=Doc("현재 프로젝트",245);var a=Doc("구조");var b=Doc("건축");
  Link(host,a,"구조.rvt",500);Link(host,a,"구조.rvt",600);Link(host,b,"건축.rvt",700);Link(host,null,"미로드.rvt",800);Link(host,Doc("중첩"),"중첩.rvt",900,true);
  var sources=UnifiedEditService.Collect(host,false);
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
}
