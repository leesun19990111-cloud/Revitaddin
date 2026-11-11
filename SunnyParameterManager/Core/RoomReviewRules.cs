namespace SunnyParameterManager.Core;

// 룸 검토는 값을 쓰지 않는다. 이름은 실제 Revit 매개변수명이며 순서는 모델명·이름을 제외한 표의 열 순서다.
public static class RoomReviewRules
{
    public static readonly string[] Fields = ["용도", "지상지하", "층", "설명", "번호", "실번호", "기존 실번호",
        "실명", "자산번호", "LOD", "공간분류코드", "구역", "실ID", "실내재료마감-걸레받이",
        "실내재료마감-바닥", "실내재료마감-벽", "실내재료마감-천장", "작성기준"];

    public static void Validate(RuleSet? file)
    {
        if (file?.Version != 12 || !file.RoomReview || string.IsNullOrWhiteSpace(file.Name) || file.SampleOnly ||
            file.Rules?.Count != 0 || file.RoomUse != null || file.Kac != null || file.Space != null || file.Spec != null ||
            file.Cleanup || file.MaterialAudit || file.TypeCodeFix || file.ProjectBasePointAudit || file.RebuildCentral ||
            file.ProjectParameterTransfer || file.PreserveWorksets?.Count != 0)
            throw new InvalidOperationException("룸 검토 룰셋은 버전12의 읽기 전용 RoomReview만 사용합니다.");
    }
}
