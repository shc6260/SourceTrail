# MCP 등록 및 설정

3.0부터 SourceTrail은 기본적으로 **로컬 HTTP 상주 서버**로 실행합니다. 서버를 PC에 하나 띄워 두고 Codex·Claude Code 등 각 클라이언트에는 주소 `http://127.0.0.1:3920/mcp`만 등록합니다. 서버 실행·설정·자동 시작은 [HTTP 상주 서버](http-server.md)를 먼저 따르세요. 클라이언트마다 프로세스를 따로 띄우는 stdio 방식은 `--stdio` 옵션으로 계속 사용할 수 있습니다(아래 [stdio로 등록](#stdio로-등록)).

## 1. 클라이언트별 등록 (HTTP, 기본)

분석 대상·캐시·색인 경로는 서버의 `appsettings.json` 또는 서버 실행 환경의 환경변수에 둡니다. HTTP 등록에는 서버 설정용 `env`를 넣지 않습니다. 서버가 실행 중이어야 클라이언트가 연결됩니다.

### Codex

사용자 설정 `~/.codex/config.toml` 또는 신뢰하는 프로젝트의 `.codex/config.toml`에 추가합니다. [OpenAI 공식 MCP 문서](https://learn.chatgpt.com/docs/extend/mcp?surface=cli)

```toml
[mcp_servers.sourcetrail]
url = "http://127.0.0.1:3920/mcp"
startup_timeout_sec = 30
tool_timeout_sec = 300
```

CLI로 등록할 때는 `codex mcp add sourcetrail --url http://127.0.0.1:3920/mcp`를 사용합니다. `startup_timeout_sec`는 연결 대기, `tool_timeout_sec`는 개별 호출 대기입니다. 공유 서버에서는 다른 클라이언트의 긴 호출이 끝날 때까지 기다릴 수 있으므로 호출 대기를 넉넉히 둡니다.

### Claude Code

분석할 프로젝트 루트의 `.mcp.json`에서 `mcpServers` 아래에 다음 항목을 추가합니다. 기존 파일이 있으면 다른 서버를 보존하고 `sourcetrail`만 병합합니다.

```json
{
  "mcpServers": {
    "sourcetrail": {
      "type": "http",
      "url": "http://127.0.0.1:3920/mcp",
      "timeout": 300000
    }
  }
}
```

프로젝트와 무관하게 개인 등록하려면 사용자 범위로 등록합니다. 프로젝트 범위와 사용자 범위에 같은 이름으로 중복 등록하지 마세요.

```powershell
claude mcp add --transport http --scope user sourcetrail http://127.0.0.1:3920/mcp
```

`timeout`은 서버 항목의 호출 대기(밀리초)입니다. Codex의 `startup_timeout_sec`·`tool_timeout_sec`는 Claude Code 설정 키가 아니므로 복사하지 않습니다. 등록 후 Claude Code를 재연결하고 `/mcp`, `claude mcp list`, `claude mcp get sourcetrail`로 상태를 확인합니다. 등록 성공과 실제 도구 호출 성공은 구분하고 아래 공통 검증을 수행하세요. [Claude Code 공식 MCP 문서](https://code.claude.com/docs/en/mcp)

### stdio로 등록

서버를 상주시키지 않고 클라이언트가 직접 실행하게 하려면 `args`에 `--stdio`를 붙입니다. **3.0에서는 `--stdio`가 없으면 HTTP 서버로 시작하므로 stdio 클라이언트가 연결되지 않습니다.** 이 방식은 클라이언트·대화마다 프로세스와 색인 메모리를 따로 사용합니다.

```toml
[mcp_servers.sourcetrail]
command = "dotnet"
args = ['D:\Tools\SourceTrail\artifacts\server\SourceTrail.Mcp.dll', '--stdio']
startup_timeout_sec = 30
tool_timeout_sec = 300

[mcp_servers.sourcetrail.env]
Analysis__SolutionPath = 'D:\Projects\Sample\Sample.sln'
Database__Mode = "SqlFiles"
Database__SqlFolder = 'D:\Projects\Sample\DatabaseScripts'
```

```json
{
  "mcpServers": {
    "sourcetrail": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["D:\\Tools\\SourceTrail\\artifacts\\server\\SourceTrail.Mcp.dll", "--stdio"],
      "env": {
        "Analysis__SolutionPath": "D:\\Projects\\Sample\\Sample.sln",
        "Database__Mode": "SqlFiles",
        "Database__SqlFolder": "D:\\Projects\\Sample\\DatabaseScripts"
      }
    }
  }
}
```

Claude Code의 stdio 시작 대기는 클라이언트를 실행하는 환경의 `MCP_TIMEOUT`(밀리초)으로 지정합니다. 로컬 절대 경로가 들어 있는 프로젝트 설정은 공유 저장소에 그대로 커밋하기 전에 각 사용자 환경에 맞는지 확인하세요.
### 공통 서버 설정

서버 옵션은 DLL 옆의 `appsettings.json`에 넣는 것을 권장합니다. publish 폴더의 [appsettings.example.json](../src/SourceTrail.Mcp/appsettings.example.json)을 복사해 사용합니다. HTTP 상주 서버는 서버를 실행하는 환경의 환경변수를, stdio는 클라이언트 등록의 `env`를 읽습니다. 환경변수가 JSON보다 우선하고, DLL 뒤에 서버 인자를 추가한 경우 명령줄 설정이 가장 우선합니다. 같은 옵션은 한 곳에서 관리하면 실제 적용값을 찾기 쉽습니다.

| 자주 쓰는 옵션 | 설정 및 용도 |
|---|---|
| `Server__Transport`, `Server__Url`, `Server__Path` | 실행 방식(기본 `Http`), 수신 주소(기본 `http://127.0.0.1:3920`, 루프백만 허용), 경로(기본 `/mcp`). [HTTP 상주 서버](http-server.md) 참고 |
| `Server__AllowTargetChanges` | 생략 시 HTTP는 `load_solution`·`select_database_source` 차단, stdio는 허용 |
| `Analysis__SolutionPath` | 기본 절대 소스 폴더 또는 `.sln` 경로. stdio에서 비워 두면 `load_solution`으로 선택. HTTP 공유 서버는 반드시 지정 |
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

1. HTTP는 서버가 수신 중인지 먼저 확인합니다(`Get-NetTCPConnection -LocalPort 3920 -State Listen`). 그다음 클라이언트의 MCP 연결을 다시 열고 `ping`을 호출해 `pong`을 확인합니다. Codex는 `codex mcp list`, Claude Code는 `claude mcp list` 또는 `/mcp`로 등록·연결 상태를 확인합니다. 목록 표시만으로 실제 검색 검증을 완료했다고 판단하지 않습니다.
2. `get_analysis_status`, `get_database_status`로 서버에 설정된 소스·SQL 경로를 확인합니다. 첫 조회 전에는 `NotLoaded`가 정상입니다.
3. stdio에서 기본 솔루션·SQL 폴더를 생략했다면 `load_solution`에 `{"solutionPath":"D:\\Projects\\Sample\\Sample.sln"}`, `select_database_source`에 `{"mode":"SqlFiles","sqlFolder":"D:\\Projects\\Sample\\DatabaseScripts"}`를 전달합니다. HTTP 공유 서버에서는 두 도구가 기본 차단되므로 서버 설정을 바꾸고 서버를 다시 시작합니다. 설정된 폴더를 미리 색인할 때는 `refresh_database_source`를 호출합니다.

| 증상 | 확인할 곳 |
|---|---|
| HTTP 연결 실패 | 서버가 실행 중인지, 등록 주소의 포트·경로(`/mcp`)가 서버 설정과 같은지, 서버 stderr의 시작 로그를 확인. 루프백이 아닌 주소를 설정하면 서버가 종료 코드 2로 끝남 |
| stdio에서 도구가 보이지 않거나 `ping` 실패 | `dotnet`이 실행되는지, DLL과 publish 폴더가 있는지, 등록 `args`에 `--stdio`가 있는지, 서버 stderr를 확인 |
| `load_solution`·`select_database_source`가 오류 | HTTP 공유 서버의 기본 차단. 서버 설정의 경로를 바꾸고 다시 시작하거나 `Server__AllowTargetChanges=true` 지정 |
| 호출이 평소보다 오래 기다림 | HTTP 공유 서버는 호출을 한 번에 하나씩 처리. 다른 클라이언트의 긴 호출이 끝나기를 기다리는 중일 수 있음 |
| `ping`은 성공하고 C#이 `NotLoaded` | 파일 색인 전이면 정상. 기본 솔루션 경로를 확인 |
| C#이 `Partial` | 읽은 파일의 구문 진단·경로·검색 범위 확인. 컴파일 결과가 아님 |
| SQL이 `NotLoaded` 또는 `NotConfigured` | `get_database_status`에서 모드가 `SqlFiles`이고 폴더가 맞는지 확인한 뒤 `refresh_database_source` 호출 |
| 첫 호출만 오래 걸림 | UI 색인, C# 선언 색인, SQL 색인이 별도로 준비됨. 후속 호출 시간과 분리해 확인 |

설치 시 미리 색인을 준비하려면 [일괄 색인 명령](index-preparation.md)을 실행한 뒤 서버를 시작(HTTP)하거나 MCP를 연결(stdio)하세요. 연결한 뒤에는 [도구 선택과 예제](tools.md)를 참고하세요.
