# HTTP 상주 서버 (3.0 기본)

3.0부터 SourceTrail의 기본 실행 방식은 **로컬 HTTP 상주 서버**입니다. PC에서 프로세스 하나를 계속 띄워 두고 Claude Code의 여러 대화, Codex, 스크립트 작업자가 같은 주소로 접속합니다. 색인은 그 프로세스가 처음 조회할 때 한 번만 메모리에 올리고 이후 모든 클라이언트가 재사용합니다.

## 왜 HTTP인가

stdio 방식에서는 클라이언트가 서버를 자기 자식 프로세스로 직접 띄워 1:1로 연결합니다. 그래서 대화·클라이언트마다 SourceTrail 프로세스가 따로 뜨고, 각 프로세스가 첫 조회 때 C#·SQL 색인을 다시 메모리에 올립니다. 큰 솔루션에서는 첫 C# 조회가 수 초 걸리고 프로세스마다 1GB 이상의 메모리를 사용할 수 있습니다.

| 구분 | HTTP (기본) | stdio (`--stdio`) |
|---|---|---|
| 프로세스 | PC에 하나, 모든 클라이언트가 공유 | 클라이언트·대화마다 하나 |
| 색인 메모리 적재 | 서버가 뜬 뒤 첫 조회 한 번 | 프로세스마다 첫 조회 때 |
| 수명 | 서버를 끌 때까지 | 클라이언트 연결이 끝나면 종료 |
| 등록 | 주소(URL)만 등록 | 실행 명령(`dotnet` + DLL) 등록 |
| 설정 위치 | 서버의 `appsettings.json`·환경변수 | 클라이언트 등록의 환경변수 또는 `appsettings.json` |
| 분석 대상 변경 도구 | 기본 차단 (모든 클라이언트에 영향) | 허용 |

## 실행

publish 폴더의 DLL을 인수 없이 실행하면 HTTP 서버로 시작합니다.

```powershell
dotnet D:\Tools\SourceTrail\artifacts\server\SourceTrail.Mcp.dll
```

시작 로그(stderr)에 `SourceTrail MCP starting (HTTP) at http://127.0.0.1:3920/mcp`가 표시됩니다. 클라이언트에는 `http://127.0.0.1:3920/mcp`를 등록합니다. 등록 방법은 [MCP 등록 및 설정](mcp-registration.md)을 참고하세요.

stdio로 실행하려면 `--stdio`를 붙입니다. `--http`는 HTTP를 명시합니다.

```powershell
dotnet D:\Tools\SourceTrail\artifacts\server\SourceTrail.Mcp.dll --stdio
```

## 서버 설정

HTTP 모드에서는 분석 대상과 캐시 설정을 **서버 쪽**에 둡니다. DLL 옆 `appsettings.json`이나 서버를 실행하는 환경의 환경변수를 사용합니다. 클라이언트 등록에는 주소만 적습니다.

```json
{
  "Server": {
    "Transport": "Http",
    "Url": "http://127.0.0.1:3920",
    "Path": "/mcp",
    "AllowTargetChanges": null
  },
  "Analysis": { "SolutionPath": "D:\\Projects\\Sample\\Sample.sln" },
  "Database": { "Mode": "SqlFiles", "SqlFolder": "D:\\Projects\\Sample\\DatabaseScripts" },
  "Indexing": { "Directory": "C:\\Users\\<사용자>\\AppData\\Local\\SourceTrail\\prepared-index" }
}
```

| 설정 | 기본값 | 설명 |
|---|---|---|
| `Server:Transport` | `Http` | `Http` 또는 `Stdio`. 명령줄 `--stdio`/`--http`가 우선 |
| `Server:Url` | `http://127.0.0.1:3920` | 수신 주소. **루프백 주소(127.0.0.1, localhost, [::1])만 허용**하며 다른 주소면 시작하지 않고 종료 코드 2로 끝납니다 |
| `Server:Path` | `/mcp` | MCP 엔드포인트 경로 |
| `Server:AllowTargetChanges` | 생략 | 생략하면 HTTP는 차단, stdio는 허용. `true`면 HTTP에서도 `load_solution`·`select_database_source` 허용 |

환경변수는 `Server__Url`처럼 `__`로 구분합니다. 우선순위는 명령줄 > 환경변수 > `appsettings.json`입니다.

## 자동 시작 (Windows)

로그인할 때 서버가 뜨도록 작업 스케줄러에 등록합니다. 경로는 자신의 publish 폴더로 바꿉니다.

```powershell
$dll = 'D:\Tools\SourceTrail\artifacts\server\SourceTrail.Mcp.dll'
$action = New-ScheduledTaskAction -Execute 'dotnet' -Argument ('"' + $dll + '"') -WorkingDirectory (Split-Path $dll)
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskName 'SourceTrail MCP' -Action $action -Trigger $trigger -Settings $settings
Start-ScheduledTask -TaskName 'SourceTrail MCP'
```

- 같은 포트에 서버가 이미 떠 있으면 두 번째 실행은 포트 사용 중 오류로 끝납니다. 프로세스는 하나만 유지됩니다.
- 상태 확인: `Get-ScheduledTask -TaskName 'SourceTrail MCP'`, 수신 확인: `Get-NetTCPConnection -LocalPort 3920 -State Listen`.
- 중지: `Stop-ScheduledTask -TaskName 'SourceTrail MCP'` 또는 해당 `dotnet` 프로세스 종료.

## 서버를 다시 시작해야 하는 경우

- 분석 대상(`Analysis:SolutionPath`, `Database:SqlFolder`)이나 캐시·색인 경로를 바꿨을 때
- `prepare-index`로 새 색인 세대를 게시했을 때 (실행 중인 프로세스는 이전 세대를 계속 사용)
- 새 버전을 publish해 실행 폴더를 바꿨을 때

클라이언트 등록은 주소가 같으면 다시 할 필요가 없습니다. 서버는 세션을 유지하지 않는(stateless) HTTP로 동작하므로 서버를 다시 시작해도 클라이언트의 다음 호출부터 바로 이어집니다.

## 동작과 제한

- **호출은 한 번에 하나씩 처리합니다.** 모든 클라이언트가 같은 분석기와 실행 게이트를 공유합니다. 결과가 섞이지 않는 대신 다른 클라이언트의 오래 걸리는 호출(예: 서버 시작 직후 첫 전체 `search_text`)이 끝날 때까지 기다릴 수 있습니다.
- **분석 대상 변경 도구는 기본 차단입니다.** `load_solution`·`select_database_source`는 한 클라이언트가 부르면 모든 클라이언트의 대상이 바뀌므로, HTTP에서는 설정을 바꾸고 서버를 다시 시작하라는 오류를 반환합니다. `reload_solution`·`refresh_database_source`·`prepare_indexes`는 대상을 바꾸지 않으므로 허용합니다.
- **인증이 없습니다.** 루프백 주소만 허용하므로 다른 PC에서는 접속할 수 없지만, 같은 PC의 다른 프로세스는 접속할 수 있습니다. 공용 PC에서는 사용하지 마세요.
- **세션을 유지하지 않습니다(stateless).** 서버에서 클라이언트로 보내는 알림(도구 목록 변경 알림 등)은 사용하지 않습니다.
- 서버 로그는 stderr로 출력됩니다. 작업 스케줄러로 실행하면 콘솔 창이 보일 수 있습니다.

## 2.x에서 옮기기

3.0에서 기본값이 바뀌어, **기존 stdio 등록(`dotnet …SourceTrail.Mcp.dll`)은 그대로 두면 동작하지 않습니다.** 클라이언트가 stdio 응답을 기다리는 동안 서버는 HTTP로 시작하기 때문입니다. 둘 중 하나를 선택하세요.

1. **권장: HTTP로 전환.** 클라이언트 등록의 환경변수를 서버의 `appsettings.json`으로 옮기고, 서버를 자동 시작에 등록한 뒤, 클라이언트 등록을 주소로 바꿉니다.
2. **stdio 유지.** 기존 등록의 `args` 끝에 `--stdio`를 추가합니다. 다른 설정은 그대로 동작합니다.
