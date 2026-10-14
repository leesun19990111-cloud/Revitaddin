// 기존 91개 계층/재료 회귀 검사 전용. 새 기능의 실제 코드는 unified-v100에서 별도로 검증한다.
namespace WallSplitter {
 internal class UnifiedRow {public bool Changed;}
 internal class UnifiedSource {internal string Key="",Name="",Path="",BlockReason="";internal bool IsHost;}
 internal static class ProjectInformationService {
  internal static List<UnifiedRow> Read(Autodesk.Revit.DB.Document doc,UnifiedSource source)=>new();
  internal static string? InputError(UnifiedRow row)=>null;
 }
 internal class ProjectInformationPanel:System.Windows.Controls.Grid {
  internal ProjectInformationPanel(List<UnifiedRow> rows,Action changed){} internal void Render(){} internal void Stage(){}
 }
}
