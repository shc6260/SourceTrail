# 도구 사용

MCP 후보 검색 → 원문 확인 → 확인한 이름으로 MCP 재검색 순서입니다. 모든 코드 결과는 텍스트·구문 후보이며 실행 관계를 확정하지 않습니다.

복사해서 사용하는 조사 프롬프트와 실행 순서는 [메인 README](../README.md)에 있습니다. 이미 원문으로 확인한 연결의 전체 사용처 조회를 추가하지 않으며, 조사 범위·방식을 늘려야 하면 먼저 확인합니다.

| 도구 | 용도와 주요 인자 |
|---|---|
| ping | 연결 확인 |
| prepare_indexes | 명시적 C#·UI·SQL 일괄 준비. rebuild 기본 false(증분), true(전체 재생성). 완료 후 HTTP 서버 재시작(stdio는 MCP 재연결). [사용법](index-preparation.md) |
| load_solution | solutionPath: 절대 소스 폴더 또는 .sln. 파일 목록만 먼저 준비. HTTP 공유 서버에서는 기본 차단 |
| get_analysis_status / reload_solution | 마지막 소스 색인 상태 / 파일 변경 대조 |
| find_ui_actions | query: 접수 저장 또는 접수저장, limit 기본 5 |
| find_event_handlers | file: C#·Designer 경로, query: 버튼·핸들러명 |
| search_text | text: 원문 문자열, directory: 알려진 폴더, extension: .cs·.Designer.cs·.config·.xml·.sql |
| find_symbol | query: 식별자·전체 선언명, offset/limit. 기본 20건 |
| get_symbol_overview | symbol: 소스 ID 또는 절대 C# 파일. 선언·멤버 목록 |
| find_reference_candidates | symbol: 선언 ID. 같은 이름의 구문상 사용 후보 |
| find_procedure_usage | procedure: SQL 이름. 문자열 리터럴 후보, IsVerifiedCall=false |
| find_database_calls | symbol: 메서드 ID·전체 이름, limit 기본 20. 현재 메서드 DB 후보와 methodCandidates |
| select_database_source | mode: SqlFiles, sqlFolder: 로컬 SQL 폴더 절대 경로. HTTP 공유 서버에서는 기본 차단 |
| get_database_status / refresh_database_source | SQL 색인 상태 / 파일 변경 대조 |
| find_sql_object | query, offset/limit, locationsOnly 또는 includeDefinition |
| analyze_procedure | procedure, includeDefinition. SQL 정의 출처와 직접 참조 |

## 검색 범위와 반환 의미

- `search_text`는 기본적으로 로컬 ripgrep으로 후보 파일을 찾고 해당 파일을 .NET으로 읽어 실제 줄·열·미리보기를 검증합니다. 숨김·gitignore 파일을 포함하고 기존 제외 폴더·확장자·원문 읽기 규칙을 유지합니다. UTF-16/UTF-32 BOM 파일도 기존 읽기로 확인합니다. 전체 결과에서 offset/limit을 적용하므로 limit은 검색 조기 종료가 아닙니다.
- rg가 없거나 실행 오류·30초 대기 제한이 발생하면 기존 검색으로 대체합니다. 사용자 취소는 대체 검색을 시작하지 않습니다. 검색 엔진·대체 여부와 `text.ripgrep`/`text.scan` 구간은 서버 로그에서 확인합니다. 설정은 [MCP 등록 및 설정](mcp-registration.md)을 참고하세요.
- 전체 이름은 일치하는 파일명을 우선합니다. 범위를 끝까지 검색하지 않은 경우 경고를 반환하며 추가 선언이 없다는 뜻이 아닙니다. 미발견 시 소스 후보 범위를 넓힙니다. 광범위 문자열·이름 참조 검색은 큰 폴더에서 오래 걸릴 수 있습니다.
- CommandConfiguration은 현재 메서드에서 이름이 설정됐다는 뜻입니다. 미사용 변수·조건 분기·나중 변경을 원문에서 확인합니다.
- AdapterCommandCandidate는 이름·파일명이 일치하는 Adapter의 행 상태 설정입니다. 실제 타입·상속·전달 행 상태는 확인 필요입니다. 찾지 못한 Adapter는 unresolved로 남깁니다.
- methodCandidates의 다음 호출명을 선택해 선언·원문 검색을 이어갑니다. 자동 재귀 탐색은 하지 않습니다.
- 후보 목록이 limit에 걸리면 truncated를 확인하고 범위를 좁히거나 한도를 조정합니다.
- locationsOnly와 includeDefinition을 동시에 사용할 수 없습니다. SQL은 로컬 파일만 읽습니다.

## 3.1 변경

- 클라이언트에 등록하는 브리지(`--bridge`)를 추가했습니다. 브리지는 공유 HTTP 서버가 없으면 켜고, 연결된 브리지가 모두 끝나면 서버가 10분 뒤 종료합니다. [HTTP 서버와 브리지](http-server.md)
- `find_procedure_usage`·`find_reference_candidates`는 ripgrep으로 이름이 들어 있는 C# 파일만 골라 읽습니다. 실제 업무 솔루션에서 프로시저 사용처 조회가 24~70초에서 0.2~1.6초로 줄었습니다. ripgrep이 없거나 실패하면 모든 파일을 읽는 기존 방식으로 동작합니다.
- 도구 이름·인자·응답 형식은 3.0과 같습니다.

## 3.0 변경

- 기본 실행 방식이 로컬 HTTP 상주 서버(`http://127.0.0.1:3920/mcp`)로 바뀌었습니다. stdio는 `--stdio`로 사용합니다. [HTTP 상주 서버](http-server.md)
- HTTP 공유 서버에서는 `load_solution`·`select_database_source`가 기본 차단됩니다. 한 클라이언트의 호출이 모든 클라이언트의 분석 대상을 바꾸기 때문입니다. `Server:AllowTargetChanges=true`로 허용할 수 있습니다.
- 도구 이름·인자·응답 형식은 2.0과 같습니다.

## 2.0 API 변경

- find_references는 find_reference_candidates로 대체했습니다. 실제 심볼 참조 판별이 아닙니다.
- trace_code_to_database, trace_procedure_to_ui, 두 compact 변형, trace_procedure_path는 제거했습니다.
- find_database_calls의 maxDepth/maxMethods와 압축 위치 응답을 제거했습니다. 현재 메서드 후보·파일·줄·다음 키워드만 반환합니다.
- LiveDatabase와 연결 문자열 설정은 제거했습니다.
- Ready는 파일 목록/검색 준비 상태입니다. 구문 진단은 실제 읽은 파일에 한정되며 CompilationErrors는 미평가(null 또는 생략)입니다.
