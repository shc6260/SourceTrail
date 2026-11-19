# 일괄 색인 준비

설치 프롬프트, 실행·검색 순서, 일괄 생성 명령의 요약은 [메인 README](../README.md)에 있습니다. 이 문서는 저장·게시 계약의 상세입니다.

`IndexPreparationService.PrepareAsync`는 C# 전체 선언, WinForms UI, 로컬 SQL 색인을 순서대로 준비합니다. 컴파일러·MSBuild·DB 연결은 사용하지 않습니다. 원본 소스와 SQL은 변경하지 않습니다.

## 실행

publish된 서버 DLL로 실행합니다. 경로를 생략하면 DLL 옆 appsettings.json과 환경변수의 Analysis.SolutionPath, Database.SqlFolder, Indexing.Directory를 사용합니다.

```powershell
dotnet .\artifacts\server\SourceTrail.Mcp.dll prepare-index --source 'D:\Projects\Sample\Sample.sln' --sql-folder 'D:\Projects\Sample\DatabaseScripts' --index-directory 'D:\Caches\SourceTrail\Prepared'
```

기본은 증분 준비입니다. 기존 캐시를 작업 폴더에 복사하고 메타데이터가 같은 C#·SQL 파일을 재사용합니다. 수정·추가 파일의 선언과 SQL을 다시 분석하고 삭제된 파일은 제외합니다. UI는 기존 fingerprint가 같으면 재사용하며, UI 입력이 바뀌면 UI 색인 전체를 다시 생성합니다. 메타데이터를 그대로 보존한 오프라인 수정은 기본 증분 모드가 구분하지 못할 수 있습니다.

전체 재생성은 같은 명령에 `--rebuild`를 추가합니다. 기존 캐시를 복사하거나 사용하지 않고 전부 다시 분석합니다. 기존 캐시를 먼저 삭제하지 않습니다.

MCP에서는 설정된 경로를 대상으로 `prepare_indexes`에 `{}` 또는 `{"rebuild":true}`를 전달합니다. 일반 검색마다 이 도구를 호출하지 않습니다. 분석 대상 경로는 명령줄 옵션 또는 설정에 명시해야 하며 현재 대화에서 임시로 선택한 경로를 추정하지 않습니다.

CLI stdout에는 결과 JSON 한 건, stderr에는 단계·진행 건수가 출력됩니다. 반환값은 Published, Mode, Generation, Code(파일/파싱/재사용/선언/구문 진단 건수), Ui(생성 또는 재사용/화면 수), Sql(파일/객체/파싱/재사용/경고), Stages(단계별 밀리초), ElapsedMs, Error를 포함합니다. UI의 생성·재사용 단위는 화면 수가 아니라 색인 하나입니다. 종료 코드는 완료 0, 실패 1, 취소 130입니다. SQL 구문/지원 범위 경고나 C# 구문 진단은 결과에 남으며 컴파일이나 의미 분석의 성공을 뜻하지 않습니다.

## 저장과 게시

Indexing.Directory 기본값은 `%LOCALAPPDATA%\SourceTrail\prepared-index`입니다. 경로는 쓰기 가능한 개인 로컬 폴더로 지정합니다. 원본 SQL 정의와 소스 선언을 포함하므로 공개 저장소에 추가하지 않습니다.

1. prepare.lock으로 같은 저장소의 다른 준비 작업을 차단합니다.
2. generations/<고유 ID>/code와 sql에 새 색인을 준비합니다.
3. 게시 직전에 C#·UI·SQL 입력 메타데이터를 다시 대조합니다.
4. 성공하면 active.json을 임시 파일에서 원자적으로 교체합니다.
5. 취소·읽기/저장 실패·입력 변경 시 기존 active.json은 유지하고 이번 미게시 작업 폴더만 정리합니다.

기존 게시 세대는 자동 삭제하지 않습니다. 이미 실행 중인 MCP가 이전 세대를 계속 사용할 수 있기 때문입니다. 따라서 전체 재생성은 추가 디스크 공간이 필요합니다. 디스크 공간이 부족하면 새 작업을 실패시키고 기존 세대를 유지합니다.

**MCP를 재연결하면**, 시작 시 active.json의 소스·SQL 경로가 설정과 일치하는 경우 해당 세대의 C#·SQL 캐시를 함께 사용합니다. `Indexing__Directory` 또는 appsettings.json의 `Indexing.Directory`가 준비 명령과 같아야 합니다. 기존 프로세스는 자동 교체하지 않습니다. 활성 색인이 없거나 경로가 다르면 기존의 요청 시 준비 방식을 사용합니다. 경로 일치 시 활성 세대의 캐시 경로가 Analysis.CodeCacheDirectory와 Database.CacheDirectory보다 우선합니다.

현재 C# 선언 캐시는 한 소스 목록 파일에 저장하므로 큰 소스 트리에서는 저장·재로딩 비용과 크기가 커질 수 있습니다. 일괄 생성은 이 비용을 사전 준비 단계로 옮기며 검색 속도 향상을 보장하지는 않습니다. 첫 생성 시간, 캐시 크기, 재실행 로딩 시간을 별도로 측정해야 합니다. 추가 텍스트 검색 경로는 C# 선언 일괄 색인 대상에 포함하지 않습니다.
