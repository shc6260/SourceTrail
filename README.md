# SourceTrail MCP

C# 솔루션의 화면·메서드·프로시저·SQL 객체 관계를 추적하는 공용 읽기 전용 MCP 서버입니다. UI 없이 stdio로 실행하며, 분석할 솔루션 경로는 도구 입력 또는 외부 설정으로 지정합니다.

## 구현 상태

- 솔루션 로딩·상태 조회·재로딩
- Roslyn 심볼 검색·구조 조회·참조 검색
- 문자열 검색 및 프로시저 문자열/상수 → 포함 심볼 연결
- 설정된 DB 호출 래퍼의 프로시저 인자 판별
- SQL Server 프로시저 정의 및 직접 의존성 조회
- 코드→DB / 프로시저→호출자·WinForms 이벤트 역추적
- 메모리 캐시, 깊이·노드 제한, 순환 탐색 방지, 부분 로딩 진단
- 17개 테스트와 실제 MCP stdio 도구 호출 검증

실제 .NET Framework 4.6.2 업무 솔루션 및 운영 DB는 아직 검증하지 않았습니다. 원격 WCF 계약→서버 구현 자동 연결과 동적 SQL 분석은 지원하지 않습니다. 인터페이스 구현 후보는 실제 실행 대상으로 확정하지 않습니다.

## 설치 및 빌드

.NET 10 SDK가 필요합니다. 분석 대상 프로젝트의 프레임워크는 변경하지 않습니다.

```powershell
dotnet restore SourceTrail.sln
dotnet build SourceTrail.sln --no-restore
```

## 실행

빌드 후 DLL을 직접 실행합니다. 프로세스는 MCP 요청을 기다립니다.

```powershell
dotnet src/SourceTrail.Mcp/bin/Debug/net10.0/SourceTrail.Mcp.dll
```

배포 시에는 `dotnet publish src/SourceTrail.Mcp -c Release -o artifacts/server`로 서버와 Roslyn BuildHost를 함께 배포합니다. 로그는 stderr에 출력합니다. 실행 중인 분석 스냅샷은 재로딩 전까지 유지되며 소스 변경은 자동 반영하지 않습니다.

## 설정

`appsettings.example.json`을 복사해 실행 DLL 옆의 `appsettings.json`으로 저장하거나 환경변수를 사용합니다. 실제 설정 파일은 Git에서 제외됩니다.

- `Analysis__SolutionPath`: 선택적 시작 솔루션 경로. 기본은 빈 값이며 `load_solution`으로 명시적 로딩.
- `Database__ConnectionString`: 선택적 SQL Server 연결 문자열.
- `Analysis__MaxDepth`: 기본 5, 도구 입력 허용 범위 0..20.
- `Analysis__MaxNodes`: 기본 200, 도구 입력 허용 범위 1..2000.
- `Analysis__MaxResults`: 기본 100, 검색 limit 허용 범위 1..1000.
- `Analysis__AdditionalSearchRoots__0`: 추가 문자열 검색 루트.
- `Analysis__ProcedureCallRules__0__TypeName`: DB 래퍼 타입의 전체 이름.
- `Analysis__ProcedureCallRules__0__MethodName`: DB 래퍼 메서드명.
- `Analysis__ProcedureCallRules__0__ArgumentIndex`: 선언된 매개변수의 0 기반 인덱스.

DB 래퍼 계약은 사용자 설정을 근거로 합니다. 메서드 이름만 보고 DB 실행을 추측하지 않습니다. 테스트 예제 설정은 `docs/settings.demo.example.json`에 있습니다. 테스트용 Db는 DB에 연결하지 않는 빈 구현입니다.

DB 계정에는 필요한 메타데이터 조회 권한만 부여합니다. 도구는 고정된 메타데이터 SELECT만 실행하고 업무 프로시저를 실행하지 않습니다. DB 미설정·실패 시에도 C# 분석은 사용 가능합니다.

## 사용 순서

1. `load_solution`에 절대 `.sln` 경로 전달.
2. `find_symbol`에서 후보의 `id` 선택.
3. 선택한 ID로 구조·참조·흐름 조회.
4. 디스크의 소스가 변경되면 `reload_solution` 호출 후 새 ID 사용.

```json
{"solutionPath":"D:\\Projects\\Sample\\Sample.sln"}
```

도구별 상세 예제는 [tools.md](docs/tools.md), Codex 등록 예제는 [codex-registration.md](docs/codex-registration.md)를 참고하세요.

## 검증

가상 WinForms 솔루션과 의도적으로 컴파일 오류가 있는 부분 로딩 테스트 솔루션을 사용합니다.

```powershell
dotnet restore tests/Fixtures/Demo/Demo.sln
dotnet restore tests/Fixtures/Broken/Broken.sln
dotnet test SourceTrail.sln --no-restore
powershell -NoProfile -File scripts/Smoke-Mcp.ps1
```

테스트 범위: 오버로드/모호한 이름, 실제 참조와 호출 구분, 이벤트 등록, 인터페이스 구현 후보, 프로시저 상수, 주석, 미등록 문자열, 순환호출, 깊이·노드 제한, DB 미설정·연결 실패, 부분 로딩, 재로딩. SQL 의존성 조합 테스트는 모의 데이터이며 실제 DB 검증과 구분합니다.

## 제한사항

[limitations.md](docs/limitations.md)에 지원 범위를 기록합니다. `Ready`는 지원되는 정적 분석 범위의 상태이며 운영 실행·저장 성공을 뜻하지 않습니다. 전체 소스 body는 반환하지 않습니다.

실제 업무 소스, 내부 접속정보, 분석 결과와 로그는 커밋하지 않습니다. GitHub 업로드 및 라이선스 선택은 아직 진행하지 않았습니다.
