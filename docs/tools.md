# 도구별 예제

모든 도구는 읽기 전용입니다. 페이지 조회 도구는 offset(기본 0), limit(기본 100, 최대 1000)을 받으며 items/total/truncated/snapshotId/warnings를 반환합니다. 줄 번호는 1부터 시작합니다. source body는 반환하지 않습니다.

| 도구 | 입력 예제 |
|---|---|
| ping | `{}` |
| load_solution | `{"solutionPath":"D:\\Projects\\Sample\\Sample.sln"}` |
| get_analysis_status | `{}` |
| reload_solution | `{}` |
| find_symbol | `{"query":"Save","offset":0,"limit":100}` |
| get_symbol_overview | `{"symbol":"find_symbol에서 반환한 타입 ID"}` |
| find_references | `{"symbol":"선택한 심볼 ID"}` |
| search_text | `{"text":"usp_TestSave"}` |
| find_procedure_usage | `{"procedure":"dbo.usp_TestSave"}` |
| analyze_procedure | `{"procedure":"dbo.usp_TestSave","includeDefinition":false}` |
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
- SqlDependency: DB 메타데이터의 직접 의존성. Access는 Unknown.

프로시저 주석은 search_text에서 발견할 수 있으나 find_procedure_usage의 호출 결과에는 포함되지 않습니다. 후보는 해당 위치를 포함하는 Roslyn 심볼과 함께 반환합니다.

## 실패·제한 상태

- NotLoaded: load_solution 호출 전.
- Partial: 일부 프로젝트 오류, 구현 후보, DB 미설정/실패, 탐색 제한 등.
- NotConfigured: DB 연결 미설정.
- Unavailable: DB 연결/조회 실패.
- NotFoundOrNotVisible: 객체가 없거나 메타데이터 권한으로 보이지 않음.
- Ambiguous: 동일 프로시저명이 여러 스키마에 존재. schema.name 필요.
- truncated: 깊이·노드·검색 결과 제한으로 일부 결과 생략.

모호한 C# 이름은 오류와 함께 정확한 심볼 ID 선택을 요청합니다. 재로딩하면 ID와 캐시가 바뀝니다. 코드와 배포된 DB 정의가 같은 버전인지 별도 확인해야 합니다.
