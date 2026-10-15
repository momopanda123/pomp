# POMP 아키텍처 보완 문서 (설계 문서 v0.9 → 구현 기준)

구성: 4계층(VS 호스트 · POMP Core · omp 엔진 · 채팅 화면), 컴포넌트, 데이터 흐름.
이 문서는 원본을 **실제 omp(oh-my-pi) RPC 프로토콜**과 **추가 요구사항(VS 2019 이상 지원)** 에 맞춰
구체화한 구현 기준이다. 원본과 충돌하면 이 문서가 우선한다. 새 기능은 이 문서를 먼저 갱신한 뒤 구현한다.

## 1. 대상 IDE — VS 2017 15.9 이상 (원본의 "VS 2022 17.8+"를 대체)

| 대상 | 프로세스 | VSIX 프로젝트 | VS SDK | VSSDK BuildTools | InstallationTarget |
|---|---|---|---|---|---|
| VS 2017 15.9 | x86 | `src/Pomp.Vsix.VS2017` | `Microsoft.VisualStudio.SDK` 15.9.3 (Roslyn 2.10.0, NuGet 4.9.6) | 16.11.71 | `[15.9,16.0)` |
| VS 2019 16.10+ | x86 | `src/Pomp.Vsix.VS2019` | `Microsoft.VisualStudio.SDK` 16.10.31321.278 | 16.11.71 | `[16.10,17.0)` |
| VS 2022 (17.x) · VS 2026 (18.x) | amd64 | `src/Pomp.Vsix.VS2022` | `Microsoft.VisualStudio.SDK` 17.0.32112.339 | 17.14.2142 | `[17.0,)` amd64 |

- VS 관련 코드는 `src/Pomp.VisualStudio/`(프로젝트 파일 없는 공유 소스 폴더)에 두고 세 VSIX 프로젝트가 링크 컴파일한다.
  `VS2017` / `VS2019` / `VS2022` 조건부 컴파일 기호가 정의되며, 세 VSIX는 같은 소스를 각자의 최소 SDK로
  컴파일한다. `#if VS2017` 분기: `Microsoft.VisualStudio.Threading` 15.8에 없는 `SwitchToMainThreadAsync(alwaysYield:)` 대신 `Task.Yield()` 뒤
  전환(세 곳), 그리고 **기본 CodeLens 캡션 제외** — VS 2017의 CodeLens API(15.8)에는 사용자 지정 상세 표시(`CodeLensDetailsDescriptor.CustomData`,
  `IViewElementFactory` 변환)가 없어 VS2017 VSIX는 CodeLens 공급자를 싣지 않고(`Editor/CodeLensCaptionFeature.cs` 제외) 옵션
  **캡션에 기본 CodeLens 사용**을 숨긴다(POMP 자체 코드 캡션은 그대로). VS 2017에서 실행 검증은 아직 하지 않았다(이 개발 환경에 VS 2017이 없다).
- C# 언어 버전은 `Directory.Build.props`에서 `12.0`으로 고정한다(설치된 SDK를 따라 조용히 올라가지 않게).
- 세 VSIX는 **서로 다른 Identity**(`Pomp.VSIX.VS2017`, `Pomp.VSIX.VS2019`, `Pomp.VSIX`)와 표시 이름(`POMP (VS2019)` 등)을 쓴다
  (Marketplace 목록도 분리). 64비트 패키지 하나(`Pomp.VSIX`, 표시 이름 `POMP`)가 VS 2022와 VS 2026을 함께 커버한다
  (VS 2026은 17.x API로 동작하고 설치 범위의 상한을 무시하므로 `[17.0,)` 하나로 양쪽에 설치된다 — Microsoft 호환성 모델).
  사용자는 자기 VS 버전의 패키지를 설치한다.
- 최소 버전의 SDK로 컴파일한다 (상위 버전 API 사용 시 하위 VS에서 로드 실패).
- 터미널 API(17.5+)는 v1 범위 밖(원본 위험 대응과 동일) — Output/ErrorList 중심.

## 2. 솔루션 구성

```
Pomp.sln
src/Pomp.Contracts/      netstandard2.0  계약: omp 와이어 모델, 브릿지/툴/권한/어댑터 인터페이스, UI 프로토콜
src/Pomp.Core/           netstandard2.0  VS 비의존 구현: OmpProcessBridge, NDJSON/v2 청크, 툴 레지스트리·디스패치,
                                          권한 게이트, 세션 컨트롤러(UI↔omp 변환)
src/Pomp.Tools/          netstandard2.0  호스트 툴 구현, 경로 검사, 버퍼 스냅샷 저장소
src/Pomp.CodeLens.Contracts/ netstandard2.0  CodeLens 위치 DTO (소스 본문/모델 실행 없음)
src/Pomp.CodeLens.Provider/  netstandard2.0  CodeLens 서비스용 OOP 공급자 (VS 2019 이상)
src/Pomp.VisualStudio/   (공유 소스)      AsyncPackage, 명령, ToolWindow(WebView2), 테마 브릿지, 옵션, 어댑터 구현
src/Pomp.Vsix.VS2017/    net472          VS 2017 15.9 VSIX (x86, 15.9 SDK, CodeLens 공급자 없음)
src/Pomp.Vsix.VS2019/    net472          VS 2019 VSIX (x86)
src/Pomp.Vsix.VS2022/    net472          VS 2022·2026 VSIX (x64, 설치 범위 [17.0,))
src/Pomp.WebUI/          Vite+React+TS   채팅 UI (오프라인 번들 → VSIX `WebUI/`)
src/Pomp.OmpExtension/   TypeScript      omp 측 확장: pomp-context Extension, pre-edit Hook, vs-project Skill
build/build.ps1                          WebUI 빌드 → 세 VSIX MSBuild → artifacts/
docs/                                    이 문서, ui-protocol.md
```

의존 방향: `Pomp.VisualStudio → Pomp.Core → Pomp.Contracts`, `Pomp.VisualStudio → Pomp.Tools → Pomp.Contracts`.
호스트 composition은 `HostToolCatalog.CreateDefault`로 **제공 가능한 19개 툴만** 레지스트리에 등록하고 실제 `PompSession`을
생성한다(`remove_project_file`은 영구 제외, 파일 삭제는 omp의 승인 게이트가 있는 `delete` 툴을 따름 — §5·§6).
솔루션 변경 재시작은 Core가, 옵션 변경 재시작은 VS 호스트가 담당한다. 옵션 적용은 **실행에 영향을 주는 값**(omp 경로, 승인 모드,
모델, thinking, 추가 인수, rpc-ui, 확장 로드 두 가지)이 바뀐 경우에만 omp를 재시작한다(750 ms 디바운스).
JSON은 `Newtonsoft.Json`(VS 제공 어셈블리 사용, VS 2019용 VSIX만 폴백 사본 포함).

### 런타임 배치

| 경로 (`%LOCALAPPDATA%\Pomp\` 아래) | 내용 |
|---|---|
| `omp\pomp-overlay.yml` | omp `--config` 오버레이 (제공 툴의 `tools.approval.<name>: allow`) |
| `state\<VS 프로세스 id>\vs-state.json` | VS 상태(열린 문서·dirty) — omp 측 확장이 읽는다. 종료된 VS의 폴더는 다음 시작 때 정리 |
| `workspace\` | 솔루션/폴더가 열려 있지 않을 때 omp의 작업 폴더(사용자 프로필 전체를 작업 루트로 쓰지 않기 위함) |
| `logs\pomp-vs.log` (+ `pomp-vs.1.log`) | 호스트 로그 (출력 창 "Pomp" 창과 동일, 경고 이상은 ActivityLog에도) |
| `WebView2\VS<주 버전>\` | WebView2 사용자 데이터 폴더 (VS 주 버전별 분리) |

| 경로 (`<작업 영역>\.pomp\` 아래) | 내용 |
|---|---|
| `agents\`, `teams\`, `rules\`, `workflows\`, `skills\`, `hooks\`, `permissions.json` | 이 프로젝트 범위로 만든 에이전트·팀·규칙·워크플로·스킬·훅·권한 정책 (저장소로 팀과 공유할 수 있다) |
| `local\` | 이 프로젝트에 대한 POMP 자체 데이터: 대화(`sessions\`, omp `--session-dir`), 최근 대화, 컨텍스트 프리셋, 지식, 체크포인트, 리뷰, 백그라운드 에이전트, 에이전트 실행 기록, 자동화 규칙, 케이스 의견, 격리 작업 트리(`wt\`), 엔진용 에이전트 사본(`engine-plugin\`). 자체 `.gitignore`(`*`)로 커밋에서 빠진다 |

- 프로젝트의 `.pomp` 폴더를 지우면 그 프로젝트의 POMP 내용이 초기화된다. 이전 버전이 `%LOCALAPPDATA%\Pomp\<기능>\<해시>`,
  `~\.omp\agent\sessions\<작업 폴더>`, `<작업 영역>\.omp\agents`에 두던 프로젝트 데이터는 처음 쓸 때 이 폴더로 **옮긴다**(복사하지
  않으므로 지운 뒤 되살아나지 않는다).
- 작업 영역 신뢰(`workspace-trust.json`)와 항상 허용한 도구 승인은 프로젝트 밖에 남는다. 저장소가 이것을 미리 담아 배포할 수 없게 하기 위해서다.

- 패키지는 백그라운드 로드를 허용하지만 자동 로드(`ProvideAutoLoad`)는 없다 — 도구 창·명령·옵션을 처음 쓸 때 로드된다.
- 도구 창은 WebView2 초기화와 채팅 페이지 탐색이 성공한 **뒤에** omp를 시작한다. WebView2 런타임이 없거나
  `111.0.1661.41`보다 오래되면 안내 패널(다운로드 링크·재시도·로그 보기)을 띄우고 omp를 시작하지 않는다.
- 도구 창을 닫아도 omp 세션은 유지된다(창 숨김). `ui/ready`마다 호스트가 대화 로그를 재생한다
  (최대 3 MiB/3000개, 오래된 턴부터 제거). 새 세션을 완료하면 재생 로그를 비운다.

슬래시 명령 목록은 엔진 연결별로 정규화해 `commands/state`로 전달하고 페이지 준비 때 재전송한다. 세션 목록은
POMP가 기억한 참조와 현재 세션 폴더의 JSONL 파일 전부(개수 제한 없음, 파일당 앞 64 KiB)을 합쳐 캐시하며,
파일 읽기는 채팅 명령 큐 밖에서 처리한다. 페이지에는 경로 대신 불투명 핸들만 주고 전환·미리보기 때 직접 경로와
재분석 지점 여부를 다시 확인한다. 미리보기는 엔진 없이도 최대 1 MiB·20개 텍스트 메시지만 읽으며, 연결이 끝나도
같은 작업 공간의 폴더는 유지하되 작업 공간이 바뀌면 버린다. 이름 변경·압축 후에는 세션 정보를 다시 조회한다.

## 3. 원본 RPC 메시지 ↔ 실제 omp RPC (`omp --mode rpc`, NDJSON over stdio)

omp 정식 명세: <https://github.com/can1357/oh-my-pi/blob/main/docs/rpc.md>

| 원본 설계 메시지 | omp 실제 프레임 | 비고 |
|---|---|---|
| handshake | `ready`(stdout) → `negotiate_protocol` v2 → `set_host_tools` → `get_state` | 브릿지가 수행. v2는 대형 프레임을 `rpc_chunk`로 분할 |
| prompt | `{type:"prompt", id, message}` → `response` 즉시 ack → … → `prompt_result` | 에디터 컨텍스트 요약(addAdditionalContext)은 호스트가 message 앞에 삽입. `streamingBehavior`(steer/followUp)는 보내지 않는다 — 아래 "한 번에 한 메시지" |
| tool_call | `host_tool_call {id, toolCallId, toolName, arguments}` | `set_host_tools`로 등록한 호스트 툴만 |
| tool_result | `host_tool_result {id, result:{content:[{type:"text",text}]}, isError?}` / 진행중 `host_tool_update` | |
| permission_request | ① 호스트 툴: 호스트 `PermissionGate`가 실행 전 직접 판단 ② omp 내장 툴: `extension_ui_request {method:"select", title:"Allow tool: <name>\n…", options:["Approve","Deny"]}` | ①은 omp 승인 오버레이에서 `allow`로 두고 호스트가 게이트 |
| permission_result | ② `extension_ui_response {id, value:"Approve"\|"Deny"}` | |
| status_update | `agent_start/end`, `message_update`(delta), `tool_execution_*`, `get_state.contextUsage` | UI에는 `session/info`, `chat/*`로 변환 |
| error | `response{success:false}`, `prompt_result{status:"error"}`, `extension_error` | |
| cancel | `abort` (진행 중 호스트 툴은 `host_tool_cancel`로 역통지) | |

턴 완료 판정: `prompt_result`(동일 id). 세션 완전 종료: `session_settled`.

- **한 번에 한 메시지**: 턴이 열려 있거나 omp가 스트리밍 중일 때의 `chat/send`는 omp에 보내지 않고 즉시 `busy` 오류로
  끝낸다. omp는 대기열에 넣은 후속 메시지(followUp)를 `abort` 뒤에도 유지하므로, omp 안에 줄을 세우면 중지가 무력해진다.
- **중지**: 응답 대기/컨텍스트 수집과 아직 제출되지 않은 메시지를 취소하고, omp가 기다리는 승인 프롬프트를 거부로
  풀어 준 뒤 `abort`를 보낸다. 이미 쓰기 시작한 NDJSON 프레임은 완성한 다음 중지하여 파이프의 메시지 경계를 보존한다.
  omp가 보고하지 않는 턴(omp가 메시지를 보류한 경우)은 omp가 정착 지연(기본 5초) 동안 유휴이면 호스트가 `aborted`로
  닫는다. omp가 아직 작업 중이면(느린 중단, omp가 스스로 깨어난 백그라운드 작업) 턴을 연 채 다시 확인한다.
- `prompt` 응답(ack) 대기는 30초다. 시간 초과는 `timeout` 오류로 끝내되 omp가 이미 실행 중일 수 있음을 알린다(재시도 불가 표시).
- **omp 세대 고정**: 메시지는 보낼 때의 omp 프로세스에 묶인다. 컨텍스트를 모으는 사이 omp가 종료·재시작되면 그 메시지는
  새 프로세스로 보내지 않는다(UI에는 이미 턴 종료가 보고됨). 턴 종료는 항상 그 턴의 시작(`chat/turnStarted`) 뒤에 게시된다.
- **입력 쓰기 제한**: 명령별 시간 제한은 쓰기 차례 대기·stdin 쓰기·응답 대기를 모두 포함한다. 시간 제한이 프레임을 쓰는 도중에
  걸리거나 프레임 하나를 쓰는 데 30초를 넘기면(omp가 입력을 읽지 않음) 반쯤 쓴 프레임이 남을 수 있으므로 그 연결을 재사용하지
  않고 omp를 종료한다(쓰기 시작 여부가 애매한 경합에서도 종료 쪽을 택한다). 아직 쓰기 전이었다면 그 명령만 시간 초과로 끝난다.
  이후는 일반 재시작 절차를 따른다.
- 하트비트: 실행 중 30초 동안 출력이 없으면 `get_state`를 보내고, 3회 연속 응답이 없으면 해당 프로세스를
  종료하여 일반 재시작 절차로 넘긴다. 출력 수신도 생존 신호로 취급한다. 프로세스 종료·stdout EOF·명령별 시간 제한도 감시한다.

## 4. omp 실행 계약 (호스트 → omp)

```
omp --mode rpc [--mode rpc-ui] --cwd <솔루션 루트> --approval-mode <always-ask|write|yolo>
    --config <%LOCALAPPDATA%\Pomp\omp\pomp-overlay.yml> --session-dir <작업 영역\.pomp\local\sessions>
    [--plugin-dir <작업 영역\.pomp\local\engine-plugin>] [--no-extensions] -e <확장 폴더\omp-extension>
    [--model <id>] [--thinking <level>] [추가 인수]
```

- **`--no-extensions`(기본)**: omp가 스스로 찾는 확장·훅(사용자 프로필, 설치된 플러그인, 작업 폴더의 `.omp/extensions`)을
  끈다. 명시한 `-e` 패키지(pomp)와 그 `skills/`(`vs-project`)는 그대로 로드된다(omp 18.4.3·18.8.6·18.10.3에서 확인: 작업 폴더
  프로브 확장은 로드되지 않고, 옵션을 켜면 로드됨. 18.10.4에서는 pomp 패키지의 명령과 `vs-project`가 로드되는 것을 확인). 도구 > 옵션 > Pomp의 "Load discovered omp extensions"(기본 꺼짐)를 켜면
  이 플래그를 빼며, 이런 확장은 omp 안에서 샌드박스 없이 실행된다.
- **프로세스 작업 폴더**: omp는 시작한 폴더의 `.env`를 `--cwd`로 옮기기 전에 읽어, 비어 있는 환경 변수(공급자 주소,
  프록시, API 키 등)를 채운다. 그래서 엔진 프로세스는 항상 POMP 폴더(`%LOCALAPPDATA%\Pomp\workspace`)에서 시작하고 작업 루트는
  `--cwd`로만 넘긴다. 저장소의 `.env`는 엔진에 들어가지 않는다.
- **작업 영역 신뢰**(아래 "작업 영역 신뢰"): `--no-extensions`와 무관하게 omp는 작업 폴더의 설정 파일(`tools.approval.*`,
  `bash.patterns`, 셸·엔드포인트 설정), 모든 공급자 형식의 MCP 서버 목록, 도구 코드(`.omp/tools` 등), 컨텍스트 파일의 `@경로`
  포함을 사용자 설정과 같은 신뢰로 읽고 실행한다(omp 18.10.4에도 프로젝트 신뢰 확인이 없다). POMP는 이런 구성이 있는 작업
  영역에서, 사용자가 그 내용을 신뢰하기 전에는 omp를 시작하지 않는다.
- 추가 인수(`ExtraArguments`)는 설정에 평문으로 저장된다. 로그에는 이름에 key/token/secret/password/passwd/credential/auth가
  들어간 플래그의 값을 `***`로 가린다(omp에는 원래 값이 전달된다).

- 실행 파일 해석 순서: 옵션의 경로 → VSIX 번들(`<확장 폴더>\omp\omp.exe`) → PATH(`omp.exe`/`omp.cmd`/`omp.bat`)
  → 알려진 설치 위치(`%LOCALAPPDATA%\omp`, `%USERPROFILE%\.bun\bin`, `%APPDATA%\npm`). 정식 패키지에는 항상 번들 엔진이
  있으므로 PATH·설치 위치는 엔진 없는 개발 패키지에서만 쓰인다. 번들 엔진이 손상되면 다른 omp로 대체하지 않는다(아래 "엔진 버전").
- 기본 승인 모드 `always-ask`: 읽기는 자동, 쓰기·실행은 사용자 승인 (원본 "권한 투명성").
- 오버레이 `pomp-overlay.yml`: 모든 호스트 툴을 `tools.approval.<name>: allow` (호스트가 자체 게이트하므로 이중 승인 방지).
  `skills.customDirectories`(POMP 스킬 폴더 2개 + 사용자·프로젝트 설정값)와 `disabledExtensions`(POMP에서 끈 스킬 +
  사용자·프로젝트 설정값)도 병합해서 쓴다 — 오버레이 배열이 하위 레이어를 통째로 대체하므로 병합하지 않으면 사용자 설정이
  지워진다. 절대 경로·유효한 YAML이어야 한다(오버레이 오류는 omp 시작 실패).
- **omp 측 확장 로딩**: `-e`에는 `.ts` 파일이 아니라 **`omp-extension` 폴더**(절대 경로)를 준다 — 폴더의 `package.json`
  (`omp.extensions`)이 모듈을 지정하고, `skills/`는 폴더 루트에서만 발견된다. `--hook`은 `-e`의 별칭이라 쓰지 않는다.
  omp 인수 파서가 확장자 없는 Windows 경로 뒤의 비(非)플래그 토큰을 붙여 해석할 수 있으므로 폴더 경로 다음 인수는
  반드시 `--`로 시작하는 플래그이거나 마지막 인수여야 한다. `--no-ui`, `--no-skills`는 쓰지 않는다.
- 호스트 툴은 `set_host_tools`에서 **`loadMode: "essential"`** 로 등록한다 (생략 시 `discoverable` → `xd://` 장치 뒤로 숨겨짐).
  이름은 접두사 없이 그대로 등록되며 승인 키도 동일하다.
- 환경 변수: `POMP_HOST=1`(필수 — 없으면 omp 측 확장이 아무것도 등록하지 않음), `POMP_STATE_DIR`(필수),
  `POMP_HOST_TOOLS`(호스트가 실제로 제공한 툴 이름, 쉼표 구분 — `set_host_tools`·오버레이와 같은 목록),
  `POMP_HOST_VERSION`, `POMP_VS_VERSION`, `POMP_SOLUTION_ROOT`, 선택 `POMP_PRE_EDIT_MODE=block|warn|off`(기본 block).
- `POMP_STATE_DIR\vs-state.json`: 호스트가 원자적으로 갱신하는 VS 상태(열린 문서·dirty 여부) — omp 측 pre-edit Hook이
  이를 보고 **VS에서 수정 중(dirty)인 파일**에 대한 omp 내장 `edit`/`write`/`ast_edit`/`lsp` 변경을 차단한다. 안내 문구는
  `POMP_HOST_TOOLS`에 따라 달라진다: `edit_buffer_hashline`이 제공되면(현재 기본) `read_buffer` → `edit_buffer_hashline`을,
  제공되지 않으면 "VS에서 저장(Ctrl+S)을 요청한 뒤 일반 도구로 다시 시도, 저장 안 된 변경을 덮어쓰지 말 것"을 안내한다.
  제공되지 않은 툴 이름은 어떤 안내문에도 나오지 않는다. 설계의 "사전 편집 시 해시 비교"가 아니라 dirty 상태로 판단하는
  이유는 저장 안 된 버퍼 내용이 디스크 해시로는 드러나지 않기 때문이다. 상태 갱신 주기는 2초이므로 그 사이에 생긴 dirty
  상태는 훅이 아직 모를 수 있다.
- omp 측 구성: `pomp-context` Extension(시스템 프롬프트에 VS 안내 추가 — 제공 툴 수에 따라 약 230토큰 이상, 8개 기준
  약 330토큰; 솔루션 변경 시 숨김 메시지로 통지), `pre-edit` Hook(`tool_call`에서 `{block:true, reason}`),
  `vs-project` Skill(`skill:vs-project`, 지연 로딩), `verify-gate`(`session_stop`에서 미확인 소스 편집 시 한 번만 확인 유도;
  고정 "완료 전 증거" 안내는 항상 적용), `untrusted-content`(웹·MCP·브라우저 결과에 외부 데이터 표시),
  `compaction-context`(자동 압축 요약에 솔루션·미저장 버퍼·워크플로 단계·거부 승인 유지), `time-context`(날 바뀔 때만 로컬 시각 통지).
  범위 규칙(`.mdc`, 최대 5개/24,000자)과 리소스 카탈로그는 시스템 프롬프트가 아니라 내용이 바뀔 때만 다시 보내는 숨김 메시지로
  전달하므로 세션 내 시스템 프롬프트는 바이트 단위로 안정적이다(공급자 프롬프트 캐시 유지).
- omp의 하위 에이전트(`task` 도구)는 omp 안에서 헤드리스로 실행되며 자체 승인 프롬프트를 띄우지 않는다(omp 동작).
  승인 경계는 부모의 `task` 호출 승인이다 — `always-ask`에서도 하위 에이전트 안의 쓰기·실행은 개별 확인되지 않는다.
- 자식 프로세스는 Windows Job Object에 넣어 VS 종료 시 함께 종료(좀비 방지).
- 재시작: 실패 1회 → 1초 후, 2회 → 2초 후 재시작, **연속 3회 실패 시 `failed`** 상태로 사용자 선택(재시작/로그 보기).
  60초 이상 정상 실행되면 실패 횟수를 초기화한다. 실행 파일을 찾지 못하는 등 치명적 오류는 즉시 `failed`.
  omp가 모델 없이 종료되면(`No models available`) 재시도하지 않고 바로 `failed`(`reason: "setupRequired"`)가 되며,
  채팅 창은 오류 대신 엔진 설정 패널을 보여 준다(아래 "엔진 설정").

### 작업 영역 신뢰

omp(18.4.3부터 현재 18.10.4까지)는 프로젝트 신뢰를 확인하지 않는다(`isProjectTrusted: () => true`). 작업 폴더에서 읽는 다음 구성은 시작과 동시에
코드를 실행하거나, 승인을 바꾸거나, 바깥 파일을 대화에 넣는다. MCP stdio 서버는 엔진 환경(POMP가 넣은 API 키 포함)을 그대로 받는다.

| 종류 | 파일 (작업 루트 기준) |
|---|---|
| 엔진 설정 | `.omp/config.yml`, `.omp/settings.json`, `.claude/settings.json`, `.cursor/settings.json`, `.gemini/settings.json`, `opencode.json[c]`, `.opencode/opencode.json[c]`, `.codex/config.toml` |
| MCP 서버 | `.omp/mcp.json`, `.omp/.mcp.json`, `mcp.json`, `.mcp.json`, `.claude/.mcp.json`, `.claude/mcp.json`, `.cursor/mcp.json`, `.windsurf/mcp_config.json`, `.vscode/mcp.json` |
| 도구 코드 | `.omp/tools`, `.claude/tools`, `.codex/tools` 아래 `.ts/.js/.mjs/.cjs`(`--no-extensions`로 꺼지지 않음) |
| 플러그인 | `.omp/plugins/{package.json, installed_plugins.json, omp-plugins.lock.json}`, `.omp/plugin-overrides.json` |
| 언어 서버 | 루트와 `.omp/ .claude/ .codex/ .gemini/`의 `lsp.json`, `.lsp.json`, `lsp.yaml`, `.lsp.yaml`, `lsp.yml`, `.lsp.yml` |
| 언어 서버 실행 파일 | omp는 기본 언어 서버(`tsc`, `gopls`, `pyright-langserver` 등 55개)를 처음 쓸 때 PATH보다 먼저 작업 영역의 도구 폴더에서 찾는다: 루트에 `package.json`류가 있으면 `node_modules/.bin`, Python 설정이 있으면 `.venv`·`venv`·`.env`의 `bin`·`Scripts`, `Gemfile`이면 `vendor/bundle/bin`·`bin`, `go.mod`류면 `bin`. 그 서버의 표지 파일이 루트에 있을 때 omp가 고를 첫 실행 파일(확장자 없음·`.exe`·`.cmd`·`.bat` 순) |
| 확장 코드 | "Load discovered omp extensions"를 켰을 때만: `.omp/extensions`, `.omp/hooks`, `.claude/hooks`, `.codex/hooks`, `*/commands` 아래 스크립트 |
| 조언자 | `WATCHDOG.yml`, `WATCHDOG.yaml`, `.omp/WATCHDOG.yml`, `.omp/WATCHDOG.yaml`(조언자의 모델·도구·지시. 신뢰 카드에는 에이전트 설정으로 보인다) |
| 바깥 파일 포함 | `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `.omp/AGENTS.md`, `.github/copilot-instructions.md`, `SYSTEM.md` 계열과 그것이 포함한 작업 영역 안 파일의 `@경로` 중 작업 영역 밖의 기존 파일(omp는 `~`, 절대 경로, `..`를 제한 없이 포함한다) |

`.pomp/agents`(POMP 역할), `.omp/rules`, 스킬처럼 지시문만 담는 파일과 그 밖의 `.pomp/`는 대상이 아니다.

- 엔진 시작(`BuildLaunchOptionsAsync`) 때 `WorkspaceTrust.Evaluate`가 위 파일을 찾고, 파일 경로·내용(SHA-256)·포함 대상으로
  지문을 만든다. 구성이 있고 그 지문을 신뢰한 적이 없으면 omp를 시작하지 않는다(`bridge/state` `failed`,
  `reason: "workspaceTrustRequired"`, 재시도 없음). 채팅의 연결 알림 자리에 목록과 [신뢰하고 시작]·파일별 [파일 열기]·[시작 안 함]이
  나온다(`workspace/trustState`, `workspace/trust`).
- 신뢰는 사용자가 본 지문만 저장한다(`%LOCALAPPDATA%\Pomp\workspace-trust.json`, 작업 영역 밖, 개수 제한 없이 1 MiB까지 최근 것부터). 파일이 추가·변경·삭제되면
  (git pull, 에이전트의 수정 포함) 지문이 달라져 다음 시작 때 다시 묻는다. 목록을 본 뒤 바뀌었으면 신뢰 요청을 거부하고 새 목록을 보인다.
- 사용자가 도구 › MCP에서 프로젝트 MCP 서버를 바꾸면(POMP가 `.omp/mcp.json`을 씀) 바꾸기 전 구성이 없었거나 신뢰한 그대로였고
  다른 파일이 바뀌지 않았을 때만 새 지문을 신뢰로 이어 간다(`WorkspaceTrust.KeepAfterOwnWrite`). 신뢰한 적 없는 구성은 계속 묻는다.
- 도구 › 권한 › 신뢰한 저장소에서 해제한다. 지금 작업 영역을 해제하면 엔진을 다시 시작해 다시 묻는다.
- 남은 한계: 작업 루트보다 위 폴더의 컨텍스트 파일과 그 `@` 포함, 지시문만 담은 파일(역할·규칙·스킬)은 확인하지 않는다.
  지시문은 승인 절차를 바꾸지 못하며, 실행·설정 변경은 승인 카드에서 묻는다. omp의 `debug`가 작업 영역에서 찾는 디버그 어댑터는
  `debug` 승인(매번 확인)을 거친다.
- 실행 중에 에이전트가 이 파일을 고치는 요청은 기억된 허용으로 답하지 않고 매번 묻는다(6장 "매번 확인하는 요청"). omp는 설정 파일을
  실행 중에도 다시 읽기 때문이다.

### 엔진 버전 (POMP가 omp를 고정)

POMP는 특정 omp 버전과 함께 만들고 시험한다. omp의 RPC 계약(`get_state`, `set_fast_mode`, `get_available_thinking_levels`,
`omp usage --json` 등)은 버전마다 바뀔 수 있으므로 **POMP 릴리스 하나 = omp 버전 하나**로 묶어 배포한다.

- **어느 버전인지**: `engines/omp/engine.props` 한 곳에서 정한다 — 버전, 공식 릴리스 URL(`github.com/can1357/oh-my-pi`),
  자산 이름(`omp-windows-x64.exe`), 크기, SHA-256, `THIRD-PARTY-NOTICES.txt`의 SHA-256. 현재 **omp 18.10.4**
  (244,256,768바이트, `c7969e65…a7d5`). 라이선스는 MIT(`engines/omp/LICENSE.upstream`).
- **빌드**(`Pomp.Vsix.Common.targets`의 `PompPrepareOmpEngine`): `artifacts\engines\omp\<버전>\`에 엔진이 없으면 릴리스에서
  내려받고, 크기·해시가 고정값과 다르면 받은 파일을 지우고 빌드를 실패시킨다(엔진 POMP1011, 고지 파일 POMP1012).
  `-p:PompOmpExe=<절대 경로>`(build.ps1 `-OmpExe`)는 오프라인 빌드용 로컬 사본일 뿐 같은 해시 검사를 거친다 — 고정되지 않은
  omp는 패키지에 넣을 수 없다. 모든 VSIX에 `omp\omp.exe`, `omp\engine.json`(`{version, sha256, size, asset, source}`, 빌드가
  작성), `omp\LICENSE.txt`, `omp\THIRD-PARTY-NOTICES.txt`가 들어간다(Release VSIX 약 117 MB, Debug 약 245 MB). `-p:PompBundleOmp=false`는 엔진 없는
  개발 패키지를 만든다.
- **실행**(`BundledEngine.Inspect`, `PompSession.Engine.cs`): 시작할 때마다 `engine.json`이 고정한 크기와 SHA-256을
  `omp\omp.exe`와 비교한다(해시는 경로·크기·수정 시각별로 VS 프로세스당 한 번 계산, 약 1초). 일치하면 그 엔진만 쓴다.
  매니페스트가 있는데 엔진이 없거나 다르면 시작하지 않고 POMP 재설치를 안내한다(재시도 없음) — PATH나 설치 위치의 다른
  omp로 몰래 대체하지 않는다. 로그인 도우미와 `omp usage` 실행도 같은 규칙을 따른다(`BundledEngine.Refusal`). 이 검사는 손상·교체·호환성 사고를 막기 위한 것이며, 확장 폴더에 쓸 수 있는 사용자의 의도적인
  변조까지 막는 보안 경계는 아니다.
- **고급 재정의**: 도구 > 옵션 > Pomp의 omp 경로를 지정하면 그 파일을 쓰되 "검증 안 됨"으로 표시한다(번들 엔진이 손상돼도
  이 경우엔 시작한다). 엔진 없는 개발 패키지가 설치된 omp를 쓸 때도 같다. 파일 이름이 `omp`이면 `omp --version`을 경로별로
  한 번 실행해(15초 제한) 버전을 보여 준다.
- **UI**: 호스트는 `engine/info`(`docs/ui-protocol.md`)를 상태로 보내고 스냅샷에 포함한다. 검증된 내장 엔진이면 따로 보이는 줄이 없고,
  검증 안 된 엔진이면 ⋯ 메뉴 아래쪽에 "에이전트 <버전> · <어디서 온 실행 파일> · 검증 안 됨"이, 채팅 위에 닫을 수 있는 경고가 뜬다.
- **업데이트 정책**: omp 업데이트는 **POMP 업데이트로만** 들어온다. omp 자체의 `omp update`·시작 시 업데이트 확인은 대화형
  모드에서만 알림을 띄우며 POMP가 쓰는 RPC 모드에서는 아무것도 설치하지 않는다. PC에 설치된 omp가 바뀌어도 POMP는 자기
  폴더의 고정 엔진만 쓴다. 새 omp를 채택하는 순서: `engine.props`의 버전·크기·해시 갱신 → 빌드(다운로드·검증) → Core/UI
  테스트와 실제 VS 확인(위 RPC 계약, 로그인·사용량·빠른 모드·생각 수준) → 두 매니페스트의 POMP 버전을 올려 함께 배포.

### 보조 모델

자동완성(Tab), 질문의 수정 제안, 코드 캡션, 지식 변환, 입력 예측 문장은 채팅 모델과 따로 정한 **보조 모델**을 쓴다. 채팅 창 계정 탭의 "보조 모델"(또는 도구 >
옵션 > Pomp > 일반 > 편집기의 "보조 모델"(`Helper model`))에서 고른다.
- 화면 선택은 **빠른 모델 / 채팅 모델과 같게 / 모델 직접 선택**. 내부 값: 비움 = **빠른 모델**(기본) — omp의 빠른 모델 역할 `@smol`(사용자가 설정한 fast 역할, 없으면 omp 내장 우선순위: Gemini Flash, Codex
  Spark, Haiku, GPT-5.6-Luna, *-mini …), `@chat` = 채팅 모델과 같게, 그 밖 = `provider/id`.
  omp가 찾지 못하는 모델은 다른 모델로 몰래 바꾸지 않고 실패한다(자동만 채팅 모델로 대신한다).
- 호출(`extensions/inline.ts`): `completeSimple`로 도구 없이 직접 호출한다. 자동완성·캡션은 추론을 끄고(`disableReasoning`),
  질문의 수정 제안은 모델이 제공하는 가장 낮은 단계(low/minimal)를 쓴다. 엔진 쪽 제한 시간은 자동완성 8초, 캡션 12초, 편집 45초다.
- 한 번에 한 요청만 엔진에 보낸다. 실행 중인 요청은 멈출 수 없으므로, 새 자동완성 요청은 그것이 끝나기를 기다리고 사용자가 계속
  입력하면 기다리던 이전 요청은 취소된다(가장 최근 것만 보낸다). 호스트 로그(디버그)에 응답한 모델과 걸린 시간이 남는다.
- 채팅 응답이나 승인 대기 중이면 편집기 요청은 보내지 않는다(같은 엔진 대화를 쓰기 때문). 순서를 기다린 요청도 보내기 직전에
  다시 확인하고, 그 사이 엔진이 다시 시작되거나 대화가 바뀌었으면 취소한다. 계정 탭의 "사용 모델" 확인이 이 때문에 거절되면
  5초마다(최대 2분) 다시 확인한다.
- 채팅 창이나 편집기 메뉴에서 바꾼 편집기 설정(보조 모델, 자동완성·코드 캡션 켜기/끄기)은 그 설정 하나만 저장한다. 열려 있는
  도구 > 옵션 대화상자의 적용하지 않은 다른 값은 저장되지도, 사라지지도 않는다.

### 에이전트·팀과 컴퓨터 사용

계약은 `docs/agent-teams.md`. 기본 omp(현재 18.10.4)만으로 동작한다(엔진 패치 불필요).
- **에이전트**는 omp 표준 형식의 정의 파일(`<작업 영역>/.pomp/agents/*.md`, `~/.omp/agent/agents/*.md`)이고 POMP는 그 파일을 만들고
  고치기만 한다. 프로젝트 역할은 `.pomp/local/engine-plugin/agents`에 복사해 `--plugin-dir`로 omp에 넘긴다. **팀**(2명 이상, `.pomp/teams/*.json` 또는 `%APPDATA%\Pomp\teams`)으로 보낸 메시지는 호스트가 확인한 뒤
  `/pomp-team <id> …`로 보내고, 확장(`extensions/teams.ts`)이 팀 안내와 함께 메인 대화에 넘긴다. 메인 대화가 omp `task` 도구로
  구성원에게 나누고, 구성원끼리는 omp IRC(`write agent://<id>`)로 조율한다.
- **하위 에이전트는 승인 없이 돈다**(omp가 `tools.approvalMode: yolo`로 실행; 부모의 `task` 승인이 경계). POMP는 하위
  에이전트에게 화면·브라우저 제어를 주지 않는다. 확장(`extensions/computer-guard.ts`)은 엔진의 `SettingsManager.create(ctx.cwd)`와
  `rawValue`로 **호출 세션의 실제 computer/browser 활성 설정**을 읽는다. 활성 기능에 `computer-state.json`의 같은 `sessionId`·기능별
  POMP 허용이 없으면 메인 세션의 `eval`도 막고, 허용이 있어도 하위 에이전트의 `eval`은 막는다. 직접 `computer`·`browser` 도구도
  같은 세션의 해당 기능 허용과 메인 세션이 필요하다. 설정 API를 읽지 못하면 `eval`을 차단한다. 상태 파일을 읽지 못하면 활성 기능의
  `eval`과 직접 제어 호출을 차단하고, 하위 에이전트의 `eval`도 제어 허용이 있거나 상태를 읽지 못할 때 차단한다. 환경 변수만으로 허용하지 않는다.
  이 경계는 엔진 설정 API의 세션별 유효 설정 해석에 기대므로 **엔진 업그레이드 때 API·세션 범위·eval prelude 우회를 다시 검증**한다.
  확장이 꺼져 있으면 컴퓨터 사용·브라우저 제어 둘 다 쓰지 않는다.
- **컴퓨터 사용**: 덮어쓰기 설정은 매 시작 `computer.enabled: false`, `browser.enabled`는 옵션(기본 끔, 네트워크 도구 차단 시 항상 끔).
  대화마다 페이지 토글(`computer/set`)이 RPC `prompt "/computer on|off"`(모델 호출 없음, 결과는 `command_output`)로 켜고 끈다.
  사용자가 입력한 `/computer`는 POMP가 가로채며 `/computer:…`·앞뒤 특이 공백·정규화된 철자도 호스트에서 차단한다
  (`ComputerUse.IsComputerCommand`, FormKC·대소문자 무시·명령 경계 검사). 엔진으로 직접 넘어가 권한을 켤 수 없고 페이지의
  `computer/set`만 토글한다. 켜기 전에 같은 세션의 상태 파일을 먼저 쓰고, 끄기는 omp가 확인한 뒤에 파일을 바꾼다.
  새 대화·세션 전환·엔진 재시작이면 꺼진다.
  옵션 "사용 안 함"은 `tools.approval.computer: deny`, "화면 보기도 확인"·Yolo 승인 모드는 `prompt`(모든 호출 확인).
  그 밖에는 omp 단계대로 화면 캡처는 묻지 않고, 클릭·입력·클립보드 쓰기는 매번 묻는다. 세 옵션을 바꾸면 엔진이 다시 시작된다.

채팅 UI는 작업 **진행 · 변경 · 코드 점검**, 에이전트 **역할 · 팀 · 조언자 · 자동화**, 도구 **대화 설정 · 권한 · 스킬 · MCP · 규칙 · 워크플로 · 지식**으로
나눈다. 목록은 공용 표(`components/ui.tsx` DataTable: 하나의 grid, 행은 subgrid)·스위치·배지·아이콘 버튼과 검색·20개씩 페이지로 그린다.
변경은 **대기 중 / 적용됨** 전환으로 보여 주고 칩에는 대기만 센다. 코드 점검은 에이전트 지적과 VS 컴파일러 오류·경고를 합쳐
현재 보낼 대상에 해결 요청 하나로 보낸다(편집기 컨텍스트 없음, 50개·12,000자). 승인된 명령은 호출 도구 카드, 구성원은 `task` 카드와
대화 상대 탭에 붙인다. 코드 검색 인덱스는 어휘 갱신과 임베딩 옵션 변경에 따른 벡터 재구축·중지를 자동으로 수행한다.
대화형 터미널은 진행 맨 아래에서 옵션·엔진 지원이 모두 있을 때만 표시한다(번들 엔진은 미지원).

### 엔진 설정

`setup/command` / `setup/state`(`docs/ui-protocol.md` "Engine setup")로 계정(로그인)·API 키 저장·실행 중인 omp의 모델
목록·선택·생각 수준·계정별 사용량을 제공한다. 모델이 없는 첫 시작은 재시도하지 않고 `setupRequired`로 설정 패널을 연다.
구현은 `Pomp.Core\Features`의 `ProviderSetup`(명령·상태), `OmpAuthHelper`(로그인 도우미 프로세스), `OmpUsage`(사용량 실행·파서),
`ProviderCredentialStore`(키)이다.

- **로그인 도우미(auth helper)** — omp RPC는 모델을 쓸 수 있어야 시작하므로, 터미널 없이 로그인하려고 호스트가 별도의 짧은
  omp를 소유한다: `omp --mode rpc --no-session --no-extensions --no-skills --no-rules --model openai/gpt --api-key
  pomp-setup-placeholder`. 자리표시자 키는 명령줄에만 두고, 환경 `OPENAI_BASE_URL=http://127.0.0.1:9/v1`(닫힌 루프백 포트)이라
  어디에도 전달되지 않으며 도우미는 모델을 호출하지 않는다. 작업 폴더는 POMP 자체의 빈 폴더 `<DataDirectory>\workspace`, 환경은
  호스트 환경 + 저장된 API 키(`ProviderCredentialStore`의 고정 변수 이름만)이고 `POMP_*` 변수는 주지 않는다. omp 프로필을
  엔진과 공유하므로 도우미가 저장한 자격 증명을 엔진이 그대로 쓰며 세션·설정은 쓰지 않는다. 엔진과 같은 `IOmpProcessLauncher`·
  `OmpConnection`(ready 프레임만 기다리고 프로토콜 협상·호스트 툴·`get_state`는 건너뜀)으로 필요할 때 시작하고, 명령은 한 번에
  하나씩 보내며, 실행 중인 명령이 없는 채 2분이 지나면(로그인 중에는 해당 없음), 세션이 끝날 때, 첫 로그인 뒤 엔진을 다시
  시작하기 직전에, 저장 키가 바뀌어 환경이 낡았을 때 종료한다. 제한 시간은 시작 30초·공급자 목록 30초·로그인 15분이며 취소하면
  프로세스를 종료한다. 도우미의 stderr는 URL을 담을 수 있어 로그에 남기지 않는다.
- **RPC 로그인** — `get_login_providers`(공급자 약 80개, `{id,name,available,authenticated}`; id는 `[a-z0-9][a-z0-9.-]{0,63}`,
  이름은 200자·제어 문자 없음·최대 256개로 걸러 `providers`에 담는다. 다른 공급자에 자격 증명을 저장하는 로그인 방식 — omp 18.8의
  `storeCredentialsAs`: `openai-codex-device`→`openai-codex`, `zai-coding-plan`→`zai` — 은 목록에 남기되 `authenticated`는 항상 false로 보낸다.
  omp가 그 항목을 저장 대상 공급자의 자격 증명으로 판단해 놓고 대상 이름은 알려 주지 않아, 그대로 두면 같은 로그인이 계정으로 두 번
  나온다. 대응표는 `ProviderSetup.StoredUnder`이고, 방식으로 로그인한 뒤의 모델 확인도 저장 대상 공급자를 기준으로 한다),
  `login {providerId}`. 로그인 동안 omp가 보내는
  `extension_ui_request`는 `open_url`(`launchUrl ?? url`이 http/https 절대 주소이고 4096자 이하이며 사용자 정보·공백·따옴표가
  없을 때만, 로그인 하나에 최대 3번 기본 브라우저로 연다 — 열기는 주입 가능한 `Action<string>`이고 기본은
  `Process.Start(UseShellExecute)`; 연 주소·`instructions`는 `login`에 보인다), `notify`(`login.progress`), `input`(`login.prompt`;
  `answerPrompt`로 답하며 답은 4096자 이하, 저장·로그하지 않고 omp 오류 문구와 붙여 넣은 리디렉션 링크의 쿼리 값에서도 `***`로 지운다)이다.
  `select`/`confirm`/`editor`는 omp가 기다리지 않도록 취소로 답한다. 로그인은 백그라운드로 돌고(명령 응답은 `login.state:
  running`) 진행·결과는 별도 `setup/state`로 알린다. 성공하면 같은 도우미에서 `providers`를 다시 읽는다. 비밀 입력이
  필요한 공급자("requires secret input… terminal UI")는 omp의 문구를 지역화 접두어와 함께 `login.message`에 그대로 두고 실패로
  끝나며, 페이지는 `loginConsole`(옛 콘솔 로그인: `omp login [provider]`를 자체 콘솔 창에서, Job Object 없이 — 브라우저가 같이
  종료되지 않게 — 실행, 종료 코드 0이면 성공)을 제안한다.
- **로그아웃** — RPC에는 로그아웃이 없어 호스트가 도우미를 끝내고 `omp auth-broker logout <공급자>`를 숨긴 채 한 번 실행한다
  (`<DataDirectory>\workspace`, 저장한 키를 뺀 환경, 20초·출력 64 KiB 제한, 종료 코드 0만 성공, 출력은 보이거나 로그에 남기지 않음).
  omp는 그 공급자에 저장된 로그인을 모두 지운다(계정 구분 없음). 로그인 중에는 거부하고, 끝나면 새 도우미로 `providers`를 다시 읽어
  응답에 담으며 그 공급자의 사용량 보고를 지운다. 저장한 API 키·환경 변수는 그대로라 계속 인증돼 있을 수 있고, 엔진에는 다시
  시작한 뒤 적용된다(키 변경과 같은 `restartRecommended`).
- **API 키** — 현재 Windows 사용자용 DPAPI로 암호화해 `<DataDirectory>\credentials\provider-keys.json`(기본
  `%LOCALAPPDATA%\Pomp\credentials\provider-keys.json`)에 원자적으로 저장한다. 고정된 제공자별 환경 변수만 다음 omp 시작(엔진·
  도우미)에 전달하며 키 원문은 UI·로그에 내보내지 않는다.
- **재시작 규칙** — 로그인 성공·키 저장 뒤 엔진이 중지돼 있으면 호스트가 스스로 다시 시작한다(로그인은 도우미를 먼저 끝낸 뒤).
  엔진이 실행 중이면 로그인 성공 시 `get_available_models`를 다시 읽어 새 공급자의 모델이 보이면 그대로 쓰고(재시작 없음),
  보이지 않을 때만 `restartRecommended`를 켠다. 키는 다음 시작의 환경 변수이므로 실행 중에 저장·삭제하면 항상 재시작을 권한다.
  `Running`이 되면 권고를 끈다.
- **생각 수준** — `listThinking`은 실행 중인 엔진의 `get_available_thinking_levels`(모델마다 다름; 이름은 `[A-Za-z][A-Za-z0-9_-]{0,31}`로
  걸러 최대 16개)를 `thinkingLevels`에 담고, 엔진이 시작되거나 모델이 바뀌면 호스트가 스스로 다시 읽는다. `setThinking`은 마지막
  목록에 있는 수준(목록이 없으면 `off/minimal/low/medium/high/xhigh/max/auto`)만 `set_thinking_level`로 보낸다.
  `currentThinking`은 `session/info.thinkingLevel`이다.
  입력창의 추론 목록은 추론 모델이면 표준 단계를 모두 보여 주되 엔진이 주지 않은 단계는 흐리게(고를 수 없게) 표시한다.
- **fast 모드** — `session/fast {requestId, enabled}` → `set_fast_mode`(현재 모델 계열의 우선 처리 티어, omp `/fast`)를 채팅 명령 큐와
  별도로 보내고 `session/fastResult`로 한 번 답한다(동시에 최대 4개). 상태는 `get_state`의 `fastModeEnabled/fastModeActive`를
  `session/info.fastMode`로, 가능 여부는 모델의 공급자 계열(openai·openai-codex·google·google-vertex, `api=anthropic-messages`,
  그 계열의 openrouter 모델)로 정한다(`PompSession.HasServiceTierFamily`; 최종 판단은 omp).
- **화면 언어** — 옵션 `Language`(자동/한국어/English)를 `PompLanguage.Choice`(Contracts, 프로세스 전역)에 둔다. 한/영 문구를
  고르는 모든 호스트 코드는 `PompLanguage.Pick`/`IsKorean`(설정 문구는 `SetupText.Get`)을 쓰고, 실제 로캘이 바뀌면 세션이
  `host/hello`를 다시 보내 페이지가 엔진 재시작 없이 언어를 바꾼다. 이미 보낸 호스트 문구는 그때의 언어로 남는다.
- **사용량** — `listUsage`는 `omp usage --json`을 숨겨서(창 없음, stdout/stderr 리디렉션, `UseShellExecute=false`) 엔진과 같은
  런처로 실행한다: 작업 폴더·환경은 도우미와 같되 자리표시자(`--api-key`·닫힌 `OPENAI_BASE_URL`)는 없다. 45초·출력 2 MiB를
  넘으면 프로세스를 끝내고, 한 번에 하나만 돌며(`usageLoading`), 명령 응답은 기다리지 않는다. 출력(`{generatedAt, reports,
  accountsWithoutUsage, disabledCredentials, capacity}`)에서 `reports`만 방어적으로 읽는다: 보고서 64개·한도 32개·문자열 200자
  상한(총 256 KiB), 제어·bidi 문자 제거, 시각(`window.resetsAt`: 에포크 밀리초·초, 숫자 문자열, ISO)은 ISO 8601 UTC로,
  `usedFraction`은 0..1로(없으면 `remainingFraction`·`used/limit`에서 계산), 상태는 `ok/warning/exhausted/unknown`(그 밖은
  `unknown`)으로 정규화하고 깨진 항목·한도가 없는 보고서는 버린다. 계정 라벨은 이메일(`metadata.email`·`email`)을 먼저, 그다음
  `account`·`metadata.account/username/accountId`·`accountId`, 마지막으로 한도의 `scope.accountId`를 쓴다. 실패는 `usage.error`(고정
  문구)로 알리고 이전 숫자는 남기며, stderr는 URL 쿼리를 지워 debug 로그에만 남긴다.

## 5. 호스트 툴 (CustomToolRegistry)

| 툴 | 권한 | 어댑터 | 구현 상태 | omp에 제공 |
|---|---|---|---|---|
| `get_editor_context` | 없음 | Editor, TextBuffers | 구현 | ✔ |
| `read_buffer` | 없음 | TextBuffers (저장 안 된 버퍼 우선) | 구현 — `[path#TAG]` + `N:TEXT` 출력 | ✔ |
| `edit_buffer_hashline` | fileWrite (정확한 Diff 미리보기) | TextBuffers | 구현 — 준비한 변경을 승인 후 그대로 적용, 파일별 Undo 1단계, 저장하지 않음, 다중 파일은 부분 적용 가능(보고함) | ✔ |
| `query_solution` | 없음 | Solution | 구현 | ✔ |
| `add_project_file` | projectChange (새 파일 전체 Diff) | Solution | 구현 — C#/VB MSBuild 프로젝트, 새 파일만(UTF-8, BOM 없음), 앞뒤 공백·MAX_PATH(파일 259자, 폴더 247자) 초과 경로는 준비 단계에서 거부, 쓰기 전 실제 경로 확인, `IVsProject.AddItem` 등록, 실패 시 남은 파일을 보고 | ✔ |
| `remove_project_file` | projectChange | Solution | 영구 비활성 — 파일 삭제는 omp 자체의 승인 게이트가 있는 `delete` 툴을 따름 | ✘ |
| `rename_project_file` | projectChange | Solution | 준비된 프로젝트 항목 변경, 기존 파일·버퍼 보호 | ✔ |
| `get_msbuild_properties` | 없음 | Solution | 평가된 프로젝트 속성 조회 | ✔ |
| `read_output_window` | 없음 | Output | 출력 창 조회 | ✔ |
| `manage_git` | 조회 없음 / commit·push vcsWrite | Git | Git CLI 래핑, 변경은 승인 후 수행 | ✔ |
| `manage_tests` | list·run 모두 testRun | Tests | 탐색도 빌드·어댑터 실행 가능하므로 승인 필요 | ✔ |
| `manage_breakpoints` | breakpointChange | Debugger | 추가/제거/활성/비활성, WhenTrue 조건·추적점 | ✔ |
| `control_debugger` | debuggerControl | Debugger | 디버거 실행·단계·중지 제어 | ✔ |
| `add_bookmark` | bookmarkChange | Bookmarks | 열린 문서 책갈피 추가 | ✔ |
| `read_error_list` | 없음 | ErrorList | 구현 | ✔ |
| `build_solution` | processExec — MSBuild target은 임의 스크립트·프로그램 실행 가능 | Build | 구현 — 구성 `Name` 또는 `Name\|Platform`(플랫폼 생략 시 활성 플랫폼 유지, 빌드 후 원래 구성 복원), 오류 목록은 100개까지·합계는 전체 | ✔ |
| `manage_nuget` | 조회 없음 / 설치·업데이트 packageChange | Solution | VS NuGet MEF 서비스, 승인 전 프로젝트·패키지 상태 고정, 변경 직전 재검사, 부분 실패 보고 | ✔ |
| `list_breakpoints` | 없음 | Debugger | 구현 | ✔ |
| `get_debug_state` | 없음 | Debugger | 구현 | ✔ |
| `list_bookmarks` | 없음 | Bookmarks | 열린 문서 기준 구현 | ✔ |

- 제공하지 않는 툴(`HostToolCatalog.NotImplemented`: 현재 `remove_project_file`)은 **omp에 등록하지 않는다**.
  목록에서 빼는 것만으로 `set_host_tools`·오버레이·`POMP_HOST_TOOLS`·omp 측 안내문에 함께 나타난다. 다른 툴 설명도 제공되지
  않은 툴 이름을 언급하지 않는다.
- 권한 대화상자의 Diff는 `edit_buffer_hashline`(수정)과 `add_project_file`(새 파일 전체)이 만든다. 긴 Diff는 처음 1,500줄만
  그리고 "모두 보기"를 누르면 전체를 보여 주며, 전체가 표시되기 전에는 허용 계열 버튼이 비활성이다(거부는 항상 가능).
  Diff·대상·메시지 안의 bidi/제로폭 제어 문자는 동작하지 않도록 `⟨U+202E⟩` 같은 표시로 바꿔 보여 준다.
- 어댑터의 예기치 않은 예외(주로 VS의 COMException)는 HRESULT와 함께 경고로 기록되어 ActivityLog에도 남는다(같은 작업은
  30초에 한 번). 인수 오류·미구현·취소 같은 예상된 결과는 기록하지 않는다.

`read_buffer`/`edit_buffer_hashline`의 TAG는 omp hashline과 같은 모양(대문자 16진수 4자리)이지만 **호스트 전용**
(정규화된 전체 내용의 SHA-256에서 파생, 호스트 스냅샷 저장소로 검증). omp 내장 `edit`와 섞어 쓰지 않는다.
해시는 CRLF와 단독 CR을 LF로 바꾼 UTF-8 텍스트로 계산하고(`BufferSnapshot.ContentHash`, 툴과 VS 어댑터가 같은 규칙),
줄은 VS 편집기와 같이 CRLF·CR·LF 및 U+0085/U+2028/U+2029에서 나뉜다. Unicode 구분자는 내용 해시에 그대로
남겨 stale 검출을 유지한다. Unicode 구분자가 있는 파일의 승인 Diff는 구분자를 ⟦U+XXXX⟧로 표시하고 문자 자체의
역슬래시/표시 괄호는 이스케이프해 구분자만 달라지는 변경도 숨기지 않는다. 패치 문법의 줄 구분자는 LF로 정규화한다.
디스크에서 읽을 때는 BOM(UTF-8/16/32) → 엄격한 UTF-8 → 시스템 ANSI 코드 페이지(예: CP949) 순으로 해석하고, 16 MiB를 넘는
파일은 읽지 않는다.

### 준비된 변경과 승인 (계약)

- 변경 툴은 `IPreparedHostTool.PrepareAsync`에서 경로·인수를 검증하고 정확한 변경 내용과 Diff를 가진 호출별
  `IPreparedHostToolCall`을 만든다. 준비 단계는 읽기만 수행하며 파일/버퍼를 변경하지 않는다.
- 디스패처는 기억된 허용 규칙이 있어도 준비를 먼저 수행한다. 준비 실패·누락된 권한 요청은 일반 승인으로 대체하지 않고
  실행을 거부한다. 승인 요청의 source/tool/kind는 디스패처가 원래 정책으로 고정한다.
- 승인 뒤에는 **같은 준비 객체**를 실행한다. 원본 JSON 재해석이나 전역 준비 캐시는 사용하지 않는다. 거부·취소·실패·성공
  모두 호출별 객체를 Dispose한다. 실행은 승인된 불변 값만 사용하며, 권한 UI가 갖는 DTO의 변경을 실행 입력으로 사용하지 않는다.
- hashline 편집은 모든 파일의 전체 해시를 먼저 재검증하고, 실제 버퍼 적용 때도 UI 스레드에서 해시 검사와 편집을 함께 한다.
  파일별 Undo만 보장하고 저장하지 않는다. 다중 파일 전체의 원자성은 보장하지 않으며 부분 적용 시 적용된 파일을 보고한다.
- 어댑터에는 원본 해시와 함께 승인한 결과의 해시(`BufferEditRequest.ExpectedResultContentHash`)를 보낸다. VS 어댑터는 자기
  편집 계획이 만들 결과를 버퍼를 바꾸기 전에 계산해 다르면 아무것도 바꾸지 않는다 — 승인한 Diff와 실제 적용이 어긋나지 않는다.
- 편집 기준은 모델이 **완전히 본** 줄뿐이다. `read_buffer`/`get_editor_context`가 출력 한도에서 잘라 보여 준 줄은 본 줄로
  기록하지 않는다. 편집 결과에는 새 TAG를 주지만 새 줄은 본 것으로 치지 않으므로, 이어서 줄 기준 편집을 하려면 다시 읽어야 한다.
  같은 경로에 남아 있는 서로 다른 버전이 같은 4자리 TAG를 가지면 모호한 것으로 보고 거부한다.
- 파일 추가는 승인 전 고정한 프로젝트 identity/루트/대상/내용을 실행 때 재검증하고, 새 파일만 생성해 VS 프로젝트 시스템에
  등록한다. 기존 파일 덮어쓰기·루트 이탈·reparse point를 거부한다. 작업 루트부터 대상 폴더까지 기존 폴더를 모두 읽기 공유로만
  열어 두어(이름 변경·삭제·쓰기 열기 불가) 새 폴더를 만들고, 새 파일은 만든 직후 **내용을 쓰기 전에** 핸들의 실제 경로가 프로젝트
  폴더 아래인지 확인한다(정션·링크로 바뀌었으면 쓰지 않고 오류). 등록 중에는 같은 파일(파일 ID)을 읽기 전용으로 붙잡고 승인한
  바이트와 같은지 확인하며, 등록 확인은 마지막 대기 뒤에도 다시 한다. 등록 실패 후 다른 주체가 바꾼 파일은 삭제하지 않는다.
  경로를 동시에 바꾸는 로컬 프로세스가 있으면 빈 파일이 작업 폴더 밖에 생길 수는 있다(내용은 쓰지 않음, 오류로 보고).
- omp에 보내는 응답(툴 결과·진행, 대화상자·승인 답)은 **요청을 보낸 omp 프로세스에만** 보낸다. 그 프로세스가 끝났으면 버리며,
  재시작한 omp가 같은 id를 다시 써도 섞이지 않는다. 끝났거나 종료 중인 프로세스의 버퍼에 남은 프레임(툴 호출·질문·스트리밍
  이벤트)은 전달하지 않는다. 정상 종료 때 남은 출력을 읽는 동안의 마지막 프레임은 그대로 전달된다.
- 브릿지 수명: 재연결 중(이전에 Running이었던 주기)의 `StartAsync`는 새 핸드셰이크가 끝날 때까지 기다린다(실패하면 예외, 중지되면
  취소). 호출자가 기다리기를 포기한 중지 뒤의 시작은 이전 주기가 끝난 뒤(ConnectionEnded 이후)에 새 omp를 띄운다.
  `get_state` 응답은 요청 뒤에 agent_start/end를 받았으면 스트리밍 여부를 바꾸지 않는다.

## 6. 권한 결정

**삭제·격리 경계(결정):** omp는 OS 샌드박스가 없으며 안전 경계는 read/write/exec 승인 계층, 기본
`--approval-mode always-ask`, 도구별 `tools.approval` allow/prompt/deny와 bash 패턴 규칙(치명적 파괴 명령 기본 차단)이다.
POMP의 `remove_project_file`은 `HostToolCatalog.NotImplemented`에 영구적으로 남기고 파일 삭제·이동은 omp 자체의
승인 게이트가 있는 `delete`·`move` 툴을 따른다. 실험적 AppContainer 실행기는 기본 꺼짐을 유지하고 샌드박스로 안내하지 않는다.

`허용`(이번만) · `세션 동안 허용`(메모리) · `항상 허용`(설정에 저장, 도구 › 권한이나 옵션 페이지에서 해제) · `거부`(+메모).
키: `host:<툴>` / `omp:<툴>`. **해당 툴 전체에 적용**되며 파일별 허용이 아니다. `항상 허용`은 **작업 영역별**로 저장한다
(`<키>@<작업 루트>`, 루트의 `%`·`;`·`,`·공백·제어 문자는 `%XX`로 이스케이프). 다른 작업 영역에서는 다시 묻고, 작업 영역이
붙지 않은 예전 키는 더 이상 답하지 않는다(목록에 "이전 형식"으로 보여 철회할 수 있다). 작업 루트를 모르면 `항상 허용`도 세션 동안만 적용한다.

- **매번 확인하는 요청**(`PermissionGate.IsOnceOnly`, 기억된 규칙으로 답하지 않고 기억형 답도 저장하지 않음): 실행
  (`ProcessExec`: omp의 bash·eval·python 등, `build_solution`), Git 쓰기(`VcsWrite`), 패키지 변경, 디버거 제어(omp의 `debug` 포함 —
  프로그램을 시작하고, 디버그 어댑터를 작업 영역의 도구 폴더에서 찾을 수 있다), 테스트 실행,
  omp의 `task`(하위 에이전트는 승인 없이 돌므로 task 승인이 그 안의 작업 전체를 승인한다), `computer`·`browser` 제어, 그리고
  프로젝트 정책(`.pomp/permissions.json`)이나 omp가 작업 영역에서 읽는 엔진 구성(`.omp/`, `mcp.json`, `.claude/settings` 등)을
  바꾸는 요청. 기억된 편집 허용과 실행·설정 파일 변경이 이어져 확인 없는 실행이 되는 경로를 막는다.
- **작업 영역 전환**: 솔루션·폴더를 열거나 닫으면 즉시 세션 허용을 지우고, omp가 새 루트에서 다시 시작하거나 루트가 그대로임을
  확인할 때까지 호스트 툴 호출과 omp 승인을 거부한다(이전 작업 영역의 omp가 호출한 호스트 툴이 VS의 새 솔루션에 적용되지 않게).
타임아웃·취소·잘못된 승인 응답은 거부한다. 응답에는 정확한 문자열 결정 값이 필요하며 누락/숫자 enum을 허용하지 않는다.
응답하지 않는 프롬프트 구현이 있어도 게이트는 취소·시간 초과 시점에 기다림을 멈추고 거부한다.
호스트 툴의 게이트는 omp의 `write`/`yolo` 모드와 별개다. 이미 부여한 세션/영구 허용 규칙은 재질문을 생략한다.

- **세션 허용의 범위**: omp 프로세스 하나(=omp 세션 하나)다. 새 omp 프로세스가 연결될 때(재시작·충돌 후 재연결)와
  새 대화(`session/new`)에서 모두 지운다. `session/new`는 omp가 기다리는 승인 프롬프트를 먼저 거부로 풀어 준다.
  초기화 전에 열린 대화상자의 "세션 동안 허용" 답은 그 호출에만 적용되고 새 세션의 허용으로 남지 않는다.
- **강제 프롬프트**: omp는 도구 자체 정책이 요구하는 확인(예: 위험한 명령)에 `Reason:` 줄을, 모델 제공자가 보낸
  computer-use 안전 점검에는 `Provider safety checks:` 절을 붙이며, 이 둘은 yolo나 허용 규칙과 무관하게 묻는다. 이런 프롬프트는
  기억된 규칙으로 답하지 않고, 사용자의 "세션 동안/항상 허용" 답도 규칙으로 저장하지 않는다(`canRemember=false`,
  UI는 "이번만 허용"과 "거부"만 표시, 호스트도 기억형 답을 "이번만"으로 낮춘다).
- **거부 메모**: 호스트 툴 거부 시에만 모델에 전달된다. omp 승인 프롬프트에는 메모를 전할 통로가 없어 로그에만 남는다
  (UI는 omp 승인에서 메모 입력란을 숨긴다).
- **호스트가 스스로 한 거부**(시간 초과·취소·UI 전달 실패·승인 대기 한도 초과·잘못된 응답)는 사용자의 답이 아니다.
  `PermissionResponse.SystemReason`으로 구분하고, 모델에는 "사용자가 거부함"이나 사용자 메모가 아니라 호스트 거부와 그 사유로 전달한다.
- 호스트 툴 호출의 정책 키는 omp가 호출한 툴 이름으로 고정한다(툴이 스스로 다른 이름을 적어도 무시).

모델이 지정하는 `read_buffer` 등의 경로는 작업 폴더 내부로 제한하고, 루트 아래의 기존 reparse point(junction/심볼릭 링크 등)를 거부한다.
사용자가 선택한 작업 루트 자체와 IDE가 제공하는 현재 문서 컨텍스트는 신뢰한다. 검사와 사용 사이의 경로 교체나
omp 내장 셸 도구까지 통제하는 파일시스템 샌드박스는 아니다.
pre-edit 훅도 상태 파일 누락/읽기 실패 시 fail-open이며, bash·MCP 등을 통한 모든 변경을 막지는 않는다.
프롬프트 앞에 붙이는 `<vs_context>` 블록 안의 파일 내용·선택 영역·오류 설명은 데이터로 취급한다: 그 안의 `<vs_context`/
`</vs_context` 표기는 `&lt;`로 바꿔 블록을 조기에 닫고 지시문처럼 보이게 만들 수 없게 한다.

## 7. 알려진 쟁점 / 후속 결정

- 기본 단축키는 텍스트 편집기 범위의 `Pomp.InlineEdit` **Alt+M**, 코드 동작 **Shift+Alt+E/R/F/U**, 파일 추가
  **Shift+Alt+I**, `Pomp.KnowledgeTag`(지식에 추가) **Alt+/**, 전역 `Pomp.ToggleFocus` **Alt+J**, POMP 열기 **Shift+Alt+Q**, 새 대화 **Shift+Alt+N**이다. 모두 한 번에 누르는 조합이고
  두 단계 코드(chord)는 쓰지 않는다. VSCT에는 바인딩을 두지 않고 도구 > 옵션 > Pomp > 일반 > 단축키 값을 시작·적용 때 DTE로 VS 키 바인딩에
  넣는다(`VsKeyBindings`). 실제 VS 2019 바인딩 표에서 이 조합들은 전역·텍스트 편집기 범위 모두 미사용이다. Ctrl+K는 주석·서식 등의 코드 시작 키,
  Ctrl+L은 줄 잘라내기, Ctrl+Alt+K는 호출 계층 보기(`View.CallHierarchy`)이고 Ctrl+Alt+L/J/Q도 이미 쓰인다.
  VS가 같은 범위에서 이미 쓰이는 확장 바인딩을 조용히 버리므로 이 조합을 피한다.
  WebView2 안의 키는 VS 명령 라우팅에 가지 않는다: 페이지가 `host/hello.shortcuts.toggleFocus`의 조합(기본 **Alt+J**; Meta·AltGr 제외,
  IME 조합·키 반복 중 무시)을 직접 잡아 호스트 내부 메시지 `ui/toggleFocus`로 알리고, 호스트가 편집기로 포커스를 돌려준다. 채팅 페이지 자체의 단축키(전송 키 Enter/Ctrl+Enter, 보낼 대상
  고르기 Ctrl+Alt+T, 권한 모드 고르기, 승인 카드 허용·거부 Alt+A/Alt+D, 도구 › 권한 Ctrl+Alt+P)는 도구 > 옵션 > Pomp > 일반 > 단축키에서
  바꾸며 `host/hello.shortcuts`로 페이지에 전달된다(docs/settings.md "단축키").
- 편집기 우클릭에서는 **POMP에게 질문 → POMP** 순서로 둔다. 이 메뉴가 여는 질문 창은 채팅 질문(Enter)과 수정 제안(Ctrl+Enter)을 함께 하며,
  고칠 수 없는 파일에서는 수정 제안이 꺼진다. POMP 하위 메뉴는 코드 동작(설명·리뷰·고치기·테스트 작성) → 컨텍스트(파일 추가) → 에이전트·팀에게 →
  채팅(열기·새 대화·포커스 전환) → 켜기/끄기(자동완성·코드 캡션)이며, 질문을 안에 중복하지 않는다.
  도구 > POMP는 채팅 → POMP에게 질문 → 켜기/끄기 순서다.
  보기 > 다른 창 > POMP 항목은 그대로 둔다.
- 책갈피: 공개 API가 없어 열린 문서의 `MARKER_BOOKMARK` 마커 열거 방식(열린 문서 한정)으로 시작.
- 실행 검증은 이 개발 PC의 VS 2019 16.11 실험 인스턴스(`/rootSuffix Exp`)에서 한다. VS 2026 패키지는 VS 2026 실험 인스턴스에 설치해
  연결·메뉴를 확인하고, VS 2022 패키지는 빌드·패키지 내용만 확인한다(VS 2022 미설치).
- omp는 모든 패키지에 고정 버전으로 들어간다(§4 "엔진 버전"). 엔진 실행 파일이 약 229 MiB라 Release VSIX가 약 117 MB가 된다 —
  Marketplace 업로드 크기 한도는 게시 전에 확인한다.
- VS 2019 16.0~16.9는 현재 매니페스트 대상이 아니다. 하위 16.x 지원에는 SDK/API 재검증이 필요하다.
- POMP의 프로젝트 파일 삭제 툴은 영구 비활성이고 파일 삭제는 omp의 승인 게이트가 있는 `delete` 툴을 따른다.
  NuGet을 포함한 나머지 구현된 툴은 기본 등록에 통합했다.
  컴파일 성공은 IDE에서의 패키지 로드나 실제 모델 제공자 응답을 검증한 것과 다르다. 초기 Debug 패키지 하나는 VS 2026
  실험 인스턴스에서 로드된 기록(ActivityLog)이 있지만, 현재 빌드는 VS 안에서 실행해 보지 않았다. 버퍼 편집(Undo 1단계,
  소스 제어의 편집 거부)과 파일 추가(레거시/CPS 프로젝트 등록)는 가짜 어댑터·격리 하네스로만 검증했다.
- 편집 요청에는 승인할 때의 작업 루트를 담고, VS 어댑터는 문서를 열기 전과 UI 스레드의 편집 직전에 그것이 여전히 현재 작업
  루트(열린 솔루션 루트, 없으면 대체 작업 폴더)인지 확인한다. 편집기 캐럿·선택 위치는 편집기 뷰와 DTE 경로 모두 CR/LF 기준 줄로 준다.
- 일반 대화상자 초안은 승인 요청이 먼저 표시되어도 유지한다. 입력란·거부 메모는 한도 초과 시 원문을 남기고
  오류를 표시한다. omp 승인/대화상자 응답은 인코딩된 NDJSON 크기도 검사한다. 한국어 IME 조합 중 Enter는
  대화상자를 제출하지 않는다(`isComposing` 및 legacy keyCode 229).
- `remove_project_file`은 영구 등록 제외한다. 남아 있는 recycle 구현의 Windows 안전성 검증 여부와 무관하게
  활성화하지 않으며, 파일 삭제는 omp 자체의 승인 게이트가 있는 `delete` 툴을 사용한다.

### 설계 대비 미구현·편차 (후속 작업)

| 설계 항목 | 현재 | 후속 |
|---|---|---|
| 입력창 멘션(@파일)·파일 첨부 | 구현, 요청 ID로 늦은 결과 무시·경로 중복 제거·개수 제한 없음 | 실제 WebView2 검증 |
| Git·테스트·출력 창·MSBuild 속성 툴 | 구현·기본 등록, 테스트 탐색에도 승인 요구 | 실제 VS 검증 |
| 권한 대화상자 Diff 미리보기 | `edit_buffer_hashline`(수정)·`add_project_file`(새 파일 전체)이 생성, inline/나란히 보기 구현, 두 모드 모두 전체 Diff 확인 후 승인 | 실제 WebView2 검증 |
| 컨텍스트 압축 "최근 3턴 원문 유지" | omp 자체 압축(토큰 기준)에 맡기고 호스트는 알림만 표시 | T10에서 재검토 |
| 1만 줄 이상 파일의 심볼 요약 | `read_buffer outline=true`, 큰 파일 에디터 컨텍스트 자동 요약 구현 | 성능 측정 재검증 |
| 토큰 미터 | 메시지가 끝날 때마다 `get_state`로 갱신(스트리밍 중 실시간 아님) | — |
| 네트워크 접근 설정·로그 | Block omp network tools 옵션·호출 로그 구현, 기본 허용 | 셸/MCP까지 통제하는 샌드박스 아님 |
| 주기적 하트비트 | 30초 무출력 후 get_state, 3회 무응답 후 재시작 | 실제 프로세스 검증 |
| 사용 지표(metrics) | 로컬 usage-metrics.json 구현, 외부 전송 없음 | 실제 VS 검증 |
| 라이선스, Marketplace 메타데이터 | POMP는 PolyForm Noncommercial 1.0.0, 번들 omp는 MIT, Publisher `deanpinb`·무료 게시, 공개 저장소 URL 생략 | 실제 설치·Marketplace 업로드 확인 |
| 대화 복구 | ui/ready마다 호스트의 현재 세션 로그 재생 | 프로세스 종료 후 영구 보관 아님 |

### NuGet 변경 수명주기 (T12)

`manage_nuget list`는 승인 없이 설치된 패키지를 조회한다. 변경은 `IPreparedHostTool`와
`IPreparedPackageChange`를 사용한다. 준비 중 다운로드나 프로젝트 변경은 없으며, 대상 프로젝트의 경로·hierarchy identity·
solution generation과 설치 패키지 지문을 캡처한다. 승인 후 작업 루트·프로젝트 identity·패키지 지문을 재확인한다.
작업 입력은 승인 DTO와 분리되고 준비된 호출은 1회만 실행되며, 거부·취소·완료 시 Dispose된다.

호환성 때문에 VS 16.10/17.0 기준 `NuGet.VisualStudio` MEF 인터페이스를 사용한다. DTE 객체 조회와 MEF 서비스 취득은
UI 스레드에서 하고, NuGet이 자체 마샬링하는 동기 API는 백그라운드에서 호출한다. `GetInstalledPackages`의
obsolete 경고는 해당 파일에서 호환성 이유로 한정 해제했으며, VSTHRD 분석기는 유지한다.
패키지 버전은 NuGet의 `IVsSemanticVersionComparer`로 검증·결과 비교한다. 최신 안정 버전 설치는
`IVsPackageInstaller2.InstallLatestPackage`를 사용한다. uninstall은 의존 패키지를 제거하지 않는다.

변경은 호스트 내에서 직렬화한다. NuGet API에는 취소 토큰이 없어 진입 전까지만 취소하고, 시작 후에는 실제 반환을
기다린다. 작업 실패는 부분 변경 가능성을 명시한다. 외부 사용자/확장이 동시에 변경하는 경우 NuGet의 프로젝트 시스템
조정에 의존한다. .csproj 직접 편집, 패키지 소스 설정 변경, 강제 롤백을 하지 않는다. 실제 VS 동작은 별도 검증 대상이다.
공식 API 근거: https://learn.microsoft.com/en-us/nuget/visual-studio-extensibility/nuget-api-in-visual-studio

선택적 native CodeLens는 별도 공급자 assembly를 CodeLens 서비스에서 로드하고, VS 내 callback으로 적용된 옵션만 읽습니다. `Pomp.CodeLens.Contracts`의 위치 DTO를 native details view로 전달한 뒤, 사용자가 요약 버튼을 눌러야 기존 inline assistant 경로를 호출합니다. 자동 표시/prefetch는 모델을 호출하지 않습니다. 양쪽 VSIX가 provider와 shared contract를 참조하며 CodeLensComponent 자산을 등록합니다.
