# SourceTrail MCP

C# WinForms의 화면·이벤트에서 메서드와 DB 호출을 찾고, SQL Server 스크립트에서 객체 정의와 참조를 확인하는 읽기 전용 MCP 서버입니다. 로컬에서 stdio로 실행하며 분석 근거를 파일과 줄 번호로 반환합니다.

DB 검색은 **로컬 SQL 파일 폴더를 색인하는 `SqlFiles` 모드**로 진행합니다. 프로시저·테이블·뷰·함수의 정의와 참조를 조사할 때 이 색인을 재사용합니다.

## Codex에 설치 맡기기

아래 프롬프트를 Windows의 Codex에 붙여넣으세요. 두 경로를 미리 적을 수 있고, 비워 두면 Codex가 누락된 경로를 물어봅니다. SQL 폴더는 프로젝트 안의 `CheckupDatabase\Latest`처럼 분석할 `.sql` 파일을 담은 폴더면 됩니다.

**이 프롬프트의 설치 기본값**

| 항목 | 신규 설치 시 적용되는 설정 |
|---|---|
| MCP 등록 | `sourcetrail`을 로컬 stdio 서버로 등록합니다. `dotnet`이 빌드된 `SourceTrail.Mcp.dll`을 실행합니다. |
| C# 분석 대상 | 입력한 `.sln`을 기본 솔루션으로 설정합니다. 폴더를 받으면 그 안에서 확인한 솔루션 경로를 사용합니다. |
| SQL 분석 대상 | `SqlFiles` 모드로 지정 폴더와 하위 폴더의 `.sql`을 읽습니다. |
| 캐시 | C# 디스크 색인은 `%LOCALAPPDATA%\SourceTrail\code-cache`, SQL 파일 색인은 `%LOCALAPPDATA%\SourceTrail\cache`를 사용합니다. |
| 대기 시간 | MCP 연결 30초, 도구 호출당 300초를 설정합니다. |

기존 등록을 갱신할 때는 확인한 분석 경로와 모드를 적용하고, 이미 지정된 캐시 경로와 대기 시간은 유지합니다.

```text
이 Windows PC에 SourceTrail MCP를 설치하고 현재 Codex에 등록해줘.
저장소는 https://github.com/shc6260/SourceTrail 이야.
설치 방법을 설명하는 데서 멈추지 말고, 가능한 단계는 직접 실행해줘.

프로젝트 경로(.sln 파일 또는 프로젝트 폴더):
DB 스크립트 폴더(.sql 파일이 있는 폴더):

1. 두 경로 중 비어 있거나 예시 값만 적힌 항목은 사용자에게 다시 물어봐 줘. 답을 받기 전에는 누락된 경로를 추정해서 등록하지 말아줘. 프로젝트 폴더 안의 .sln이 하나면 그 경로를 쓰고, 여러 개면 선택을 요청해줘. 경로를 읽을 수 없거나 .sln을 찾지 못하면 경로를 다시 받아줘.
2. 기존 SourceTrail 등록과 체크아웃을 확인해줘. 사용할 수 있는 체크아웃이 없으면 사용자 폴더에 저장소를 복제해줘. Git이나 .NET 10 SDK가 없으면 공식 배포본으로 설치하고, 권한 승인이 필요한 경우에만 알려줘.
3. 저장소의 README에 나온 publish 명령을 실행하고, 생성된 SourceTrail.Mcp.dll의 절대 경로를 확인해줘. 기존 소스 변경사항이 있으면 덮어쓰지 말아줘.
4. docs/codex-registration.md를 따라 sourcetrail을 로컬 stdio 서버로 등록해줘. Analysis__SolutionPath에 확인한 .sln 절대 경로를, Database__SqlFolder에 SQL 폴더 절대 경로를 넣고 Database__Mode는 SqlFiles로 설정해줘. 캐시와 대기 시간은 README의 설치 기본값을 적용하되 기존에 설정된 값은 유지해줘. C# 캐시를 새로 설정할 때는 %LOCALAPPDATA%를 실제 절대 경로로 변환해서 Analysis__CodeCacheDirectory에 넣어줘. 다른 MCP 항목은 유지하고 기존 sourcetrail 항목은 중복 없이 갱신해줘.
5. 실제 MCP에서 ping을 호출해 pong을 확인해줘. get_analysis_status를 조회하고, 지정한 .sln이 활성 상태가 아니면 load_solution으로 열어서 경로·상태·진단을 확인해줘. refresh_database_source로 SQL 폴더를 읽고 get_database_status로 모드·폴더·색인 파일 수·진단을 확인해줘. 현재 대화에 도구가 안 보이면 등록 결과와 실제 연결 검증 여부를 구분하고, 재연결 방법을 알려줘.

설치 후 실제 설정값과 각 항목의 의미를 아래 표로 반환해줘.

| 항목 | 적용값 또는 확인 결과 | 의미 |
| --- | --- | --- |
| 설치 위치 | 저장소와 실행 DLL의 절대 경로 | 설치된 파일 위치 |
| Codex 등록 | 설정 파일 경로, 서버 이름, 실행 명령·인자 | Codex가 서버를 시작하는 방법 |
| 프로젝트 | 실제 .sln 절대 경로 | C# 분석 대상 |
| DB 분석 | 실제 모드와 SQL 폴더 절대 경로 | SQL 정의와 참조를 읽는 자료 |
| 캐시 | C#·SQL 캐시의 실제 경로 또는 미사용 | 재사용하는 색인의 저장 위치 |
| 대기 시간 | 연결·호출 제한 시간 | Codex가 기다리는 시간 |
| 연결 검증 | ping 응답 또는 확인 필요 | 현재 대화에서 MCP 호출 가능 여부 |
| 분석 검증 | C# 상태, SQL 상태·색인 파일 수, 주요 경고 | 분석 준비 범위와 누락 항목 |

확인하지 못한 항목은 확인 필요로 표시해줘. 분석 대상 소스와 SQL 원본은 읽기만 해줘. DB 관련 검색은 지정한 로컬 SQL 폴더에서만 하고 SQL Server에 직접 접속하지 말아줘.
```

## 성능 실측

**접수 저장 조사에서 같은 파일·줄 근거를 찾는 도구 시간이 3.069초 → 1.045초로 줄었습니다(약 66% 단축).** 직접 검색과 **MCP 검색 → 원문 확인 → MCP 재검색**을 비교한 결과입니다. 저장 이벤트에서 서비스 구현, DB 호출, 로컬 SQL 정의까지 조사했습니다.

준비 후 각 3회 중앙값입니다. PowerShell 시작 시간을 빼면 **1.915초 → 0.820초**였으며, AI 판단과 답변 작성 시간은 측정에서 제외했습니다. 초기 준비를 포함한 첫 실행은 직접 검색 3.193초, MCP를 활용한 조사 47.915초였습니다.

## 구조와 동작 원리

Codex가 로컬 SourceTrail 서버에 검색을 요청하면, 서버는 질문에 맞는 방식으로 소스와 SQL 파일을 읽습니다.

| 조사 단계 | SourceTrail이 하는 일 |
|---|---|
| 화면·이벤트 찾기 | `find_ui_actions`가 “접수 저장” 같은 업무 문장을 화면·메뉴·버튼 문구와 대조합니다. WinForms Designer의 이벤트 구독과 핸들러 위치를 반환합니다. |
| 서비스·DB 호출 따라가기 | 호출 이름을 확인한 뒤 서버 구현 위치를 좁혀 검색합니다. 호출 대상을 해석할 때는 Roslyn으로 심볼·참조를 읽고, 확인된 서버 메서드에서 DB 실행 근거를 찾습니다. |
| SQL 정의·참조 확인 | `SqlFiles`가 지정 폴더와 하위 폴더의 `.sql`을 읽어 정의와 직접 참조를 색인합니다. 확인한 프로시저 이름으로 정의의 파일·줄과 의존관계를 조회합니다. |

화면 검색은 C# 의미 분석 작업 공간을 열기 전에도 가능합니다. 같은 입력은 캐시를 재사용하고, 파일 변경을 확인해 필요한 색인을 갱신합니다. [구성 요소와 캐시 상세](docs/architecture.md)

## Codex에서 사용하는 방법

**권장 흐름은 MCP로 위치 찾기 → 원문에서 흐름 확인 → 확인한 이름으로 MCP 재검색입니다.** 화면 이름을 모르면 `find_ui_actions`로 이벤트 후보를 찾습니다. Codex가 핸들러와 관련 메서드의 원문을 읽어 다음 호출명이나 서비스 계약을 확인한 뒤, 그 이름을 다음 MCP 검색에 넘겨 서버 구현·DB 호출·SQL 정의를 좁혀갑니다. 후보가 없거나 연결이 불명확하면 해당 구간의 원문을 확인하고, 확보한 근거부터 이어갑니다.

Codex 대화창에 아래 예시를 붙여넣으면 이 순서로 조사를 요청할 수 있습니다.

```text
SourceTrail MCP로 접수 저장 로직을 찾아줘.
먼저 MCP로 저장 버튼과 이벤트 후보를 찾아줘.
핸들러와 관련 메서드의 원문을 읽어 실제 서비스 호출명과 계약을 확인해줘.
확인한 이름으로 MCP를 다시 써서 서버 구현과 DB 호출 위치를 찾고, 확인된 프로시저의 SQL 정의까지 조회해줘.
단계별 파일·줄 번호를 보여주고, 후보이거나 연결을 확인하지 못한 부분은 확인 필요로 표시해줘.
```

| 알고 있는 정보 | 이렇게 요청하세요 | Codex가 사용할 도구 |
|---|---|---|
| 업무 동작만 앎 | “SourceTrail MCP로 검사 결과 저장 버튼과 이벤트를 찾아줘.” | `find_ui_actions`로 후보를 찾고, 0건이면 메뉴·화면 제목이나 Designer 원문을 확인합니다. |
| 서버 메서드명을 앎 | “SourceTrail MCP로 `Set_InfoResult`가 호출하는 DB 프로시저와 파일·줄을 찾아줘.” | `search_text`로 서버 구현 위치를 찾고, 확인된 메서드를 `find_database_calls`로 조사합니다. `FillBy` 조회는 어댑터 원문을 확인합니다. |
| 프로시저명을 앎 | “SourceTrail MCP로 `dbo.usp_update_SmartInfoReception` 정의와 직접 의존관계를 찾아줘.” | `find_sql_object`로 정의 위치를 찾고, `analyze_procedure`로 참조를 확인합니다. |

설치 때 설정한 솔루션과 SQL 폴더를 그대로 쓰면 질문마다 경로를 입력할 필요가 없습니다. 다른 대상을 조사할 때는 `.sln`이나 SQL 폴더 경로를 질문에 적으세요. [도구별 입력 예제와 영향 범위 조사](docs/tools.md)

## 문서 바로가기

- [설치·Codex 설정](docs/codex-registration.md) — 등록 예제, 기본 솔루션·SQL 폴더 설정, 연결 확인
- [사용법과 요청 예제](docs/tools.md) — 화면 이벤트에서 WCF·DB 호출·SQL 정의까지 조사하는 순서
- [구조와 동작 원리](docs/architecture.md) — 화면 검색, Roslyn 의미 분석, 디스크 캐시
- [SQL 자료와 캐시](docs/sql-sources.md) — 로컬 SQL 폴더 설정과 색인 갱신
- [지원 범위와 제한](docs/limitations.md) — WCF·TableAdapter 등 원문 확인이 필요한 경우

## 수동 설치 및 등록

Windows에서 .NET 10 SDK가 필요합니다. 레거시 C# 솔루션을 의미 분석하려면 해당 프로젝트의 참조 어셈블리, 외부 DLL 및 MSBuild targets도 준비되어 있어야 합니다.

저장소 루트에서 실행합니다.

```powershell
dotnet publish src/SourceTrail.Mcp/SourceTrail.Mcp.csproj -c Release -o artifacts/server
```

MCP 클라이언트에 명령을 `dotnet`, 인자를 `artifacts/server/SourceTrail.Mcp.dll`의 **절대 경로**로 등록합니다. Roslyn BuildHost를 함께 사용하므로 publish 폴더 전체를 보관하세요. Codex의 복사 가능한 설정과 연결 확인 순서는 [Codex 등록 및 설정](docs/codex-registration.md)에 있습니다.

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

가상 WinForms와 오류가 있는 프로젝트로 화면 검색, 정적 호출, SQL 파일 분석, 부분 로딩 및 갱신을 검증합니다.
