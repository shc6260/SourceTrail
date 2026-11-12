# 도구별 예제

## 선택형 compact 추적

- `trace_code_to_database_compact`: 일반 forward trace와 같은 입력(`symbol`, `maxDepth`, `maxNodes`).
- `trace_procedure_to_ui_compact`: 일반 reverse trace와 같은 입력(`procedure`, `maxDepth`, `maxNodes`).
- 기존 추적 도구와 기본 응답은 변경하지 않습니다. compact는 schemaVersion=1인 별도 응답입니다.
- nodes에는 정확한 심볼 ID가 그대로 있습니다. edges의 from/to는 nodes의 0-based 인덱스이며 relation/evidence/file도 각각 relations/evidence/files 사전의 인덱스입니다. null 파일·줄은 그대로 보존합니다.
- 모든 호출 위치·관계·경고·snapshot·Partial/Truncated를 보존합니다. 생략·가지치기·검증되지 않은 호출 승격은 하지 않습니다. Core의 FlowCompactor.Expand로 원형을 복원할 수 있습니다.
- 호출 수를 줄이거나 WCF 구현을 자동 추론하는 기능은 아닙니다. 전체 영향 범위의 완전성을 보장하지 않습니다.

## 성능 계측과 캐시

- analyze_procedure는 C# freshness를 검사하지 않으며 SQL 소스 자체의 freshness·잠금·오류 처리는 유지합니다.
- 코드 입력 내용 해시는 스트림으로 계산하지만 전체 내용 검증 정책과 fingerprint 바이트 결합 방식은 유지합니다.
- 심볼 색인은 수집이 완료된 경우에만 공개합니다. 취소/실패한 부분 결과는 재사용하지 않습니다.
- SourceTrail.Analysis Meter는 단계별 ms, 입력 파일/바이트 수, 캐시 적중을 제공합니다. 호스트는 단계 시간과 입력 통계를 stderr 로그에 기록합니다. 캐시 적중은 exporter용 지표로 유지하고 노드마다 로그를 출력하지 않습니다. 로그에는 소스 본문·연결 정보가 포함되지 않습니다.
- queue 시간과 operation/freshness 시간은 분리됩니다. 단계는 중첩될 수 있으므로 duration을 단순 합산하지 마세요. 실제 MCP 응답 직렬화·네트워크·클라이언트 렌더링 시간은 해당 단계 로그에 포함되지 않습니다.

## 기존 도구

모든 도구는 읽기 전용입니다. 페이지 조회 도구는 offset(기본 0), limit(기본 100, 최대 1000)을 받으며 items/total/truncated/snapshotId/warnings를 반환합니다. 줄 번호는 1부터 시작합니다. source body는 반환하지 않습니다.

| 도구 | 입력 예제 |
|---|---|
| select_database_source | `{"mode":"SqlFiles","sqlFolder":"E:\\DatabaseScripts"}` |
| get_database_status | `{}` |
| refresh_database_source | `{}` |
| find_sql_object | `{"query":"Reception","includeDefinition":false}` |
| ping | `{}` |
| load_solution | `{"solutionPath":"D:\\Projects\\Sample\\Sample.sln"}` |
| get_analysis_status | `{}` |
| reload_solution | `{}` |
| find_symbol | `{"query":"Save","offset":0,"limit":100}` |
| get_symbol_overview | `{"symbol":"find_symbol에서 반환한 타입 ID"}` |
| find_references | `{"symbol":"선택한 심볼 ID"}` |
| search_text | `{"text":"usp_TestSave"}` |
| find_ui_actions | `{"query":"접수 저장","limit":5}` |
| trace_procedure_path | `{"symbol":"<exact symbol id>","procedure":"dbo.usp_TestSave"}` |
| find_procedure_usage | `{"procedure":"dbo.usp_TestSave"}` |
| analyze_procedure | `{"procedure":"dbo.usp_TestSave","includeDefinition":false}` |
| find_database_calls | `{"symbol":"Demo.Data.SaveService.Save","maxDepth":3,"maxMethods":32}` |
| trace_code_to_database | `{"symbol":"선택한 메서드 ID","maxDepth":5,"maxNodes":200}` |
| trace_procedure_to_ui | `{"procedure":"dbo.usp_TestSave","maxDepth":5,"maxNodes":200}` |

get_symbol_overview에는 절대 C# 파일 경로도 전달할 수 있습니다. 파일은 로딩된 솔루션의 분석 문서여야 합니다.

## 근거 종류

- TextMatch: 문자열·상수 후보. 호출을 의미하지 않음.
- RoslynReference: 심볼의 실제 참조 위치. 호출을 의미하지 않음.
- RoslynInvocation: 컴파일 모델에서 호출 대상 확인.
- EventSubscription: 이벤트 처리기 연결. 이벤트 발생을 의미하지 않음.
- DispatchCandidate: 인터페이스 호출의 구현 후보. 실행 대상 확인 필요.
- ConfiguredProcedureCall: Roslyn으로 해석한 메서드와 외부 DB 래퍼 계약이 일치.
- ProcedureCommandConfiguration: DbCommand 생성 시 StoredProcedure와 상수 CommandText 설정을 확인. 실행 확인이 아니므로 verified flow에서 제외.
- SqlDependency: 선택된 자료의 의존성. 출처와 파일/줄 또는 메타데이터 근거를 표시. LiveDatabase의 Access는 Unknown, SqlFiles는 Insert/ReadCandidate 등의 문법 근거를 표시.

프로시저 주석은 search_text에서 발견할 수 있으나 find_procedure_usage의 호출 결과에는 포함되지 않습니다. 후보는 해당 위치를 포함하는 Roslyn 심볼과 함께 반환합니다.

`search_text`는 마지막으로 로드한 솔루션의 검색 루트에서 현재 디스크 파일을 읽습니다. 의미 분석 변경 검사를 수행하지 않으며, 응답의 snapshot ID는 마지막 의미 분석 상태를 가리킵니다.

정방향 추적은 탐색 중인 심볼의 선언 문서에서 등록된 DB 래퍼 호출을 확인합니다. 전체 솔루션의 문자열 후보가 필요한 경우 `find_procedure_usage`를 사용합니다.

`trace_procedure_path`는 지정 프로시저로 이어지는 경로 한 개를 반환합니다. 일반 정적 연결을 우선하고, 구현 후보를 경유하면 `Candidate`로 표시합니다. `NotConnected`는 탐색 범위에서 연결을 찾지 못했다는 뜻입니다. SQL 직접 의존관계는 연결 여부와 별도로 반환합니다. 변경 후보, 실행 참조, 분류 미확정 항목을 먼저 보존하며 `Read`와 `ReadCandidate`만 최대 8개로 제한합니다. 전체 개수는 `dependencyCount`에 표시하고 각 항목의 분류와 확인 상태는 그대로 유지합니다. 전체 그래프, 경고 전문, SQL 본문은 기존 도구에서 조회할 수 있습니다. WCF 원격 구현 추론과 런타임 실행 검증은 하지 않습니다.

## 실패·제한 상태

- NotLoaded: load_solution 호출 전.
- Partial: 일부 프로젝트 오류, 구현 후보, DB 미설정/실패, 탐색 제한 등.
- NotConfigured: DB 연결 미설정.
- Unavailable: DB 연결/조회 실패.
- NotFoundOrNotVisible: 객체가 없거나 메타데이터 권한으로 보이지 않음.
- Ambiguous: 동일 프로시저명이 여러 스키마에 존재. schema.name 필요.
- truncated: 깊이·노드·검색 결과 제한으로 일부 결과 생략.

모호한 C# 이름은 오류와 함께 정확한 심볼 ID 선택을 요청합니다. 재로딩하면 ID와 캐시가 바뀝니다. 코드와 배포된 DB 정의가 같은 버전인지 별도 확인해야 합니다.

NotFoundInProvidedFiles는 제공된 SQL 파일에 없는 상태이며 실제 DB 부재를 의미하지 않습니다. [자료 선택·캐시·갱신](sql-sources.md)을 참고하세요.

`find_database_calls`는 메서드에서 도달할 수 있는 정적 SQL 실행 근거를 요약합니다. 생성 TableAdapter의 저장 명령, 상수가 확정된 직접 DB 명령, 등록된 프로시저 래퍼를 반환합니다. `site`, `via`, `unresolved`의 위치는 `[files의 0 기반 인덱스, 1 기반 줄 번호]`입니다. 상대 경로는 `solutionRoot`를 기준으로 합니다. 메서드별 한 경로를 유지하며, 실행 대상을 해석하지 못한 위치와 탐색 한도는 응답에 표시합니다.

`find_ui_actions`는 솔루션의 C# 프로젝트 폴더에서 Designer의 이벤트 구독, 폼·컨트롤 문구, 대응 핸들러와 메뉴의 폼 생성 근거를 색인합니다. 폼 경로를 모르고 시작하는 검색에 사용합니다. 기본 리소스 파일에서 `ApplyResources` 문구를 읽을 수 있고, 저장·수정 같은 동작은 코드 식별자와 함께 검색합니다. `StaticCandidates` 응답에서 파일·줄을 확인한 뒤 핸들러 본문을 읽어 실행 조건을 판단합니다. Roslyn 컴파일 작업 공간을 열 필요가 없습니다. 설정된 코드 캐시가 있으면 메모리와 디스크 색인을 재사용합니다.
