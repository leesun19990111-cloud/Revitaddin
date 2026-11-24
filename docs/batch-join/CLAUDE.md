# 일괄 결합금지/허용 (BatchJoin)

`BatchJoinService.cs` / `BatchJoinWindow.xaml(.cs)` / `BatchJoinCommand.cs` /
`BatchJoinSelectionCommands.cs`를 건드리기 전에 이 문서를 읽을 것.

## 이름

**화면에 보이는 이름은 "일괄 결합금지/허용"이고, 코드의 이름은 `BatchJoin*`이다** — 일부러 다르다.

2026-09-23 처음 낼 때는 화면 이름도 "일괄결합"이었는데, 낸 직후 사용자가 *"일괄결합이라는 게 또 보면
헷갈리는 단어 같다, 일괄 결합금지/허용 이런 식으로 하면 알기 쉬울 것 같다"*고 해서 바꿨다. 실제로
"일괄결합"은 Revit의 **결합(Join Geometry)** 을 한꺼번에 건다는 뜻으로 읽히는데, 이 기능이 하는 일은
정반대에 가까운 **끝단 결합 금지**다. 지금 이름은 동작(금지/허용)을 제목에 드러내 그 오해를 막는다.

- 리본 패널 `일괄 결합금지/허용` / 큰 버튼 `결합 금지·허용`(두 줄) / 작은 버튼 `선택 금지`·`선택 허용`.
- 창 제목과 `TaskDialog` 제목도 `일괄 결합금지/허용`. 실행취소 목록에 보이는 트랜잭션 이름은
  `일괄 결합금지` / `일괄 결합허용`.
- `SunnyToolsCommands.All`의 커스텀 버튼 목록 이름은 `일괄 결합금지/허용`,
  `일괄 결합금지 - 선택 요소`, `일괄 결합허용 - 선택 요소`.
- **클래스·파일 이름(`BatchJoinCommand` 등)은 바꾸지 않았다.** 커스텀 "기능 버튼"은 설정 파일에
  `IExternalCommand` 클래스의 **FullName**으로 저장되므로(`SunnyToolsCommands` 주석 참고), 클래스
  이름을 바꾸면 사용자가 이미 만들어 둔 버튼이 조용히 깨진다. 리본 패널/버튼 이름 쪽은 `App.OnStartup`이
  리본을 다 만든 뒤 실제 `RibbonPanel`/`PushButton`에서 읽어 명령 id를 채우므로 바꿔도 안전하다.

## 무엇을 하는 기능인가

2026-09-23 사용자 요청: *"부재의 끝에 결합되는 요소와의 결합을 일괄적으로 금지 또는 허용을 시켜줄 수
있는 기능이 필요해. 선택한 여러가지 부재들의 결합을 금지/허용 시키거나, 각각의 유형들을 일괄선택해서
결합을 금지/허용 할 수 있도록."*

Revit의 **"결합 허용 안 함"(Disallow Join)** — 벽/보의 **끝**이 맞닿은 부재와 결합되는 것을 막는 설정 —
을 한 번에 건다. Revit 기본 UI에서는 부재 하나의 끝 하나를 오른쪽 클릭해서만 걸 수 있어, 벽이 수백 개인
모델에서는 사실상 손으로 못 하는 작업이다.

**이것은 "결합(Join Geometry)"이 아니다.** 이름이 비슷하지만 `JoinGeometryUtils`(두 솔리드를 서로 파고들게
하는 기하 결합)와는 다른 개념이고, 이 기능은 그쪽을 건드리지 않는다. 여기서 다루는 것은 **선형 부재의
끝단 결합**뿐이다. 나중에 "결합"이라는 말만 보고 `JoinGeometryUtils`를 섞어 넣지 말 것 — 사용자가 말한
"부재의 끝에 결합되는 요소와의 결합"은 정확히 끝단 결합이다.

## 대상은 벽과 구조 프레임(보·가새)뿐이다

Revit은 같은 개념을 **서로 다른 유틸리티 두 개**로 나눠 놨다. 하나로 합쳐진 API는 없다.

| 대상 | API |
|---|---|
| 벽 (`Wall`) | `WallUtils.DisallowWallJoinAtEnd / AllowWallJoinAtEnd / IsWallJoinAllowedAtEnd` |
| 보·가새 (`FamilyInstance`) | `StructuralFramingUtils.DisallowJoinAtEnd / AllowJoinAtEnd / IsJoinAllowedAtEnd` |

끝 번호는 둘 다 `0`(시작) / `1`(끝)이며, 그 부재를 **그릴 때의 방향** 기준이다(위치선의 시작점/끝점).
눈으로 어느 쪽이 시작인지 알기 어려우므로 창의 기본값은 **양쪽 끝**이다.

- **`StructuralFramingUtils`는 보/가새가 아닌 패밀리 인스턴스에 부르면 예외를 던진다.** 그래서 조회조차
  하기 전에 `FamilyInstance.StructuralType`이 `Beam`/`Brace`인지 먼저 거른다(`BatchJoinService.AsFraming`).
  기둥(`Column`)은 끝단 결합 개념이 없으므로 대상이 아니다. **이 필터를 지우지 말 것** — 지우면 일반
  패밀리가 섞인 선택에서 예외가 터진다.
- 기초·바닥·지붕 등에는 이 설정 자체가 없다. "왜 벽만 되냐"는 질문이 나오면 Revit API에 그것뿐이기 때문이다.

## 절대 규칙

- **`BatchJoinService`는 트랜잭션을 열지 않는다.** 부르는 쪽(창의 `Apply`, 선택 명령의 `Run`)이 연다 —
  이 프로젝트의 관례(`RoomBoundingService`, `ParamCombineEngine`와 같다).
- **요소 하나가 거부해도 나머지는 계속 처리한다.** `Set`/`Disallow`에서 올라온 예외를 그냥 흘려보내면
  트랜잭션이 통째로 취소돼 "아무것도 안 바뀐다"가 된다. 개별 실패는 `FailedEnds`로 세어 결과로 보고한다.
  스택 벽의 하위 벽 등이 실제로 거부한다.
- **상태 조회도 실패할 수 있다** — `IsJoinAllowed`는 `bool?`를 돌려주고 `null`이면 "모름/변경 불가"로
  센다. `false`(=금지됨)와 섞어 쓰지 말 것. 모르는 것을 "허용됨"으로 가정하면 잘못된 변경을 하게 된다.
- **이미 그 상태인 끝은 건드리지 않는다**(`AlreadyEnds`). 쓸데없는 변경으로 워크셋 체크아웃/동기화 부담을
  만들지 않기 위함(`RoomBoundingService`와 같은 방침).
- 유형은 **이름이 아니라 유형 `ElementId`**로 다룬다 — 벽과 보에 같은 이름의 유형이 있을 수 있다.
  (이 프로젝트가 다른 기능에서 "이름으로 다시 찾는" 방침을 쓰는 것과 일부러 다르다. 여기서는 한 번의
  모달 창 안에서만 쓰는 식별자라 ElementId가 정확하고 더 안전하다.)

## 구성

- `BatchJoinService` — Revit 조회/변경 **전부**. 창에는 Revit 조회 코드를 두지 않는다는 관례를 따른다.
- `BatchJoinWindow` — 고르는 일만 한다. 대상(지금 선택 / 유형), 적용할 끝(양쪽·시작·끝), 금지/허용.
- `BatchJoinCommand` — 큰 리본 버튼(`결합 금지·허용`). `TransactionMode.Manual`, 모달. 창을 그냥 닫으면 아무것도 바뀌지 않는다.
- `BatchJoinSelectionCommands` — 작은 스택 버튼 두 개(`선택 금지` / `선택 허용`). **창을 열지 않고** 지금
  선택한 요소의 **양쪽 끝**을 바로 처리한다. "여러 개 골라놓고 바로 건다"가 가장 잦은 작업이라 클릭 한
  번으로 끝내기 위한 것. 두 명령은 `BatchJoinSelectionRunner.Run(…, allow)` 하나를 공유한다.
- 세 명령 모두 `SunnyToolsCommands.All`에 등록되어 커스텀 기능 버튼으로도 실행할 수 있다.

## 창에서 신경 쓴 것들

- **창이 모달이라 열려 있는 동안 선택을 바꿀 수 없다.** 그래서 선택은 열리는 시점의 것을 그대로 들고
  있고, 선택이 비어 있으면 그 라디오를 비활성화한 채 **유형 모드로 시작**한다 — 아무것도 못 하는 모드를
  기본으로 두지 않는다.
- **"현재 뷰에 보이는 요소만"** 체크박스로 범위를 좁힐 수 있다(`FilteredElementCollector(doc, view.Id)`).
  끄면 모델 전체다. 이 체크는 **세는 것과 적용하는 것 양쪽에 같이 적용된다** — 목록에 보이는 숫자와 실제
  바뀌는 개수가 어긋나면 안 되기 때문이다.
- 유형마다 **지금 금지 몇 곳 / 허용 몇 곳**인지 보여주고, 전부 금지인 유형은 눈에 띄게 표시한다(다시 걸
  필요가 없다는 뜻).
- **걸러내기(필터)가 걸린 상태의 "전체 선택"은 지금 보이는 유형만 고른다.** 안 보이는 유형까지 몰래 고르면
  위험하다.
- 체크 상태는 화면이 아니라 `_checkedTypeIds`에 둔다 — 걸러내거나 적용 후 목록을 다시 그려도 고른 것이
  풀리지 않는다(`RoomBoundingWindow`와 같은 이유).
- 적용 후 창을 닫지 않고 **목록을 다시 읽어** 바뀐 상태를 그 자리에서 보여준다 — 금지/허용을 번갈아 해
  보는 도구라 매번 닫았다 여는 것보다 낫다.
- **생성자에서 `RadioButton.IsChecked`를 세팅하는 것만으로 `Checked` 핸들러가 불린다.** 그때는 목록이 아직
  없어 `NullReferenceException`이 난다. `IsInitialized`는 `InitializeComponent()` 직후 이미 `true`라 방어가
  안 되므로 `_ready` 플래그를 따로 둔다 — **이 플래그를 지우지 말 것.**

## 검증

- **API 실측**: `WallUtils.{Disallow,Allow}WallJoinAtEnd`/`IsWallJoinAllowedAtEnd`,
  `StructuralFramingUtils.{Disallow,Allow}JoinAtEnd`/`IsJoinAllowedAtEnd`,
  `StructuralType`, `OST_StructuralFraming`이 **2023~2027 참조 어셈블리에 시그니처까지 동일하게 존재**함을
  `MetadataLoadContext`로 확인했다(스크래치패드 `ApiProbe`). net48 참조 어셈블리는 net8에서
  `Assembly.LoadFrom` 할 수 없으므로 반드시 MetadataLoadContext를 쓸 것.
- **창 렌더**: 스크래치패드 `BjPreview`가 실제 XAML/코드비하인드를 그대로 쓰고 Revit 타입만 가짜로 바꿔
  렌더한다(선택 있음/없음 두 가지). 캡처할 때 `root.ActualWidth`가 아니라 **`DesiredSize`**(마진 포함)로
  비트맵을 잡아야 오른쪽이 잘리지 않는다 — 잘린 그림을 보고 "레이아웃이 깨졌다"고 오해하기 쉽다.
- **리본 아이콘**도 같은 하네스로 렌더해 확인했다(금지: 세로 부재가 틈을 두고 끊긴 채 끝면이 강조색으로
  막힘 / 허용: 맞닿은 지점이 강조색으로 이어짐).

## 이 작업에서 같이 고친 것: 테마 CheckBox의 흰 띠

`BjPreview` 렌더에서 **라벨이 있는 CheckBox 뒤에 흰 띠**가 생기는 것을 발견했다. `Theme.xaml`의 CheckBox
템플릿이 `Background`를 그대로 칠하는데 WPF 기본 `Background`가 불투명한 밝은 회색이라, 카드
(`SurfaceBrush`) 위에 올리면 라벨 뒤가 하얗게 떴다. `Theme.xaml`의 CheckBox 스타일에
`<Setter Property="Background" Value="Transparent"/>`를 추가해 고쳤다 — **이 Setter를 지우지 말 것**
(이 창뿐 아니라 매개변수 조합 창 등 라벨 있는 체크박스 전부에 해당한다). 투명이어도 템플릿의 StackPanel이
그 영역을 그대로 hit-test하므로 라벨 클릭은 계속 동작한다.

## 알려진 제한

- 대상은 벽·보·가새뿐이다(Revit API에 그것뿐). 기초·바닥·지붕에는 끝단 결합 설정 자체가 없다.
- 링크 모델의 요소는 바꿀 수 없다(호스트 문서만).
- 작업공유 모델에서 다른 사용자가 소유한 요소는 Revit이 거부하며 "바꾸지 못한 끝"으로 센다.
- "이 끝이 지금 어느 부재와 결합돼 있는가"는 보여주지 않는다 — 금지/허용 상태만 다룬다.
