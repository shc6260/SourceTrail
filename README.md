# <img width="30" height="30" alt="image" src="https://github.com/user-attachments/assets/9cd1343f-3b3a-47c7-a9a4-ab92b6e2bb91" /> SourceTrail MCP 2.0

C# WinForms의 화면·이벤트에서 서비스와 DB 호출 위치를 찾고, 로컬 SQL 파일에서 프로시저 정의와 직접 참조를 확인하는 MCP 서버입니다. 파일·줄 번호를 근거로 **MCP로 위치 찾기 → 원문에서 실제 이름 확인 → 그 이름으로 MCP 재검색** 순서로 사용합니다.

C# 구문 파서를 사용하며 컴파일·MSBuild·참조 DLL 로딩은 하지 않습니다. DB는 `SqlFiles`만 지원하고 SQL Server에 접속하지 않습니다. 검색은 원본을 읽고, 색인 준비·갱신은 로컬 캐시만 저장합니다. 반환된 이름·Adapter 매핑은 정적 후보이므로 실제 연결과 조건은 원문으로 확인합니다.

- [Codex 설치 프롬프트](#codex에-설치-맡기기)
- [프로젝트에 사용 지침 적용](#프로젝트에-사용-지침-적용)
- [실행·검색 순서](#실행검색-순서)
- [일괄 색인 생성·갱신](#일괄-색인-생성갱신)
- [효율적으로 사용하는 프롬프트](#효율적으로-사용하는-프롬프트)
- [상세 사용 지침·프로젝트 지침 연결](docs/usage-guidelines.md)
- [성능 측정의 범위](#성능-측정의-범위)

## Codex에 설치 맡기기

아래 프롬프트를 Windows의 Codex에 붙여넣으세요. 소스와 SQL 경로는 자신의 경로로 채웁니다. 비워 두면 설치 에이전트가 확인하도록 되어 있습니다. `.sln` 없이 소스 폴더를 지정해도 됩니다.

```text
이 Windows PC에 SourceTrail MCP 2.0을 설치하고 현재 Codex에 등록해줘.
저장소: https://github.com/shc6260/SourceTrail
설명에서 멈추지 말고, 확인된 경로와 아래 절차로 설치를 진행해줘.

C# 소스 경로(.sln 파일 또는 소스 폴더):
SQL 스크립트 폴더(.sql 파일이 있는 폴더):

1. 비어 있거나 예시 값인 경로는 사용자에게 확인한다. 답을 받기 전에 추정하지 않는다.
   소스 폴더는 .sln 없이 그대로 사용할 수 있다. 파일·폴더가 실제로 읽히는지 확인한다.
2. 기존 SourceTrail 등록과 체크아웃을 확인한다. 없으면 사용자 폴더에 저장소를 복제한다.
   기존 변경사항은 덮어쓰지 않는다. Git과 .NET 10 SDK가 없으면 공식 배포본으로 설치하고
   권한 승인이 필요한 경우에만 알린다.
3. 저장소에서 아래 명령으로 publish하고 실행 DLL의 절대 경로를 확인한다.
   dotnet publish src/SourceTrail.Mcp/SourceTrail.Mcp.csproj -c Release -o artifacts/server
   실행에는 publish 폴더 전체를 보관한다. 분석 대상 프로젝트의 컴파일·빌드는 하지 않는다.
4. 일괄 색인 저장소를 정한다. 기본은 실제 MCP 실행 사용자의
   %LOCALAPPDATA%\SourceTrail\prepared-index이며 실제 절대 경로로 확정한다.
   기존에 지정한 저장소가 있으면 유지한다. Codex 프로젝트 폴더에 임의로 만들지 않는다.
5. 최초 일괄 색인을 생성한다.
   dotnet <실행 DLL 절대 경로> prepare-index --source <확인한 C# 경로>
     --sql-folder <확인한 SQL 폴더> --index-directory <확정한 색인 저장소>
   위 인수는 한 명령에 전달하고 공백을 포함한 경로는 인용한다.
   기존 C#·SQL 캐시 설정이 있으면 명령에도 같은 설정이나 환경변수로 전달한다.
   기본 증분 모드를 사용한다. 사용자가 전체 재생성을 명시한 경우에만 --rebuild를 붙인다.
   기존 색인을 먼저 지우지 않는다. 종료 코드 0과 Published=true를 확인한다.
   생성·재사용 건수, 구문 진단·SQL 경고, 단계별 시간을 기록한다.
   실패하면 설치 완료로 보고하지 않는다.
6. docs/codex-registration.md에 따라 sourcetrail을 로컬 stdio MCP 서버로 등록한다.
   command는 dotnet, args는 publish한 SourceTrail.Mcp.dll의 절대 경로로 지정한다.
   Analysis__SolutionPath, Database__SqlFolder는 준비에 사용한 경로와 같게 설정한다.
   Database__Mode=SqlFiles, Indexing__Directory는 4~5번과 같은 절대 경로를 사용한다.
   신규 설치 권장값은 Analysis__WatchCodeFiles=true,
   Analysis__CodeRescanIntervalSeconds=30이다. 사용자가 명시한 기존 설정은 유지한다.
   연결 대기 30초·도구 대기 300초를 신규 기본값으로 사용하고 기존 값은 유지한다.
   다른 MCP 등록을 변경하지 않고 sourcetrail을 중복 등록하지 않는다.
7. MCP를 새로 연결하고 ping의 pong을 확인한다.
   설정된 소스에서 실제로 확인한 식별자로 선언 검색을 한 번 수행하고,
   실제 SQL 파일에서 확인한 객체명으로 find_sql_object를 호출한다.
   상태·대상 경로·파일 수·주요 진단을 확인한다.
   일괄 준비가 성공했으면 검증을 위해 전체 재생성이나 refresh를 반복하지 않는다.
   현재 대화에 도구가 보이지 않으면 등록 완료와 실제 호출 검증 여부를 구분하고
   재연결이 필요하다고 설명한다.

설치 결과를 표로 정리해줘:
- 저장소·실행 DLL 위치
- Codex 설정 파일과 MCP 실행 명령
- C#·SQL 분석 대상
- 일괄 색인 저장소, 활성 세대, 생성·재사용 건수와 주요 경고
- 변경 감지 설정과 연결·호출 대기 시간
- 연결 및 실제 검색 검증 결과

확인 못 한 내용은 확인 필요로 표시한다.
원본 소스·SQL은 수정하지 않고, SQL Server 접속·SQL 실행·운영 반영은 하지 않는다.
```

### 설치 기본값과 저장 위치

| 항목 | 기본·권장값 |
|---|---|
| 실행 방식 | 로컬 stdio, `dotnet` + publish된 `SourceTrail.Mcp.dll` |
| 분석 대상 | 확인한 절대 소스 폴더 또는 `.sln`, 로컬 SQL 폴더 |
| 일괄 색인 | `%LOCALAPPDATA%\SourceTrail\prepared-index` |
| 활성 세대가 없을 때 C# 캐시 | `Analysis.CodeCacheDirectory`. 빈 값이면 C# 디스크 재사용 비활성화 |
| 활성 세대가 없을 때 SQL 캐시 | `%LOCALAPPDATA%\SourceTrail\cache` |
| 변경 확인 | 설치 프롬프트·배포 예제는 `WatchCodeFiles=true`, 30초 주기. 설정 생략 시 라이브러리 기본은 `false` |
| 연결·도구 대기 | 신규 설치 권장 30초·300초 |

일괄 색인은 Codex 프로젝트 폴더와 별개입니다. **어떤 Codex 프로젝트를 열었는지가 아니라 MCP에 설정된 소스·SQL 경로를 따릅니다.** 저장소 하나에는 활성 세대 하나가 있고, 소스·SQL 경로가 모두 일치할 때 해당 세대의 캐시를 사용합니다. 여러 분석 대상을 각각 유지하려면 저장소와 MCP 설정을 분리하세요. Codex 프로젝트별 자동 분리 기능은 없습니다.

설정은 실행 DLL 옆 `appsettings.json` 또는 MCP 환경변수로 지정합니다. 환경변수가 JSON보다 우선합니다. `Indexing.Directory`를 생략하면 기본 저장소를 사용하며, 별도 경로를 지정하면 준비 명령과 MCP 설정을 동일하게 맞춰야 합니다. 활성 세대가 일치하면 그 안의 C#·SQL 캐시가 개별 캐시 경로보다 우선합니다.

## 프로젝트에 사용 지침 적용

설치 후 **실제로 분석할 프로젝트**의 `AGENTS.md` 또는 기존 검색 지침에 아래 내용을 추가하세요. 이미 공통 지침을 참조한다면 그 공통 문서에 한 번만 추가하면 됩니다.

아래 `D:/Tools/SourceTrail`은 예시입니다. 설치한 SourceTrail 저장소의 실제 절대 경로로 바꾸세요. MCP 등록만으로 이 MD가 자동 적용되지는 않습니다.

```markdown
## SourceTrail 사용

- SourceTrail을 사용하는 조사에서는 [상세 사용 지침](D:/Tools/SourceTrail/docs/usage-guidelines.md)을 먼저 읽는다.
- MCP 후보 → 필요한 원문에서 실제 이름·계약·인수 확인 → MCP 재조회 순서로 진행한다. 질문에 맞는 도구를 선택하고 확보한 근거를 재사용한다.
- 요청한 근거를 확보하면 종료한다. 추가 사용처 조사나 범위·방식 변경은 먼저 묻는다. 결과는 채팅에 간단히 답하며 보고서 파일·상세 계측은 요청할 때만 작성한다.
- 기존 프로젝트의 역할·쓰기·DB 접근 제한을 유지한다.
```

경로에 공백이 있으면 링크 대상을 `<D:/설치 경로/SourceTrail/docs/usage-guidelines.md>`처럼 꺾쇠로 감싸세요. 도구별 규칙과 자세한 설명은 [상세 사용 지침](docs/usage-guidelines.md)에 있습니다.

## 실행·검색 순서

### 설치할 때

```text
소스·SQL 경로 확인 → publish → 최초 일괄 색인 생성 → 파일로 저장
→ MCP 등록 → 연결 → 실제 검색 검증
```

### MCP를 실행할 때

```text
MCP 실행 → 설정 읽기 → 분석 경로와 일치하는 활성 색인 선택 → 요청 대기
```

일괄 색인이 있어도 시작 즉시 모든 내용을 메모리에 읽지는 않습니다. 첫 관련 요청에서 파일 목록·캐시를 확인하고 필요한 색인을 메모리에 올립니다.

```text
첫 C# 선언 요청 → 파일 목록·메타데이터 대조 → 저장된 선언 재사용
첫 UI 요청      → UI 입력 메타데이터 대조 → UI 캐시 로딩 또는 생성
첫 SQL 요청     → 저장된 SQL 캐시 로딩 → 목록·메타데이터 대조 → 변경분 갱신
```

일괄 색인을 아직 만들지 않았다면 필요한 요청 시점에 준비합니다. C#은 파일 목록을 먼저 만들고 필요한 선언을 구문 분석하며, SQL은 지정 폴더의 정의를 색인합니다. UI는 별도 색인을 사용합니다.

### 실행 중 계속 사용할 때

```text
요청 → 메모리 색인 재사용 → 필요한 위치·후보 반환
변경 알림 또는 대조 주기 경과 → 다음 요청에서 변경 확인·갱신 → 응답
```

C# 감시 모드와 SQL 감시는 알림·주기를 사용합니다. UI는 조회 시 입력 메타데이터의 fingerprint를 비교하고, 바뀌면 UI 색인을 다시 만듭니다. 감시 알림 전달 전에는 짧은 지연이 있을 수 있고, 감시 오류 시 전체 대조로 복귀합니다. 요청마다 C#을 대조해야 하면 `WatchCodeFiles=false`를 사용합니다.

### 실제 코드 조사를 할 때

```text
사용자 지시 → MCP로 후보 위치 찾기 → 핸들러·관련 메서드 원문 읽기
→ 실제 호출명·계약·인수 확인 → 그 이름으로 MCP 재검색
→ 필요한 다음 연결만 반복 → 확인된 프로시저의 SQL 정의 확인 → 보고
```

MCP는 위치와 후보를 찾습니다. 원문의 호출 관계·조건 분기·인수·행 상태를 대조하는 작업이 필요합니다. 원문에서 연결을 확인했다면 같은 연결을 전체 사용처 검색으로 다시 검증할 필요는 없습니다.

## 일괄 색인 생성·갱신

C# 선언 → WinForms UI → SQL 정의·직접 참조를 미리 분석해 로컬 파일에 저장합니다. 컴파일러·MSBuild·DB 연결은 사용하지 않습니다. 준비 비용을 설치·갱신 단계로 옮기는 기능이며 첫 메모리 로딩 비용까지 없어지는 것은 아닙니다.

### CLI

publish 후 PowerShell에서 실행합니다. 아래 소스·SQL 경로는 자신의 경로로 바꾸세요.

```powershell
$preparedIndex = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'SourceTrail\prepared-index'
dotnet .\artifacts\server\SourceTrail.Mcp.dll prepare-index `
  --source 'D:\Projects\Sample\Sample.sln' `
  --sql-folder 'D:\Projects\Sample\DatabaseScripts' `
  --index-directory $preparedIndex
```

DLL 옆 설정이나 환경변수에 대상과 저장소가 지정돼 있다면 다음처럼 실행할 수도 있습니다.

```powershell
dotnet .\artifacts\server\SourceTrail.Mcp.dll prepare-index
```

| 요청 | 동작 |
|---|---|
| `prepare-index` | 기존 결과를 재사용하며 변경·추가 파일을 분석하고 삭제 파일을 제외 |
| 같은 명령에 `--rebuild` 추가 | 기존 캐시를 사용하지 않고 전체 재생성 |
| MCP `prepare_indexes`에 `{}` | 설정된 경로로 일괄 생성·증분 갱신 |
| MCP `prepare_indexes`에 `{"rebuild":true}` | 설정된 경로로 전체 재생성 |

C#·SQL의 메타데이터가 같으면 캐시를 재사용합니다. SQL은 변경 파일의 내용 해시를 비교해 실제 내용이 달라진 파일만 다시 파싱합니다. UI 입력이 달라지면 UI 색인 전체를 다시 생성합니다. 크기·시각을 모두 유지한 오프라인 수정은 메타데이터만으로 구분하지 못할 수 있으므로 전체 재생성으로 확인할 수 있습니다.

### 완료·실패와 재연결

```text
별도 작업 세대에 생성 → 입력 변경 여부 재확인 → 완료된 세대를 활성화
실패·취소 → 기존 활성 색인 유지 → 이번 미완료 작업만 정리
```

결과에는 `Published`, 생성·재사용 건수, 구문 진단·SQL 경고, 단계별 시간이 나옵니다. CLI 종료 코드는 완료 0, 실패 1, 취소 130입니다. `Published=true`여도 구문 진단이나 SQL `Partial` 경고를 확인해야 합니다. 모든 객체나 실행 관계의 완전성을 뜻하지 않습니다.

기존 활성 색인을 먼저 지우지 않습니다. 이전 세대는 실행 중인 MCP가 참조할 수 있어 자동 삭제하지 않으며, 재생성에는 추가 디스크 공간이 필요합니다. **새 세대를 사용하려면 MCP를 재연결합니다. 재연결 때문에 색인을 다시 만들 필요는 없습니다.**

원본 SQL 정의와 소스 선언이 캐시에 포함되므로 로컬 캐시를 공개 저장소에 올리지 마세요. 큰 프로젝트에서는 선언 캐시의 크기·메모리 로딩 비용이 커질 수 있습니다. [세부 동작과 반환값](docs/index-preparation.md)

## 효율적으로 사용하는 프롬프트

질문별 시작 도구와 중복 조회 방지 규칙은 [상세 사용 지침](docs/usage-guidelines.md)을 참고하세요. 프로젝트 지침에는 해당 문서의 링크와 핵심 규칙만 두면 됩니다.

아래는 실제 조사용 프롬프트입니다. 시작할 때 조사 범위를 고정하고, 원문에서 확인한 이름으로만 다음 연결을 찾습니다. 이름을 알고 있으면 전체 이름과 알려진 폴더로 범위를 좁히세요. 이 프롬프트가 특정 응답시간을 보장하는 것은 아닙니다.

```text
SourceTrail 2.0으로 접수 저장 로직을 찾아줘.

대상은 MCP에 설정된 C# 소스와 로컬 SQL 폴더(SqlFiles)를 사용한다.
주요 접수정보·접수항목의 개별/일괄 저장, Added/Modified/Deleted 및 후처리를 확인한다.
별도 화면·예약·외부연동·출력과 SQL 내부 호출의 재귀 추적은 제외한다.

1. find_ui_actions에 "접수 저장"을 전달한다.
   버튼 문구와 이벤트 원문으로 신규 접수·수정 버튼을 구분한다.
2. 후보의 핸들러와 저장 메서드 원문을 읽어 실제 호출명·계약·인수를 확인한다.
   이름을 추정해서 먼저 검색하지 않는다.
3. 확인한 식별자·전체 이름으로 find_symbol 또는 범위를 좁힌 search_text를 사용한다.
   반환된 소스 ID는 추정·재작성하지 말고 그대로 다음 도구에 전달한다.
   알려진 C# 절대 경로는 get_symbol_overview에 사용할 수 있다.
4. find_database_calls는 현재 메서드 하나만 조회한다. symbol과 limit을 사용하고
   methodCandidates와 원문에서 필요한 다음 연결만 골라 같은 순서로 이어간다.
   maxDepth/maxMethods와 자동 전체 그래프 도구는 사용하지 않는다.
5. 실제 WCF 계약, 인수 타입, 조건부 분기, 행 상태와 개별/일괄 방식을 원문으로 대조한다.
   CommandText 설정·Adapter 매핑만으로 실제 실행을 단정하지 않는다.
6. 연결을 원문으로 확인한 프로시저만 find_sql_object/analyze_procedure로 확인한다.
   위치만 필요하면 locationsOnly=true, 본문 분석이 필요하면 정의와 직접 참조를 조회한다.

조회 규칙:
- search_text에는 확인한 코드 문자열과 알려진 directory·허용 extension을 사용한다.
- 원문은 필요한 구간만 읽고 이미 읽은 같은 내용은 재사용한다.
- 이미 확인한 연결을 전체 이름 사용처 검색으로 재검증하지 않는다.
- find_reference_candidates 등 추가 사용처 조사가 필요하면 목적·범위를 먼저 설명하고 묻는다.
- 파일명 우선 검색은 전체 범위 확인으로 단정하지 않는다.
- truncated/unresolved와 Partial 경고를 확인하고 남은 부분은 확인 필요로 표시한다.
- 요청한 범위의 근거를 확보하면 종료한다. 범위·진행 방식 변경은 먼저 물어본다.
- 일반 조회를 위해 prepare_indexes/reload/refresh를 반복 실행하지 않는다.

보고:
단계별 파일·줄 번호와 확인한 정적 연결, 후보, 확인 필요사항을 간단히 나눠 보여준다.
시간 기록을 요청받았다면 준비·도구별 응답·원문 확인·전체 조사·최종 정리를 구분한다.
각 호출의 시각을 즉시 저장하고, 겹치는 대기를 중복 합산하지 않는다.
실패·재시도도 포함하며 미계측 값은 추정해서 채우지 않는다.

소스·설정 수정, 빌드, DB 접속, SQL 실행, 운영 반영은 하지 않는다.
```

첫 UI 결과가 없거나 동명이 많으면 화면 제목·메뉴·확인한 폴더를 이용해 후보를 좁힙니다. 범위를 바꿔야 하면 먼저 확인합니다. 요청한 분기를 생략하거나 SQL 개수를 임의로 줄여 빠르게 끝내는 규칙은 아닙니다.

| 이미 알고 있는 정보 | 시작 방법 |
|---|---|
| 화면·업무 동작 | `find_ui_actions`에 화면과 동작을 분리한 짧은 문장 |
| 메서드명·소스 파일 | 원문으로 이름 확인 후 제한한 `find_symbol`/`search_text`, 반환 ID로 후속 조회 |
| 프로시저명 | `find_sql_object`로 정의 위치, 필요하면 `analyze_procedure`로 직접 참조 |

## 성능 측정의 범위

동일한 고정 5단계의 위치 조회 실험에서 준비 후 각 3회 중앙값은 다음과 같았습니다. 양쪽에 같은 원문 추출 절차를 포함했고, 실행 순서를 교대했습니다.

| 수정 저장 경로의 고정 작업 | 직접검색 | MCP 혼합 |
|---|---:|---:|
| UI·원문 이름 추출·WCF 선언 4곳·DB 지점 3곳·SQL 정의 위치 1개 | 3.416초 | 1.869초 |
| 같은 실행의 첫 표본(준비·조회 포함) | 3.932초 | 6.366초 |

이 범위의 반복 도구 왕복은 약 45% 짧았습니다. 파일·줄 근거는 같았으며 현재 API에서 필요한 후속 조회·원문 읽기도 포함했습니다. 사전 색인 생성 시간, AI 판단과 답안 작성은 포함하지 않았습니다. 첫 표본은 기존 디스크 캐시를 사용했고 OS 캐시를 초기화하지 않았으므로 최초 설치·완전 cold start 시간은 아닙니다.

이는 이름과 위치를 단계별로 전달하는 고정 절차의 결과입니다. 임의의 질문에 대한 전체 조사시간·정확도·토큰 사용량 개선을 보장하지 않습니다. 과거 v12 수치 1.045초는 다른 버전의 기록이며 현재 2.0의 수치로 사용하지 않습니다.

## 지원 범위와 문서

선언·참조·DB 명령은 파일·구문 기반 후보입니다. 오버로드·수신 타입·런타임 분기·실제 endpoint와 DB 배포를 자동으로 확정하지 않습니다. `Ready`는 색인/검색 준비 상태이며 컴파일 성공이 아닙니다. 전체 그래프·자동 WCF 연결·LiveDatabase는 2.0에서 제거했습니다.

- [도구와 반환 의미](docs/tools.md)
- [설정·Codex 등록](docs/codex-registration.md)
- [일괄 색인 상세](docs/index-preparation.md)
- [구조와 캐시](docs/architecture.md)
- [SQL 정의·캐시](docs/sql-sources.md)
- [변경 감지](docs/code-freshness.md)
- [한계와 확인 필요사항](docs/limitations.md)

## 수동 설치·검증

서버를 publish하려면 .NET 10 SDK가 필요합니다. 분석 대상 프로젝트의 SDK·참조 DLL·MSBuild targets는 필요하지 않습니다.

```powershell
dotnet publish src/SourceTrail.Mcp/SourceTrail.Mcp.csproj -c Release -o artifacts/server
```

MCP에는 `dotnet`과 생성된 DLL 절대 경로를 등록합니다. publish 폴더 전체를 유지하고, 이 README의 일괄 준비 명령을 실행한 뒤 같은 경로 설정으로 연결하세요.

개발 검증은 합성 C#·WinForms·SQL fixture를 사용하며 운영 DB에 접속하지 않습니다.

```powershell
dotnet restore SourceTrail.sln
dotnet test tests/SourceTrail.Tests/SourceTrail.Tests.csproj -c Release --no-restore
```
