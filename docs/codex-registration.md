# Codex 등록 및 설정

## 1. 서버 등록

[README](../README.md)의 publish 명령을 실행한 뒤, 사용자 설정 `~/.codex/config.toml` 또는 신뢰하는 프로젝트의 `.codex/config.toml`에 아래 항목을 추가합니다. 예시 경로를 자신의 절대 경로로 바꿉니다. [OpenAI 공식 MCP 문서](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)

```toml
[mcp_servers.sourcetrail]
command = "dotnet"
args = ['D:\Tools\SourceTrail\artifacts\server\SourceTrail.Mcp.dll']
startup_timeout_sec = 30
tool_timeout_sec = 300

[mcp_servers.sourcetrail.env]
Analysis__SolutionPath = 'D:\Projects\Sample\Sample.sln'
Analysis__CodeCacheDirectory = 'D:\Caches\SourceTrail\Code'
Database__Mode = "SqlFiles"
Database__SqlFolder = 'D:\Projects\Sample\DatabaseScripts'
```

`startup_timeout_sec`는 서버 연결 대기, `tool_timeout_sec`는 개별 호출 대기입니다. 예시는 대형 소스 폴더의 첫 색인을 고려한 값입니다. 프로젝트를 매번 선택하려면 `env` 표의 경로 설정을 생략하고 도구에서 지정할 수 있습니다.

같은 서버 옵션은 DLL 옆의 `appsettings.json`에도 넣을 수 있습니다. publish 폴더의 [appsettings.example.json](../src/SourceTrail.Mcp/appsettings.example.json)을 복사해 사용합니다. 환경변수가 JSON보다 우선하고, DLL 뒤에 서버 인자를 추가한 경우 명령줄 설정이 가장 우선합니다. 같은 옵션은 한 곳에서 관리하면 실제 적용값을 찾기 쉽습니다.

| 자주 쓰는 옵션 | 설정 및 용도 |
|---|---|
| `Analysis__SolutionPath` | 기본 절대 소스 폴더 또는 `.sln` 경로. 비워 두면 `load_solution`으로 선택 |
| `Analysis__CodeCacheDirectory` | 쓰기 가능한 로컬 C# 디스크 색인 경로. 빈 값은 디스크 재사용 비활성화 |
| `Indexing__Directory` | 일괄 색인 저장소. 기본 `%LOCALAPPDATA%\SourceTrail\prepared-index`. 준비 명령과 같은 경로 사용. 활성 세대가 분석 경로와 일치하면 해당 세대의 C#·SQL 캐시가 개별 캐시 설정보다 우선 |
| `Analysis__WatchCodeFiles` | 배포 예제·신규 설치 프롬프트는 `true`: 파일 알림이 없고 주기 내이면 대조 생략. 라이브러리의 설정 생략 시 기본은 `false`: 매 분석 요청마다 입력 대조. 알림 전달 지연 없이 요청마다 확인해야 하면 `false` 사용 |
| `Analysis__CodeRescanIntervalSeconds` | 감시 모드 전체 대조 주기. 기본 30초, 1~3600초 범위로 제한. 실제 검사는 다음 분석 요청에서 수행 |
| `Database__Mode`, `Database__SqlFolder` | `SqlFiles`와 로컬 SQL 스크립트 폴더의 절대 경로 |
| `Database__CacheDirectory`, `Database__WatchFiles` | SQL 캐시 경로와 변경 감지. 기본 경로와 갱신 조건은 [SQL 자료와 캐시](sql-sources.md) 참고 |
| `Analysis__MaxResults` | 검색 기본 반환 건수. 기본 20, 페이지로 추가 조회 |
| `Analysis__AdditionalSearchRoots__0` | 기본 솔루션 외 추가 텍스트 검색 경로 |
| `Analysis__ProcedureCallRules__0__TypeName`, `Analysis__ProcedureCallRules__0__MethodName`, `Analysis__ProcedureCallRules__0__ArgumentIndex` | DB 래퍼 규칙의 타입 힌트(의미 바인딩하지 않음), 메서드명, 0부터 시작하는 인자 위치. [JSON 설정 예제](settings.demo.example.json) 참고 |

DB 검색에 사용할 자료는 `.sql` 파일이 들어 있는 로컬 폴더입니다. `Database__Mode`를 `SqlFiles`로 명시하고 폴더 경로를 지정하세요. 이 설정에는 DB 연결 문자열이 필요 없습니다.

## 2. 연결과 대상 확인

1. 새 설정을 읽도록 MCP 연결을 다시 열고 Codex에서 `ping`을 호출합니다. CLI의 `codex mcp list`는 등록 여부를 확인할 때 사용합니다.
2. 기본 솔루션을 생략했다면 `load_solution`에 `{"solutionPath":"D:\\Projects\\Sample\\Sample.sln"}`을 전달합니다. `get_analysis_status`로 프로젝트 진단을 확인합니다.
3. 기본 SQL 폴더를 생략했다면 `select_database_source`에 `{"mode":"SqlFiles","sqlFolder":"D:\\Projects\\Sample\\DatabaseScripts"}`를 전달합니다. 설정된 폴더를 미리 색인할 때는 `refresh_database_source`를 호출합니다. `get_database_status`는 마지막 상태를 읽습니다.

| 증상 | 확인할 곳 |
|---|---|
| 도구가 보이지 않거나 `ping` 실패 | `dotnet`이 실행되는지, DLL과 publish 폴더가 있는지, 등록된 인자와 서버 stderr를 확인 |
| `ping`은 성공하고 C#이 `NotLoaded` | 파일 색인 전이면 정상. 기본 솔루션 경로를 확인하거나 `load_solution` 호출 |
| C#이 `Partial` | 읽은 파일의 구문 진단·경로·검색 범위 확인. 컴파일 결과가 아님 |
| SQL이 `NotLoaded` 또는 `NotConfigured` | `get_database_status`에서 모드가 `SqlFiles`이고 폴더가 맞는지 확인한 뒤 `refresh_database_source` 호출 |
| 첫 호출만 오래 걸림 | UI 색인, C# 선언 색인, SQL 색인이 별도로 준비됨. 후속 호출 시간과 분리해 확인 |

설치 시 미리 색인을 준비하려면 [일괄 색인 명령](index-preparation.md)을 실행한 뒤 MCP를 연결하세요. 연결한 뒤에는 [도구 선택과 예제](tools.md)를 참고하세요.
