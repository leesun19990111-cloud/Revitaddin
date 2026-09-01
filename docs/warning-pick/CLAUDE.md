# 경고Pick (Warning Pick)

`WarningPick*.cs`를 건드리기 전에 이 문서를 읽을 것.

Added 2026-08-25 on request: Revit 기본 경고 대화상자는 경고를 클릭하면 뷰에서 하이라이트만 해 주고,
"표시"를 눌러도 그 뷰로 안내만 할 뿐 요소를 실제로 선택해 주지는 않아 직접 찾아 클릭해야 하는 불편함이
있었다. `Document.GetWarnings()`로 모은 경고의 실패 요소(`FailureMessage.GetFailingElements()`)를
행 단위로 펼쳐 보여주고, 행의 "선택" 버튼 한 번으로 `Selection.SetElementIds` + `ShowElements`를 함께
호출해 "뷰 이동 + 실제 선택"을 한 번에 끝낸다.

- **모드리스(Show), 모달(ShowDialog) 아님 — 의도적 선택.** 사용자에게 직접 확인: "경고를 고치려면 선택
  후에도 모델을 계속 조작(수정·삭제)할 수 있어야 하는가"에 "그렇다"는 답을 받고 결정했다. 모달로
  만들면 Revit 메인 창이 비활성화되어 요소를 선택해도 편집할 수 없어 기능 가치가 사라진다. 이 프로젝트의
  다른 창들(NamerWindow, MaterialAssignWindow 등)은 전부 커맨드 `Execute()` 안의 `ShowDialog()`라
  비동기 콜백이 필요 없었지만, 경고Pick은 `QuickToggleToolbar`와 같은 이유로 세션 중 계속 열려 있어야
  해서 `WarningPickExternalEventHandler`(`IExternalEventHandler`)가 필요하다 — 창의 버튼 클릭 시점엔
  유효한 Revit API 컨텍스트가 없으므로 `ExternalEvent.Raise()`로 다음 기회에 실행되게 위임한다
  (선택 실행, 목록 새로고침 둘 다 같은 이벤트를 재사용 — `QuickToggleExternalEventHandler`의 패턴 그대로).
- **문서 불일치 가드 — `ReferenceEquals`로 `Document`를 비교하지 말 것.** 창을 열어 둔 채 사용자가 다른
  문서로 전환하면, 그 문서에서 원래 문서의 `ElementId` 정수값으로 선택을 시도하면 안 된다(ElementId는
  문서마다 독립적이라 같은 정수가 전혀 다른 요소를 가리킬 수 있음). 처음엔 `TargetDocument`와
  `app.ActiveUIDocument.Document`를 `ReferenceEquals`로 비교했는데, **CONFIRMED LIVE BUG (2026-08-25)**:
  문서를 하나만 열어 두고 뷰조차 바꾸지 않았는데도 "선택"을 누를 때마다 "문서가 더 이상 활성 문서가
  아닙니다"가 떴다 — `Document`는 Revit API 래퍼 객체라, 창을 연 시점(`WarningPickCommand.Execute`)과
  `ExternalEvent`가 실제 실행되는 시점에 각각 조회한 게 같은 열린 문서를 가리켜도 서로 다른 래퍼
  인스턴스일 수 있어 참조 비교가 항상 false로 나올 수 있다. `WarningPickExternalEventHandler.DocKey`
  (경로, 저장 안 된 문서는 제목)로 문서를 식별하는 값 비교로 고쳤다 — `QuickToggleToolbar.DocKey`와 같은
  이유·같은 패턴. `Document`를 다시 `ReferenceEquals`나 `==`로 비교하는 코드를 추가하지 말 것.
- **`ElementId.IntegerValue`를 직접 쓰지 말 것.** 2023 API에만 있고 2024+에서는 `Value`(long)로 완전히
  대체되어 컴파일 자체가 깨진다(실측 확인, `QuickToggleSettings.cs`의 `ElementIdCompat.ToInt()` 참고).
  `WarningPickElement.IdText`는 그 확장 메서드(`ElementId.ToInt()`)를 그대로 재사용한다.
- **`Grid` 이름 충돌.** `Autodesk.Revit.DB.Grid`(그리드 라인 요소)와 `System.Windows.Controls.Grid`가
  같은 이름이라, `WarningPickWindow.xaml.cs`처럼 두 네임스페이스를 동시에 `using`하는 코드비하인드에서는
  `using WpfGrid = System.Windows.Controls.Grid;` 별칭이 필요하다(`QuickToggleToolbar.xaml.cs`의
  `RevitView`/`RevitDocument` 별칭과 같은 이유 — `docs/design-system/CLAUDE.md` 참고).
- **종류(상위)-발생 건(중위)-요소(하위) 3단 구조, 2026-08-25 재설계(2차).** 처음엔 (경고 × 요소)를
  평평하게 펼친 `WarningPickRow` 목록 → 경고(상위)-요소(하위) 2단(`WarningPickGroup`) 순으로 넓혔다가,
  "경고 유형에 따라 상위요소로 한 번 더 분류해 달라"는 요청으로 3단이 됐다: `WarningPickTypeGroup`(경고
  종류) → `WarningPickGroup`(그 종류의 발생 건 하나) → `WarningPickElement`(그 발생 건에 얽힌 요소).
  종류를 나누는 기준은 `FailureMessage.GetFailureDefinitionId().Guid`다 — 겹침·조인처럼 "쌍(pair)"
  단위인 경고는 Revit이 같은 종류를 발생 건마다 별도 `FailureMessage`로 쪼개 내보내는 경우가 흔해서
  (예: 벽 A-B, B-C, A-C가 서로 겹치면 "벽이 겹칩니다"라는 큰 경고 하나가 아니라 페어별 발생 건 3개가
  생김), 발생 건을 평평하게 늘어놓으면 같은 문구가 반복되어 "같은 종류구나"를 알아보기 어려웠다.
  종류 이름(`TypeLabel`)은 대표로 첫 발생 건의 설명 문구를 쓴다(같은 `FailureDefinitionId`는 보통 요소별
  고유값 없이 같은 생성 문구를 재사용하므로 대부분 정확하고, 드물게 다르더라도 발생 건 항목에서 실제
  문구를 다시 보여주므로 오해할 일은 없다). `WarningPickGroup.TryBuild`(발생 건 하나 빌드)는
  `GetFailingElements()`(직접 원인)와 `GetAdditionalElements()`(같이 얽힌 상대 요소, 예: 겹친 벽의
  반대쪽 벽)를 구분하지 않고 ID 기준으로 합쳐서 자식 목록을 만든다 — "이 경고에 어떤 요소들이 얽혀
  있는지"가 중요하지 실패/부가 구분이 중요한 게 아니기 때문. 실제 트리 컨트롤(`TreeView`) 대신 들여쓰기한
  `StackPanel` 중첩으로 구현했다 — `Theme.xaml`에 `TreeView`/`TreeViewItem` 스타일이 없어서 그대로 쓰면
  기본 WPF 흰 배경이 이 창의 밝은 테마와도 미묘하게 어긋나는데, 이 프로젝트의 다른 목록(NamerWindow
  등)도 전부 실제 `ListBox`/`TreeView`가 아니라 코드로 그린 `StackPanel` 행이라 그 관례를 따랐다.
  요소 목록은 기본적으로 접혀 있고("하위 요소는 드롭다운으로 펼쳐야 보이게" 요청), 발생 건 헤더의 "▸"를
  누르면 `WarningPickWindow.BuildOccurrencePanel`이 만든 `elementsPanel`의 `Visibility`가 토글된다.
  **`Visibility.Collapsed`/`Visible`을 `Window`(사실은 `UIElement`) 상속 멤버 안에서 그냥 쓰면 CS0176**
  이 난다 — `Window`에도 인스턴스 속성 `Visibility`가 있어서 타입이 아니라 `this.Visibility`로 해석되기
  때문에, 이 파일에서는 항상 `System.Windows.Visibility.Collapsed`처럼 완전한 이름을 써야 한다.
- **다중 선택 - 체크박스 3단 + 드래그/쉬프트 범위.** 종류·발생 건·요소 세 레벨 모두 체크박스가 있고,
  상위 체크박스는 그 아래 전체를 한꺼번에 켜고 끈다(종류 체크 → 발생 건들 체크 → 각 발생 건 체크가 다시
  요소들을 체크하는 연쇄). "체크한 요소 선택"/단면상자/격리 버튼은 `WarningPickWindow._elementCheckboxes`
  (매 렌더링마다 다시 채워지는, 화면 위→아래 순서 그대로인 체크박스-요소 짝 목록)에서 체크된 것만 모은다.
  **"전체 선택"/"전체 해제"는 반드시 모든 레벨의 체크박스를 담은 `_allCheckboxes`를 순회해야 한다** -
  처음엔 요소 체크박스만 껐다 켜서, "전체 해제"를 눌러도 종류/발생 건 체크박스는 계속 체크된 채로 남는
  라이브 버그가 있었다(2026-08-25 확인, 상위 체크박스는 하위를 향한 캐스케이드만 있고 하위 상태를 보고
  스스로 갱신하는 역방향 로직이 없었기 때문 - 애초에 상위도 같이 명시적으로 꺼야 했다).
  요소 행 자체도 NamerWindow의 드래그 체크(`ItemsPanel_MouseMove` 등)와 같은 패턴으로 클릭/드래그
  다중 체크를 지원한다 - 체크박스는 `IsHitTestVisible=false`로 시각 표시 전용이고, 행(Grid)의
  `Tag`에 `WarningPickElement`를 담아 `GroupsPanel` 전체에서 히트테스트로 찾는다(중첩 깊이와 무관하게
  `VisualTreeHelper.GetParent`로 위로 훑어 올라가면 되므로 3단 구조에서도 그대로 작동). 추가로 쉬프트+
  클릭 범위 선택(`ApplyRangeCheck`)을 넣었다 - `_elementCheckboxes`의 인덱스 순서(화면 순서와 동일)로
  두 클릭 사이를 전부 체크하며, 접힌 발생 건 안의 요소도 인덱스상 범위에 들면 함께 체크된다(탐색기 쉬프트
  선택과 같은 감각 - 펼치지 않아도 범위에 들어간 요소는 체크됨). "선택" 버튼만은 행 클릭 핸들러보다 먼저
  살아남아야 해서 `IsWithinButton`으로 클릭 지점이 버튼 안인지 먼저 걸러낸 뒤 행 로직을 실행한다.
- **체크박스 옆 "흰 막대" 버그 — `FocusVisualStyle` 누락.** 체크박스에 라벨(Content)이 없다 보니, WPF
  기본 포커스 사각형 어도너가 체크박스 옆 빈 공간까지 그려져 이상한 흰 막대처럼 보이는 라이브 버그가
  있었다(2026-08-25 확인). `Theme.xaml`의 `Button` 스타일(`BaseButtonStyle`)은 이미
  `FocusVisualStyle="{x:Null}"`로 꺼뒀는데 `CheckBox`/`RadioButton` 스타일엔 빠져 있었다 - 둘 다 추가해
  고쳤다. 새 컨트롤 스타일을 추가할 때(특히 라벨 없이 아이콘/체크박스만 쓰는 경우) 이 셋터를 빼먹지 말 것.
- **단면상자 보기 / 임시 격리.** "체크한 요소 단면상자로 보기"는 활성 뷰가 `View3D`가 아니면 뷰를 마음대로
  바꾸지 않고 그냥 안내만 한다(사용자가 보던 뷰를 도구가 몰래 바꾸는 걸 피하려는 의도적 설계 — 필요하면
  나중에 "3D 뷰가 없으면 새로 만들어서 전환" 옵션을 추가할 수 있음, `WarningPickExternalEventHandler.
  ExecuteSectionBox` 참고). 체크된 요소들의 바운딩박스는 로컬 좌표계일 수 있어(패밀리 인스턴스 등)
  `BoundingBoxXYZ.Transform`으로 8개 모서리를 실좌표로 변환한 뒤 min/max를 넓힌다(`ExpandByElementBoundingBox`)
  — 코너 변환 없이 `Min`/`Max`만 그대로 쓰면 회전된 인스턴스에서 박스가 어긋난다. "체크한 요소만 표시"는
  `View.IsolateElementsTemporary`(임시 숨기기/격리, "적용"하지 않는 한 이 뷰를 다시 열면 초기화되는 세션
  한정 상태)를 쓰고, "격리 해제"는 `View.DisableTemporaryViewMode(TemporaryViewMode.TemporaryHideIsolate)`로
  되돌린다 — 둘 다 모델 형상/파라미터는 전혀 건드리지 않는 뷰 전용 상태지만, Revit API 관례상 View 상태를
  바꾸는 호출은 트랜잭션 안에서 해야 해서 각각 전용 트랜잭션으로 감쌌다.
- **"경고 자동 해결" 기능은 없음 — 사용자가 명시적으로 보류 결정 (2026-08-25).** 사용자가 "경고를 요소 삭제 없이
  자동으로 해결해 달라"고 요청했으나, 조사 결과 Revit 공개 API로는 이미 커밋되어 `Document.GetWarnings()`
  목록에 들어간 "과거" 경고를 프로그램적으로 해결/삭제할 방법이 없다 — `FailuresAccessor`(경고를
  `ResolveFailures`/`DeleteWarning`하는 데 필요한 유일한 통로)는 오직 `IFailuresPreprocessor.
  PreprocessFailures` 콜백 안에서만 얻을 수 있고, 이 콜백은 그 트랜잭션에서 실제로 재검증되어 "새로
  발생한" 실패에만 열린다. 이미 지나간 경고를 다시 그 콜백에 띄우려면 원인이 된 요소들을 강제로 다시
  regenerate시켜야 하는데(예: 0벡터 이동으로 "건드리기"), 어떤 요소를 어떻게 건드려야 그 경고가
  재검증되는지는 경고 종류마다 달라 일반화할 수 없다. 게다가 이 프로젝트에는 트랜잭션에 커스텀
  `IFailuresPreprocessor`를 붙였다가 원인 불명의 666회 재처리 루프로 전체 롤백된 확인된 라이브 버그
  이력이 있다(`NamerCommand.cs` 참고) — 이 기능을 구현하게 되면 반드시 그 코드부터 읽고, 실제 구현
  전 사용자와 합의한 안전장치(예: 실제 적용 전 미리보기/시험 실행, 전용 트랜잭션 격리, 삭제형 해결책
  스킵, `PreprocessFailures`는 항상 `FailureProcessingResult.Continue`로 끝내기)를 그대로 지킬 것.
  이 설계안을 사용자에게 설명한 뒤(위험성·불확실성 포함), **사용자가 지금은 넣지 말자고 결정했다** — 요청
  없이 먼저 구현하지 말 것. 나중에 다시 요청이 오면 이 설계안부터 재검토.
- 아이콘은 별도 PNG 없이 `App.CreateWarningIcon`이 실행 시점에 그린다(패턴 스튜디오 아이콘과 같은 방식).
- **ExternalEvent 예외 차단 (2026-09-01, 선제 보강)**: `WarningPickExternalEventHandler.Execute`의 본문을
  `ExecuteCore`로 옮기고 최상위에서 예외를 잡아 `TaskDialog`로 알린다. `IExternalEventHandler.Execute`에서
  예외가 밖으로 나가면 Revit은 사용자에게 아무 것도 보여주지 않아 "버튼을 눌러도 반응이 없다"는 증상으로만
  남는다(`docs/quick-toggle/CLAUDE.md`에 기록된 것과 같은 문제). 문서 불일치·요소 삭제 같은 예상 가능한 경우는
  기존처럼 각 `Execute*` 메서드가 직접 안내하고, 이 최상위 catch는 예상 못 한 예외 전용이다.
