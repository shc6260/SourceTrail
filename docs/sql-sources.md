# 로컬 SQL 폴더와 색인

DB 검색에는 로컬에 저장된 SQL 스크립트를 사용합니다. [Codex 등록 설정](codex-registration.md)에서 `SqlFiles`와 폴더를 지정하거나, 현재 세션에서 `select_database_source`에 아래 입력을 전달하세요.

## SQL 폴더

```json
{"mode":"SqlFiles","sqlFolder":"E:\\DatabaseScripts"}
```

이 입력은 `select_database_source`의 arguments입니다. 경로는 사용자마다 지정하며 특정 경로를 코드에 넣지 않습니다. 하위 폴더의 `.sql`을 탐색하고 ScriptDom SQL Server 2025 문법 분석기로 실제 CREATE/ALTER/CREATE OR ALTER 정의를 분석합니다. 파일명은 개체 식별에 사용하지 않습니다. SSMS 개체별 파일, 여러 개체를 포함하는 파일, GO 구분, USE 문을 지원합니다.

프로시저·테이블·뷰·함수를 색인합니다. 파라미터와 테이블 컬럼, 선언된 제약조건 텍스트, 참조 이름과 SQL 위치를 반환합니다. ALTER TABLE ADD로 추출된 컬럼/제약조건도 테이블 정의에 연결합니다. 사용자·스키마·DB 생성·사용자 정의 타입·인덱스와 그 밖의 ALTER TABLE DDL은 아직 구조 색인 대상이 아니며 경고를 표시합니다. CHECK/NOCHECK CONSTRAINT의 활성 상태나 적용 순서에 따른 최종 배포 상태는 재현하지 않습니다. NULL 선언이 없으면 nullable은 null(확인 필요)입니다.

`find_sql_object`는 객체/멤버/참조/파일/줄을 보여줍니다. 원본 정의는 includeDefinition=true일 때 반환합니다. `analyze_procedure`는 선택 모드에 따라 공통 ProcedureAnalysis를 반환합니다. SQL 파일 모드는 database.schema.name도 허용합니다. 중복 정의는 자동 병합하지 않습니다. 이름이 없는 기본 스키마, CTE, 별칭, 임시 객체는 후보 또는 미해결 상태로 남을 수 있습니다.

파일과 줄만 찾을 때는 `find_sql_object`에 `locationsOnly=true`를 전달합니다. 같은 후보·순서·페이지 정보와 색인 상태, 관련 경고를 반환하며 객체별 본문·멤버·참조·제약조건을 생략합니다. `includeDefinition=true`는 상세 조회에서 사용할 수 있습니다.

두 도구의 `indexStatus`와 `indexWarningCount`는 해당 `snapshotId`의 전체 색인 상태와 경고 건수입니다. 각 응답의 `warnings`에는 관련 파일·객체 진단이 포함되며, 색인 전체 경고는 `get_database_status`에서 확인합니다. 검색 결과가 비어 있고 색인이 `Partial`이면 제공된 스크립트에서 객체를 찾지 못한 이유를 확인해야 합니다. `trace_procedure_path`는 결과별 SQL 경고 건수를 `sqlWarningCount`, 색인 전체 경고 건수를 `sqlIndexWarningCount`로 반환합니다.

INSERT 대상은 Insert, 조회 참조는 ReadCandidate, UPDATE/DELETE/MERGE는 별칭 바인딩 확인이 필요해 Candidate로 표시합니다. 컬럼 사용은 완전한 의미 바인딩/데이터 흐름 분석을 제공하지 않습니다. EXEC 문자열·변수와 sp_executesql은 동적 SQL 경고를 반환하고 실행하지 않습니다. 본문 참조 없음은 업무 영향 없음의 증거가 아닙니다.

결과에는 `Source=SqlFiles`, 원본 파일, 분석 시각을 표시합니다. `NotFoundInProvidedFiles`가 반환되면 분석 대상으로 지정한 폴더에 해당 정의가 포함되어 있는지 확인하세요.

## 캐시와 갱신

SQL 색인은 기본 `%LOCALAPPDATA%\SourceTrail\cache`에 폴더별 JSON으로 저장합니다. `Database.CacheDirectory`가 빈 값이면 기본 경로를 사용합니다. 원본 SQL 정의를 포함하는 개인 로컬 캐시이므로 공개 저장소에 넣지 않습니다. SQL 원본은 변경하지 않습니다. 캐시는 삭제해도 다시 생성할 수 있습니다.

최초 로딩/재실행/수동 refresh_database_source 때 전체 파일 목록과 SHA-256 내용을 대조하고, 새 파일·수정 파일만 파싱합니다. 삭제된 파일 정보는 제거합니다. 파서/모델 버전 변경 시 캐시 버전을 올려 재생성합니다. 손상된 캐시는 재생성하고 저장 실패 시 메모리 색인을 유지하며 경고를 반환합니다. ReusedFiles와 ParsedFiles로 재사용 여부를 확인합니다.

WatchFiles=true이면 파일 감지 후 다음 요청에서 갱신합니다. 이벤트 누락 보완을 위해 다음 요청에서 30초 주기로 전체 목록/내용을 다시 대조합니다. WatchFiles=false이면 각 SQL 요청에서 대조합니다. 즉 조회 중인 한 요청은 로딩된 분석 자료를 사용하며 실시간 DB 실행 결과를 보장하지 않습니다. get_database_status는 마지막 색인 상태를 보고합니다.

C# 작업 공간, UI 색인, SQL 파일 색인은 각기 준비됩니다. C# 입력 변경 확인과 디스크 색인 재사용 조건은 [구조와 동작 원리](architecture.md)에 있습니다.

## 검증

합성 SQL로 개별/통합 파일, 하위 폴더, UTF-16 BOM, 디스크 재사용, 파일 추가·수정·삭제, 감지, 손상 캐시 복구, 동적 SQL, 중복 정의와 모드 전환을 검증합니다.
