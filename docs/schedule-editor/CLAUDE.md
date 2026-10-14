# 일람표 에디터 (Schedule Editor)

## v109 — 2026-10-14 실제 진단 조회 / 공유 매개변수 소유 범위 / 작업세트

- 사용자가 연 실제 Revit 2026 일람표 에디터의 접근성 텍스트를 computer-use로 조회했다. 캡처는 시간 초과, 클릭은 geometry unavailable, 키보드 이동은 검증되지 않아 사용자가 ‘행 연결 진단’을 열었다. 표 2338, 후보 2339, 연결 2327, 다른 원시값 충돌 11행. 제작정보/KBIMS-부위코드/조달청표준공사코드는 표시 분포 불일치, 매개변수 연결 오류 4037셀. 모델 이름/원문 진단은 저장소에 보관하지 않는다. 실제 값 쓰기·중앙 동기화·설치는 하지 않았다.
- 공식 ScheduleFieldType.Instance는 공유 **유형** 매개변수도 포함한다. GUID 접근자는 실제로 소유하지 않는 인스턴스/유형에서도 대리 매개변수를 반환할 수 있다. Resolve가 반환값!=null이면 실제 유형 탐색을 중단하던 결함을 수정: 공유 GUID는 owner.Parameters의 실제 소유 집합에서 유일한 항목만 사용한다. IsReadOnly/HasValue로 소유를 추정하지 않는다. 양쪽 실제 소유가 확인되면 임의 우선순위로 쓰지 않고 차단. 기존 built-in 전용 접근자는 그대로 유지한다. 실제 사용자 필드마다 이 원인이 맞는지는 새 버전에서 대조해야 하며 모두 해결됐다고 단정하지 않는다.
- 작업세트는 ELEM_PARTITION_PARAM+Integer에만 opt-in ReferenceKind=작업세트. 문서별 UserWorkset GUID→이름 목록, 원래 작업세트 GUID 및 선택 GUID/이름 재검증 후 Set(int). 문서별 숫자 ID를 공유하지 않으며 원시 식별 키도 모델별 분리한다. 시스템 작업세트·임의 정수 직접 입력 제외, 작업세트 생성/강제 체크아웃/소유권 반납 없음. IsEditable(작업세트 소유 여부)을 모든 이동 가능성으로 오인해 필터링하지 않는다. Revit의 실제 Set/Commit 실패는 기존 문서 트랜잭션 롤백으로 처리한다.
- ConfigureEditors는 실제 편집 가능한 타깃의 저장형/참조 종류를 비교한다. 읽기 전용 행은 그대로 제한하고 다른 정상 행까지 전체 필드를 막지 않는다. 새 ‘편집 가능 행만 선택’은 명시적으로 누를 때만 선택을 교체하며 공동 RequiredRows의 일부라도 불가하면 그 공동 집합 모두 제외. Stage는 여전히 선택된 불가 행을 자동 건너뛰지 않고 차단한다. UI에 가능 행 수를 표시한다.
- Read/Snapshot의 doc.IsLinked는 영구 읽기 전용 대신 원본 로컬 미연결 안내. 실제 열린 원본의 IsReadOnly는 유지. MissingParameterReason은 모델/실제 GUID 또는 ID/유형·인스턴스 범위를 표시한다. 진단에 필드별 가능 행·원본 권한 집계 추가. GUID 불일치를 이름으로 우회하거나 매개변수를 자동 생성하지 않는다.
- 실제 서비스/UI+대역 unified-v109 306건, link-safety52, release91. 작업세트 이동·선택 전용·GUID/이름 stale·실패 rollback·실제 읽기 전용 유지, GUID 대리값(읽기 전용 및 writable-empty)과 실제 소유 유형/인스턴스·중복·다른 GUID 방어를 포함한다. 2023~2027 오류0. 사용자 모델에서 수정본 적용과 서버 동기화는 미검증. 새 설치 후 유형 공유값/작업세트 각각 소수 행으로 테스트하고 진단을 재비교할 것.
- 근거: [공유 매개변수 FieldType](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/9888db7d-00d0-4fd7-a1a9-cdd1fb5fce16.htm), [GUID 대리값 재현과 실제 Parameters 소유 확인](https://forums.autodesk.com/t5/revit-api-forum/element-get-parameter-guid-returns-unreliable-result/td-p/11340145), [작업세트 요소 변경](https://help.autodesk.com/cloudhelp/2024/CHS/Revit-API/files/Revit_API_Developers_Guide/Advanced_Topics/Worksharing/Revit_API_Revit_API_Developers_Guide_Advanced_Topics_Worksharing_Elements_in_Worksets_html.html).

## v108 — 2026-10-14 다중 카테고리의 누락 매개변수 / 행 연결 진단

- 사용자 v107 실측: 모든 소속 확인 불가, ‘표시값이 정확히 대응하는 식별 필드가 없습니다’, 승인 버튼은 ‘승인 불필요’. 실제 연결 세션은 없어 원본 표 수치의 직접 실측은 못 했다. 코드에서 Direct의 candidates.All, 전체 분포 완전 일치 요구, 링크 helper 검사 전 identityFields.Count==0 throw를 확인하고 대역으로 해당 실패 조건을 재현했다. **실제 모델의 유일한 원인을 확정한 것으로 기록하지 말 것.**
- 일부 요소에만 있는 필드도 식별 후보로 사용한다(Any). 직접 해석할 매개변수가 없는 후보는 표시 빈 문자열, 원시키 `missing`으로 비교하므로 존재하는 빈 문자열/unset과 혼동한 공동 집합은 차단한다. 수정 대상 매개변수 없는 셀은 Errors 유지, 생성/바인딩 변경 없음.
- 개별 표는 후보 열의 값별 개수가 표시 열의 값별 Count 이상이면 복합키 후보로 사용한다. 표 밖 후보 때문에 모든 열을 버리지 않되 **마지막 MatchRows에서 각 표시 키의 실제 후보 개수=표 Count 합과 원시값 동일 조건은 유지**한다. 임의 순번/수동 강제 연결 없음. 묶인 표의 필드 분포 완전 일치 규칙은 유지. 링크 helper 검사 후에만 식별 열 0개를 판단한다. 보조 열 추가 불가도 진단에 남기고 다른 열로 검증을 계속한다.
- ParameterIdentity를 순수 값(음수 ID/공유 GUID/host ID/비공유 이름+spec, 소유 범위)으로 분리하여 Resolve와 누락 guard가 동일 조회 경로를 쓴다. 누락 매개변수가 나중에 생기거나 유일한 연결로 바뀌면 ValidateGuards가 차단한다. 직접 해석 불가능한 필터/정렬은 해당 후보 행만 제한하며 모든 행의 mappingError를 덮어쓰지 않는다. 표에 실제 연결된 후보만 guard에 포함한다. 기존 열린 원본 값 비교·이름 연결 명시 승인·필드 편집 권한·중앙 처리 경로 불변.
- `[요소 연결 필요]`와 실제 `[읽기 전용]`을 구분한다. 승인 버튼은 미연결/셀 오류가 있으면 먼저 그 사유를 안내한다. 새 `행 연결 진단`은 로컬 모달 순수값 창이며 복사 가능한 읽기 전용 텍스트: 모델 이름별 후보 수, 데이터 행/Count, 필드 종류/연결 수/식별 제외 원인, 링크 파일·배치 표/후보 분포(각20종까지), 결과/셀 제한 집계. 매개변수 실제 값·요소 ID·파일 경로 자동 기록/전송 없음. WPF Guard 및 창 로컬 Theme 유지.
- `.codex-build/unified-v108` 280(기존263+신규17), link-safety52, release91 = 423 대역 회귀. 누락 필드/링크 원본만 수정/누락 guard/표 밖 후보/동일 키 과잉 차단/보조 열 fallback/승인 오안내/진단 UI/원본 임시 복제 정리를 검증했다. WPF 정상·실패·진단 렌더 확인. 2023~2027 빌드 오류0, 실제 Revit 표 표시 및 중앙 서버는 미검증. 남는 실패는 화면 한 줄이 아니라 `행 연결 진단`으로 후보/표 분포를 확인한다.

## v107 — 2026-10-14 다중 카테고리 링크 사용자 매개변수 / 동일 표시 행

- 사용자 실측: 링크 포함 시 동일 식별값 행으로 전체 읽기 전용. 대상은 다중 카테고리의 재료명·층·업체명·조달청표준공사코드·건축물번호 등 공유/프로젝트 매개변수, 추가로 `다음으로 IFC로 내보내기`. 기존 Match가 중복 키 한 건에도 전체 throw하고, Resolve가 링크 비공유 매개변수를 전부 제외하며, 표시용 링크 캐시 Parameter.IsReadOnly를 그대로 사용하던 제한을 수정했다.
- 개별 행의 임시 복제에 RVT_LINK_FILE_NAME_WITHOUT_EXT / RVT_LINK_INSTANCE_NAME을 노출하여 표시 분포가 실제 후보와 정확히 맞는 보조 열만 식별에 사용한다. 원본 정렬/필터 불변, 복제 finally 롤백, 보조 ScheduleField 객체는 롤백 밖으로 넘기지 않는다. 묶인 행에는 보조 필드를 추가하지 않는다(그룹 분할 방지). 링크 메타데이터 정렬/필터는 직접 요소 매개변수로 오인하지 않으며 최종 fingerprint로 확인한다.
- `MatchRows`는 키별로 개수와 원시값을 검사한다. 정상 키는 유지, 모호한 키만 사유를 표시한다. 개별 행의 동일 표시 키는 실제 후보 수=해당 데이터 행 Count 합이고 원시값도 동일한 경우에 한해 모든 행이 공동 대상 집합을 참조한다. 한 행↔한 요소를 주장하지 않는다. RequiredRows/`공동 N` 표시, Stage/StageReferences는 관련 행을 하나라도 빠뜨리면 누적 변경 전에 차단한다. 새 `동일 표시 행 함께 선택` 버튼은 사용자가 눌렀을 때만 관련 행을 추가한다. 비개별 중복 그룹/반올림 원시값 차이/개수 불일치는 여전히 차단한다. 이전 v100 '모든 중복 행 무조건 차단' 대신 이 전체 집합 편집만 허용하며 순번으로 배분 금지는 유지한다.
- 공유 필드는 GUID, built-in은 음수 ID 사용. 비공유 프로젝트 필드는 host ParameterElement.GetDefinition의 실제 이름/자료형과 대상 문서의 비공유 ParameterElement 파라미터를 비교하여 유일한 후보만 연결한다. 열 제목이나 문서 간 양수 ID를 식별자로 사용하지 않는다. 사용자 요청에 따라 이전 '링크 사용자 매개변수는 GUID만' 제한을 확장하되 **명시적 승인 없이 이름 매핑으로 수정하지 않는다**. 행별 ParameterMappings에 모델 키/경로·정의명·자료형·유형/인스턴스·실제 로컬 ID를 기록하고 `프로젝트 매개변수 연결 확인`에서 기본 No 확인 후 해당 연결만 이번 조회의 ApprovedMappings로 승인한다. Stage 두 경로가 승인 여부를 확인한다. 후보 중복/자료형 불일치/없는 파라미터는 차단하며 새 매개변수 생성/바인딩 변경 없음.
- 실제 편집 스냅샷은 FindOpenTarget의 열린 원본에서 요소 UniqueId와 매개변수를 다시 찾는다. 캐시와 원본의 owner/parameter key·원시값·자료형이 같을 때 원본의 읽기 전용 및 참조 목록을 사용한다. 원본의 기존 미동기화 값/삭제된 요소는 셀 오류로 처리한다. 필터/정렬 guards는 캐시 기준으로 원본 쓰기 직전 다시 확인한다. 최종 fingerprint에 공동 선택 범위·행 오류·대상 매개변수 Key도 포함했다. 실제 쓰기와 중앙 동기화는 기존 Run/Preflight/Validate/Apply만 사용한다.
- 일반 요소 일람표의 ProjectInfo 필드를 소속 문서 ProjectInformation으로 해석하고 모델 전체 영향 표시. IFC_EXPORT_ELEMENT_AS / IFC_EXPORT_ELEMENT_TYPE_AS 문자열 필드는 IsIfcEntity로 구분해 엔티티 입력 안내 제공. 예/아니요/유형별 IFC 내보내기 토글이나 PredefinedType을 자동 변경하지 않는다. IFC 스키마별 엔티티 선택기/검증/실제 export는 이번 범위 밖이며 별도 검증 필요. 공식 [필드 타입](https://help.autodesk.com/cloudhelp/2026/ENU/Revit-API-MainReference/files/html/9888db7d-00d0-4fd7-a1a9-cdd1fb5fce16.htm), [IFC 엔티티 지정과 export 여부](https://help.autodesk.com/cloudhelp/2024/ENU/Revit-DocumentPresent/files/GUID-7119A8C3-A0EE-4568-8C35-750410D867C9.htm).
- 동일 표시 행은 공동 대상 스냅샷/RequiredRows를 공유하여 행 수×공동 대상 수만큼 Revit API를 반복하지 않는다. 행별 Cells와 원래 순서는 유지하며 매개변수 작업값은 의도대로 공유한다.
- `.codex-build/unified-v107` 263건(기존235+28), link-safety52, release91 = 406 대역 회귀 통과. 모델 열 분리/원본 권한/캐시 미변경/기존 작업 보호/부분 모호성/공동 선택·스냅샷 공유/프로젝트 매개변수 승인·중복·자료형·GUID/IFC 저장을 검증했다. 실제 WPF 기본/최소 렌더 확인. 2023~2027 컴파일 성공(2023 일반 링크 포함은 기존 제한 유지). 실제 Revit 다중 카테고리 표 렌더링/서버 동기화/IFC export 성공은 보증하지 않으며 아래 테스트 문서에 따라 테스트 중앙에서 확인할 것.

## v105 — 2026-10-14 공통 링크 충돌 경로 차단

- NAMER/재료 지정과 공유하는 LinkedModelEditService가 자동 임시 로컬 생성·링크 해제·재열기·닫기를 제거했다. 같은 Revit에 편집 원본/로컬을 먼저 열어야 한다. 아래 과거 ‘없으면 전용 로컬’ 지침은 폐기한다. 상세 원인/규칙은 namer v105 및 linked-edit-crash-v105.md 참조.
- ScheduleEditorCommand도 첫 모델 쓰기 전에 변경 대상 전체에 UnifiedEditService.Preflight를 실행한다. 준비되지 않은 링크가 하나라도 있으면 현재 모델 포함 변경 없음. 기존 행 fingerprint/매개변수 guard/확인창/형식별 편집은 유지한다. 런타임 동기화 실패 시 이미 완료한 모델을 되돌릴 수 없으며, 실패 문서 변경을 보존하고 나머지를 중단한다.
- 실제 서비스/기존 UI 대역 회귀와 5개 연도 빌드 통과, 설치 코드의 사전검사 선행 및 위험 API 부재 검사. 실제 Revit 서버 쓰기 검증은 별도로 필요하다.

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
