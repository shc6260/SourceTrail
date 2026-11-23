# HTTP 서버와 브리지

SourceTrail은 PC에서 **HTTP 서버 하나**가 분석기와 메모리 색인을 갖고, Claude Code의 여러 대화·Codex·스크립트 작업자가 그 서버를 함께 씁니다. 클라이언트에는 보통 **브리지**(`--bridge`)를 stdio MCP로 등록합니다. 브리지는 서버가 없으면 켜고 요청을 전달하며, 연결된 브리지가 하나도 없는 상태가 이어지면 서버가 스스로 꺼집니다.

## 실행 방식 비교

stdio 방식에서는 클라이언트가 서버를 자기 자식 프로세스로 직접 띄워 1:1로 연결합니다. 그래서 대화·클라이언트마다 SourceTrail 프로세스가 따로 뜨고, 각 프로세스가 첫 조회 때 C#·SQL 색인을 다시 메모리에 올립니다. 큰 솔루션에서는 첫 C# 조회가 수 초 걸리고 프로세스마다 1GB 가까운 메모리를 사용할 수 있습니다.

| 구분 | 브리지 (`--bridge`, 권장) | HTTP 상주 (`--http`, 기본) | 단독 stdio (`--stdio`) |
|---|---|---|---|
| 클라이언트 등록 | 실행 명령 `dotnet <DLL> --bridge` | 주소 `http://127.0.0.1:3920/mcp` | 실행 명령 `dotnet <DLL> --stdio` |
| 서버 프로세스 | PC에 하나, 모든 클라이언트 공유 | PC에 하나, 모든 클라이언트 공유 | 클라이언트·대화마다 하나 |
| 서버가 켜지는 때 | 브리지를 쓰는 클라이언트를 처음 열 때 | 직접 실행 또는 자동 시작 | 클라이언트가 실행 |
| 서버가 꺼지는 때 | 연결된 브리지가 없는 상태가 10분 이어지면 | 직접 끌 때까지 | 클라이언트 연결이 끝나면 |
| 설정 위치 | 서버의 `appsettings.json` (브리지도 같은 파일을 읽음) | 서버의 `appsettings.json`·환경변수 | 클라이언트 등록의 환경변수 또는 `appsettings.json` |
| 분석 대상 변경 도구 | 기본 차단 (모든 클라이언트에 영향) | 기본 차단 | 허용 |

DLL을 인수 없이 실행하면 HTTP 서버로 시작합니다. 시작 로그(stderr)에 `SourceTrail MCP starting (HTTP) at http://127.0.0.1:3920/mcp`가 표시됩니다.

## 브리지 동작

```text
클라이언트가 브리지 실행(stdio)
→ 서버가 수신 중인지 확인
→ 없으면 PC 전체 잠금을 잡고 작업 스케줄러 'SourceTrail MCP'로 서버 실행(작업이 없으면 자동 등록, 등록·실행이 안 되면 직접 실행)
→ JSON-RPC 한 줄씩 서버 /mcp로 전달, 응답을 stdout으로 반환
→ 30초마다 /bridge/heartbeat로 연결 신호
→ 클라이언트가 닫혀 stdin이 끝나면 /bridge/leave 후 종료
```

- **서버는 하나만 뜹니다.** 클라이언트 두 개가 동시에 열려도 PC 전체 잠금(임시 폴더의 잠금 파일)으로 한쪽만 서버를 켭니다.
- **서버는 클라이언트와 분리돼 실행됩니다.** 작업 스케줄러로 켠 서버는 클라이언트의 프로세스 묶음 밖에 있으므로, 서버를 켠 대화를 닫아도 다른 대화가 계속 사용합니다. 작업을 등록·실행할 수 없으면 브리지가 셸 실행으로 서버를 직접 켭니다. 이 경우 클라이언트가 프로세스 트리 전체를 종료하면 서버도 함께 종료될 수 있습니다.
- **서버 창은 뜨지 않습니다.** 작업과 직접 실행 모두 창 없는 실행기 `SourceTrail.Launcher.exe`로 `SourceTrail.Mcp.exe --http`를 켭니다. 서버 로그(stderr)는 화면에 표시되지 않습니다. publish 폴더에 실행기가 없는 이전 버전은 `dotnet …dll`로 켭니다.
- **서버가 도중에 꺼지면** 다음 요청이나 연결 신호에서 브리지가 서버를 다시 켜고 요청을 한 번 재시도합니다. 다시 켠 뒤에는 첫 조회에서 색인을 다시 메모리에 올립니다.
- **서버를 켜지 못하면** 브리지는 종료하지 않고 요청에 JSON-RPC 오류(`SourceTrail server unavailable`)를 반환합니다. 다음 요청에서 다시 시도합니다.
- 브리지는 요청을 전달만 하므로 색인을 메모리에 올리지 않습니다. stdout에는 JSON-RPC 메시지만 쓰고 진단은 stderr로 보냅니다.

## 자동 종료

`--Server:AutoShutdown=true`로 시작한 서버는 연결된 브리지를 셉니다. 90초 넘게 연결 신호가 없는 브리지는 끊어진 것으로 보고, 브리지가 하나도 없는 상태가 10분 이어지면 서버가 정상 종료합니다. 서버가 켜진 뒤 브리지가 한 번도 연결하지 않아도 10분 뒤 종료합니다. 대화를 열어 두기만 해도 그 브리지가 연결 신호를 보내므로 서버는 켜져 있습니다.

주소로 직접 접속한 클라이언트는 브리지 수에 포함되지 않습니다. 자동 종료 서버를 쓸 때는 모든 클라이언트를 브리지로 등록하세요. `AutoShutdown` 없이 직접 띄운 서버는 끌 때까지 유지됩니다.

## 서버 설정

분석 대상과 캐시 설정은 **서버 쪽**에 둡니다. DLL 옆 `appsettings.json`을 권장합니다. 브리지는 같은 파일에서 서버 주소와 연결 설정을 읽습니다.

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
| `Server:Transport` | `Http` | `Http`, `Bridge`, `Stdio`. 명령줄 `--http`/`--bridge`/`--stdio`가 우선 |
| `Server:Url` | `http://127.0.0.1:3920` | 서버 수신 주소이자 브리지가 접속할 주소. **루프백 주소(127.0.0.1, localhost, [::1])만 허용**하며 다른 주소면 시작하지 않고 종료 코드 2로 끝납니다 |
| `Server:Path` | `/mcp` | MCP 엔드포인트 경로 |
| `Server:AllowTargetChanges` | 생략 | 생략하면 HTTP는 차단, stdio는 허용. `true`면 HTTP에서도 `load_solution`·`select_database_source` 허용 |
| `Server:AutoShutdown` | `false` | `true`면 연결된 브리지가 없을 때 자동 종료. 브리지가 켜는 서버(작업 스케줄러 작업)는 `true`로 실행 |
| `Server:ShutdownAfterNoBridgesSeconds` | `600` | 브리지가 하나도 없을 때 종료까지 기다리는 시간 |
| `Server:BridgeTimeoutSeconds` | `90` | 이 시간 동안 연결 신호가 없는 브리지는 끊어진 것으로 판단 |
| `Server:HeartbeatSeconds` | `30` | 브리지의 연결 신호 주기. `BridgeTimeoutSeconds`보다 짧아야 함 |
| `Server:LaunchTask` | `SourceTrail MCP` | 브리지가 서버를 켤 때 실행할 작업 스케줄러 작업 이름. 없으면 브리지가 자동 등록. 비우면 작업 없이 직접 실행 |
| `Server:StartTimeoutSeconds` | `30` | 브리지가 서버 수신을 기다리는 최대 시간 |

환경변수는 `Server__Url`처럼 `__`로 구분합니다. 우선순위는 명령줄 > 환경변수 > `appsettings.json`입니다.

## 서버 실행용 작업 (Windows)

따로 등록하지 않습니다. 브리지가 서버를 켜야 할 때 `Server:LaunchTask` 작업이 없으면 다음 내용으로 자동 등록합니다. 현재 사용자 권한으로 등록하므로 관리자 권한이 필요 없습니다.

| 항목 | 값 |
|---|---|
| 실행 | publish 폴더의 `SourceTrail.Launcher.exe --http --Server:AutoShutdown=true` (창 없는 실행기) |
| 트리거 | 없음 (브리지가 필요할 때만 실행) |
| 계정 | 현재 사용자, 로그온한 상태에서 실행, 일반 권한 |
| 설정 | 실행 시간 제한 없음, 이미 실행 중이면 새 실행 무시, 배터리에서도 실행, 보통 우선순위 |
| 설명 | `Managed by the SourceTrail MCP bridge` 표시 포함 |

- **브리지가 등록한 작업**(설명에 위 표시가 있음)은 실행기 경로가 바뀌면(새 폴더에 publish) 다음 서버 시작 때 자동으로 다시 등록됩니다.
- **손으로 등록한 작업**은 바꾸지 않고 그대로 실행합니다. 3.1 이전 문서대로 `dotnet …dll`을 실행하도록 등록했다면 서버가 켜질 때 콘솔 창이 뜹니다. 지우면 다음 시작 때 브리지가 창 없는 작업으로 등록합니다: `Unregister-ScheduledTask -TaskName 'SourceTrail MCP' -Confirm:$false`
- 서버를 계속 켜 두려면 이 작업에 로그인 트리거(`New-ScheduledTaskTrigger -AtLogOn`)를 추가하고 `--Server:AutoShutdown=true`를 뺍니다. 이 경우 클라이언트는 브리지나 주소 어느 쪽으로 등록해도 됩니다. 새 폴더에 publish하면 브리지가 작업을 다시 등록하면서 추가한 트리거가 사라지므로 다시 추가합니다.
- 같은 포트에 서버가 이미 떠 있으면 두 번째 실행은 포트 사용 중 오류로 끝납니다. 프로세스는 하나만 유지됩니다.
- 상태 확인: `Get-NetTCPConnection -LocalPort 3920 -State Listen`. 실행 중인 서버는 `SourceTrail.Mcp.exe --http` 프로세스입니다(이전 방식은 `dotnet …SourceTrail.Mcp.dll --http`).

## 서버를 다시 시작해야 하는 경우

- 분석 대상(`Analysis:SolutionPath`, `Database:SqlFolder`)이나 캐시·색인 경로를 바꿨을 때
- `prepare-index`로 새 색인 세대를 게시했을 때 (실행 중인 프로세스는 이전 세대를 계속 사용)
- 새 버전을 publish해 실행 폴더를 바꿨을 때

실행 중인 서버(`--http`) 프로세스를 끄면 다음 호출에서 브리지가 새 서버를 켭니다. 서버는 세션을 유지하지 않는(stateless) HTTP로 동작하므로 서버가 바뀌어도 클라이언트의 다음 호출부터 바로 이어집니다.

## 동작과 제한

- **호출은 한 번에 하나씩 처리합니다.** 모든 클라이언트가 같은 분석기와 실행 게이트를 공유합니다. 결과가 섞이지 않는 대신 다른 클라이언트의 오래 걸리는 호출(예: 서버 시작 직후 첫 전체 `search_text`)이 끝날 때까지 기다릴 수 있습니다.
- **분석 대상 변경 도구는 기본 차단입니다.** `load_solution`·`select_database_source`는 한 클라이언트가 부르면 모든 클라이언트의 대상이 바뀌므로, HTTP 서버에서는 설정을 바꾸고 서버를 다시 시작하라는 오류를 반환합니다. `reload_solution`·`refresh_database_source`·`prepare_indexes`는 대상을 바꾸지 않으므로 허용합니다.
- **인증이 없습니다.** 루프백 주소만 허용하므로 다른 PC에서는 접속할 수 없지만, 같은 PC의 다른 프로세스는 접속할 수 있습니다. 공용 PC에서는 사용하지 마세요.
- **세션을 유지하지 않습니다(stateless).** 서버에서 클라이언트로 보내는 알림(도구 목록 변경 알림 등)은 사용하지 않습니다.
- **메모리:** 서버는 색인을 올린 뒤 최대 1GB 안팎을 사용할 수 있습니다. 오래 쓰지 않으면 Windows가 메모리를 디스크로 내보내 실제 사용량이 줄고, 다음 첫 조회가 몇 초 느려질 수 있습니다. 자동 종료 서버는 모든 클라이언트를 닫으면 메모리를 반환합니다.

## 2.x에서 옮기기

3.0부터 DLL을 인수 없이 실행하면 HTTP 서버로 시작하므로, **기존 stdio 등록(`dotnet …SourceTrail.Mcp.dll`)은 그대로 두면 연결되지 않습니다.** 하나를 선택하세요.

1. **권장: 브리지로 전환.** 클라이언트 등록의 환경변수를 서버의 `appsettings.json`으로 옮기고, 등록 `args` 끝에 `--bridge`를 붙입니다. 서버 실행용 작업은 브리지가 자동 등록합니다.
2. **HTTP 주소로 전환.** 서버를 계속 띄워 두고(로그인 트리거) 클라이언트에 주소를 등록합니다.
3. **단독 stdio 유지.** 기존 등록의 `args` 끝에 `--stdio`를 추가합니다. 다른 설정은 그대로 동작합니다.
