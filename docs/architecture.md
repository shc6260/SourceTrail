# 구조와 동작 원리

```text
Codex 또는 MCP 클라이언트
          │ stdio
    SourceTrail.Mcp
       ├─ Roslyn ── C# 구문 색인 / MSBuildWorkspace 의미 분석
       ├─ SqlServer ── ScriptDom SQL 파일 분석 / SQL Server 메타데이터 조회
       └─ Core ── 분석기 계약 / 결과 모델 / C#·SQL 흐름 조합
```

## 검색의 세 경로

| 경로 | 읽는 자료와 처리 방식 |
|---|---|
| WinForms 화면·이벤트 | `find_ui_actions`는 C# 프로젝트 폴더의 Designer 이벤트 구독, 화면·메뉴·버튼 문구와 기본 `.resx`, 대응 핸들러를 구문 수준에서 읽습니다. 업무 문장의 단어를 식별자와 문구에 대조하고 일부 동작 별칭으로 후보를 정렬합니다. `search_text`는 현재 디스크 파일을 지정 폴더·확장자로 좁혀 읽습니다. |
| C# 심볼·호출 | Roslyn의 `MSBuildWorkspace`로 프로젝트와 참조를 읽어 선언과 호출 대상을 해석합니다. `find_database_calls`는 한 메서드에서 제한된 범위로 탐색해 직접 명령, 설정된 래퍼, 일부 생성 TableAdapter `Update` 매핑을 반환합니다. |
| SQL 정의·참조 | `SqlFiles`는 ScriptDom으로 정의와 직접 참조를 색인합니다. `LiveDatabase`는 SQL Server의 메타데이터를 조회합니다. `find_sql_object`는 파일 색인에서 검색하고 `analyze_procedure`는 선택된 모드에서 프로시저를 분석합니다. |

MCP 호스트는 stdout으로 프로토콜 응답을 보내고 stderr로 진단을 남깁니다. 분석기는 한 프로세스에서 활성 솔루션 하나와 SQL 자료 하나를 사용합니다. 분석·자료 전환 작업은 직렬화합니다.

## 캐시와 갱신

- **UI 색인**은 솔루션과 관련 Designer·코드·리소스 파일의 경로, 크기, 시각을 대조합니다. `indexState`는 현재 응답이 `Built`(구축), `Disk`(디스크 재사용), `Memory`(메모리 재사용) 중 어디서 왔는지 보여줍니다. 이 경로는 의미 분석 작업 공간을 열지 않습니다.
- **C# 의미 분석**은 현재 프로세스의 작업 공간을 재사용합니다. `Analysis.CodeCacheDirectory`를 설정하면 입력 목록과 파일 상태, 검증된 로딩 정보 및 일부 분석 결과를 디스크에서도 재사용합니다. 입력을 대조한 뒤 필요할 때 작업 공간을 다시 엽니다. 디스크 색인 적중과 Roslyn 작업 공간 준비 완료는 별개입니다. `reload_solution` 후에는 최신 심볼 ID를 사용하세요.
- **SQL 파일 색인**은 파일 목록과 SHA-256 내용을 비교해 바뀐 파일을 다시 파싱합니다. 변경 감지와 캐시 경로는 [SQL 자료와 캐시](sql-sources.md)에 있습니다.

## 반환 근거 해석

Designer 이벤트 구독은 처리기 연결을, Roslyn 호출은 해석된 정적 호출을 보여줍니다. 텍스트 일치, 인터페이스 구현 후보, SQL의 읽기·쓰기 후보는 각 근거의 수준에 맞춰 판단합니다. 운영에서 실제로 실행되거나 저장됐는지는 실행 환경에서 따로 검증합니다.

`trace_procedure_path`는 명시한 프로시저로 가는 경로를 제한 범위에서 찾습니다. 조건에 따라 WCF 호스트 후보부터 별도 탐색을 시도합니다. `Candidate`, `NotConnected`, `Partial`, `truncated`는 계약·구현 또는 탐색 범위를 원문에서 다시 확인할 지점입니다. [지원 범위와 제한](limitations.md)
