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

`startup_timeout_sec`는 서버 연결 대기, `tool_timeout_sec`는 개별 호출 대기입니다. 예시는 대형 솔루션의 첫 로딩을 고려한 값입니다. 프로젝트를 매번 선택하려면 `env` 표의 경로 설정을 생략하고 도구에서 지정할 수 있습니다.

같은 서버 옵션은 DLL 옆의 `appsettings.json`에도 넣을 수 있습니다. publish 폴더의 [appsettings.example.json](../src/SourceTrail.Mcp/appsettings.example.json)을 복사해 사용합니다. 환경변수가 JSON보다 우선하고, DLL 뒤에 서버 인자를 추가한 경우 명령줄 설정이 가장 우선합니다. 같은 옵션은 한 곳에서 관리하면 실제 적용값을 찾기 쉽습니다.

| 자주 쓰는 옵션 | 설정 및 용도 |
|---|---|
| `Analysis__SolutionPath` | 기본 절대 `.sln` 경로. 비워 두면 `load_solution`으로 선택 |
| `Analysis__CodeCacheDirectory` | 쓰기 가능한 로컬 C# 디스크 색인 경로. 빈 값은 디스크 재사용 비활성화 |
| `Database__Mode`, `Database__SqlFolder` | SQL 파일 사용 시 `SqlFiles`와 절대 폴더 경로. 모드를 생략하면 `LiveDatabase` |
| `Database__CacheDirectory`, `Database__WatchFiles` | SQL 캐시 경로와 변경 감지. 기본 경로와 갱신 조건은 [SQL 자료와 캐시](sql-sources.md) 참고 |
| `Analysis__MaxDepth`, `Analysis__MaxNodes`, `Analysis__MaxResults` | 흐름·검색 기본 한도. 기본값은 각각 5, 200, 100 |
| `Analysis__AdditionalSearchRoots__0` | 기본 솔루션 외 추가 텍스트 검색 경로 |
| `Analysis__ProcedureCallRules__0__TypeName`, `Analysis__ProcedureCallRules__0__MethodName`, `Analysis__ProcedureCallRules__0__ArgumentIndex` | DB 래퍼 규칙의 타입 전체 이름, 메서드명, 0부터 시작하는 인자 위치. [JSON 설정 예제](settings.demo.example.json) 참고 |

실제 DB를 쓸 경우 `LiveDatabase`와 `Database__ConnectionString`을 서버에 설정합니다. 연결 문자열은 비공개 설정이나 서버가 상속받는 환경변수로 관리하고, 계정은 필요한 메타데이터 조회 권한만 사용합니다.

## 2. 연결과 대상 확인

1. 새 설정을 읽도록 MCP 연결을 다시 열고 Codex에서 `ping`을 호출합니다. CLI의 `codex mcp list`는 등록 여부를 확인할 때 사용합니다.
2. 기본 솔루션을 생략했다면 `load_solution`에 `{"solutionPath":"D:\\Projects\\Sample\\Sample.sln"}`을 전달합니다. `get_analysis_status`로 프로젝트 진단을 확인합니다.
3. 기본 SQL 폴더를 생략했다면 `select_database_source`에 `{"mode":"SqlFiles","sqlFolder":"D:\\Projects\\Sample\\DatabaseScripts"}`를 전달합니다. 설정된 폴더를 미리 색인할 때는 `refresh_database_source`를 호출합니다. `get_database_status`는 마지막 상태를 읽습니다.

| 증상 | 확인할 곳 |
|---|---|
| 도구가 보이지 않거나 `ping` 실패 | `dotnet`이 실행되는지, DLL과 publish 폴더가 있는지, 등록된 인자와 서버 stderr를 확인 |
| `ping`은 성공하고 C#이 `NotLoaded` | 의미 분석 전이면 정상. 기본 솔루션 경로를 확인하거나 `load_solution` 호출 |
| C#이 `Partial` | 프로젝트 진단의 누락된 SDK·참조 어셈블리·외부 DLL·targets 확인 |
| SQL이 `NotLoaded` 또는 `NotConfigured` | `get_database_status`의 모드와 폴더 설정 확인. `LiveDatabase`의 `Configured`는 연결 성공을 보장하지 않음 |
| 첫 호출만 오래 걸림 | UI 색인, C# 작업 공간, SQL 색인이 별도로 준비됨. 후속 호출 시간과 분리해 확인 |

연결한 뒤에는 [도구 선택과 예제](tools.md)를 참고하세요.
