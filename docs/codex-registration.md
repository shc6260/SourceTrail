# Codex 등록 예제

사용자 설정의 `~/.codex/config.toml` 또는 신뢰하는 프로젝트의 `.codex/config.toml`에 아래 형식으로 등록합니다. 실제 설정 파일은 이 작업에서 변경하지 않았습니다. 경로는 각 사용자의 빌드 출력 또는 publish 경로로 바꿉니다.

```toml
[mcp_servers.sourcetrail]
command = "dotnet"
args = ['D:\Tools\SourceTrail\src\SourceTrail.Mcp\bin\Debug\net10.0\SourceTrail.Mcp.dll']
startup_timeout_sec = 30
tool_timeout_sec = 300
```

솔루션은 연결 후 load_solution 입력으로 지정합니다. DB 접속정보는 외부 환경변수 또는 서버 DLL 옆의 비공개 appsettings.json으로 설정합니다. tool timeout 예제는 대형 솔루션용이며 실제 로딩 시간에 맞게 조정합니다.

기본 사용 예제:
1. SourceTrail의 load_solution으로 원하는 .sln 열기.
2. find_symbol로 원하는 클래스·메서드 찾기.
3. 반환된 ID로 find_references 또는 trace_code_to_database 호출.

실제 Codex 등록·호출은 아직 검증하지 않았으며 로컬 MCP stdio 연결은 검증했습니다.

공식 형식 근거: [OpenAI 공식 MCP 설정 문서](https://learn.chatgpt.com/docs/extend/mcp?surface=cli).
