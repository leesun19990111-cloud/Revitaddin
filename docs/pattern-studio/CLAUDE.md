# 패턴 스튜디오 개발 기록

## 현재 범위

Revit의 기존 비솔리드 채우기 패턴과 `.PAT` 패턴을 같은 내부 모델로 읽어, 독립 WPF 창에서 편집한 뒤 새 Revit 패턴 또는 PAT 파일로 저장한다. 2026-08-21 확장판부터 모델선 캡처와 패턴 타공을 같은 `패턴` 리본 패널의 별도 버튼으로 제공한다. 다음 기능은 아직 넣지 않는다.

- 빈 화면에서 선/원/호/곡선을 직접 그리는 새 패턴 제작기
- CAD 링크 선을 직접 파싱하거나 자체 CAD 트레이서 창에서 따라 그리는 기능 (현재는 Revit 모델선 또는 상세선으로 따라 그린 뒤 캡처)
- 벽·바닥의 특정 면에서만 모델 패턴 위치나 각도를 맞추는 기능 (후속 별도 리본 버튼)

## 주요 파일

- `PatternDefinition.cs` — Revit/PAT 공통 패턴 모델, 전체/선군별 편집 상태, 저장 요청
- `PatternTransformService.cs` — 전체 회전·균일 스케일·폭/높이와 선군별 회전·크기·간격 변환
- `PatFileService.cs` — 여러 PAT 패턴 읽기와 현재 패턴 1개 내보내기
- `PatternStudioWindow.xaml`, `.xaml.cs` — 모달 독립 편집 창과 무한 반복 미리보기
- `PatternStudioCommand.cs` — Revit 패턴 수집과 최종 트랜잭션 저장
- `ModelLinePatternCaptureCommand.cs` — Revit 기본 스냅을 쓰는 ㄱ자 세 점 반복 틀과 모델선·상세선→FillGrid 변환
- `PatternGeometry.cs` — 공통 패턴 선 생성, 폐영역 탐지, Clipper 정수 불리언 연산
- `PatternDisplayedLineCollector.cs` — 2D CustomExporter로 면에 실제 표시된 패턴선 수집
- `PatternPunchModel.cs`, `PatternPunchWindow.xaml`, `.xaml.cs` — 타공 계획과 폐영역 선택/전체 미리보기/사전검증
- `PatternPunchCommand.cs` — 벽·바닥·천장 스케치 차집합과 대상별 probe/적용
- `CurtainPanelPunchService.cs` — 로드 가능한 커튼패널 전용 복제 패밀리와 관통 void
- `SystemCurtainPanelPunchService.cs` — 단순 평면 시스템 커튼패널의 검증된 고정형 대체 패밀리 생성
- `PatternPunchRecordStore.cs`, `PatternPunchRestoreCommand.cs` — ExtensibleStorage 이력과 최근 1회 안전 복원
- `App.cs` — `패턴` 리본 패널의 스튜디오/모델선 캡처/패턴 타공/타공 복원 버튼
- `SunnyToolsCommands.cs` — 커스텀 버튼의 기능 명령 목록

패턴 타공의 제도 패턴 좌표계는 화면의 `RightDirection`/`UpDirection`으로 만들되, 정면 여부는 반드시 `abs(FaceNormal·ViewDirection)`으로 계산한다. 0°(평행 또는 반평행)가 정면이며 최대 2°까지만 허용한다. 화면 가로·세로축을 면에 투영한 길이로 정면을 판단하면 옆면만 우연히 거부하고 일반 비스듬한 면을 통과시키므로 다시 사용하지 않는다. 오류에는 대상 라벨, 현재 뷰, 정면 이탈각을 포함한다. `PatternPunchWindow`는 XAML 로드 도중 `SelectionChanged`/`TextChanged`가 먼저 발생할 수 있으므로 `_isInitialized` 전에는 렌더링과 카운트 갱신을 실행하지 않는다.

## 모델선 패턴 캡처 절대 동작

반복 틀은 ㄱ자 순서의 세 점으로만 지정한다. 첫째 점은 첫 모서리, 둘째 점은 첫 변의 끝이자 꺾이는 모서리, 셋째 점은 둘째 변의 끝이다. 첫째→둘째 벡터가 가로축이고 둘째→셋째 벡터를 그 축의 직교 방향으로 투영한 값이 높이다. 셋째 점의 가로축 방향 오차는 버리고 보정된 셋째 모서리와 넷째 모서리를 자동으로 완성한다. 평면·천장평면·입면·단면 2D 뷰만 허용하며, `PickPoint` 전에 현재 뷰와 평행한 활성 작업 기준면이 있는지 검사하고 없으면 설정 방법을 안내한다. `PickPoint(ObjectSnapTypes...)`를 사용해 끝점·중간점·근처점·교차점·중심·직교·사분점의 Revit 네이티브 커서 스냅 표식을 그대로 유지한다. 자체 스냅 아이콘을 겹쳐 그리지 않는다. 모델/제도 선택용 `TaskDialog`는 CommandLink를 먼저 추가한 뒤에만 `DefaultButton`을 지정한다. Revit은 기본 버튼 대입 시점에 해당 링크의 존재를 즉시 검사한다.

현재 뷰의 `CurveElement` 중 `ModelCurve`와 `DetailCurve`를 모두 수집한다. 작업 기준면은 평행 여부 확인 뒤 화면 캡처 투영의 법선으로 사용하고, 뷰 방향과 반대일 때만 부호를 맞춘다. 선의 원래 깊이가 작업 기준면과 달라도 화면 방향으로 반복 틀 평면에 투영해 판정하며, 투영된 선이 원래 반복 틀과 실제로 교차할 때만 포함한다. 틀과 닿지 않은 주변 선을 타일 이동으로 강제로 틀 안에 끌어오면 안 된다. CAD 링크 선 자체는 읽지 않는다. 호·원은 Revit FillPattern이 직선 선군만 저장할 수 있으므로 `Curve.Tessellate()` 결과를 짧은 유한 선분 선군으로 바꾼다. 반복 틀 경계에 걸친 선은 반대편 타일로 wrap+clip하며, 최대 경계(`x=폭`, `y=높이`)와 동일한 최소 경계(`x=0`, `y=0`)는 같은 주기 위치로 정규화해 중복 선군을 만들지 않는다. 캡처한 유한 선분을 실선 무한선으로 바꾸지 않고 반드시 양수 선/음수 공백 세그먼트로 저장한다. Bézout 해로 구한 선군 `Shift`는 같은 선 방향 주기를 modulo로 제거해 `[-period/2,+period/2]` 범위로 정규화한다. 제도 패턴은 현재 뷰 축척으로 나누고 모델 패턴은 1:1을 유지한다. 최종 저장은 기존 `PatternStudioCommand.BuildFillPattern` 경로를 재사용한다. 캡처 전용 편집 창에도 편집 가능 여부와 무관한 문서 전체 패턴 이름 집합을 넘겨 솔리드 채우기 등을 포함한 이름 충돌을 저장 전에 알려야 한다.

## 패턴 타공 절대 동작

타공은 패턴이 실제로 보이는 평면·천장평면·입면·단면 2D 뷰에서만 실행한다. `FillGrid.Origin`은 면에 배치된 실제 원점이 아니라 패턴 정의 좌표이므로, 실제 타공 위치 계산에 그것만 사용하면 안 된다. `PatternDisplayedLineCollector`는 같은 HLR 2D 뷰를 `Export2DGeometricObjectsIncludingPatternLines=false/true`로 두 번 내보내고, 공선 구간까지 정규화한 `패턴 포함 - 패턴 제외` 차집합으로 재료 패턴선만 남겨야 한다. 로드 가능한 패널의 선택 Face가 패밀리 심벌 좌표로 반환될 수 있으므로 `ComputeReferences=true` 형상을 `GeometryInstance.GetSymbolGeometry()`로 재귀 탐색하며 전체 Transform을 누적해 면 원점·축·경계를 프로젝트 좌표로 복원한다. stable reference 재탐색이 불가능한 1단계 인스턴스는 `Reference.UVPoint`를 Face에 Evaluate한 위치와 `GlobalPoint`의 화면 Right/Up 오차를 비교해 Identity/인스턴스 변환을 결정한다. 수집 선은 화면 시선으로 선택 면에 투영한 뒤 양 끝점 포함 검사가 아니라 선분-경계 clip으로 잘라야 한다. 선 소유 ID는 패널·심벌·내부 부품 별칭을 허용하되 공유 ID는 다수 target으로 팬아웃하고 면 공간으로 다시 거른다. 같은 선이 둘 이상의 선택 면에 투영되면 오배정하지 말고 모호한 선으로 제외한다. 링크 문서의 ElementNode는 현재 미지원으로 수집하지 않는다. 표시 선을 읽지 못하면 추정 위치로 타공하지 말고, 전체/일반/패턴후보/소유/공간/제외/모호/채택 개수 진단을 표시한 뒤 중단한다.

`PatternPunchWindow`는 합성 패턴 공간의 유사 prototype을 사용하지 않는다. 각 `PatternPunchTarget`에서 현재 면에 실제로 검출된 `Regions`를 면 경계로 clip해 표시하고, 사용자가 클릭한 정확한 영역만 target별로 보관한다. 다시 클릭하면 그 한 영역만 해제하며 같은 모양·크기의 다른 반복 영역으로 자동 확장하지 않는다. 선택하지 않은 target은 실행·실패 집계에서 제외한다. 최종 타공은 target별 직접 선택 영역을 면 경계와 intersect한 뒤 union해 경계에 걸린 부분을 잘린 모양 그대로 유지한다. 최소 폭·높이는 면 경계로 잘린 최종 경계에 적용한다. 완전 관통만 지원한다. 남는 재료가 없거나 여러 outer island로 분리되거나 Revit 최소 길이보다 짧은 선이 생기면 실행하지 않는다. 대상이 여러 개면 같은 표면 패턴만 허용하고, 대상별 probe 트랜잭션을 rollback한 뒤 실제 적용하므로 한 대상 실패가 다른 대상을 취소하지 않는다. 현재 collector 결과는 ElementId 단위이므로 같은 요소의 여러 면을 한 번에 고르면 명확히 거부하고 면별로 나누어 실행하도록 안내한다.

벽·바닥·천장은 `SketchEditScope`로 기존 프로파일에 차집합을 적용한다. 벽에 프로파일 스케치가 없으면 `CreateProfileSketch`를 별도 `TransactionGroup` 안에서 먼저 만들며, 기존 벽체 분리 명령의 Tx1→SketchEditScope→Tx2 규칙과 코드를 합치지 않는다. 로드 가능한 커튼패널은 원본 패밀리를 편집한 뒤 새 이름으로 저장·로드한 선택 패널 전용 복제본에 void extrusion을 만들고 패널 유형만 교체한다. 내부 시스템 커튼패널은 Revit API로 원본을 편집할 수 없으므로, 평면·단일 프리즘·유효 두께·남는 재료 outer island 1개 조건을 모두 검증한 경우에만 현재 연도의 `Metric Curtain Wall Panel.rft` 계열 템플릿으로 선택 패널 전용 고정형 로드 패밀리를 만든다. `FacePaths - punchPaths` 남는 단면을 실제 두께만큼 solid extrusion하고 원본 재료를 family material type parameter에 연결한 뒤 패널 유형을 교체한다. probe는 실제 적용과 같은 형상 생성을 시도한 뒤 되돌린다: 시스템 커튼패널은 로드·교체·재생성·형상 검증까지 전부 수행하고 `TransactionGroup`을 rollback하며, 로드 가능한(사용자 제작) 커튼패널은 패밀리 편집기에서 같은 void extrusion 생성만 시도한 뒤 저장하지 않고 닫는다(`CurtainPanelPunchService.ProbeExtrusionGeometry`) — 프로젝트 문서는 어느 쪽도 건드리지 않는다. 실제 적용만 assimilate한다. probe를 아무 형상도 만들지 않고 바로 성공을 반환하도록 되돌리지 말 것 — 자기교차 등 형상 오류가 사전 검증을 통과했다고 잘못 보고된다. 템플릿 부재·비정형·다중 solid·두께/형상 불일치·인플레이스는 문서를 바꾸지 않고 실패 사유를 알린다. 고정형 대체 패널은 이후 커튼그리드 셀 크기 변경에 자동으로 늘어나지 않는 제한을 사용자 결과와 기록에 남긴다.

성공 결과는 호스트의 ExtensibleStorage에 원본 프로파일 또는 생성 Opening ID 또는 원래 패널 유형 ID와 적용 후 해시를 남긴다. `타공 복원`은 최근 1회만 되돌리고, 현재 프로파일 해시가 적용 직후와 다르면 다른 사용자의 후속 편집을 덮어쓰지 않고 중단한다. 이 ExtensibleStorage 기록은 반드시 실제 타공을 커밋/assimilate하는 바로 그 `Transaction`(또는 `TransactionGroup`)이 끝나기 **전에** 같은 범위 안에서 함께 써야 한다(`PatternPunchExecutor.AppendRecordWithinGroup` 등). 타공이 끝난 뒤 별도의 새 트랜잭션으로 기록을 저장하면, 사용자가 Revit 되돌리기(Ctrl+Z)를 한 번만 눌러도 기록만 사라지고 타공 형상은 그대로 남아 `타공 복원` 기능 자체가 무력화된다 — 실제로 2026-08-25에 이 문제가 있었다.

## 절대 유지할 동작

### Revit 문서 변경 시점

창을 여는 동안에는 Revit 문서를 수정하지 않는다. 사용자가 `Revit에 저장`을 누르고 창이 닫힌 뒤 `PatternStudioCommand`의 단일 트랜잭션에서만 `FillPatternElement.Create` 또는 `SetFillPattern`을 호출한다. 취소하면 문서 변경은 0건이어야 한다.

기본값은 항상 새 패턴 저장이다. 원본 덮어쓰기는 Revit에서 읽은 패턴(`SourceElementId`가 있는 경우)에만 활성화하며, 모든 사용처가 함께 달라진다는 경고 후 다시 확인한다. PAT 원본 파일은 절대로 직접 덮어쓰지 않는다. PAT 내보내기는 사용자가 고른 별도 파일 경로에 저장한다.

### 각도와 길이 단위

Revit API의 `FillGrid.Angle`은 **라디안**으로 동작하고 PAT 각도는 **도(degree)**다. `PatternGridDefinition.AngleDegrees`는 도 단위로 유지한다. 따라서 Revit에서 읽을 때 라디안→도, `FillGrid`를 만들 때 도→라디안으로 반드시 변환하고, 미리보기/변환 서비스도 삼각함수 계산 직전에만 라디안으로 바꾼다. Autodesk 2026 API 참조의 `FillGrid` 클래스 설명에는 degree라고 적혀 있지만, Revit 2026.4의 실제 `RevitAPI.dll`에서 `new FillGrid(Math.PI / 4, ...)`와 `GetSegmentDirection()`을 대조해 45°가 되는 것을 확인했다. 이 경계 변환을 다시 제거하지 말 것. Revit의 `FillGrid.Origin`, `Shift`, `Offset`, `Segments`는 내부 길이 단위인 피트로 저장한다.

PAT 가져오기 길이는 `;%UNITS=MM`이면 mm→ft, `;%UNITS=INCH`면 inch→ft로 변환한다. 단위 선언이 없으면 mm로 해석하고 사용자에게 경고한다. 내보내기는 항상 `;%UNITS=MM`을 기록하고 ft→mm로 바꾼다. 모델 패턴은 `;%TYPE=MODEL`을 기록하고 제도 패턴은 TYPE 줄을 생략한다.

### 선군별 편집 상태

`PatternGridEditState`는 선군 인덱스마다 별도 객체를 가진다. 목록에서 다른 선군을 선택할 때 우측 컨트롤은 그 객체의 값을 읽어야 하며, 직전에 선택했던 선군의 값을 새 선군에 복사하면 안 된다. 이 분리는 사용자가 HTML 목업에서 직접 지적한 필수 조건이다.

전체 회전은 모든 선군의 방향·원점·반복 격자를 공통 원점 기준으로 함께 회전한다. 선군 회전은 해당 선군의 기준점은 고정한 채 그 선군의 방향과 반복 격자를 함께 회전한다. 선군 크기는 그 선군의 반복 벡터와 대시 길이에 적용하고, 선군 간격은 법선 방향 `Offset`에 추가로 적용한다.

### 폭/높이 독립 변환

폭/높이 기준축은 원본 선군 1의 방향이다. `PatternTransformService`는 선 방향 `d`, 법선 `n`, 반복 벡터 `v = Shift*d + Offset*n`을 만들고, 기준축 좌표에서 폭/높이 비율을 적용한 뒤 전체 회전한다. 변환된 반복 벡터를 새 방향/법선에 투영해 `Shift`와 `Offset`을 다시 구하고, 대시/간격 세그먼트는 변환된 선 방향 길이만큼 바꾼다. 단순히 각도와 Offset 숫자만 바꾸면 비균일 폭/높이에서 패턴 격자가 틀어지므로 이 벡터 계산을 축약하지 말 것.

### 모서리 자동 채움과 미리보기

Revit 채우기 패턴은 유한한 선 묶음이 아니라 무한 반복되는 `FillGrid` 정의다. 전체 회전 뒤 모서리가 비는 것을 막기 위해 미리보기에서도 화면 안 선을 먼저 자른 뒤 회전하면 안 된다. `BuildGridGeometry`는 회전/스케일된 정의로부터 현재 화면 법선 투영 범위를 덮는 반복 인덱스 `k`를 다시 구하고, 각 무한 직선을 화면 경계에 클리핑한다. 따라서 어떤 각도에서도 네 모서리까지 같은 패턴이 이어져야 한다.

원본 겹쳐보기는 원본 정의를 흐리게 먼저 그리고 편집 결과를 그 위에 그린다. 편집 결과의 선 경로를 클릭하면 해당 선군이 목록에서 선택된다. 선군 색은 구분용일 뿐 Revit 패턴 색을 저장하는 기능이 아니다.

### 점과 대시

PAT와 `PatternGridDefinition.Segments` 내부 표현은 양수가 선, 음수가 공백, 0이 점이다. 그러나 Revit 2026.4 실측 결과 `FillGrid` API 배열과 PAT 사이에서는 홀수 인덱스의 부호가 반대다. 실제 API에서 `[1, 0.25]`를 `SetSegments`한 뒤 `FillPattern.ExportToPAT`하면 `1, -0.25`가 나오며, `[1, -0.25]`를 직접 넘기면 `1, 0.25`가 나온다. 따라서 Revit에서 읽고 쓸 때 모두 `PatternSegmentCodec`으로 홀수 인덱스의 부호를 한 번 뒤집는다. 절댓값으로 강제하면 비정형 연속 선/공백 배열의 원래 부호를 잃으므로 parity 부호 반전 그대로 유지한다. 이 변환을 생략하면 일반적인 HEX·원형 근사·끊긴 선 패턴의 공백까지 그려져 무한 실선 격자로 무너진다.

Revit 패턴 생성 직전에 `FillPattern.ExpandDots()`를 호출해 0 길이 점이 표시되게 한다. 미리보기는 0 세그먼트를 짧은 화면 선으로 그린다. `Offset == 0`은 Revit에서 유효하지 않으므로 가져오기·변환·저장 검증에서 차단한다.

세그먼트 배열이 있는 선군은 어떤 경우에도 실선으로 대체하지 않는다. 반복 주기가 0인 점 전용 선군은 기준점에 짧은 점 하나만 표시하고 화면 끝까지 선을 연장하지 않는다. Revit 원본의 `GetSegments()`가 실패한 패턴도 빈 세그먼트 배열로 바꿔 목록에 넣지 않는다. 빈 배열은 실제 실선만 의미해야 한다. 원처럼 보이는 Revit/PAT 패턴도 API상 여러 방향의 짧은 직선·점 선군 조합이므로, 각 선군의 각도와 양수/음수/0 세그먼트를 그대로 보존해 원래 형상을 재현한다.

## PAT 파서 범위

- 한 파일의 `*이름,설명` 헤더 여러 개를 각각 목록 항목으로 읽는다.
- `;%TYPE=MODEL`, `;%TYPE=DRAFTING`, `;%UNITS=MM`, `;%UNITS=INCH`를 처리한다.
- 소수와 단순 분수(`1/8`)를 읽는다.
- 잘못된 선군은 행 번호 경고와 함께 건너뛰고, 유효 선군이 하나도 없는 패턴은 목록에서 제외한다.
- UTF BOM을 감지하고, BOM 없는 UTF-8을 우선 판별하며, UTF-8이 아니면 현재 Windows ANSI 코드 페이지를 시도한다.

## WPF / Revit 호환 주의

`PatternStudioWindow`는 `Resources/Theme.xaml`을 반드시 자기 `Window.Resources`에 로컬 병합한다. `Application.Resources`에 넣지 않는다. Revit 프로세스에 시스템 WPF 테마가 없을 수 있어 이 창에서 처음 사용한 `Slider`도 로컬 `ControlTemplate`으로 완전히 정의했다. 기본 Slider 템플릿으로 되돌리면 Revit 안에서 XAML 로드 실패 가능성이 있다.

창 코드비하인드는 `System.Windows`만 직접 사용하고 Revit enum은 완전한 이름으로 적어 `Grid`, `Path`, `Point`, `Color` 등의 충돌을 피한다. 미리보기 `Path`와 `Point`는 각각 `WpfPath`, `WpfPoint` 별칭을 유지한다.

## 멀티 버전 검증 이력

- 2026-08-25: (커밋 전 코드 리뷰로 발견, 라이브 실행 없이 수정) 패턴 타공 성공 뒤 `PatternPunchRecordStore.TryAppend`가 실제 타공을 assimilate/commit한 트랜잭션(그룹)과 별개인 새 트랜잭션으로 안전 복원 기록을 저장하던 문제를 수정했다. 사용자가 타공 직후 Revit 되돌리기를 한 번만 눌러도 (가장 최근 트랜잭션인) 기록만 사라지고 타공 형상은 그대로 남아 `타공 복원`이 무력화되는 결함이었다. `PatternPunchExecutor`의 각 실행 경로(`ExecuteWithNewWallSketch`, `ExecuteSketchDifference`, `ExecuteNativeOpenings`)와 `CurtainPanelPunchService`/`SystemCurtainPanelPunchService`가 `TransactionGroup.Assimilate()`/`Transaction.Commit()` 직전, 같은 범위 안에서 `PatternPunchRecordStore.AppendEntity`를 직접 호출하도록 바꿔 하나의 되돌리기 단위로 묶었다. 함께, 로드 가능한(사용자 제작) 커튼패널의 probe가 아무 형상도 만들지 않고 바로 성공을 반환하던 문제도 고쳐 `CurtainPanelPunchService.ProbeExtrusionGeometry`가 실제 패밀리 편집기에서 같은 void extrusion 생성을 시도한 뒤 저장 없이 되돌리도록 했다. Revit 2023~2027 Release 빌드 오류 0개를 확인했다. 이 커밋 이전 상태와 마찬가지로 아직 Revit 내 실제 실행(라이브) 검증은 하지 않았다.
- 2026-08-21: 패턴 타공의 반복 유사영역 자동 확장을 제거하고 각 선택 면에서 사용자가 클릭한 정확한 폐영역만 target별로 타공하도록 변경했다. 면 경계로 잘린 최종 영역에 최소 크기를 적용하고 선택하지 않은 면은 실행하지 않는다. 모델선 캡처는 `ModelCurve`뿐 아니라 저널에서 실제 사용된 `DetailCurve`도 수집하며, 첫 모서리→첫 변 끝→둘째 변 끝 ㄱ자 세 점을 직교 사각형으로 보정해 화면 투영으로 clip한다. 단순 평면·단일 프리즘 시스템 커튼패널은 현재 버전의 커튼월 패널 템플릿으로 고정형 대체 패밀리를 만들고 원본 두께·재료·남는 단면과 교체 후 형상을 probe에서 검증하는 제한 지원을 추가했다. Revit 2023~2027 격리 Release 빌드 오류 0개, 2026 외부 WPF 타공 창 초기화 스모크 통과, 모든 런타임 의존성·API 참조·Payload/ZIP 파일 집합과 SHA-256 일치를 확인했다.
- 2026-08-21: 로드 가능한 커튼패널의 선택 Face는 패밀리 심벌 좌표인데 2D CustomExporter 표시선은 프로젝트 좌표인 좌표계 불일치로 패턴선이 0개가 되던 문제를 수정했다. stable reference와 누적 GeometryInstance Transform으로 면을 프로젝트 위치로 복원하고, 패턴 OFF/ON 내보내기의 공선 구간 차집합으로 패널 형상선과 재료 패턴선을 분리했다. 공유 심벌 ID 팬아웃, 선분-면 경계 clip, 다중 면 모호성 거부, 링크 문서 제외, 수집 단계 진단 개수를 함께 보강했다. Revit 2023~2027 Release API 교차 빌드 오류 0개를 확인했다.
- 2026-08-21: 모델/제도 선택 `TaskDialog`에서 아직 추가하지 않은 `CommandLink1`을 먼저 기본 버튼으로 지정해 명령 시작 즉시 `Corresponding button not found`로 중단되던 문제를 수정했다. CommandLink 등록 뒤 기본값을 설정하고 선택 단계도 전체 오류 처리 안으로 옮겼다. 작업 기준면 검증·법선 사용, 틀 밖 주변선 제외, 선분 전체 단위의 경계 중복 제거, Bézout 반복 Shift 정규화, 전체 기존 패턴 이름 선검사도 함께 보강했다. 패턴 스튜디오에서 이미 안내한 창·저장 오류는 외부 명령 실패로 다시 게시하지 않고 취소로 종료한다.
- 2026-08-21: Revit `FillGrid`의 양수 교대 길이를 PAT 부호 규칙으로 잘못 해석해 HEX·원형 근사 패턴이 연속 실선으로 보이던 문제를 수정했다. Revit 읽기/쓰기 경계에 `PatternSegmentCodec`을 추가하고 패턴 스튜디오와 타공 프로토타입 생성 경로에 모두 적용했다.
- 2026-08-21: 패턴 타공의 정면 판정을 실제 `ViewDirection`과 면 법선의 각도로 교체하고, 잘못 선택한 끝면·상하면·리빌면의 정면 이탈각을 표시하도록 했다. 타공 창의 XAML 초기화 중 조기 이벤트가 미생성 컨트롤을 참조해 발생하던 `NullReferenceException`도 초기화 가드로 수정했으며, 예상 가능한 선택/형상 안내는 외부 명령 실패로 다시 게시하지 않고 취소로 종료한다.
- 2026-08-21: 세 점 모델선 캡처, 실제 표시 패턴 기반 폐영역 선택, 벽·바닥·천장·로드 가능한 커튼패널 완전 관통 타공, 최근 1회 안전 복원을 추가했다. 2023~2027 Debug API 교차 빌드 오류 0개를 확인했다. 실행 중 Revit에는 이전 어셈블리가 이미 로드되어 있어 새 설치본 적용 뒤 재시작 라이브 검증이 필요하다.
- 2026-08-20: 끊긴 선과 점으로 만든 패턴이 미리보기에서 긴 실선으로 연장되고, 원형 근사 패턴이 선처럼 무너지는 문제 수정. 점 전용 선군의 실선 fallback을 제거하고, 반복 주기마다 원본 세그먼트의 선·공백·점을 독립적으로 다시 그리며, 세그먼트 읽기 실패를 실선으로 변환하지 않도록 했다.
- 2026-08-20: Revit 패턴 선택 즉시 모든 미리보기가 원본과 다르게 무너지는 라이브 버그 수정. 원인은 `FillGrid.Angle`의 실제 라디안 값을 도로 오인한 것이었다. Revit 경계에서 라디안↔도 변환을 추가하고 내부/PAT 모델은 계속 도 단위로 유지했다.
- 2026-08-20: 첫 구현. `Verify2023`~`Verify2027`(각 이름 끝의 연도를 읽는 프로젝트 규칙상 Release와 동일 TFM/API)을 모두 빌드해 5개 연도에서 오류 0개를 확인했다. 실제 `Release2026` 출력 DLL은 Revit/다른 프로세스가 기존 `obj` DLL을 메모리 매핑해 잠근 상태여서 별도 Configuration을 사용했다. 검증용 bin/obj 폴더는 확인 뒤 삭제했다.
- 기존 프로젝트 전체의 nullable/구식 API 경고는 남아 있지만 이번 패턴 스튜디오 파일에서 새로 발생한 컴파일 경고는 없다.
- Revit 라이브 UI에서 실제 패턴 생성/덮어쓰기/재료 적용 결과는 별도 수동 검증이 필요하다.
