namespace SunnyParameterManager.Core;

// 구버전/응답 불명/저장 후 실패는 절대로 다음 모델 진행 승인을 요청하지 않는다.
public static class ApplyContinuation
{
    public static bool CanOffer(Response result) => result.CanContinueAfterPreflightFailure && !result.Success &&
        !result.RequiresAttention && !result.CentralSynchronized && result.Applied == 0 &&
        !result.ReadRestartRequired && !result.CanRestartForRead;
}
