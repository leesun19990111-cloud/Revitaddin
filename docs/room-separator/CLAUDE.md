# 룸 구분선 자동 생성 (RoomSeparator)

`RoomSeparatorCommand.cs` / `RoomSeparatorWindow.xaml(.cs)` / `RoomSeparatorService.cs`를 건드리기 전에 이 문서를 읽을 것.

## 무엇을 하는 기능인가

2026-09-06 사용자 요청: *"지정한 벽체 유형의 중심선에 맞춰서(LINK모델 포함) 룸 구분선을 자동으로 넣어주는
기능을 추가하고 싶다."* 고른 벽 유형에 해당하는 벽을 고른 레벨마다 찾아, 그 벽의 **중심선** 위에 룸
구분선(`OST_RoomSeparationLines`)을 만든다. 링크된 모델의 벽도 대상이다(구분선 자체는 언제나 호스트 모델에 생긴다).

Q&A로 확정한 범위(2026-09-06):
- **레벨은 여러 개를 한 번에** 고른다(현재 뷰 하나만이 아니라).
- **진짜 벽 중심선으로 보정한다** — 벽 위치선이 마감면/코어면이어도 두께를 계산해 옮긴다.
- **재실행하면 무조건 추가한다** — 기존 구분선을 지우지 않는다(되돌리기는 Revit의 Ctrl+Z).

## 구조

- `RoomSeparatorService` — Revit 조회/생성 **전부**. 창에는 Revit 조회 코드를 두지 않는다는 이 프로젝트의
  관례를 따른다.
- `RoomSeparatorWindow` — 고르는 일만 한다. 목록은 Revit 타입이 아니라 평범한 데이터(`WallTypeChoice`,
  `LevelChoice`)로 받는다 — **그래야 스크래치패드 하네스에서 창을 그대로 렌더해 볼 수 있다**(`Level`은 Revit
  문서 없이 만들 수 없어서, 그대로 뒀다면 목록이 빈 채로만 확인됐을 것이다). 이 분리를 되돌리지 말 것.
- `RoomSeparatorCommand` — `TransactionMode.Manual`. 창이 모달이고, 트랜잭션은 "만들기"를 누른 시점에
  창이 직접 연다(그냥 닫으면 아무것도 바뀌지 않는다).

## 중심선 보정 — 이 기능의 핵심

벽의 `LocationCurve`는 **위치선(Location Line)**이지 중심선이 아니다. 유형마다 위치선이 마감면/코어면일 수
있어, 그대로 쓰면 구분선이 벽 두께의 절반만큼 어긋난다.

`CenterlineShift(totalWidth, widthBeforeCore, coreWidth, locationLine)`가 그 계산이며 **일부러 Revit 타입을
쓰지 않는 순수 계산**이다(층별 단면상자의 `RejectOutliersAndUnion`과 같은 이유 — 하네스에서 리플렉션으로
직접 돌려 검증할 수 있게).

좌표 약속: 벽 **외부면에서 안쪽으로** 잰 거리를 `d`라 한다(외부면 d=0, 내부면 d=W). 중심선은 d=W/2.
`Wall.Orientation`이 **외부를 향한 법선**이므로, 위치선에서 중심선으로 가려면 그 법선 방향으로 `d - W/2`만큼
옮긴다(음수면 안쪽). `WALL_KEY_REF_PARAM` 값별 d는 코드의 switch 그대로다.

- **부호를 반대로 잡으면 구분선이 벽 두께만큼 통째로 밀려 생긴다.** Revit을 띄우지 않고 이걸 잡는 유일한
  방법이 위 순수 계산 + 리플렉션 테스트라, 스크래치패드 `ApiProbe`에 9가지 경우를 넣어 두고 확인했다
  (대칭/비대칭 다층 벽, 마감면·코어면 6종, 모르는 값 fallback).
- `CompoundStructure`가 없는 벽(커튼월 등)은 코어 = 벽 전체로 보고 계산한다.
- 위치선이 이미 중심선(가장 흔한 경우)이면 아무것도 하지 않고 그대로 쓴다.

## 링크 모델 처리

- 중심선 보정은 **링크 문서 좌표계에서** 먼저 한다 — `Wall.Orientation`과 곡선이 같은 좌표계여야 하기
  때문이다. 호스트 좌표로 옮기는 것(`GetTotalTransform`)은 그 다음 한 번에 한다. **순서를 바꾸지 말 것.**
- 링크 벽이 "이 레벨의 벽"인지는 **이름이 아니라 높이**로 판단한다. 링크의 레벨 높이를 호스트 좌표로 환산해
  (`transform.OfPoint(new XYZ(0,0,linkLevel.Elevation)).Z`) 호스트 레벨 높이와 `LinkLevelToleranceFeet`(1ft)
  안에서 비교한다. 링크가 위아래로 옮겨져 배치돼 있을 수 있으므로 링크 문서의 높이를 그대로 비교하면 틀린다.
- 언로드된 링크는 `GetLinkDocument()`가 null이라 조용히 건너뛴다.

## 만들 때 주의한 것들

- **룸 구분선은 평면뷰에서만 만들 수 있다.** 레벨마다 그 레벨의 평면뷰(`ViewPlan.GenLevel`)를 찾아 쓰고,
  하나도 없으면 그 레벨은 건너뛰고 결과 창에 알린다. 지금 보고 있는 뷰가 그 레벨의 평면뷰면 그것을 쓴다.
- **곡선을 레벨 높이로 눕힌다.** 벽의 베이스 간격띄우기 때문에 위치선이 레벨보다 위/아래에 있을 수 있는데,
  스케치 평면 밖의 곡선은 만들 수 없다. 벽 위치선은 항상 수평이라 Z만 평행이동하면 된다.
- **한 레벨분을 한 번에 만들고, 실패하면 하나씩 다시 시도한다.** 곡선 하나가 잘못돼 전체가 통째로 실패하면
  "아무것도 안 생긴다"가 되기 때문이다. 하나씩 시도에서 실패한 것만 건너뛴 개수로 보고한다.
- 벽 유형은 호스트와 링크에 같은 이름이 있으면 **한 줄로 합쳐서** 보여준다 — 사용자는 "이 이름의 벽"을
  고르는 것이고, 이 프로젝트가 대상을 늘 이름으로 다시 찾는 방침과도 같다.

## 검증

- **API 실측**: `NewRoomBoundaryLines`, `SketchPlane.Create`, `Plane.CreateByNormalAndOrigin`,
  `RevitLinkInstance.GetTotalTransform/GetLinkDocument`, `Curve.CreateTransformed`, `WallType.GetCompoundStructure`,
  `CompoundStructure.GetLayers/GetFirstCoreLayerIndex/GetLastCoreLayerIndex`, `WALL_KEY_REF_PARAM`,
  `OST_RoomSeparationLines` 등이 **2023~2027 참조 어셈블리에 모두 존재**함을 `MetadataLoadContext`로 확인했다
  (스크래치패드 `ApiProbe`). net48 참조 어셈블리는 net8에서 `Assembly.LoadFrom` 할 수 없으므로 반드시
  MetadataLoadContext를 쓸 것.
- **중심선 계산**: 빌드된 DLL에 리플렉션으로 9가지 경우를 돌려 전부 통과(`CENTERLINE: ALL OK`).
- **창 렌더**: 스크래치패드 `RspPreview`가 실제 XAML/코드비하인드를 그대로 쓰고 생성자만 가짜 데이터로
  바꿔 렌더한다(`rsp_regen.sh`). 캡처할 때 `root.ActualWidth`가 아니라 **`DesiredSize`**(마진 포함)로
  비트맵을 잡아야 오른쪽이 잘리지 않는다 - 잘린 그림을 보고 "레이아웃이 깨졌다"고 오해하기 쉽다.
