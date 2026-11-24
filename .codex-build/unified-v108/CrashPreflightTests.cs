using System.Windows;
using System.Windows.Threading;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using WallSplitter;
partial class Program {
 static void CrashPreflightRegression(){
  TestState.Reset();TestState.OpenTargets.Clear();
  var host=Doc("현재");var a=Doc("A");var b=Doc("B");
  Link(host,a,"A",500);Link(host,b,"B",600);
  TestState.OpenTargets["A"]=a;
  var app=new UIApplication{ActiveUIDocument=new(){Document=host}};
  var sources=UnifiedEditService.Collect(host,false,app);
  Throws(()=>UnifiedEditService.Preflight(app,sources),"뒤 링크가 열리지 않았으면 전체 사전 검사 거부");
  Check(TestState.LinkCalls==0&&TestState.ApplyCalls==0&&TestState.Events.Count==0,"사전 실패는 앞 링크와 현재 모델 모두 미변경");
  UnifiedEditService.Preflight(app,sources.Where(s=>s.IsHost));
  Check(TestState.Events.Count==0,"변경하지 않는 링크가 닫혀 있어도 현재 모델만 편집 허용");
  Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,new Action(()=>{
   var w=Application.Current.Windows.OfType<UnifiedEditWindow>().Single();
   foreach(var row in Field<List<UnifiedSource>>(w,"_sources").SelectMany(s=>s.Rows).Where(r=>r.IsFamily))row.Working="변경";
   w.DialogResult=true;
  }));
  var result=UnifiedEditService.Execute(app,false,()=>Result.Cancelled);
  Check(result==Result.Cancelled&&TestState.LinkCalls==0&&TestState.Confirmations==0&&TestState.Events.Count==0,"실제 통합 명령도 최종 확인/첫 모델 쓰기 전에 전체 차단");
  Check(TestState.Report.Contains("아무 모델에도")&&TestState.Report.Contains("B"),"미변경 범위와 준비할 모델을 명확히 안내");
  TestState.OpenTargets["B"]=b;
  UnifiedEditService.Preflight(app,sources);
  Check(TestState.LinkCalls==0&&TestState.Events.Count==0,"모든 열린 원본 준비 검사도 읽기 전용");
  host.IsReadOnly=true;
  Throws(()=>UnifiedEditService.Preflight(app,sources),"현재 모델 읽기 전용이면 첫 링크 쓰기 전 중단");
  host.IsReadOnly=false;sources[1].Path="다른경로";
  Throws(()=>UnifiedEditService.Preflight(app,sources),"링크 조회 경로 변경은 전체 사전 차단");
  TestState.OpenTargets.Clear();TestState.Reset();
 }
}
