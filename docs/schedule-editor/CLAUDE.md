# 일람표 에디터 (Schedule Editor)

## v103 — 2026-10-14 도면목록표 전용 행 연결 / 기본 매개변수 접근

- 사용자 재현: 링크 포함 도면목록표의 동일 도면번호가 전부 소속 확인 불가, `시트 번호` 필터/정렬 재검증 실패. 링크 제외+빈 `RVT 링크: 파일 이름` 열에서도 모든 필드가 읽기 전용. 시트는 모델 형상이 아니므로 일반 뷰/링크 그래픽 collector에 의존하지 않는다. `ScheduleEditorService.Read`에서 OST_Sheets는 새 `ScheduleEditorSheets.Read`로 분기한다. 키/내포/재료 산출 기존 차단은 유지한다.
- 현재 및 로드된 링크 Document의 ViewSheet를 직접 수집한다. 모델 권한/원본 경로는 UnifiedEditService에서 조회하고 이미 열린 편집 원본은 기존 FindOpenTarget으로 재사용한다. 조회 중 다른 원본을 새로 열거나 중앙을 수정하지 않는다. 링크 문서 자체에는 쓰지 않으며 저장은 기존 LinkedModelEditService.Run 경로뿐이다.
- 개별 행은 원래 정렬/필터를 유지한 임시 복제에 SHEET_NUMBER 및 링크 포함 시 RVT_LINK_FILE_NAME_WITHOUT_EXT 열을 추가/노출하여 읽는다. Count를 읽고 성공/실패 모두 RollBack한다. 원본에는 열/서식/템플릿 변경이 남지 않는다. 기본 번호·원본 파일·직접 표시 필드를 대조하고 Source.Key+시트 UniqueId로 연결한다. 같은 ID/번호인 다른 모델은 분리, 같은 원본 반복 링크는 중복 제거. 서로 다른 경로의 동명 파일 등 여전히 모호하면 해당 행만 읽기 전용으로 두며 정상 행까지 막지 않는다. 복제에서 얻은 ScheduleField API 객체는 롤백 이후 사용하지 않는다.
- 묶인 행은 기존 정렬 필드만 기준으로 모든 직접 값/원시값 및 실제 Count를 검증한다. 정렬에 없는 보조 열을 그룹 식별에 추가하지 않는다. 필터/정렬 의존 매개변수가 해석 불가하거나 후보 개수가 다르면 그 행을 차단한다. 반올림/동명 참조로 원시값이 다른 그룹도 차단한다.
- 링크 포함 OFF의 빈 파일 정보는 식별 필수조건이 아니다. 링크 정보·계산식·합계는 해당 열만 읽기 전용; 일반 시트 필드는 개별 writable 상태로 판단한다. 열린 원본의 문자열 값과 링크 표의 표시가 다르면 해당 셀을 차단하여 기존 미동기화 작업을 덮어쓰지 않는다. 시트번호와 직접 표시/필터/정렬 값을 guard로 보관하고 원본 쓰기 직전 다시 검사한다. 최종 재조회 fingerprint 비교도 유지한다.
- 기본 매개변수는 Parameters 열거 대신 get_Parameter(BuiltInParameter)로 조회한다. 일반 일람표 Resolve 및 공유 ProjectInformationService.Validate도 동일 방식으로 보완한다. 공유 GUID/원시값/정의/권한 검사를 생략하지 않는다. 문서별 양수 ID나 이름으로 링크 사용자 매개변수를 추측하지 않는다.
- 도면목록표는 2023~2027 지원 API만 사용한다. 아래 v100/v102의 **2023 링크 포함 차단은 일반 모델 요소 일람표에만 적용**하며 도면목록표는 예외이다. UI 드래그/Shift·필드별 입력/중앙 확인 및 다른 로컬에 대한 제약은 유지한다.
- API 대역+실제 서비스/UI 회귀: unified-v103 **199**(신규22), release-v103 **91**, link-safety-v103 **72**, 총 **362개**. 같은 번호/ID의 호스트+2링크·표 순서·선택 모델만 수정·Parameters에 없는 기본 시트번호 저장·미동기화 원본·모호한 행 부분 차단·링크 OFF/빈 파일열·그룹 개수·임시 복제 정리 검사. WPF 기본/최소 크기 렌더 확인. 2023~2027 컴파일 오류0. **실제 Revit 표시 엔진 및 중앙 서버는 미검증**이다.
- 공식 근거: [링크 뷰 collector는 visible graphical elements 대상](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/968b52a0-de55-2f96-de40-968812bc41c7.htm), [ScheduleFieldType의 링크 파일/인스턴스 필드](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/9888db7d-00d0-4fd7-a1a9-cdd1fb5fce16.htm), [GetLinkDocument는 변경/저장 대상이 아님](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/6f81c365-15d9-06b8-48ef-df84914fec60.htm).

## v102 — 2026-10-14 비텍스트 행 식별 / 필드별 입력기

- 사용자 오류 원인: v100의 `TextDirect`와 텍스트 전용 그룹 제한. 단순히 숫자 필드가 섞인 문제와는 다르다. 아래 v100의 텍스트 전용/참조 전체 금지 규칙은 이 절로 대체한다. 키/내포/재료 수량 산출, 2023 링크 포함 제한은 유지한다.
- `ScheduleEditorParameters.Display`: Double은 **호스트 일람표 Units + 필드 GetFormatOptions**로 UnitFormatUtils.Format. 링크 문서 단위를 표시 행 식별에 대신 쓰지 말 것. String/Integer/ElementId는 실제 매개변수 표시값/참조명을 읽는다. 개별 행에서 표시 분포가 정확히 맞는 직접 필드만 복합 식별에 사용한다. 일부 특수 표시 필드(예/아니요 등)가 대응하지 않아도 다른 필드로 정확히 1:1 식별되면 허용한다. 묶인 행은 모든 정렬/그룹 필드가 직접 해석 가능하고 분포가 맞아야 하며 하나라도 제외해 추측하지 않는다.
- 표시 복합키 + 저장형별 원시값 + 실제 Revit Count를 대조한다. 같은 표시키에 다른 원시값(반올림 또는 동명 참조)이 겹치면 차단한다. 참조 원시키는 모델 키를 포함하므로 서로 다른 모델의 동명 레벨 등을 임의 병합하지 않는다. 결과 불일치/의존값 검증 불가 시 조회 창은 열되 모든 행에 읽기 전용 사유, 빈 Targets를 넣는다. 원본/임시 복제 롤백 규칙 유지. 직접 정렬 필드와 필터 필드는 식별에서 제외됐더라도 사전검증 guard에 포함한다.
- UI 입력기: Text, Integer, Number(단위 안내), Boolean(예/아니요), Reference(모델별 검색/목록), ReadOnly. 필드 전환 시 입력값/참조 선택을 초기화하여 다른 필드에 이월되지 않는다. 행 선택/스크롤/누적 미리보기는 유지한다. 숫자 문법/단위의 최종 판정은 실제 Revit `SetValueString`이며 실패 시 해당 문서 트랜잭션은 롤백한다. 여러 중앙 파일의 이미 끝난 동기화까지 되돌리지는 않는다.
- 참조 편집은 writable 재료 spec 및 명시한 레벨 built-in만: LEVEL_PARAM, FAMILY_LEVEL_PARAM, SCHEDULE_LEVEL_PARAM, WALL_BASE_CONSTRAINT, WALL_HEIGHT_TYPE, ROOF_BASE_LEVEL_PARAM, STAIRS_BASE_LEVEL_PARAM, STAIRS_TOP_LEVEL_PARAM. 임의 참조를 이름/현재 값이 Level이라는 이유만으로 허용하지 말 것. 없음/카테고리별/연결 안 됨 등 음수 sentinel은 신규 선택 목록에 넣지 않는다(원래 값은 변경하지 않으면 보존). 다른 참조·특수 정수 열거값·계산식·합성·합계·읽기 전용은 사유 표시만 한다.
- 참조 목록은 원본 모델마다 한 번 수집한 UniqueId→이름을 공유한다. `StageReferences`에서 선택된 모든 행/모델을 먼저 검증하고 하나라도 누락하면 전체 미리보기 불변. 대상 문서에서 UniqueId/이름/클래스를 다시 검증한 뒤 Set(ElementId). 이름 자동 매핑 금지. 레벨의 형상 영향 안내 유지.
- `ProjectInformationService`/`UnifiedRow.Changed` 공용 확장: 일람표 opt-in 참조만 읽기 전용을 해제하고 참조 ID로 변경 여부 판단. NAMER 프로젝트 정보 참조는 종전대로 읽기 전용이다. 실제 쓰기/로그/중앙 동기화 안전 경로와 IFailuresPreprocessor 금지 유지.
- 테스트: unified-v102 177(기존145 + 신규32), 기존 UI/재료91 + 링크72 = **340개**. 숫자만 있는 일람표/필드 서식/반올림 차단/숫자 및 레벨 그룹/특수 표시 fallback/형식 분류/참조 원자성·stale·실패 rollback/모델 격리/계산식 읽기 전용/기본·최소 UI 렌더. Revit 2023~2027 컴파일 오류0. API 대역 검증이며 실제 Revit 표시 엔진·서버는 미검증.
- 공식 근거: [매개변수 저장형·ElementId 특수값](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Parameters/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Parameters_Parameter_html.html), [UnitFormatUtils.Format](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/2aa08848-a28b-6fd9-79ef-d708b79ec28d.htm), [ScheduleField.GetFormatOptions](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/c0f20eb6-3e5e-11b3-458c-f38acbe2cddd.htm).

## v101 — 2026-10-14 드래그 / Shift 범위 체크·해제

- `ScheduleEditorWindow`만 변경. NAMER 통합 창과 같은 시작 행 반대 상태 고정 드래그, 마지막 클릭 행 기준 Shift 범위 선택/해제. 빠르게 건너간 중간 행도 처리하고 되짚어도 반전하지 않는다. Shift 클릭 뒤 기준은 그 클릭 행으로 갱신한다.
- `_selected`가 선택 상태를 보관한다. 체크를 바꿀 때 행을 재생성하지 않고 체크/배경/상태만 갱신한다. `_syncingRange`로 압축 범위 문자열(`1-10,12`)의 역방향 TextChanged 재진입을 차단한다. 직접 범위 입력은 이전 Shift 기준과 드래그를 종료하며, 잘못된 입력이면 선택을 비우고 오류 표시; Stage는 기존 파서로 다시 검사한다. 필드 변경·더 보기·미리보기 적용에는 선택/페이지/스크롤을 유지한다.
- 행 전체가 입력 영역이며 체크박스는 포인터 히트 대상에서 제외해 이중 토글을 막는다. 체크박스의 키보드/접근성 Click은 같은 선택 경로를 사용한다. 목록이 마우스를 캡처하고 끝 24dip에서 22dip씩 스크롤한다(80ms 타이머, 정지한 커서도 이어서 스크롤). MouseUp/왼쪽 버튼 해제/목록 자체 LostMouseCapture/Deactivated/Closed/Render/직접 입력/예외에서 캡처·타이머를 정리한다. 자식의 LostMouseCapture 버블링으로 취소하지 말 것.
- 범위는 전체 데이터 행 번호 기준이고 200행 페이지를 넘어도 포함한다. 선택은 기존처럼 편집 불가 행도 가능하지만 Stage의 모호성·읽기 전용 차단은 바꾸지 않았다. Revit 행 대응/저장/동기화 서비스는 변경하지 않는다. 선택/해제는 누적 미리보기 값을 지우지 않는다.
- `.codex-build/unified-v101`: 기존 114 + 새 선택 회귀 31 = 145개. 새 검사는 드래그/Shift 양방향, 직접 입력, 잘못된 입력, 페이지 경계, 필드 전환, 실제 WPF 라우팅 이벤트·히트테스트·자동 스크롤·캡처/종료, 재생성 없는 체크 및 원본 불변을 포함한다. release-v101 기존 91 + link-safety-v101 72 = 총 308개. 기본/최소 창 렌더 확인, 2023~2027 컴파일 오류 0. OS 실제 마우스/키보드 입력과 Revit 실모델·중앙 동기화는 별도 라이브 확인 필요.

## v100 — 2026-10-13 첫 구현

사용자 요청: 별도 리본 버튼에서 특정 일람표의 필드와 정렬 기준 위에서부터의 행 번호를 지정해 값을 일괄 입력/변경. 링크 원본의 중앙 동기화까지 처리. 묶인 행은 **대상 요소를 확인할 수 있을 때 개수를 보여주고 전부 수정, 모호하면 차단**으로 명시 확정했다.

### 파일 / 흐름

- `ScheduleEditorCommand`: 일람표 선택, snapshot, 모달 순수값 UI, 최종 확인, 다시 조회해서 행 fingerprint 비교, 링크부터/현재 모델 마지막 처리. 이미 중앙 반영했거나 복구 변경을 남겼으면 이후 실패가 있어도 외부 명령은 Succeeded로 반환하고 실패를 별도 보고한다(v99 자동 롤백 회귀 방지).
- `ScheduleEditorService`: Revit 뷰 범위 collector로 현재 및 링크의 표시 요소 조회. 링크 collector는 2024+ 3인자 API. 2023 링크 포함 일람표는 명시적 차단. `UnifiedEditService.Collect(readRows:false)`로 모델 편집 가능 여부만 조회한다.
- `ScheduleEditorModel/Logic`: 순수 값, 길이 접두 키, 범위 `1-10,12` 파싱, 행-요소 집합 매칭, 전부 검증 후 미리보기 값 누적. 동일 문서/요소/매개변수는 공유 행 객체로 하나만 수정하며 여러 행에서 보이면 마지막 지정값이 같이 표시된다.
- `ScheduleEditorWindow`: 필드/행/값, 행별 소속 모델·실제 요소 수·원래 값·변경 미리보기·상태, 체크 및 범위 입력, 조절 가능한 열, 200행 페이징, 최종 적용. UI에서 Revit API 호출/문서 보관 없음. local Theme only.
- `ProjectInformationService` 재사용: 순수 매개변수 스냅샷, 저장형별 Set, 원시값 재검증, transaction 성공 후 로그. 이 파일을 수정할 때 NAMER 규칙도 반드시 읽을 것.

### 행을 추측하지 않는 제한

공개 API의 표 셀은 곧바로 요소 ID를 주지 않는다. 임시 복제 일람표에서 원래 정렬/필터/그룹 유지, 제목/열 제목/소계/합계/빈 구분행만 제외하고 데이터 행을 읽는다. 정렬의 숨은 필드는 복제에서만 표시한다. Count 필드를 읽어 실제 그룹 요소 개수까지 대조한다. 복제 트랜잭션은 성공/실패 모두 RollBack하며 원본 뷰와 요소 값은 변경하지 않는다. 실제 Revit에서 이 경로는 아직 미검증이므로 테스트 문서에서 먼저 검증해야 한다.

- 개별 행: 모든 후보가 직접 문자열 매개변수로 해석 가능한 표시 필드들의 값으로 정확히 1:1일 때만 매칭.
- 묶인 행: 모든 정렬/그룹 필드가 직접 문자열 매개변수여야 함. 표시 순서는 Revit이 만든 표를 그대로 사용하고 자체 정렬하지 않음. 텍스트 기준 그룹 키의 후보 집합, 데이터 행 키, Count가 모두 맞아야 함. 정렬 기준이 없으면 한 그룹/한 데이터 행인 경우만 허용.
- 동일 표시 키가 여러 행이면 순번으로 분배하지 말 것. 후보-행 집합 불일치/개수 불일치도 전체 조회 중단. 숫자 반올림/단위/레벨/계산식 기준을 텍스트로 임의 변환해 지원한다고 주장하지 말 것.
- 원본 요소·유형의 필터/정렬 매개변수 스냅샷과 유형 UniqueId를 보관. 실제 쓰기 직전 각 원본 문서에서 재검사. 조회 후 바뀐 값/유형/범위는 덮어쓰지 않음. 새 요소를 기존 선택 범위에 자동 추가하지 않음.
- 직접 Instance/ElementType 매개변수만 편집. 계산식/합성/관련 요소/합계 표시(DisplayType != Standard)/ElementId 참조값은 차단. 링크 사용자 매개변수는 공유 GUID로만 매칭(문서별 숫자 ID나 이름 추정 금지). 유형 값은 선택 밖 동일 유형에도 영향이 있어 UI/최종 확인에 안내.
- 일반 요소 일람표만. 키/재료 수량 산출/내포 일람표 제외. 후보 20,000개, 표 5,000행 상한. 필터 의존값을 직접 재검증할 수 없으면 중단.

### 중앙/로컬 안전

`LinkedModelEditService.Run`을 그대로 재사용. 운영 파일 직접 SaveAs/파일 복사 덮어쓰기 금지. 같은 Revit의 열린 원본/로컬을 재사용할 경우 기존 작업 포함 동기화 확인(기본 No), 문서/소유권 유지. 없으면 전용 로컬. 같은 파일의 여러 배치에서 나온 값은 한 번만 수정. 다른 사용자의 로컬을 원격으로 강제 저장/갱신하지 않는다. 현재 모델은 자동 동기화하지 않는다. 중앙 성공 뒤만 로그. 중간 실패는 나머지 파일 중단, 이미 동기화한 파일은 자동 되돌릴 수 없음. IFailuresPreprocessor 추가 금지.

### 검증

`.codex-build/unified-v100`에서 실제 모델/서비스/UI + Revit 대역을 실행. 행 범위, 충돌 방지 키, 표시 순서, 그룹 전체/개별 모호성 차단, 숨은 정렬/원본 보존/임시 복제 정리, Count 불일치, 유형 공동 편집, 입력 오류 원자성, 링크 ID 격리, 정렬 의존값 변경 감지 등 27개. 실제 코드는 2023~2027 빌드. Revit/서버는 [testing.md](testing.md) 별도 수동 검증.

공식 API 근거: [ViewSchedule / 그룹·필터·표시 요소](https://help.autodesk.com/cloudhelp/2023/ITA/Revit-API/files/Revit_API_Developers_Guide/Basic_Interaction_with_Revit_Elements/Views/View_Types/TableView/ViewSchedule/Revit_API_Revit_API_Developers_Guide_Basic_Interaction_with_Revit_Elements_Views_View_Types_TableView_ViewSchedule_Working_with_ViewSchedule_html.html), [ScheduleField](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/3d6b0eb5-ed36-278d-a5df-38b6d600e876.htm). 설치 API XML의 CustomField 인터페이스는 내부 API라고 명시되어 있어 사용하지 않았다.
