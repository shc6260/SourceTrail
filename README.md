# SourceTrail MCP

C# WinForms의 화면·이벤트에서 메서드와 DB 호출을 찾고, SQL Server 스크립트에서 객체 정의와 참조를 확인하는 읽기 전용 MCP 서버입니다. 로컬에서 stdio로 실행하며 분석 근거를 파일과 줄 번호로 반환합니다.

## Codex에 설치 맡기기

아래 프롬프트를 Windows의 Codex에 그대로 붙여넣으세요. 분석할 솔루션과 SQL 폴더를 알고 있다면 프롬프트 끝에 경로를 적어도 됩니다.

```text
이 Windows PC에 SourceTrail MCP를 설치하고 현재 Codex에 등록해줘.
저장소는 https://github.com/shc6260/SourceTrail 이야.
설치 방법을 설명하는 데서 멈추지 말고, 가능한 단계는 직접 실행해줘.

1. 기존 SourceTrail 등록과 체크아웃을 먼저 확인해줘. 사용할 수 있는 체크아웃이 없으면 사용자 폴더에 저장소를 복제해줘. Git이나 .NET 10 SDK가 없으면 공식 배포본으로 설치하고, 권한 승인이 필요한 경우에만 알려줘.
2. 저장소의 README에 나온 publish 명령을 실행하고, 생성된 SourceTrail.Mcp.dll의 절대 경로를 확인해줘. 기존 소스 변경사항이 있으면 덮어쓰지 말아줘.
3. docs/codex-registration.md를 따라 현재 Codex 설정에 sourcetrail을 로컬 stdio 서버로 등록해줘. 다른 MCP 항목은 유지하고, 기존 sourcetrail 항목이 있으면 중복 등록 없이 갱신해줘.
4. 내가 지정한 .sln과 SQL 폴더는 경로를 확인한 뒤 기본값으로 등록해줘. 솔루션 경로를 주지 않았고 기존 설정도 없으면 현재 작업 폴더에서 .sln을 찾아줘. 한 개면 사용하고, 여러 개면 선택을 요청하고, 없으면 나중에 지정할 수 있게 해줘. SQL 폴더를 지정했으면 SqlFiles 모드로 설정하고, 경로를 주지 않았으면 기존 설정을 유지해줘.
5. 등록한 sourcetrail의 실제 MCP 도구로 ping을 호출해서 pong을 확인해줘. 현재 대화에서 도구가 아직 안 보이면 필요한 재연결 방법과 재연결 뒤 확인할 명령을 알려줘. 마지막에 설치 경로, 등록한 설정 위치, 연결 검증 여부를 보여줘.

분석 대상 소스와 SQL 원본은 읽기만 하고, 운영 DB에는 접속하지 말아줘.
```

## 문서 바로가기

- [설치·Codex 설정](docs/codex-registration.md) — 등록 예제, 기본 솔루션·SQL 폴더 설정, 연결 확인
- [사용법과 요청 예제](docs/tools.md) — 화면 이벤트에서 WCF·DB 호출·SQL 정의까지 조사하는 순서
- [구조와 동작 원리](docs/architecture.md) — 화면 검색, Roslyn 의미 분석, 디스크 캐시
- [SQL 자료와 캐시](docs/sql-sources.md) — SQL 파일 모드와 실제 DB 모드 설정 및 갱신
- [지원 범위와 제한](docs/limitations.md) — WCF·TableAdapter 등 원문 확인이 필요한 경우

## 수동 설치 및 등록

Windows에서 .NET 10 SDK가 필요합니다. 레거시 C# 솔루션을 의미 분석하려면 해당 프로젝트의 참조 어셈블리, 외부 DLL 및 MSBuild targets도 준비되어 있어야 합니다.

저장소 루트에서 실행합니다.

```powershell
dotnet publish src/SourceTrail.Mcp/SourceTrail.Mcp.csproj -c Release -o artifacts/server
```

MCP 클라이언트에 명령을 `dotnet`, 인자를 `artifacts/server/SourceTrail.Mcp.dll`의 **절대 경로**로 등록합니다. Roslyn BuildHost를 함께 사용하므로 publish 폴더 전체를 보관하세요. Codex의 복사 가능한 설정과 연결 확인 순서는 [Codex 등록 및 설정](docs/codex-registration.md)에 있습니다.

## 처음 사용하는 방법

1. MCP에서 `ping`을 호출해 `pong`을 확인합니다.
2. 의미 분석용 솔루션은 `load_solution`에 절대 `.sln` 경로를 전달하거나 시작 설정에 지정합니다. 시작 설정이 있으면 `find_ui_actions`와 `search_text`는 의미 분석 작업 공간을 열기 전에 실행할 수 있습니다.
3. SQL 스크립트를 쓸 때는 `select_database_source`에서 `SqlFiles`와 절대 폴더 경로를 지정합니다. 반복 사용 시 시작 설정으로 등록할 수 있습니다.
4. 화면을 모를 때는 “접수 저장 로직을 찾아줘. 화면 이벤트, 서비스 호출, DB 실행 위치를 파일과 줄로 보여줘”처럼 요청합니다. 첫 후보의 이벤트와 본문을 확인하고, 서비스·DB·SQL 도구는 확인된 이름이 있을 때 사용합니다.

## 연결 후 확인할 것

- `get_analysis_status`와 `get_database_status`에서 분석 대상과 진단을 확인합니다. 시작 경로만 지정한 직후에는 첫 사용 전이라 `NotLoaded`일 수 있습니다.
- UI 후보, 정적으로 연결된 호출, 실행 대상이 미확정인 후보를 구분합니다. `Partial`, `truncated`, 빈 결과는 원문 확인이 필요한 지점입니다.
- 운영 DB 접속정보, 실제 업무 소스, 로컬 설정·캐시와 분석 결과는 로컬에서 관리합니다.

## 개발 검증

```powershell
dotnet restore SourceTrail.sln
dotnet restore tests/Fixtures/Demo/Demo.sln
dotnet restore tests/Fixtures/Broken/Broken.sln
dotnet test SourceTrail.sln --no-restore
```

가상 WinForms와 오류가 있는 프로젝트로 화면 검색, 정적 호출, SQL 파일 분석, 부분 로딩 및 갱신을 검증합니다. 실제 SQL Server 연결은 환경별로 확인하세요.
