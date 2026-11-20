# MCP 등록 및 설정

SourceTrail은 로컬 stdio MCP 서버입니다. Codex와 Claude Code는 같은 publish 폴더와 서버 설정을 사용할 수 있으며 등록은 클라이언트별로 수행합니다. 다른 로컬 stdio MCP 클라이언트는 해당 제품의 등록 방식을 따르세요.

## 1. 클라이언트별 서버 등록

### Codex

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

### Claude Code

분석할 프로젝트 루트의 `.mcp.json`에서 `mcpServers` 아래에 다음 항목을 추가합니다. 기존 파일이 있으면 다른 서버를 보존하고 `sourcetrail`만 병합합니다. 아래 경로는 예시이므로 실제 절대 경로로 바꾸세요.

```json
{
  "mcpServers": {
    "sourcetrail": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["D:\\Tools\\SourceTrail\\artifacts\\server\\SourceTrail.Mcp.dll"],
      "env": {
        "Analysis__SolutionPath": "D:\\Projects\\Sample\\Sample.sln",
        "Analysis__CodeCacheDirectory": "D:\\Caches\\SourceTrail\\Code",
        "Database__Mode": "SqlFiles",
        "Database__SqlFolder": "D:\\Projects\\Sample\\DatabaseScripts"
      }
    }
  }
}
```

프로젝트 범위 설정은 Claude Code에서 사용 승인을 요청할 수 있습니다. 로컬 절대 경로가 들어 있으므로 공유 저장소에 그대로 커밋하기 전에 각 사용자 환경에 맞는지 확인하세요.

프로젝트 간 개인 등록이 필요하면 서버 설정을 DLL 옆 `appsettings.json`에 지정한 후 PowerShell에서 다음처럼 사용자 범위로 등록할 수도 있습니다. 프로젝트 범위와 사용자 범위에 같은 이름으로 중복 등록하지 마세요.

```powershell
claude mcp add --transport stdio --scope user sourcetrail -- dotnet 'D:\Tools\SourceTrail\artifacts\server\SourceTrail.Mcp.dll'
```

Claude Code를 재연결한 뒤 `/mcp`에서 상태를 확인합니다. CLI의 `claude mcp list`와 `claude mcp get sourcetrail`로 등록 정보를 확인할 수 있습니다. 등록 성공과 실제 도구 호출 성공은 구분하고 아래 공통 검증을 수행하세요. [Claude Code 공식 MCP 문서](https://code.claude.com/docs/en/mcp)

Codex의 `startup_timeout_sec`·`tool_timeout_sec`는 Claude Code 설정 키가 아닙니다. Claude Code의 시작 대기는 클라이언트를 실행하는 환경의 `MCP_TIMEOUT`(밀리초), 호출 대기는 서버 항목의 `timeout`(밀리초, 예: `300000`)으로 설정할 수 있습니다. 기존 값을 유지하고 설치된 버전의 지원 여부를 확인하세요. 시작 대기 30초를 지정하려면 PowerShell에서 `$env:MCP_TIMEOUT='30000'`을 지정한 뒤 같은 셸에서 `claude`를 실행합니다. 이는 서버의 `env`에 넣는 설정과 구분합니다.

### 공통 서버 설정

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
| `Analysis__UseRipgrep` | 텍스트 검색에 ripgrep 후보 파일 검색 사용. 기본 `true`; `false`는 기존 .NET 검색. rg가 없거나 실행에 실패하면 기존 검색으로 대체 |
| `Analysis__RipgrepPath` | 선택적 `rg.exe`/`rg` 절대 경로. 비워 두면 PATH에서 찾으며 Windows는 `rg.exe` 사용. 코드·SQL 경로나 색인 경로와 별개 |
| `Analysis__ProcedureCallRules__0__TypeName`, `Analysis__ProcedureCallRules__0__MethodName`, `Analysis__ProcedureCallRules__0__ArgumentIndex` | DB 래퍼 규칙의 타입 힌트(의미 바인딩하지 않음), 메서드명, 0부터 시작하는 인자 위치. [JSON 설정 예제](settings.demo.example.json) 참고 |

DB 검색에 사용할 자료는 `.sql` 파일이 들어 있는 로컬 폴더입니다. `Database__Mode`를 `SqlFiles`로 명시하고 폴더 경로를 지정하세요. 이 설정에는 DB 연결 문자열이 필요 없습니다.

## 2. 연결과 대상 확인

1. 새 설정을 읽도록 사용하는 클라이언트의 MCP 연결을 다시 열고 `ping`을 호출해 `pong`을 확인합니다. Codex는 `codex mcp list`, Claude Code는 `claude mcp list` 또는 `/mcp`로 등록·연결 상태를 확인합니다. 목록 표시만으로 실제 검색 검증을 완료했다고 판단하지 않습니다.
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
