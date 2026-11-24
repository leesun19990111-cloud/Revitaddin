using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;

partial class Program
{
 static Document InfoDoc(string title,string value)
 {
  var doc=Doc(title);var info=new ProjectInfo{Id=new(1000),Name="프로젝트 정보"};doc.Elements.Add(info);
  info.Parameters.Add(new(){Id=new(-1),Definition=new(){Name="프로젝트 이름",DataType=new("text")},StorageType=StorageType.String,Text=value});
  info.Parameters.Add(new(){Id=new(-2),Definition=new(){Name="프로젝트 번호",DataType=new("text")},StorageType=StorageType.String,Text=""});
  info.Parameters.Add(new(){Id=new(-3),Definition=new(){Name="읽기 전용",DataType=new("text")},StorageType=StorageType.String,Text="보존",IsReadOnly=true});
  return doc;
 }
 static void LinkedProjectInformationRegression()
 {
  TestState.Reset();TestState.OpenTargets.Clear();
  var host=InfoDoc("현재 프로젝트","현재값");var cacheA=InfoDoc("건축 캐시","옛 건축값");var cacheB=InfoDoc("구조 캐시","옛 구조값");
  var openA=InfoDoc("열린 건축 로컬","최신 건축값");var openB=InfoDoc("열린 구조 로컬","최신 구조값");
  Link(host,cacheA,"info-A.rvt",500);Link(host,cacheA,"info-A.rvt",600);Link(host,cacheB,"info-B.rvt",700);
  TestState.OpenTargets["info-A.rvt"]=openA;TestState.OpenTargets["info-B.rvt"]=openB;
  var app=new UIApplication{ActiveUIDocument=new(){Document=host}};
  var sources=UnifiedEditService.Collect(host,false,app);
  var project=sources.SelectMany(s=>s.Rows).Where(r=>r.ProjectValue!=null).ToList();
  Check(project.Count==9&&project.Select(r=>r.Key).Distinct().Count()==9,"현재+두 링크 정보 모두 표시·반복 링크 중복 제거·동일 ID 모델별 분리");
  Check(project.Any(r=>r.Original=="최신 건축값")&&project.Any(r=>r.Original=="최신 구조값")&&!project.Any(r=>r.Original.StartsWith("옛")),"링크 캐시가 아닌 열린 로컬의 최신 프로젝트 정보 조회");
  var w=new UnifiedEditWindow(sources,false);var family=sources[0].Rows.Single(r=>r.IsFamily);family.Working="보존할 패밀리 변경";
  Field<ComboBox>(w,"_model").SelectedIndex=1;Field<TextBox>(w,"_search").Text="보이지 않는 필터";
  Field<TextBox>(w,"_first").Text="옛 입력";
  Click(w,"프로젝트 정보 · 모든 모델");
  Check(Field<List<UnifiedRow>>(w,"_display").Count==9&&Field<ComboBox>(w,"_model").SelectedIndex==0,"바로가기는 모든 모델의 정보 표시·이전 모델/검색 필터 제거");
  Check(Field<ComboBox>(w,"_mode").SelectedIndex==4&&Field<TextBox>(w,"_first").Text==""&&family.Working=="보존할 패밀리 변경","값 전체 지정 진입·이전 입력 제거·누적 이름 변경 보존");
  Check(Field<TextBlock>(w,"_targetHeader").Text=="정보 항목"&&Field<TextBlock>(w,"_originalHeader").Text=="현재 값"&&Field<TextBlock>(w,"_projectHelp").Visibility==Visibility.Visible,"프로젝트 정보 전용 열 이름과 안전 안내");
  Field<TextBox>(w,"_search").Text="프로젝트 번호";Click(w,"전체 선택");Field<TextBox>(w,"_first").Text="통합-P001";
  Check(project.Where(r=>r.Family=="프로젝트 번호").All(r=>!r.Changed),"실시간 미리보기는 원본·누적 값 미변경");
  Call(w,"Stage");Check(project.Count(r=>r.Changed)==3&&project.Where(r=>r.Changed).All(r=>r.Working=="통합-P001"),"동명 정보 항목을 모델별 선택하여 일괄 값 지정");
  Field<TextBox>(w,"_search").Clear();Click(w,"전체 선택");
  Check(project.Where(r=>r.Family=="읽기 전용").All(r=>!r.Selected),"링크 프로젝트 정보에서도 실제 읽기 전용 항목 선택 제외");
  Capture(w,"linked-project-info.png");Capture(w,"linked-project-info-min.png",1040,620);
  Field<ComboBox>(w,"_category").SelectedIndex=2;
  Check(Field<TextBlock>(w,"_targetHeader").Text=="패밀리 / 대상"&&Field<TextBlock>(w,"_projectHelp").Visibility==Visibility.Collapsed,"일반 이름 편집으로 돌아가면 전용 안내/열 복원");w.Close();
  Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
   var modal=Application.Current.Windows.OfType<UnifiedEditWindow>().Single();Click(modal,"프로젝트 정보 · 모든 모델");
   var rows=Field<List<UnifiedSource>>(modal,"_sources").SelectMany(s=>s.Rows).Where(r=>r.ProjectValue!=null&&r.Family=="프로젝트 번호");
   foreach(var row in rows)row.Working=row.Source.IsHost?"현재-P001":row.Source.Path=="info-A.rvt"?"건축-P001":"구조-P001";
   modal.DialogResult=true;
  }));
  var result=UnifiedEditService.Execute(app,false,()=>Result.Cancelled);
  Check(result==Result.Succeeded&&TestState.LinkCalls==2,"프로젝트 정보 변경도 두 열린 링크 원본 처리 경로를 통과");
  Check(host.ProjectInformation!.Parameters[1].Text=="현재-P001"&&openA.ProjectInformation!.Parameters[1].Text=="건축-P001"&&openB.ProjectInformation!.Parameters[1].Text=="구조-P001","현재/건축/구조 동일 매개변수 ID에 각자의 값 반영");
  Check(cacheA.ProjectInformation!.Parameters[1].Text==""&&cacheB.ProjectInformation!.Parameters[1].Text=="","링크 캐시는 직접 수정하지 않음");
  Check(openA.ProjectInformation!.Parameters[0].Text=="최신 건축값"&&openA.ProjectInformation.Parameters[2].Text=="보존","선택하지 않은 프로젝트 정보 보존");
  TestState.OpenTargets.Remove("info-B.rvt");
  Throws(()=>UnifiedEditService.Preflight(app,sources),"프로젝트 정보도 대상 로컬 하나라도 없으면 전체 사전 검사 차단");
  TestState.Reset();TestState.OpenTargets.Clear();
 }
}
