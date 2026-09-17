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

---

# 룸 경계 ON/OFF (RoomBounding)

`RoomBoundingCommand.cs` / `RoomBoundingWindow.xaml(.cs)` / `RoomBoundingService.cs`. 룸 구분선과 같은 리본
패널("룸 경계")에 나란히 있는 형제 기능이라 이 문서에 함께 적는다.

2026-09-17 사용자 요청: *"모델에 존재하는 모든 벽, 바닥, 지붕, 기초 등등 룸경계를 결정짓는 모델요소들의
룸경계를 ON/OFF 하는 기능도 추가로 있으면 좋을 것 같아."* 요소의 **룸 경계(Room Bounding) 인스턴스
파라미터**를 카테고리 단위로 한 번에 켜고 끈다.

## 카테고리 목록을 하드코딩하지 않는다 — 이 기능의 핵심 결정

룸 경계 파라미터를 가진 카테고리는 벽/바닥/지붕/기초 말고도 천장·기둥·커튼시스템·매스·RVT 링크 등 여러
가지이고 Revit 버전마다 늘어날 수 있다. 그래서 목록을 코드에 적어 두는 대신 **모델 요소를 훑어 그 파라미터를
실제로 가진 것만** 모은다(`CategoryType.Model`인 요소 중 `get_Parameter(WALL_ATTR_ROOM_BOUNDING)`이
`StorageType.Integer`로 존재하는 것). 사용자가 말한 "등등"이 저절로 포함되고, 새 Revit에서 카테고리가 늘어도
코드를 고칠 필요가 없다. **카테고리 화이트리스트로 "최적화"하지 말 것** — 그 순간 이 성질을 잃는다.

- `WALL_ATTR_ROOM_BOUNDING`은 이름이 `WALL_`로 시작하지만 **벽 전용이 아니다**. 바닥·지붕·기둥·링크가 함께
  쓰는 공용 파라미터다(Revit API의 오래된 이름). 이름에 속아 벽만 처리하게 만들지 말 것.
- **RVT 링크가 목록에 자연스럽게 들어온다**: `RevitLinkInstance`도 이 파라미터를 가진 모델 요소라 특별 취급
  없이 잡힌다. 그 값은 "이 링크가 호스트의 방 경계를 만드는가"이고, 링크 **안의** 요소를 바꾸는 게 아니다.
- 읽기 전용인 경우(`IsReadOnly`)는 "변경 불가"로 세어 보여주고 건드리지 않는다.

## 주의한 것들

- **요소 하나가 거부해도 나머지는 계속 처리한다.** `Set`에서 올라온 예외를 그냥 흘려보내면 트랜잭션이 통째로
  취소돼 "아무것도 안 바뀐다"가 된다. 개별 실패는 세어서 결과로 보고한다.
- **이미 그 값인 요소는 건너뛴다**(`AlreadySet`). 쓸데없는 변경으로 워크셋 체크아웃/동기화 부담을 만들지 않기 위함.
- 적용 후 창을 닫지 않고 **목록을 다시 읽어** 바뀐 상태를 그 자리에서 보여준다 - 켜고 끄기를 번갈아 해 보는
  도구라 매번 닫았다 여는 것보다 낫다. 고른 카테고리는 유지된다.
- 목록에서 **"켜짐 0 · 꺼짐 N"인 카테고리는 경고색**으로 보여준다 - "왜 방이 안 나뉘지?"의 원인이 대개 이것이다.
- 한 번의 실행이 트랜잭션 하나라 Ctrl+Z로 한 번에 되돌아간다. 룸 경계를 끄면 방이 합쳐지거나 면적이 달라져
  일람표·태그까지 영향을 받으므로, 창에 그 경고를 눈에 띄게 적어 뒀다.

## 검증

- `WALL_ATTR_ROOM_BOUNDING` / `CategoryType.Model` / `Parameter.IsReadOnly`·`StorageType`·`Set` /
  `Category.CategoryType` / `OST_RvtLinks`가 2023~2027 참조 어셈블리에 모두 존재함을 `ApiProbe`로 확인했다.
- 창은 스크래치패드 `RbPreview`로 렌더 검증한다(`rb_regen.sh` — 실제 XAML/코드비하인드를 그대로 쓰고
  생성자만 가짜 데이터로 바꾼다).
- **DLL 심볼 검증의 함정**: `WALL_ATTR_ROOM_BOUNDING`은 enum 멤버라 **정수 상수로 컴파일되어 문자열로 찾을 수
  없고**, 창 제목 같은 XAML 문자열은 BAML에 들어가 평범한 UTF-16 검색에 안 잡힌다. 배포 검증에는 **C# 코드의
  진짜 문자열 리터럴**(예: 트랜잭션 이름 `룸 경계 켜기`)과 타입 이름을 쓸 것.
