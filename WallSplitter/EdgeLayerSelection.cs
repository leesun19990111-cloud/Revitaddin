using System;
using Autodesk.Revit.UI;

namespace WallSplitter
{
    // 한 층만 떼어낼 때는 원본의 남은 층을 한 요소로 유지할 수 있는 양 끝만 고른다.
    internal static class EdgeLayerSelection
    {
        internal static int? Choose(string kind, string typeName,
            string firstMaterial, double firstWidthMm, string lastMaterial, double lastWidthMm,
            int layerCount)
        {
            var dialog = new TaskDialog("한 레이어 분리")
            {
                MainInstruction = $"{kind} 유형 '{typeName}'에서 떼어낼 끝 레이어를 선택하세요.",
                MainContent = $"같은 유형의 선택 요소에는 같은 선택을 적용합니다. 가운데 레이어는 원래 위치를 유지하며 한 요소로 남길 수 없어 이번 버전에서 제외합니다. (총 {layerCount}층)",
                CommonButtons = TaskDialogCommonButtons.Cancel,
            };
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink1,
                kind == "벽" ? "외측 첫 레이어" : "맨 위 첫 레이어",
                $"{firstMaterial} · {firstWidthMm:0.###} mm");
            dialog.AddCommandLink(TaskDialogCommandLinkId.CommandLink2,
                kind == "벽" ? "내측 마지막 레이어" : "맨 아래 마지막 레이어",
                $"{lastMaterial} · {lastWidthMm:0.###} mm");
            dialog.DefaultButton = TaskDialogResult.Cancel;
            var answer = dialog.Show();
            if (answer == TaskDialogResult.CommandLink1) return 0;
            if (answer == TaskDialogResult.CommandLink2) return layerCount - 1;
            return null;
        }
    }
}
