# POMP 아키텍처 보완 문서 (설계 문서 v0.9 → 구현 기준)

이 문서는 원본 설계를 구현 기준으로 구체화한다.
기준은 **실제 omp(oh-my-pi) RPC 프로토콜**과 **추가 요구사항(VS 2019 이상 지원)** 이다.

- 구성: 4계층(VS 호스트 · POMP Core · omp 엔진 · 채팅 화면), 컴포넌트, 데이터 흐름.
- 원본과 충돌하면 이 문서가 우선한다.
- 새 기능은 이 문서를 먼저 갱신한 뒤 구현한다.

## 1. 대상 IDE — VS 2017 15.9 이상 (원본의 "VS 2022 17.8+"를 대체)

| 대상 | 프로세스 | VSIX 프로젝트 | VS SDK | VSSDK BuildTools | InstallationTarget |
|---|---|---|---|---|---|
| VS 2017 15.9 | x86 | `src/Pomp.Vsix.VS2017` | `Microsoft.VisualStudio.SDK` 15.9.3 (Roslyn 2.10.0, NuGet 4.9.6) | 16.11.71 | `[15.9,16.0)` |
| VS 2019 16.10+ | x86 | `src/Pomp.Vsix.VS2019` | `Microsoft.VisualStudio.SDK` 16.10.31321.278 | 16.11.71 | `[16.10,17.0)` |
| VS 2022 (17.x) · VS 2026 (18.x) | amd64 | `src/Pomp.Vsix.VS2022` | `Microsoft.VisualStudio.SDK` 17.0.32112.339 | 17.14.2142 | `[17.0,)` amd64 |

### 공유 소스와 컴파일

- VS 관련 코드는 `src/Pomp.VisualStudio/`에 둔다. 프로젝트 파일 없는 공유 소스 폴더이고, 세 VSIX 프로젝트가 링크 컴파일한다.
- 조건부 컴파일 기호는 `VS2017` / `VS2019` / `VS2022`이다.
- 세 VSIX는 같은 소스를 각자의 최소 SDK로 컴파일한다. 상위 버전 API를 쓰면 하위 VS에서 로드가 실패한다.
- C# 언어 버전은 `Directory.Build.props`에서 `12.0`으로 고정한다. 설치된 SDK를 따라 조용히 올라가지 않게 하기 위해서다.
- 터미널 API(17.5+)는 v1 범위 밖이다. 원본의 위험 대응과 같고, Output/ErrorList 중심으로 간다.

### `#if VS2017` 분기

| 분기 | 이유와 처리 |
|---|---|
| 메인 스레드 전환 | `Microsoft.VisualStudio.Threading` 15.8에 `SwitchToMainThreadAsync(alwaysYield:)`가 없다. 대신 `Task.Yield()` 뒤 전환한다(세 곳). |
| 기본 CodeLens 캡션 제외 | VS 2017의 CodeLens API(15.8)에는 사용자 지정 상세 표시가 없다(`CodeLensDetailsDescriptor.CustomData`, `IViewElementFactory` 변환). VS2017 VSIX는 CodeLens 공급자를 싣지 않는다(`Editor/CodeLensCaptionFeature.cs` 제외). 옵션 **캡션에 기본 CodeLens 사용**도 숨긴다. POMP 자체 코드 캡션은 그대로다. |

VS 2017에서의 실행 검증은 아직 하지 않았다. 이 개발 환경에 VS 2017이 없다.

### 패키지 Identity

- 세 VSIX는 서로 다른 Identity를 쓴다: `Pomp.VSIX.VS2017`, `Pomp.VSIX.VS2019`, `Pomp.VSIX.VS2022`.
- 표시 이름도 다르다(`POMP (VS2019)` 등). Marketplace 목록도 분리된다.
- 64비트 패키지 하나(`Pomp.VSIX.VS2022`, 표시 이름 `POMP (VS2022)`)가 VS 2022와 VS 2026을 함께 커버한다.
- VS 2026은 17.x API로 동작하고 설치 범위의 상한을 무시한다. 그래서 `[17.0,)` 하나로 양쪽에 설치된다(Microsoft 호환성 모델).
- 사용자는 자기 VS 버전의 패키지를 설치한다.

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

### 의존과 조립

- 의존 방향: `Pomp.VisualStudio → Pomp.Core → Pomp.Contracts`, `Pomp.VisualStudio → Pomp.Tools → Pomp.Contracts`.
- 호스트 composition은 `HostToolCatalog.CreateDefault`로 **제공 가능한 19개 툴만** 레지스트리에 등록하고 실제 `PompSession`을 만든다.
- `remove_project_file`은 영구 제외다. 파일 삭제는 omp의 승인 게이트가 있는 `delete` 툴을 따른다(§5, §6).
- JSON은 `Newtonsoft.Json`을 쓴다. VS가 제공하는 어셈블리를 쓰고, VS 2019용 VSIX만 폴백 사본을 포함한다.

### 재시작 책임

- 솔루션 변경에 따른 재시작은 Core가 담당한다.
- 옵션 변경에 따른 재시작은 VS 호스트가 담당한다.
- 옵션 적용은 **실행에 영향을 주는 값**이 바뀐 경우에만 omp를 재시작한다. 750 ms 디바운스가 걸린다.
- 실행에 영향을 주는 값: omp 경로, 승인 모드, 모델, thinking, 추가 인수, rpc-ui, 확장 로드 두 가지.

### 런타임 배치

`%LOCALAPPDATA%\Pomp\` 아래:

| 경로 | 내용 |
|---|---|
| `omp\pomp-overlay.yml` | omp `--config` 오버레이 (제공 툴의 `tools.approval.<name>: allow`) |
| `state\<VS 프로세스 id>\vs-state.json` | VS 상태(열린 문서·dirty). omp 측 확장이 읽는다. 종료된 VS의 폴더는 다음 시작 때 정리한다. |
| `workspace\` | 솔루션/폴더가 열려 있지 않을 때 omp의 작업 폴더. 사용자 프로필 전체를 작업 루트로 쓰지 않기 위함이다. |
| `logs\pomp-vs.log` (+ `pomp-vs.1.log`) | 호스트 로그. 출력 창 "Pomp" 창과 같고, 경고 이상은 ActivityLog에도 남는다. |
| `WebView2\VS<주 버전>\` | WebView2 사용자 데이터 폴더. VS 주 버전별로 분리한다. |

`<작업 영역>\.pomp\` 아래:

| 경로 | 내용 |
|---|---|
| `agents\`, `teams\`, `rules\`, `workflows\`, `skills\`, `hooks\`, `permissions.json` | 이 프로젝트 범위로 만든 에이전트·팀·규칙·워크플로·스킬·훅·권한 정책. 저장소로 팀과 공유할 수 있다. |
| `local\` | 이 프로젝트에 대한 POMP 자체 데이터. 자체 `.gitignore`(`*`)로 커밋에서 빠진다. |

`local\`에 들어가는 것:

- 대화(`sessions\`, omp `--session-dir`), 최근 대화, 지식 선택, 체크포인트, 리뷰
- 백그라운드 에이전트, 에이전트 실행 기록, 자동화 규칙, 케이스 의견
- 격리 작업 트리(`wt\`), 엔진용 에이전트 사본(`engine-plugin\`)
- 이 데이터가 속한 작업 영역(`workspace.json`)

### `.pomp` 초기화와 이전

- 프로젝트의 `.pomp` 폴더를 지우면 그 프로젝트의 POMP 내용이 초기화된다.
- 이전 버전은 프로젝트 데이터를 `%LOCALAPPDATA%\Pomp\<기능>\<해시>`, `~\.omp\agent\sessions\<작업 폴더>`, `<작업 영역>\.omp\agents`에 두었다.
- 이 데이터는 처음 쓸 때 `.pomp`로 **옮긴다**. 복사하지 않으므로 지운 뒤 되살아나지 않는다.
- 작업 영역 신뢰(`workspace-trust.json`)와 항상 허용한 도구 승인은 프로젝트 밖에 남는다. 저장소가 이것을 미리 담아 배포할 수 없게 하기 위해서다.

### 다른 경로에서 온 `.pomp` (`WorkspaceOrigin`)

엔진을 시작할 때 기록된 작업 영역과 지금 작업 영역을 비교한다.
기록은 `local\workspace.json`이고, 없으면 최근 대화 목록 등에 기록된 작업 영역을 쓴다.
다르면 프로젝트 폴더를 옮겼거나, 이름을 바꿨거나, 복사한 것이다. 어느 경우든 대화와 기록을 그대로 가져오고 채팅에 한 줄 알림이 나온다.

- 이전 경로로 기록된 값을 새 경로로 고친다. 최근 대화, 리뷰·요약 캐시, 실행 기록, 체크포인트 같은 POMP 저장소가 대상이다.
- omp 대화 파일(`sessions\` 아래 `*.jsonl`, 하위 에이전트 대화 포함)은 머리 줄의 `cwd`와 경로만 고친다. 나머지 내용은 바이트 그대로 둔다.
  omp는 기록된 `cwd` 폴더가 있으면 그 폴더에서 대화를 이어 가므로, 고치지 않으면 복사본의 대화가 원래 프로젝트에서 돈다.
- 이전 경로는 `workspace.json`의 `previousRoots`에 남는다. 대화 삭제 때의 작업 영역 확인에 쓰인다.
- 원래 저장소를 가리키는 격리 작업 트리(`wt\`)는 휴지통으로 옮긴다. 옮기지 못하면 그 폴더에 모아 두고 채팅에 알린다.

### 패키지 로드와 도구 창

- 패키지는 백그라운드 로드를 허용하지만 자동 로드(`ProvideAutoLoad`)는 없다. 도구 창·명령·옵션을 처음 쓸 때 로드된다.
- 도구 창은 WebView2 초기화와 채팅 페이지 탐색이 성공한 **뒤에** omp를 시작한다.
- WebView2 런타임이 없거나 `111.0.1661.41`보다 오래되면 안내 패널을 띄우고 omp를 시작하지 않는다. 패널에는 다운로드 링크·재시도·로그 보기가 있다.
- 도구 창을 닫아도 omp 세션은 유지된다(창 숨김).
- `ui/ready`마다 호스트가 대화 로그를 재생한다. 최대 3 MiB/3000개이고, 오래된 턴부터 제거한다.
- 새 세션을 완료하면 재생 로그를 비운다.

### 슬래시 명령과 세션 목록

- 슬래시 명령 목록은 엔진 연결별로 정규화해 `commands/state`로 전달한다. 페이지 준비 때 다시 보낸다.
- 세션 목록은 두 출처를 합쳐 캐시한다: POMP가 기억한 참조, 현재 세션 폴더의 JSONL 파일 전부.
- JSONL은 개수 제한이 없고 파일당 앞 64 KiB만 읽는다. 파일 읽기는 채팅 명령 큐 밖에서 처리한다.
- 페이지에는 경로 대신 불투명 핸들만 준다. 전환·미리보기 때 직접 경로와 재분석 지점 여부를 다시 확인한다.
- 미리보기는 엔진 없이도 된다. 최대 1 MiB·20개 텍스트 메시지만 읽는다.
- 연결이 끝나도 같은 작업 공간의 폴더는 유지한다. 작업 공간이 바뀌면 버린다.
- 이름 변경·압축 후에는 세션 정보를 다시 조회한다.

## 3. 원본 RPC 메시지 ↔ 실제 omp RPC (`omp --mode rpc`, NDJSON over stdio)

omp 정식 명세: <https://github.com/can1357/oh-my-pi/blob/main/docs/rpc.md>

| 원본 설계 메시지 | omp 실제 프레임 | 비고 |
|---|---|---|
| handshake | `ready`(stdout) → `negotiate_protocol` v2 → `set_host_tools` → `get_state` | 브릿지가 수행한다. v2는 대형 프레임을 `rpc_chunk`로 분할한다. |
| prompt | `{type:"prompt", id, message}` → `response` 즉시 ack → `prompt_result` | 에디터 컨텍스트 요약(addAdditionalContext)은 호스트가 message 앞에 넣는다. `streamingBehavior`(steer/followUp)는 보내지 않는다. 아래 "한 번에 한 메시지" 참고. |
| tool_call | `host_tool_call {id, toolCallId, toolName, arguments}` | `set_host_tools`로 등록한 호스트 툴만 |
| tool_result | `host_tool_result {id, result:{content:[{type:"text",text}]}, isError?}`, 진행 중에는 `host_tool_update` | |
| permission_request | 아래 "승인 프롬프트 두 경로" | |
| permission_result | omp 내장 툴: `extension_ui_response {id, value:"Approve"\|"Deny"}` | 가운데 세션 승인 항목은 고르지 않는다. 고르면 omp가 그 대상의 이후 호출을 호스트 게이트 없이 실행한다. |
| status_update | `agent_start/end`, `message_update`(delta), `tool_execution_*`, `get_state.contextUsage` | UI에는 `session/info`, `chat/*`로 변환한다. |
| error | `response{success:false}`, `prompt_result{status:"error"}`, `extension_error` | |
| cancel | `abort` | 진행 중인 호스트 툴에는 `host_tool_cancel`로 역통지한다. |

승인 프롬프트 두 경로:

1. 호스트 툴: 호스트 `PermissionGate`가 실행 전에 직접 판단한다. omp 승인 오버레이에서는 `allow`로 둔다.
2. omp 내장 툴: `extension_ui_request {method:"select", title:"Allow tool: <name>\n(설명)", options:["Approve","Deny"]}`가 온다.
   승인 모드가 띄운 프롬프트는 omp 18.11.3부터 `options:["Approve","Approve <대상> for this session","Deny"]`이다.

### 턴과 중지

- 턴 완료 판정은 같은 id의 `prompt_result`이다. 세션 완전 종료는 `session_settled`이다.
- **한 번에 한 메시지**: 턴이 열려 있거나 omp가 스트리밍 중일 때의 `chat/send`는 omp에 보내지 않고 호스트 명령 대기열에 넣는다.
  페이지는 `chat/turnStarted` 대신 `chat/queued`를 받는다. 대기열 규칙은 §8.8 "명령 대기열"에 있다.
  omp는 대기열의 후속 메시지(followUp)를 `abort` 뒤에도 유지한다. omp 안에 줄을 세우면 중지가 무력해진다.
- **중지** 순서:
  1. 응답 대기/컨텍스트 수집과 아직 제출되지 않은 메시지를 취소한다.
  2. omp가 기다리는 승인 프롬프트를 거부로 풀어 준다.
  3. `abort`를 보낸다.
- 이미 쓰기 시작한 NDJSON 프레임은 완성한 다음 중지한다. 파이프의 메시지 경계를 지키기 위해서다.
- omp가 보고하지 않는 턴(omp가 메시지를 보류한 경우)은 정착 지연(기본 5초) 동안 omp가 유휴이면 호스트가 `aborted`로 닫는다.
- omp가 아직 작업 중이면 턴을 연 채 다시 확인한다. 느린 중단이나 omp가 스스로 깨어난 백그라운드 작업이 이 경우다.
- `prompt` 응답(ack) 대기는 30초다. 시간 초과는 `timeout` 오류로 끝내되 omp가 이미 실행 중일 수 있음을 알린다(재시도 불가 표시).
- **omp 세대 고정**: 메시지는 보낼 때의 omp 프로세스에 묶인다.
  컨텍스트를 모으는 사이 omp가 종료·재시작되면 그 메시지는 새 프로세스로 보내지 않는다. UI에는 이미 턴 종료가 보고되어 있다.
- 턴 종료는 항상 그 턴의 시작(`chat/turnStarted`) 뒤에 게시된다.

### 입력 쓰기 제한

- 명령별 시간 제한은 쓰기 차례 대기·stdin 쓰기·응답 대기를 모두 포함한다.
- 다음 경우 반쯤 쓴 프레임이 남을 수 있으므로 그 연결을 재사용하지 않고 omp를 종료한다:
  - 시간 제한이 프레임을 쓰는 도중에 걸림
  - 프레임 하나를 쓰는 데 30초를 넘김(omp가 입력을 읽지 않음)
  - 쓰기 시작 여부가 애매한 경합
- 아직 쓰기 전이었다면 그 명령만 시간 초과로 끝난다.
- 이후는 일반 재시작 절차를 따른다.

### 하트비트와 감시

- 실행 중 30초 동안 출력이 없으면 `get_state`를 보낸다.
- 3회 연속 응답이 없으면 그 프로세스를 종료하고 일반 재시작 절차로 넘긴다.
- 출력 수신도 생존 신호로 취급한다.
- 프로세스 종료·stdout EOF·명령별 시간 제한도 감시한다.

## 4. omp 실행 계약 (호스트 → omp)

```
omp --mode rpc [--mode rpc-ui] --cwd <솔루션 루트> --approval-mode <always-ask|write|yolo>
    --config <%LOCALAPPDATA%\Pomp\omp\pomp-overlay.yml> --session-dir <작업 영역\.pomp\local\sessions>
    [--plugin-dir <작업 영역\.pomp\local\engine-plugin>] [--no-extensions] -e <확장 폴더\omp-extension>
    [--model <id>] [--thinking <level>] [추가 인수]
```

### 플래그와 인수

| 항목 | 계약 |
|---|---|
| `--no-extensions` (기본) | omp가 스스로 찾는 확장·훅을 끈다: 사용자 프로필, 설치된 플러그인, 작업 폴더의 `.omp/extensions`. 명시한 `-e` 패키지(pomp)와 그 `skills/`(`vs-project`)는 그대로 로드된다. |
| "Load discovered omp extensions" | 도구 > 옵션 > Pomp의 옵션, 기본 꺼짐. 켜면 `--no-extensions`를 뺀다. 이런 확장은 omp 안에서 샌드박스 없이 실행된다. |
| `--approval-mode` | 기본 `always-ask`: 읽기는 자동, 쓰기·실행은 사용자 승인(원본 "권한 투명성"). |
| `-e` | `.ts` 파일이 아니라 **`omp-extension` 폴더**(절대 경로)를 준다. 아래 "omp 측 확장 로딩" 참고. |
| 추가 인수 (`ExtraArguments`) | 설정에 평문으로 저장된다. 로그에서는 이름에 key/token/secret/password/passwd/credential/auth가 들어간 플래그의 값을 `***`로 가린다. omp에는 원래 값이 간다. |

`--no-extensions` 검증 기록:

- omp 18.4.3·18.8.6·18.10.3: 작업 폴더 프로브 확장은 로드되지 않고, 옵션을 켜면 로드된다.
- omp 18.10.4·18.12.1: pomp 패키지의 명령과 `vs-project`가 로드된다.

### 프로세스 작업 폴더

- omp는 시작한 폴더의 `.env`를 `--cwd`로 옮기기 전에 읽는다. 비어 있는 환경 변수(공급자 주소, 프록시, API 키 등)를 채운다.
- 그래서 엔진 프로세스는 항상 POMP 폴더(`%LOCALAPPDATA%\Pomp\workspace`)에서 시작한다. 작업 루트는 `--cwd`로만 넘긴다.
- 저장소의 `.env`는 엔진에 들어가지 않는다.

### 작업 영역 구성 (요약)

- `--no-extensions`와 무관하게 omp는 작업 폴더의 다음 구성을 사용자 설정과 같은 신뢰로 읽고 실행한다:
  - 설정 파일(`tools.approval.*`, `bash.patterns`, 셸·엔드포인트 설정)
  - 모든 공급자 형식의 MCP 서버 목록
  - 도구 코드(`.omp/tools` 등)
  - 컨텍스트 파일의 `@경로` 포함
- omp 18.12.1에도 프로젝트 신뢰 확인이 없다.
- POMP는 이런 구성이 있는 작업 영역에서, 사용자가 그 내용을 신뢰하기 전에는 omp를 시작하지 않는다. 자세한 내용은 아래 "작업 영역 신뢰".

### 실행 파일 해석

해석 순서:

1. 옵션의 경로
2. VSIX 번들(`<확장 폴더>\omp\omp.exe`)
3. PATH(`omp.exe`/`omp.cmd`/`omp.bat`)
4. 알려진 설치 위치(`%LOCALAPPDATA%\omp`, `%USERPROFILE%\.bun\bin`, `%APPDATA%\npm`)

- 정식 패키지에는 항상 번들 엔진이 있다. PATH·설치 위치는 엔진 없는 개발 패키지에서만 쓰인다.
- 번들 엔진이 손상되면 다른 omp로 대체하지 않는다(아래 "엔진 버전").

### 오버레이 `pomp-overlay.yml`

- 모든 호스트 툴을 `tools.approval.<name>: allow`로 둔다. 호스트가 자체 게이트하므로 이중 승인을 막는다.
- 다음 두 배열은 병합해서 쓴다. 오버레이 배열은 하위 레이어를 통째로 대체하므로, 병합하지 않으면 사용자 설정이 지워진다.
  - `skills.customDirectories`: POMP 스킬 폴더 2개 + 사용자·프로젝트 설정값
  - `disabledExtensions`: POMP에서 끈 스킬 + 사용자·프로젝트 설정값
- 다른 프로그램의 스킬 폴더를 끈다: `skills.enableAgentsUser`, `enableAgentsProject`, `enableClaudeUser`, `enableClaudeProject`, `enableCodexUser`가 모두 `false`다.
  - 그래서 `~/.agents`, `<작업 영역>/.agents`(`.agent` 포함), `~/.claude`, `<작업 영역>/.claude`, `~/.codex`의 스킬은 엔진에 올라가지 않고 도구 › 스킬에도 없다.
  - omp 자체 폴더(`~/.omp/agent/skills`, `<작업 영역>/.omp/skills`)는 그대로 읽는다. omp의 `enablePiUser`·`enablePiProject`가 이 폴더의 스위치라 쓰지 않는다.
- `task.maxRecursionDepth: 1`: 메인 대화만 하위 에이전트를 띄운다. 아래 "하위 에이전트와 화면 제어 경계".
- 절대 경로·유효한 YAML이어야 한다. 오버레이 오류는 omp 시작 실패가 된다.

### omp 측 확장 로딩

- 폴더의 `package.json`(`omp.extensions`)이 모듈을 지정한다. `skills/`는 폴더 루트에서만 발견된다.
- `--hook`은 `-e`의 별칭이라 쓰지 않는다.
- omp 인수 파서는 확장자 없는 Windows 경로 뒤의 플래그 아닌 토큰을 붙여 해석할 수 있다.
  그래서 폴더 경로 다음 인수는 반드시 `--`로 시작하는 플래그이거나, 폴더 경로가 마지막 인수여야 한다.
- `--no-ui`, `--no-skills`는 쓰지 않는다.
- 호스트 툴은 `set_host_tools`에서 **`loadMode: "essential"`** 로 등록한다. 생략하면 `discoverable`이 되어 `xd://` 장치 뒤로 숨겨진다.
- 툴 이름은 접두사 없이 그대로 등록되며 승인 키도 같다.

### 환경 변수

| 변수 | 의미 |
|---|---|
| `POMP_HOST=1` | 필수. 없으면 omp 측 확장이 아무것도 등록하지 않는다. |
| `POMP_STATE_DIR` | 필수. `vs-state.json`이 있는 폴더. |
| `POMP_HOST_TOOLS` | 호스트가 실제로 제공한 툴 이름, 쉼표 구분. `set_host_tools`·오버레이와 같은 목록이다. |
| `POMP_HOST_VERSION`, `POMP_VS_VERSION`, `POMP_SOLUTION_ROOT` | 호스트·VS 버전, 솔루션 루트 |
| `POMP_PRE_EDIT_MODE` | 선택. `block\|warn\|off`, 기본 `block`. |

### `vs-state.json`과 pre-edit Hook

- `POMP_STATE_DIR\vs-state.json`은 호스트가 원자적으로 갱신하는 VS 상태다(열린 문서·dirty 여부).
- omp 측 pre-edit Hook은 이를 보고 **VS에서 수정 중(dirty)인 파일**에 대한 omp 내장 `edit`/`write`/`ast_edit`/`lsp` 변경을 차단한다.
- 안내 문구는 `POMP_HOST_TOOLS`에 따라 다르다:
  - `edit_buffer_hashline`이 제공되면(현재 기본): `read_buffer` → `edit_buffer_hashline`을 안내한다.
  - 제공되지 않으면: "VS에서 저장(Ctrl+S)을 요청한 뒤 일반 도구로 다시 시도, 저장 안 된 변경을 덮어쓰지 말 것"을 안내한다.
- 제공되지 않은 툴 이름은 어떤 안내문에도 나오지 않는다.
- 설계의 "사전 편집 시 해시 비교" 대신 dirty 상태로 판단한다. 저장 안 된 버퍼 내용은 디스크 해시로 드러나지 않기 때문이다.
- 상태 갱신 주기는 2초다. 그 사이에 생긴 dirty 상태는 훅이 아직 모를 수 있다.

### omp 측 구성 요소

| 구성 요소 | 역할 |
|---|---|
| `pomp-context` Extension | 시스템 프롬프트에 VS 안내를 더한다. 제공 툴 수에 따라 약 230토큰 이상, 8개 기준 약 330토큰. 솔루션 변경은 숨김 메시지로 통지한다. |
| `pre-edit` Hook | `tool_call`에서 `{block:true, reason}`을 돌려준다. |
| `vs-project` Skill | `skill:vs-project`, 지연 로딩 |
| `verify-gate` | `session_stop`에서 미확인 소스 편집이 있으면 한 번만 확인을 유도한다. 고정 "완료 전 증거" 안내는 항상 적용된다. |
| `untrusted-content` | 웹·MCP·브라우저 결과에 외부 데이터임을 표시한다. |
| `compaction-context` | 자동 압축 요약에 솔루션·미저장 버퍼·워크플로 단계·거부 승인을 유지한다. |
| `time-context` | 날이 바뀔 때만 로컬 시각을 통지한다. |

- 범위 규칙(`.mdc`, 최대 5개/24,000자)과 리소스 카탈로그는 시스템 프롬프트에 넣지 않는다.
- 이들은 내용이 바뀔 때만 다시 보내는 숨김 메시지로 전달한다.
- 그래서 세션 안의 시스템 프롬프트는 바이트 단위로 안정적이고, 공급자 프롬프트 캐시가 유지된다.

### 하위 에이전트, 프로세스 수명, 재시작

- omp의 하위 에이전트(`task` 도구)는 omp 안에서 헤드리스로 실행되며 자체 승인 프롬프트를 띄우지 않는다(omp 동작).
- 승인 경계는 부모의 `task` 호출 승인이다. `always-ask`에서도 하위 에이전트 안의 쓰기·실행은 개별 확인되지 않는다.
- 자식 프로세스는 Windows Job Object에 넣는다. VS가 끝나면 함께 종료된다(좀비 방지).

| 상황 | 처리 |
|---|---|
| 실패 1회 | 1초 후 재시작 |
| 실패 2회 | 2초 후 재시작 |
| 연속 3회 실패 | **`failed`** 상태. 사용자가 고른다(재시작/로그 보기). |
| 60초 이상 정상 실행 | 실패 횟수 초기화 |
| 치명적 오류(실행 파일을 찾지 못함 등) | 즉시 `failed` |
| 모델 없이 종료(`No models available`) | 재시도 없이 바로 `failed`(`reason: "setupRequired"`). 채팅 창은 오류 대신 엔진 설정 패널을 보여 준다(아래 "엔진 설정"). |

### 작업 영역 신뢰

omp(18.4.3부터 현재 18.12.1까지)는 프로젝트 신뢰를 확인하지 않는다(`isProjectTrusted: () => true`).
작업 폴더에서 읽는 다음 구성은 시작과 동시에 코드를 실행하거나, 승인을 바꾸거나, 바깥 파일을 대화에 넣는다.
MCP stdio 서버는 엔진 환경을 그대로 받는다. POMP가 넣은 API 키도 포함된다.

| 종류 | 파일 (작업 루트 기준) |
|---|---|
| 엔진 설정 | `.omp/config.yml`, `.omp/settings.json`, `.claude/settings.json`, `.cursor/settings.json`, `.gemini/settings.json`, `opencode.json[c]`, `.opencode/opencode.json[c]`, `.codex/config.toml` |
| MCP 서버 | `.omp/mcp.json`, `.omp/.mcp.json`, `mcp.json`, `.mcp.json`, `.claude/.mcp.json`, `.claude/mcp.json`, `.cursor/mcp.json`, `.windsurf/mcp_config.json`, `.vscode/mcp.json` |
| 도구 코드 | `.omp/tools`, `.claude/tools`, `.codex/tools` 아래 `.ts/.js/.mjs/.cjs`. `--no-extensions`로 꺼지지 않는다. |
| 플러그인 | `.omp/plugins/{package.json, installed_plugins.json, omp-plugins.lock.json}`, `.omp/plugin-overrides.json` |
| 언어 서버 | 루트와 `.omp/ .claude/ .codex/ .gemini/`의 `lsp.json`, `.lsp.json`, `lsp.yaml`, `.lsp.yaml`, `lsp.yml`, `.lsp.yml` |
| 언어 서버 실행 파일 | 아래 "언어 서버 실행 파일" 참고 |
| 확장 코드 | "Load discovered omp extensions"를 켰을 때만: `.omp/extensions`, `.omp/hooks`, `.claude/hooks`, `.codex/hooks`, `*/commands` 아래 스크립트 |
| 조언자 | `WATCHDOG.yml`, `WATCHDOG.yaml`, `.omp/WATCHDOG.yml`, `.omp/WATCHDOG.yaml`. 조언자의 모델·도구·지시다. 신뢰 카드에는 에이전트 설정으로 보인다. |
| 바깥 파일 포함 | 아래 "바깥 파일 포함" 참고 |

언어 서버 실행 파일:

- omp는 기본 언어 서버(`tsc`, `gopls`, `pyright-langserver` 등 55개)를 처음 쓸 때 PATH보다 먼저 작업 영역의 도구 폴더에서 찾는다.
- 찾는 폴더:
  - 루트에 `package.json`류가 있으면 `node_modules/.bin`
  - Python 설정이 있으면 `.venv`·`venv`·`.env`의 `bin`·`Scripts`
  - `Gemfile`이면 `vendor/bundle/bin`·`bin`
  - `go.mod`류면 `bin`
- 대상은 그 서버의 표지 파일이 루트에 있을 때 omp가 고를 첫 실행 파일이다. 확장자 없음·`.exe`·`.cmd`·`.bat` 순으로 고른다.

바깥 파일 포함:

- 컨텍스트 파일: `AGENTS.md`, `CLAUDE.md`, `GEMINI.md`, `.omp/AGENTS.md`, `.github/copilot-instructions.md`, `SYSTEM.md` 계열.
- 이 파일들과 그것이 포함한 작업 영역 안 파일의 `@경로` 중, 작업 영역 밖의 기존 파일이 대상이다.
- omp는 `~`, 절대 경로, `..`를 제한 없이 포함한다.

대상이 아닌 것: `.pomp/agents`(POMP 역할), `.omp/rules`, 스킬처럼 지시문만 담는 파일과 그 밖의 `.pomp/`.

#### 평가와 신뢰 저장

- 엔진 시작(`BuildLaunchOptionsAsync`) 때 `WorkspaceTrust.Evaluate`가 위 파일을 찾는다.
- 파일 경로·내용(SHA-256)·포함 대상으로 지문을 만든다.
- 구성이 있고 그 지문을 신뢰한 적이 없으면 omp를 시작하지 않는다. `bridge/state`는 `failed`, `reason: "workspaceTrustRequired"`이고 재시도하지 않는다.
- 채팅의 연결 알림 자리에 목록과 [신뢰하고 시작]·파일별 [파일 열기]·[시작 안 함]이 나온다(`workspace/trustState`, `workspace/trust`).
- 신뢰는 사용자가 본 지문만 저장한다.
  - 위치: `%LOCALAPPDATA%\Pomp\workspace-trust.json`, 작업 영역 밖.
  - 개수 제한 없이 1 MiB까지, 최근 것부터 남긴다.
- 파일이 추가·변경·삭제되면 지문이 달라져 다음 시작 때 다시 묻는다. git pull과 에이전트의 수정도 포함된다.
- 목록을 본 뒤 바뀌었으면 신뢰 요청을 거부하고 새 목록을 보인다.

#### POMP 자신의 쓰기와 해제

- 사용자가 도구 › MCP에서 프로젝트 MCP 서버를 바꾸면 POMP가 `.omp/mcp.json`을 쓴다.
- 이때 새 지문을 신뢰로 이어 가는 조건(`WorkspaceTrust.KeepAfterOwnWrite`):
  - 바꾸기 전 구성이 없었거나 신뢰한 그대로였다.
  - 다른 파일이 바뀌지 않았다.
- 신뢰한 적 없는 구성은 계속 묻는다.
- 해제는 도구 › 권한 › 신뢰한 저장소에서 한다. 지금 작업 영역을 해제하면 엔진을 다시 시작해 다시 묻는다.

#### 남은 한계

- 확인하지 않는 것: 작업 루트보다 위 폴더의 컨텍스트 파일과 그 `@` 포함, 지시문만 담은 파일(역할·규칙·스킬).
- 지시문은 승인 절차를 바꾸지 못한다. 실행·설정 변경은 승인 카드에서 묻는다.
- omp의 `debug`가 작업 영역에서 찾는 디버그 어댑터는 `debug` 승인(매번 확인)을 거친다.
- 실행 중에 에이전트가 이 파일들을 고치는 요청은 기억된 허용으로 답하지 않고 매번 묻는다(6장 "매번 확인하는 요청"). omp는 설정 파일을 실행 중에도 다시 읽기 때문이다.

### 엔진 버전 (POMP가 omp를 고정)

POMP는 특정 omp 버전과 함께 만들고 시험한다.
omp의 RPC 계약(`get_state`, `set_fast_mode`, `get_available_thinking_levels`, `omp usage --json` 등)은 버전마다 바뀔 수 있다.
그래서 **POMP 릴리스 하나 = omp 버전 하나**로 묶어 배포한다.

#### 버전 정의

- `engines/omp/engine.props` 한 곳에서 정한다.
- 담는 값: 버전, 공식 릴리스 URL(`github.com/can1357/oh-my-pi`), 자산 이름(`omp-windows-x64.exe`), 크기, SHA-256, `THIRD-PARTY-NOTICES.txt`의 SHA-256.
- 현재 **omp 18.12.1**이다. 크기 222,742,016바이트, SHA-256은 `1ba0274b`로 시작해 `77bb`로 끝난다.
- 라이선스는 MIT(`engines/omp/LICENSE.upstream`).

#### 빌드 (`Pomp.Vsix.Common.targets`의 `PompPrepareOmpEngine`)

- `artifacts\engines\omp\<버전>\`에 엔진이 없으면 릴리스에서 내려받는다.
- 크기·해시가 고정값과 다르면 받은 파일을 지우고 빌드를 실패시킨다. 엔진은 POMP1011, 고지 파일은 POMP1012.
- `-p:PompOmpExe=<절대 경로>`(build.ps1 `-OmpExe`)는 오프라인 빌드용 로컬 사본일 뿐이다. 같은 해시 검사를 거치므로 고정되지 않은 omp는 패키지에 넣을 수 없다.
- `-p:PompBundleOmp=false`는 엔진 없는 개발 패키지를 만든다.
- 모든 VSIX에 들어가는 파일:

| 파일 | 내용 |
|---|---|
| `omp\omp.exe` | 엔진 |
| `omp\engine.json` | `{version, sha256, size, asset, source}`, 빌드가 작성 |
| `omp\LICENSE.txt` | omp 라이선스 |
| `omp\THIRD-PARTY-NOTICES.txt` | 제3자 고지 |

- 패키지 크기: Release VSIX 약 117 MB, Debug 약 245 MB.

#### 실행 (`BundledEngine.Inspect`, `PompSession.Engine.cs`)

- 시작할 때마다 `engine.json`이 고정한 크기와 SHA-256을 `omp\omp.exe`와 비교한다.
- 해시는 경로·크기·수정 시각별로 VS 프로세스당 한 번 계산한다(약 1초).
- 일치하면 그 엔진만 쓴다.
- 매니페스트가 있는데 엔진이 없거나 다르면 시작하지 않고 POMP 재설치를 안내한다. 재시도하지 않는다.
- PATH나 설치 위치의 다른 omp로 몰래 대체하지 않는다.
- 로그인 도우미와 `omp usage` 실행도 같은 규칙을 따른다(`BundledEngine.Refusal`).
- 이 검사는 손상·교체·호환성 사고를 막기 위한 것이다. 확장 폴더에 쓸 수 있는 사용자의 의도적인 변조까지 막는 보안 경계는 아니다.

#### 고급 재정의

- 도구 > 옵션 > Pomp의 omp 경로를 지정하면 그 파일을 쓰되 "검증 안 됨"으로 표시한다.
- 이 경우에는 번들 엔진이 손상돼도 시작한다.
- 엔진 없는 개발 패키지가 설치된 omp를 쓸 때도 같다.
- 파일 이름이 `omp`이면 `omp --version`을 경로별로 한 번 실행해 버전을 보여 준다. 제한 시간은 15초다.

#### UI

- 호스트는 `engine/info`(`docs/ui-protocol.md`)를 상태로 보내고 스냅샷에 포함한다.
- 검증된 내장 엔진이면 따로 보이는 줄이 없다.
- 검증 안 된 엔진이면 두 곳에 표시된다:
  - ⋯ 메뉴 아래쪽: "에이전트 <버전> · <어디서 온 실행 파일> · 검증 안 됨"
  - 채팅 위: 닫을 수 있는 경고

#### 업데이트 정책

- omp 업데이트는 **POMP 업데이트로만** 들어온다.
- omp 자체의 `omp update`와 시작 시 업데이트 확인은 대화형 모드에서만 알림을 띄운다. POMP가 쓰는 RPC 모드에서는 아무것도 설치하지 않는다.
- PC에 설치된 omp가 바뀌어도 POMP는 자기 폴더의 고정 엔진만 쓴다.

새 omp를 채택하는 순서:

1. `engine.props`의 버전·크기·해시를 갱신한다.
2. 빌드한다(다운로드·검증).
3. Core/UI 테스트와 실제 VS 확인을 한다. 위 RPC 계약, 로그인·사용량·빠른 모드·생각 수준을 본다.
4. 두 매니페스트의 POMP 버전을 올려 함께 배포한다.

### 보조 모델

자동완성(Tab), 질문의 수정 제안, 코드 캡션, 지식 변환, 입력 예측 문장은 채팅 모델과 따로 정한 **보조 모델**을 쓴다.
코드 주석에서는 이것을 "편집기 모델"(editor model)이라고 부른다.

#### 선택

- 고르는 곳: 채팅 창 계정 탭의 "보조 모델", 또는 도구 > 옵션 > Pomp > 일반 > 편집기의 "보조 모델"(`Helper model`).

| 화면 선택 | 내부 값 | 의미 |
|---|---|---|
| 빠른 모델 (기본) | 비움 | omp의 빠른 모델 역할 `@smol`. 사용자가 설정한 fast 역할, 없으면 omp 내장 우선순위 목록의 빠른 모델. |
| 채팅 모델과 같게 | `@chat` | 채팅 모델 |
| 모델 직접 선택 | `provider/id` | omp가 해석하는 모델 문자열 |

- omp가 찾지 못하는 모델은 다른 모델로 몰래 바꾸지 않고 실패한다. 자동(빠른 모델)만 채팅 모델로 대신한다.

#### 호출 (`extensions/inline.ts`)

- `completeSimple`로 도구 없이 직접 호출한다.
- 자동완성·캡션은 추론을 끈다(`disableReasoning`).
- 질문의 수정 제안은 모델이 제공하는 가장 낮은 단계(low/minimal)를 쓴다.

| 요청 | 엔진 쪽 제한 시간 |
|---|---|
| 자동완성 | 8초 |
| 캡션 | 12초 |
| 편집 | 45초 |

#### 순서와 충돌

- 한 번에 한 요청만 엔진에 보낸다.
- 실행 중인 요청은 멈출 수 없다. 새 자동완성 요청은 그것이 끝나기를 기다린다.
- 사용자가 계속 입력하면 기다리던 이전 요청은 취소된다. 가장 최근 것만 보낸다.
- 호스트 로그(디버그)에 응답한 모델과 걸린 시간이 남는다.
- 채팅 응답이나 승인 대기 중이면 편집기 요청은 보내지 않는다. 같은 엔진 대화를 쓰기 때문이다.
- 순서를 기다린 요청도 보내기 직전에 다시 확인한다. 그 사이 엔진이 다시 시작되거나 대화가 바뀌었으면 취소한다.
- 계정 탭의 "사용 모델" 확인이 이 때문에 거절되면 5초마다 다시 확인한다. 최대 2분이다.

#### 설정 저장

- 채팅 창이나 편집기 메뉴에서 바꾼 편집기 설정은 그 설정 하나만 저장한다. 대상은 보조 모델, 자동완성·코드 캡션 켜기/끄기다.
- 열려 있는 도구 > 옵션 대화상자의 적용하지 않은 다른 값은 저장되지도, 사라지지도 않는다.

### 에이전트·팀과 컴퓨터 사용

구현 계약은 §8 "에이전트·팀 구현 계약"에 있고, 사용 안내는 `docs/agent-teams.md`에 있다.
기본 omp(현재 18.12.1)만으로 동작하고 엔진 패치는 필요 없다.

#### 에이전트와 팀

- **에이전트**는 omp 표준 형식의 정의 파일이다: `<작업 영역>/.pomp/agents/*.md`, `~/.omp/agent/agents/*.md`.
- POMP는 그 파일을 만들고 고치기만 한다.
- 프로젝트 역할은 `.pomp/local/engine-plugin/agents`에 복사해 `--plugin-dir`로 omp에 넘긴다.
- **팀**은 2명 이상이다. 위치는 `.pomp/teams/*.json` 또는 `%APPDATA%\Pomp\teams`.
- 팀으로 보낸 메시지의 흐름:
  1. 호스트가 확인한 뒤 `/pomp-team <id> (메시지)`로 보낸다.
  2. 확장(`extensions/teams.ts`)이 팀 안내와 함께 메인 대화에 넘긴다.
  3. 메인 대화가 omp `task` 도구로 구성원에게 나눈다.
  4. 구성원끼리는 omp IRC(`write agent://<id>`)로 조율한다.

#### 하위 에이전트와 화면 제어 경계

- **하위 에이전트는 승인 없이 돈다.** omp가 `tools.approvalMode: yolo`로 실행하며, 부모의 `task` 승인이 경계다.
- **하위 에이전트는 다시 하위 에이전트를 띄우지 못한다.** 오버레이의 `task.maxRecursionDepth: 1` 때문이고, 승인 모드와 관계없다.
  - omp는 하위 에이전트에게 `task` 도구를 주지 않고, eval의 `agent()`·`workpool()` 도움 함수도 알리지 않는다.
  - 그래도 띄우려 하면 omp가 "Cannot spawn another agent at task depth 1; maximum depth is 1." 오류를 모델에 돌려준다.
  - 메인 대화의 `task` 승인은 그대로 승인 모드 규칙을 따른다.
- POMP는 하위 에이전트에게 화면·브라우저 제어를 주지 않는다.
- 확장(`extensions/computer-guard.ts`)은 엔진의 `SettingsManager.create(ctx.cwd)`와 `rawValue`로 **호출 세션의 실제 computer/browser 활성 설정**을 읽는다.

| 호출 | 차단 조건 |
|---|---|
| 메인 세션의 `eval` | 활성 기능에 `computer-state.json`의 같은 `sessionId`·기능별 POMP 허용이 없음 |
| 하위 에이전트의 `eval` | 허용이 있어도 막는다. 제어 허용이 있거나 상태를 읽지 못할 때 차단한다. |
| 직접 `computer`·`browser` 도구 | 같은 세션의 해당 기능 허용과 메인 세션이 모두 필요하다. |
| 설정 API를 읽지 못함 | `eval` 차단 |
| 상태 파일을 읽지 못함 | 활성 기능의 `eval`과 직접 제어 호출 차단 |

- 환경 변수만으로는 허용하지 않는다.
- 이 경계는 엔진 설정 API의 세션별 유효 설정 해석에 기댄다. **엔진 업그레이드 때 API·세션 범위·eval prelude 우회를 다시 검증**한다.
- 확장이 꺼져 있으면 컴퓨터 사용·브라우저 제어 둘 다 쓰지 않는다.

#### 컴퓨터 사용

- 덮어쓰기 설정은 매 시작 `computer.enabled: false`이다.
- `browser.enabled`는 옵션이다. 기본 끔이고, 네트워크 도구 차단 시 항상 끔이다.
- 대화마다 페이지 토글(`computer/set`)이 RPC `prompt "/computer on|off"`로 켜고 끈다. 모델 호출은 없고 결과는 `command_output`이다.
- 사용자가 입력한 `/computer`는 POMP가 가로챈다.
  - `/computer:` 형태, 앞뒤 특이 공백, 정규화된 철자도 호스트에서 차단한다.
  - 검사는 `ComputerUse.IsComputerCommand`: FormKC, 대소문자 무시, 명령 경계 검사.
  - 엔진으로 직접 넘어가 권한을 켤 수 없다. 페이지의 `computer/set`만 토글한다.
- 켜기 전에 같은 세션의 상태 파일을 먼저 쓴다. 끄기는 omp가 확인한 뒤에 파일을 바꾼다.
- 새 대화·세션 전환·엔진 재시작이면 꺼진다.
- 켜져 있는 동안 확장(`extensions/computer-guard.ts`)이 메인 대화의 시스템 프롬프트 끝에 영어 안내 한 절(`## Screen & input control (pomp)`)을 붙인다.
  - 내용: 이 대화에서 화면·입력 제어가 켜져 있다, Eval 도구의 `computer` prelude를 쓰고 처음 쓰기 전에 `xd://eval/computer`를 읽는다, GUI 제어에 Orca 같은 외부 프로그램·CLI를 실행하거나 쓰지 않는다.
  - 매 턴 `before_agent_start`에서 `computer-state.json`을 다시 읽는다. 같은 `sessionId`에 `desktop: true`일 때만 붙이고, 꺼지면 다음 턴부터 없다.
- 엔진 쪽 반영: `/computer on`은 세션 설정만 바꾸고 Eval 도구 설명은 바꾸지 않는다(시작 때 목록 유지).
  - 대신 다음 턴에 omp가 숨은 알림(`<system-notice id="prelude-extension">`)으로 `computer` prelude가 생겼음과 `xd://eval/computer` 링크, omp의 컴퓨터 사용 지침을 넣는다.
  - `xd://eval/computer` 문서는 켜진 즉시 읽을 수 있다.

| 옵션 | omp 설정 |
|---|---|
| 사용 안 함 | `tools.approval.computer: deny` |
| 화면 보기도 확인, 또는 Yolo 승인 모드 | `prompt`(모든 호출 확인) |
| 그 밖 | omp 단계대로: 화면 캡처는 묻지 않고, 클릭·입력·클립보드 쓰기는 매번 묻는다. |

세 옵션을 바꾸면 엔진이 다시 시작된다.

#### 채팅 UI 구성

- 탭 구성:
  - 작업: **진행 · 변경 · 코드 점검**
  - 에이전트: **역할 · 팀 · 조언자 · 자동화**
  - 도구: **대화 설정 · 권한 · 스킬 · MCP · 규칙 · 워크플로 · 지식**
- 목록은 공용 표(`components/ui.tsx` DataTable: 하나의 grid, 행은 subgrid)·스위치·배지·아이콘 버튼으로 그린다. 검색과 20개씩 페이지가 있다.
- 변경은 **대기 중 / 적용됨** 전환으로 보여 준다. 칩에는 대기만 센다.
- 코드 점검은 에이전트 지적과 VS 컴파일러 오류·경고를 합쳐, 현재 보낼 대상에 해결 요청 하나로 보낸다. 편집기 컨텍스트는 없고 50개·12,000자까지다.
- 승인된 명령은 호출 도구 카드에 붙인다. 구성원은 `task` 카드와 대화 상대 탭에 붙인다.
- 코드 검색 인덱스는 어휘 갱신을 자동으로 한다. 임베딩 옵션이 바뀌면 벡터 재구축·중지도 자동으로 한다.
- 대화형 터미널은 진행 탭 맨 아래에, 옵션과 엔진 지원이 모두 있을 때만 표시한다. 번들 엔진은 지원하지 않는다.

### 엔진 설정

`setup/command` / `setup/state`(`docs/ui-protocol.md` "Engine setup")가 다음을 제공한다:
계정(로그인), API 키 저장, 실행 중인 omp의 모델 목록·선택, 생각 수준, 계정별 사용량.
모델이 없는 첫 시작은 재시도하지 않고 `setupRequired`로 설정 패널을 연다.

구현(`Pomp.Core\Features`):

| 클래스 | 역할 |
|---|---|
| `ProviderSetup` | 명령·상태 |
| `OmpAuthHelper` | 로그인 도우미 프로세스 |
| `OmpUsage` | 사용량 실행·파서 |
| `ProviderCredentialStore` | API 키 |

#### 로그인 도우미 (auth helper)

omp RPC는 모델을 쓸 수 있어야 시작한다. 그래서 터미널 없이 로그인하려고 호스트가 별도의 짧은 omp를 소유한다.

```
omp --mode rpc --no-session --no-extensions --no-skills --no-rules --model openai/gpt --api-key pomp-setup-placeholder
```

- 자리표시자 키는 명령줄에만 둔다.
- 환경 `OPENAI_BASE_URL=http://127.0.0.1:9/v1`(닫힌 루프백 포트)이라 키는 어디에도 전달되지 않는다. 도우미는 모델을 호출하지 않는다.
- 작업 폴더는 POMP 자체의 빈 폴더 `<DataDirectory>\workspace`이다.
- 환경은 호스트 환경 + 저장된 API 키다. 키는 `ProviderCredentialStore`의 고정 변수 이름만 쓴다. `POMP_*` 변수는 주지 않는다.
- omp 프로필을 엔진과 공유한다. 도우미가 저장한 자격 증명을 엔진이 그대로 쓴다. 세션·설정은 쓰지 않는다.
- 엔진과 같은 `IOmpProcessLauncher`·`OmpConnection`으로 필요할 때 시작한다. ready 프레임만 기다리고 프로토콜 협상·호스트 툴·`get_state`는 건너뛴다.
- 명령은 한 번에 하나씩 보낸다.
- stderr는 URL을 담을 수 있어 로그에 남기지 않는다.

종료 시점:

- 실행 중인 명령이 없는 채 2분이 지났을 때(로그인 중에는 해당 없음)
- 세션이 끝날 때
- 첫 로그인 뒤 엔진을 다시 시작하기 직전
- 저장 키가 바뀌어 환경이 낡았을 때

| 제한 시간 | 값 |
|---|---|
| 시작 | 30초 |
| 공급자 목록 | 30초 |
| 로그인 | 15분 |

취소하면 프로세스를 종료한다.

#### RPC 로그인

`get_login_providers`는 공급자 약 80개를 `{id,name,available,authenticated}`로 준다. `providers`에 담기 전에 거른다:

| 필드 | 필터 |
|---|---|
| `id` | `[a-z0-9][a-z0-9.-]{0,63}` |
| `name` | 200자, 제어 문자 없음 |
| 목록 | 최대 256개 |

다른 공급자에 자격 증명을 저장하는 로그인 방식:

- omp 18.8의 `storeCredentialsAs`: `openai-codex-device`→`openai-codex`, `zai-coding-plan`→`zai`.
- 목록에는 남기되 `authenticated`는 항상 false로 보낸다.
- omp가 그 항목을 저장 대상 공급자의 자격 증명으로 판단하면서 대상 이름은 알려 주지 않는다. 그대로 두면 같은 로그인이 계정으로 두 번 나온다.
- 대응표는 `ProviderSetup.StoredUnder`이다. 이 방식으로 로그인한 뒤의 모델 확인도 저장 대상 공급자를 기준으로 한다.

로그인은 `login {providerId}`로 한다. 로그인 동안 omp가 보내는 `extension_ui_request` 처리:

| method | 처리 |
|---|---|
| `open_url` | 아래 조건일 때 기본 브라우저로 연다. 연 주소와 `instructions`는 `login`에 보인다. |
| `notify` | `login.progress` |
| `input` | `login.prompt`. `answerPrompt`로 답한다. |
| `select` / `confirm` / `editor` | omp가 기다리지 않도록 취소로 답한다. |

`open_url` 조건:

- `launchUrl ?? url`이 http/https 절대 주소다.
- 4096자 이하이고 사용자 정보·공백·따옴표가 없다.
- 로그인 하나에 최대 3번 연다.
- 열기는 주입 가능한 `Action<string>`이다. 기본은 `Process.Start(UseShellExecute)`.

`input` 답:

- 4096자 이하.
- 저장·로그하지 않는다.
- omp 오류 문구와 붙여 넣은 리디렉션 링크의 쿼리 값에서도 `***`로 지운다.

진행과 결과:

- 로그인은 백그라운드로 돈다. 명령 응답은 `login.state: running`이다.
- 진행·결과는 별도 `setup/state`로 알린다.
- 성공하면 같은 도우미에서 `providers`를 다시 읽는다.
- 비밀 입력이 필요한 공급자는 실패로 끝난다. omp의 문구("requires secret input" 로 시작해 terminal UI를 언급)를 지역화 접두어와 함께 `login.message`에 그대로 둔다.
- 그러면 페이지는 `loginConsole`을 제안한다. 옛 콘솔 로그인이다:
  - `omp login [provider]`를 자체 콘솔 창에서 실행한다.
  - Job Object 없이 실행한다. 브라우저가 같이 종료되지 않게 하기 위해서다.
  - 종료 코드 0이면 성공이다.

#### 로그아웃

- RPC에는 로그아웃이 없다. 호스트가 도우미를 끝내고 `omp auth-broker logout <공급자>`를 숨긴 채 한 번 실행한다.
- 실행 조건: 작업 폴더 `<DataDirectory>\workspace`, 저장한 키를 뺀 환경, 20초·출력 64 KiB 제한.
- 종료 코드 0만 성공이다. 출력은 보이거나 로그에 남기지 않는다.
- omp는 그 공급자에 저장된 로그인을 모두 지운다. 계정 구분은 없다.
- 로그인 중에는 거부한다.
- 끝나면 새 도우미로 `providers`를 다시 읽어 응답에 담는다. 그 공급자의 사용량 보고도 지운다.
- 저장한 API 키·환경 변수는 그대로라 계속 인증돼 있을 수 있다.
- 엔진에는 다시 시작한 뒤 적용된다. 키 변경과 같은 `restartRecommended`를 쓴다.

#### API 키

- 현재 Windows 사용자용 DPAPI로 암호화한다.
- 위치는 `<DataDirectory>\credentials\provider-keys.json`이다. 기본은 `%LOCALAPPDATA%\Pomp\credentials\provider-keys.json`. 원자적으로 저장한다.
- 고정된 제공자별 환경 변수만 다음 omp 시작(엔진·도우미)에 전달한다.
- 키 원문은 UI·로그에 내보내지 않는다.

#### 재시작 규칙

| 상황 | 처리 |
|---|---|
| 로그인 성공·키 저장, 엔진 중지 상태 | 호스트가 스스로 다시 시작한다. 로그인은 도우미를 먼저 끝낸 뒤 시작한다. |
| 로그인 성공, 엔진 실행 중 | `get_available_models`를 다시 읽는다. 새 공급자의 모델이 보이면 그대로 쓰고, 보이지 않을 때만 `restartRecommended`를 켠다. |
| 실행 중 키 저장·삭제 | 키는 다음 시작의 환경 변수이므로 항상 재시작을 권한다. |
| `Running`이 됨 | 권고를 끈다. |

#### 생각 수준

- `listThinking`은 실행 중인 엔진의 `get_available_thinking_levels`를 `thinkingLevels`에 담는다. 목록은 모델마다 다르다.
- 이름은 `[A-Za-z][A-Za-z0-9_-]{0,31}`로 거르고 최대 16개까지다.
- 엔진이 시작되거나 모델이 바뀌면 호스트가 스스로 다시 읽는다.
- `setThinking`은 마지막 목록에 있는 수준만 `set_thinking_level`로 보낸다. 목록이 없으면 `off/minimal/low/medium/high/xhigh/max/auto`를 허용한다.
- `currentThinking`은 `session/info.thinkingLevel`이다.
- 입력창의 추론 목록은 추론 모델이면 표준 단계를 모두 보여 준다. 엔진이 주지 않은 단계는 흐리게, 고를 수 없게 표시한다.

#### fast 모드

- `session/fast {requestId, enabled}` → `set_fast_mode`. 현재 모델 계열의 우선 처리 티어이고 omp `/fast`와 같다.
- 채팅 명령 큐와 별도로 보내고 `session/fastResult`로 한 번 답한다. 동시에 최대 4개다.
- 상태: `get_state`의 `fastModeEnabled/fastModeActive`를 `session/info.fastMode`로 보낸다.
- 가능 여부는 모델의 공급자 계열로 정한다(`PompSession.HasServiceTierFamily`). 최종 판단은 omp다.
  - openai, openai-codex, google, google-vertex
  - `api=anthropic-messages`
  - 그 계열의 openrouter 모델

#### 화면 언어

- 옵션 `Language`(자동/한국어/English)를 `PompLanguage.Choice`에 둔다. Contracts에 있고 프로세스 전역이다.
- 한/영 문구를 고르는 모든 호스트 코드는 `PompLanguage.Pick`/`IsKorean`을 쓴다. 설정 문구는 `SetupText.Get`이다.
- 실제 로캘이 바뀌면 세션이 `host/hello`를 다시 보낸다. 페이지는 엔진 재시작 없이 언어를 바꾼다.
- 이미 보낸 호스트 문구는 그때의 언어로 남는다.

#### 사용량

실행:

- `listUsage`는 `omp usage --json`을 엔진과 같은 런처로 숨겨서 실행한다. 창 없음, stdout/stderr 리디렉션, `UseShellExecute=false`.
- 작업 폴더·환경은 도우미와 같다. 다만 자리표시자(`--api-key`·닫힌 `OPENAI_BASE_URL`)는 없다.
- 45초 또는 출력 2 MiB를 넘으면 프로세스를 끝낸다.
- 한 번에 하나만 돈다(`usageLoading`). 명령 응답은 기다리지 않는다.
- 실패는 `usage.error`(고정 문구)로 알리고 이전 숫자는 남긴다.
- stderr는 URL 쿼리를 지워 debug 로그에만 남긴다.

파싱:

- 출력은 `{generatedAt, reports, accountsWithoutUsage, disabledCredentials, capacity}`이다. `reports`만 방어적으로 읽는다.

| 항목 | 규칙 |
|---|---|
| 상한 | 보고서 64개, 한도 32개, 문자열 200자, 총 256 KiB |
| 문자 | 제어·bidi 문자 제거 |
| 시각 `window.resetsAt` | 에포크 밀리초·초, 숫자 문자열, ISO를 받아 ISO 8601 UTC로 |
| `usedFraction` | 0..1. 없으면 `remainingFraction`·`used/limit`에서 계산 |
| 상태 | `ok/warning/exhausted/unknown`. 그 밖은 `unknown` |
| 버림 | 깨진 항목, 한도가 없는 보고서 |

계정 라벨 우선순위:

1. 이메일(`metadata.email`·`email`)
2. `account`·`metadata.account/username/accountId`·`accountId`
3. 한도의 `scope.accountId`

## 5. 호스트 툴 (CustomToolRegistry)

| 툴 | 권한 | 어댑터 | 구현 상태 | omp에 제공 |
|---|---|---|---|---|
| `get_editor_context` | 없음 | Editor, TextBuffers | 구현 | ✔ |
| `read_buffer` | 없음 | TextBuffers (저장 안 된 버퍼 우선) | 구현. `[path#TAG]` + `N:TEXT` 출력 | ✔ |
| `edit_buffer_hashline` | fileWrite (정확한 Diff 미리보기) | TextBuffers | 구현. 아래 "준비된 변경과 승인" 참고 | ✔ |
| `query_solution` | 없음 | Solution | 구현 | ✔ |
| `add_project_file` | projectChange (새 파일 전체 Diff) | Solution | 구현. 아래 "`add_project_file`" 참고 | ✔ |
| `remove_project_file` | projectChange | Solution | 영구 비활성. 파일 삭제는 omp 자체의 승인 게이트가 있는 `delete` 툴을 따른다. | ✘ |
| `rename_project_file` | projectChange | Solution | 준비된 프로젝트 항목 변경, 기존 파일·버퍼 보호 | ✔ |
| `get_msbuild_properties` | 없음 | Solution | 평가된 프로젝트 속성 조회 | ✔ |
| `read_output_window` | 없음 | Output | 출력 창 조회 | ✔ |
| `manage_git` | 조회는 없음, commit·push는 vcsWrite | Git | Git CLI 래핑, 변경은 승인 후 수행 | ✔ |
| `manage_tests` | list·run 모두 testRun | Tests | 탐색도 빌드·어댑터를 실행할 수 있으므로 승인 필요 | ✔ |
| `manage_breakpoints` | breakpointChange | Debugger | 추가/제거/활성/비활성, WhenTrue 조건·추적점 | ✔ |
| `control_debugger` | debuggerControl | Debugger | 디버거 실행·단계·중지 제어 | ✔ |
| `add_bookmark` | bookmarkChange | Bookmarks | 열린 문서 책갈피 추가 | ✔ |
| `read_error_list` | 없음 | ErrorList | 구현 | ✔ |
| `build_solution` | processExec. MSBuild target은 임의 스크립트·프로그램을 실행할 수 있다. | Build | 구현. 아래 "`build_solution`" 참고 | ✔ |
| `manage_nuget` | 조회는 없음, 설치·업데이트는 packageChange | Solution | VS NuGet MEF 서비스. 아래 "NuGet 변경 수명주기" 참고 | ✔ |
| `list_breakpoints` | 없음 | Debugger | 구현 | ✔ |
| `get_debug_state` | 없음 | Debugger | 구현 | ✔ |
| `list_bookmarks` | 없음 | Bookmarks | 열린 문서 기준 구현 | ✔ |

### 툴별 세부

`edit_buffer_hashline`:

- 준비한 변경을 승인 후 그대로 적용한다.
- 파일별 Undo 1단계, 저장하지 않는다.
- 다중 파일은 부분 적용될 수 있고, 그 경우 보고한다.

`add_project_file`:

- C#/VB MSBuild 프로젝트, 새 파일만(UTF-8, BOM 없음).
- 준비 단계에서 거부하는 경로: 앞뒤 공백, MAX_PATH 초과(파일 259자, 폴더 247자).
- 쓰기 전 실제 경로를 확인하고 `IVsProject.AddItem`으로 등록한다.
- 실패 시 남은 파일을 보고한다.

`build_solution`:

- 구성은 `Name` 또는 `Name|Platform`이다.
- 플랫폼을 생략하면 활성 플랫폼을 유지한다. 빌드 후 원래 구성을 복원한다.
- 오류 목록은 100개까지 보내고, 합계는 전체를 센다.

`manage_nuget`:

- 승인 전 프로젝트·패키지 상태를 고정하고 변경 직전에 다시 검사한다.
- 부분 실패를 보고한다.

### 등록, Diff 표시, 오류 기록

- 제공하지 않는 툴은 **omp에 등록하지 않는다**. 목록은 `HostToolCatalog.NotImplemented`이고 현재 `remove_project_file`만 있다.
- 목록에서 빼는 것만으로 `set_host_tools`·오버레이·`POMP_HOST_TOOLS`·omp 측 안내문에 함께 반영된다.
- 다른 툴 설명도 제공되지 않은 툴 이름을 언급하지 않는다.
- 권한 대화상자의 Diff는 `edit_buffer_hashline`(수정)과 `add_project_file`(새 파일 전체)이 만든다.
- 긴 Diff는 처음 1,500줄만 그린다. "모두 보기"를 누르면 전체를 보여 준다.
- 전체가 표시되기 전에는 허용 계열 버튼이 비활성이다. 거부는 항상 가능하다.
- Diff·대상·메시지 안의 bidi/제로폭 제어 문자는 동작하지 않도록 `⟨U+202E⟩` 같은 표시로 바꿔 보여 준다.
- 어댑터의 예기치 않은 예외(주로 VS의 COMException)는 HRESULT와 함께 경고로 기록된다. ActivityLog에도 남는다.
- 같은 작업의 경고는 30초에 한 번만 기록한다.
- 인수 오류·미구현·취소 같은 예상된 결과는 기록하지 않는다.

### 버퍼 TAG와 텍스트 규칙

- `read_buffer`/`edit_buffer_hashline`의 TAG는 omp hashline과 모양이 같다(대문자 16진수 4자리).
- 하지만 **호스트 전용**이다. 정규화된 전체 내용의 SHA-256에서 파생하고, 호스트 스냅샷 저장소로 검증한다.
- omp 내장 `edit`와 섞어 쓰지 않는다.

| 규칙 | 내용 |
|---|---|
| 해시 | CRLF와 단독 CR을 LF로 바꾼 UTF-8 텍스트로 계산한다(`BufferSnapshot.ContentHash`). 툴과 VS 어댑터가 같은 규칙을 쓴다. |
| 줄 나눔 | VS 편집기와 같이 CRLF·CR·LF 및 U+0085/U+2028/U+2029에서 나눈다. |
| Unicode 구분자 | 내용 해시에 그대로 남겨 stale 검출을 유지한다. |
| 승인 Diff의 구분자 | ⟦U+XXXX⟧로 표시한다. 문자 자체의 역슬래시/표시 괄호는 이스케이프한다. 구분자만 달라지는 변경도 숨기지 않기 위해서다. |
| 패치 문법 | 줄 구분자를 LF로 정규화한다. |
| 디스크 읽기 | BOM(UTF-8/16/32) → 엄격한 UTF-8 → 시스템 ANSI 코드 페이지(예: CP949) 순으로 해석한다. |
| 크기 | 16 MiB를 넘는 파일은 읽지 않는다. |

### 준비된 변경과 승인 (계약)

#### 준비

- 변경 툴은 `IPreparedHostTool.PrepareAsync`에서 경로·인수를 검증한다.
- 결과는 정확한 변경 내용과 Diff를 가진 호출별 `IPreparedHostToolCall`이다.
- 준비 단계는 읽기만 한다. 파일/버퍼를 바꾸지 않는다.
- 디스패처는 기억된 허용 규칙이 있어도 준비를 먼저 한다.
- 준비 실패나 누락된 권한 요청은 일반 승인으로 대체하지 않고 실행을 거부한다.
- 승인 요청의 source/tool/kind는 디스패처가 원래 정책으로 고정한다.

#### 실행

- 승인 뒤에는 **같은 준비 객체**를 실행한다. 원본 JSON 재해석이나 전역 준비 캐시는 쓰지 않는다.
- 거부·취소·실패·성공 모두 호출별 객체를 Dispose한다.
- 실행은 승인된 불변 값만 쓴다. 권한 UI가 갖는 DTO의 변경을 실행 입력으로 쓰지 않는다.

#### hashline 편집

- 모든 파일의 전체 해시를 먼저 재검증한다.
- 실제 버퍼 적용 때도 UI 스레드에서 해시 검사와 편집을 함께 한다.
- 파일별 Undo만 보장하고 저장하지 않는다.
- 다중 파일 전체의 원자성은 보장하지 않는다. 부분 적용 시 적용된 파일을 보고한다.
- 어댑터에는 원본 해시와 함께 승인한 결과의 해시(`BufferEditRequest.ExpectedResultContentHash`)를 보낸다.
- VS 어댑터는 자기 편집 계획이 만들 결과를 버퍼를 바꾸기 전에 계산한다. 다르면 아무것도 바꾸지 않는다. 그래서 승인한 Diff와 실제 적용이 어긋나지 않는다.

#### 본 줄 기준

- 편집 기준은 모델이 **완전히 본** 줄뿐이다.
- `read_buffer`/`get_editor_context`가 출력 한도에서 잘라 보여 준 줄은 본 줄로 기록하지 않는다.
- 편집 결과에는 새 TAG를 주지만 새 줄은 본 것으로 치지 않는다. 이어서 줄 기준 편집을 하려면 다시 읽어야 한다.
- 같은 경로에 남은 서로 다른 버전이 같은 4자리 TAG를 가지면 모호한 것으로 보고 거부한다.

#### 파일 추가

- 승인 전 고정한 프로젝트 identity/루트/대상/내용을 실행 때 재검증한다.
- 새 파일만 생성해 VS 프로젝트 시스템에 등록한다.
- 거부: 기존 파일 덮어쓰기, 루트 이탈, reparse point.
- 작업 루트부터 대상 폴더까지 기존 폴더를 모두 읽기 공유로만 열어 둔다. 이름 변경·삭제·쓰기 열기를 막은 채 새 폴더를 만든다.
- 새 파일은 만든 직후, **내용을 쓰기 전에** 핸들의 실제 경로가 프로젝트 폴더 아래인지 확인한다. 정션·링크로 바뀌었으면 쓰지 않고 오류다.
- 등록 중에는 같은 파일(파일 ID)을 읽기 전용으로 붙잡고, 승인한 바이트와 같은지 확인한다.
- 등록 확인은 마지막 대기 뒤에도 다시 한다.
- 등록 실패 후 다른 주체가 바꾼 파일은 삭제하지 않는다.
- 경로를 동시에 바꾸는 로컬 프로세스가 있으면 빈 파일이 작업 폴더 밖에 생길 수는 있다. 내용은 쓰지 않고 오류로 보고한다.

#### omp 프로세스별 응답

- omp에 보내는 응답(툴 결과·진행, 대화상자·승인 답)은 **요청을 보낸 omp 프로세스에만** 보낸다.
- 그 프로세스가 끝났으면 버린다. 재시작한 omp가 같은 id를 다시 써도 섞이지 않는다.
- 끝났거나 종료 중인 프로세스의 버퍼에 남은 프레임(툴 호출·질문·스트리밍 이벤트)은 전달하지 않는다.
- 정상 종료 때 남은 출력을 읽는 동안의 마지막 프레임은 그대로 전달된다.

#### 브릿지 수명

- 재연결 중(이전에 Running이었던 주기)의 `StartAsync`는 새 핸드셰이크가 끝날 때까지 기다린다. 실패하면 예외, 중지되면 취소다.
- 호출자가 기다리기를 포기한 중지 뒤의 시작은 이전 주기가 끝난 뒤(ConnectionEnded 이후)에 새 omp를 띄운다.
- `get_state` 응답은, 요청 뒤에 agent_start/end를 받았으면 스트리밍 여부를 바꾸지 않는다.

## 6. 권한 결정

### 삭제·격리 경계 (결정)

- omp에는 OS 샌드박스가 없다.
- 안전 경계는 다음으로 이루어진다:
  - read/write/exec 승인 계층
  - 기본 `--approval-mode always-ask`
  - 도구별 `tools.approval` allow/prompt/deny
  - bash 패턴 규칙(치명적 파괴 명령 기본 차단)
- POMP의 `remove_project_file`은 `HostToolCatalog.NotImplemented`에 영구적으로 남긴다.
- 파일 삭제·이동은 omp 자체의 승인 게이트가 있는 `delete`·`move` 툴을 따른다.
- 실험적 AppContainer 실행기는 기본 꺼짐을 유지하고, 샌드박스로 안내하지 않는다.

### 승인 답과 규칙 키

| 답 | 범위 |
|---|---|
| `허용` | 이번만 |
| `세션 동안 허용` | 메모리 |
| `항상 허용` | 설정에 저장. 도구 › 권한이나 옵션 페이지에서 해제한다. |
| `거부` | 메모를 붙일 수 있다. |

- 키는 `host:<툴>` / `omp:<툴>`이다. **해당 툴 전체에 적용**되며 파일별 허용이 아니다.
- `항상 허용`은 **작업 영역별**로 저장한다: `<키>@<작업 루트>`.
- 루트의 `%`·`;`·`,`·공백·제어 문자는 `%XX`로 이스케이프한다.
- 다른 작업 영역에서는 다시 묻는다.
- 작업 영역이 붙지 않은 예전 키는 더 이상 답하지 않는다. 목록에 "이전 형식"으로 보여 철회할 수 있다.
- 작업 루트를 모르면 `항상 허용`도 세션 동안만 적용한다.
- 호스트 툴의 게이트는 omp의 `write`/`yolo` 모드와 별개다. 이미 부여한 세션/영구 허용 규칙은 재질문을 생략한다.
- 호스트 툴 호출의 정책 키는 omp가 호출한 툴 이름으로 고정한다. 툴이 스스로 다른 이름을 적어도 무시한다.

### 매번 확인하는 요청

`PermissionGate.IsOnceOnly`가 판단한다. 기억된 규칙으로 답하지 않고, 기억형 답도 저장하지 않는다.

- 실행(`ProcessExec`): omp의 bash·eval·python 등, `build_solution`
- Git 쓰기(`VcsWrite`)
- 패키지 변경
- 디버거 제어. omp의 `debug`도 포함한다. 프로그램을 시작하고, 디버그 어댑터를 작업 영역의 도구 폴더에서 찾을 수 있기 때문이다.
- 테스트 실행
- omp의 `task`. 하위 에이전트는 승인 없이 돌므로 task 승인이 그 안의 작업 전체를 승인한다.
- `computer`·`browser` 제어
- 프로젝트 정책(`.pomp/permissions.json`)을 바꾸는 요청
- omp가 작업 영역에서 읽는 엔진 구성(`.omp/`, `mcp.json`, `.claude/settings` 등)을 바꾸는 요청

목적: 기억된 편집 허용과 실행·설정 파일 변경이 이어져 확인 없는 실행이 되는 경로를 막는다.

### 작업 영역 전환

- 솔루션·폴더를 열거나 닫으면 즉시 세션 허용을 지운다.
- omp가 새 루트에서 다시 시작하거나 루트가 그대로임을 확인할 때까지 호스트 툴 호출과 omp 승인을 거부한다.
- 이전 작업 영역의 omp가 호출한 호스트 툴이 VS의 새 솔루션에 적용되지 않게 하기 위해서다.

### 응답 검증

- 타임아웃·취소·잘못된 승인 응답은 거부한다.
- 응답에는 정확한 문자열 결정 값이 필요하다. 누락이나 숫자 enum은 허용하지 않는다.
- 응답하지 않는 프롬프트 구현이 있어도 게이트는 취소·시간 초과 시점에 기다림을 멈추고 거부한다.

### 세션 허용의 범위

- 범위는 omp 프로세스 하나, 곧 omp 세션 하나다.
- 지우는 시점: 새 omp 프로세스가 연결될 때(재시작·충돌 후 재연결), 새 대화(`session/new`).
- `session/new`는 omp가 기다리는 승인 프롬프트를 먼저 거부로 풀어 준다.
- 초기화 전에 열린 대화상자의 "세션 동안 허용" 답은 그 호출에만 적용된다. 새 세션의 허용으로 남지 않는다.

### 강제 프롬프트

- omp는 두 경우에 프롬프트에 표시를 붙인다:
  - 도구 자체 정책이 요구하는 확인(예: 위험한 명령): `Reason:` 줄
  - 모델 제공자가 보낸 computer-use 안전 점검: `Provider safety checks:` 절
- 이 둘은 yolo나 허용 규칙과 무관하게 묻는다.
- 이런 프롬프트는 기억된 규칙으로 답하지 않는다(`canRemember=false`).
- 사용자의 "세션 동안/항상 허용" 답도 규칙으로 저장하지 않는다.
- UI는 "이번만 허용"과 "거부"만 표시한다. 호스트도 기억형 답을 "이번만"으로 낮춘다.

### 거부 메모와 호스트 거부

- 거부 메모는 호스트 툴 거부 시에만 모델에 전달된다.
- omp 승인 프롬프트에는 메모를 전할 통로가 없어 로그에만 남는다. UI는 omp 승인에서 메모 입력란을 숨긴다.
- **호스트가 스스로 한 거부**는 사용자의 답이 아니다: 시간 초과, 취소, UI 전달 실패, 승인 대기 한도 초과, 잘못된 응답.
- 이것은 `PermissionResponse.SystemReason`으로 구분한다.
- 모델에는 "사용자가 거부함"이나 사용자 메모가 아니라 호스트 거부와 그 사유로 전달한다.

### 경로와 컨텍스트 경계

- 모델이 지정하는 `read_buffer` 등의 경로는 작업 폴더 내부로 제한한다.
- 루트 아래의 기존 reparse point(junction/심볼릭 링크 등)를 거부한다.
- 사용자가 선택한 작업 루트 자체와 IDE가 제공하는 현재 문서 컨텍스트는 신뢰한다.
- 검사와 사용 사이의 경로 교체나 omp 내장 셸 도구까지 통제하는 파일시스템 샌드박스는 아니다.
- pre-edit 훅도 상태 파일 누락/읽기 실패 시 fail-open이다. bash·MCP 등을 통한 모든 변경을 막지는 않는다.
- 프롬프트 앞에 붙이는 `<vs_context>` 블록 안의 파일 내용·선택 영역·오류 설명은 데이터로 취급한다.
- 그 안의 `<vs_context`/`</vs_context` 표기는 `&lt;`로 바꾼다. 블록을 조기에 닫고 지시문처럼 보이게 만들 수 없게 하기 위해서다.

## 7. 알려진 쟁점 / 후속 결정

### 기본 단축키

| 명령 | 범위 | 기본 키 |
|---|---|---|
| `Pomp.InlineEdit` | 텍스트 편집기 | **Alt+M** |
| 코드 동작 | 텍스트 편집기 | **Shift+Alt+E/R/F/U** |
| 파일 추가 | 텍스트 편집기 | **Shift+Alt+I** |
| `Pomp.KnowledgeTag` (지식에 추가) | 텍스트 편집기 | **Alt+/** |
| `Pomp.ToggleFocus` | 전역 | **Alt+J** |
| POMP 열기 | 전역 | **Shift+Alt+Q** |
| 새 대화 | 전역 | **Shift+Alt+N** |

- 모두 한 번에 누르는 조합이다. 두 단계 코드(chord)는 쓰지 않는다.
- VSCT에는 바인딩을 두지 않는다. 도구 > 옵션 > Pomp > 일반 > 단축키 값을 시작·적용 때 DTE로 VS 키 바인딩에 넣는다(`VsKeyBindings`).
- 실제 VS 2019 바인딩 표에서 이 조합들은 전역·텍스트 편집기 범위 모두 미사용이다.
- 피하는 조합: VS가 같은 범위에서 이미 쓰이는 확장 바인딩을 조용히 버리기 때문이다.
  - Ctrl+K: 주석·서식 등의 코드 시작 키
  - Ctrl+L: 줄 잘라내기
  - Ctrl+Alt+K: 호출 계층 보기(`View.CallHierarchy`)
  - Ctrl+Alt+L/J/Q: 이미 쓰인다.

WebView2 안의 키:

- WebView2 안의 키는 VS 명령 라우팅에 가지 않는다.
- 페이지가 `host/hello.shortcuts.toggleFocus`의 조합(기본 **Alt+J**)을 직접 잡는다. Meta·AltGr는 제외하고, IME 조합·키 반복 중에는 무시한다.
- 페이지는 호스트 내부 메시지 `ui/toggleFocus`로 알리고, 호스트가 편집기로 포커스를 돌려준다.
- 채팅 페이지 자체의 단축키는 도구 > 옵션 > Pomp > 일반 > 단축키에서 바꾸며 `host/hello.shortcuts`로 페이지에 전달된다(docs/settings.md "단축키").
  - 전송 키 Enter/Ctrl+Enter
  - 보낼 대상 고르기 Ctrl+Alt+T
  - 권한 모드 고르기
  - 승인 카드 허용·거부 Alt+A/Alt+D
  - 도구 › 권한 Ctrl+Alt+P

### 메뉴 배치

- 편집기 우클릭에서는 **POMP에게 질문 → POMP** 순서로 둔다.
- 이 메뉴가 여는 질문 창은 채팅 질문(Enter)과 수정 제안(Ctrl+Enter)을 함께 한다. 고칠 수 없는 파일에서는 수정 제안이 꺼진다.
- POMP 하위 메뉴 순서는 다음과 같다. 질문을 안에 중복하지 않는다.
  1. 코드 동작(설명·리뷰·고치기·테스트 작성)
  2. 컨텍스트(파일 추가)
  3. 에이전트·팀에게
  4. 채팅(열기·새 대화·포커스 전환)
  5. 켜기/끄기(자동완성·코드 캡션)
- 도구 메뉴에는 POMP 하위 메뉴를 두지 않는다. 모든 명령이 편집기 우클릭·채팅 창에 있어 중복이었다.
- Live·북마크 켜기/끄기는 메뉴 없이 단축키용 명령으로만 남는다. 채팅 입력창 위 아이콘과 같다.
- POMP 창은 VS 관례대로 보기 > 다른 창 > POMP로 연다.

### 책갈피

- 공개 API가 없다. 열린 문서의 `MARKER_BOOKMARK` 마커를 열거하는 방식으로 시작했다(열린 문서 한정).

### 검증 환경과 범위

- 실행 검증은 이 개발 PC의 VS 2019 16.11 실험 인스턴스(`/rootSuffix Exp`)에서 한다.
- VS 2026 패키지는 VS 2026 실험 인스턴스에 설치해 연결·메뉴를 확인한다.
- VS 2022 패키지는 빌드·패키지 내용만 확인한다. VS 2022가 설치되어 있지 않다.
- VS 2019 16.0~16.9는 현재 매니페스트 대상이 아니다. 하위 16.x 지원에는 SDK/API 재검증이 필요하다.
- 컴파일 성공은 IDE에서의 패키지 로드나 실제 모델 제공자 응답을 검증한 것과 다르다.
- 초기 Debug 패키지 하나는 VS 2026 실험 인스턴스에서 로드된 기록(ActivityLog)이 있다. 현재 빌드는 VS 안에서 실행해 보지 않았다.
- 버퍼 편집(Undo 1단계, 소스 제어의 편집 거부)과 파일 추가(레거시/CPS 프로젝트 등록)는 가짜 어댑터·격리 하네스로만 검증했다.

### 패키지 크기

- omp는 모든 패키지에 고정 버전으로 들어간다(§4 "엔진 버전").
- 엔진 실행 파일이 약 229 MiB라 Release VSIX가 약 117 MB가 된다.
- Marketplace 업로드 크기 한도는 게시 전에 확인한다.

### 툴 등록

- POMP의 프로젝트 파일 삭제 툴(`remove_project_file`)은 영구 등록 제외다.
- 남아 있는 recycle 구현의 Windows 안전성 검증 여부와 무관하게 활성화하지 않는다.
- 파일 삭제는 omp 자체의 승인 게이트가 있는 `delete` 툴을 쓴다.
- NuGet을 포함한 나머지 구현된 툴은 기본 등록에 통합했다.

### 편집 요청과 대화상자

- 편집 요청에는 승인할 때의 작업 루트를 담는다.
- VS 어댑터는 문서를 열기 전과 UI 스레드의 편집 직전에, 그것이 여전히 현재 작업 루트인지 확인한다. 현재 작업 루트는 열린 솔루션 루트, 없으면 대체 작업 폴더다.
- 편집기 캐럿·선택 위치는 편집기 뷰와 DTE 경로 모두 CR/LF 기준 줄로 준다.
- 일반 대화상자 초안은 승인 요청이 먼저 표시되어도 유지한다.
- 입력란·거부 메모는 한도를 넘으면 원문을 남기고 오류를 표시한다.
- omp 승인/대화상자 응답은 인코딩된 NDJSON 크기도 검사한다.
- 한국어 IME 조합 중 Enter는 대화상자를 제출하지 않는다(`isComposing` 및 legacy keyCode 229).

### 설계 대비 미구현·편차 (후속 작업)

| 설계 항목 | 현재 | 후속 |
|---|---|---|
| 입력창 멘션(@파일)·파일 첨부 | 구현. 요청 ID로 늦은 결과 무시, 경로 중복 제거, 개수 제한 없음 | 실제 WebView2 검증 |
| Git·테스트·출력 창·MSBuild 속성 툴 | 구현·기본 등록. 테스트 탐색에도 승인 요구 | 실제 VS 검증 |
| 권한 대화상자 Diff 미리보기 | `edit_buffer_hashline`(수정)·`add_project_file`(새 파일 전체)이 생성. inline/나란히 보기 구현. 두 모드 모두 전체 Diff 확인 후 승인 | 실제 WebView2 검증 |
| 컨텍스트 압축 "최근 3턴 원문 유지" | omp 자체 압축(토큰 기준)에 맡기고 호스트는 알림만 표시 | T10에서 재검토 |
| 1만 줄 이상 파일의 심볼 요약 | `read_buffer outline=true`, 큰 파일 에디터 컨텍스트 자동 요약 구현 | 성능 측정 재검증 |
| 토큰 미터 | 메시지가 끝날 때마다 `get_state`로 갱신. 스트리밍 중 실시간은 아님 | — |
| 네트워크 접근 설정·로그 | Block omp network tools 옵션·호출 로그 구현, 기본 허용 | 셸/MCP까지 통제하는 샌드박스 아님 |
| 주기적 하트비트 | 30초 무출력 후 get_state, 3회 무응답 후 재시작 | 실제 프로세스 검증 |
| 사용 지표(metrics) | 로컬 usage-metrics.json 구현, 외부 전송 없음 | 실제 VS 검증 |
| 라이선스, Marketplace 메타데이터 | POMP는 PolyForm Noncommercial 1.0.0, 번들 omp는 MIT. Publisher `deanpinb`, 무료 게시, 공개 저장소 URL 생략 | 실제 설치·Marketplace 업로드 확인 |
| 대화 복구 | ui/ready마다 호스트의 현재 세션 로그 재생 | 프로세스 종료 후 영구 보관 아님 |

### NuGet 변경 수명주기 (T12)

#### 조회와 준비

- `manage_nuget list`는 승인 없이 설치된 패키지를 조회한다.
- 변경은 `IPreparedHostTool`과 `IPreparedPackageChange`를 쓴다.
- 준비 중에는 다운로드나 프로젝트 변경이 없다.
- 준비 때 캡처하는 것: 대상 프로젝트의 경로·hierarchy identity·solution generation, 설치 패키지 지문.
- 승인 후 작업 루트·프로젝트 identity·패키지 지문을 재확인한다.
- 작업 입력은 승인 DTO와 분리된다. 준비된 호출은 1회만 실행되며, 거부·취소·완료 시 Dispose된다.

#### NuGet API 사용

- 호환성 때문에 VS 16.10/17.0 기준 `NuGet.VisualStudio` MEF 인터페이스를 쓴다.
- DTE 객체 조회와 MEF 서비스 취득은 UI 스레드에서 한다.
- NuGet이 자체 마샬링하는 동기 API는 백그라운드에서 호출한다.
- `GetInstalledPackages`의 obsolete 경고는 해당 파일에서 호환성 이유로 한정 해제했다. VSTHRD 분석기는 유지한다.
- 패키지 버전은 NuGet의 `IVsSemanticVersionComparer`로 검증·결과 비교한다.
- 최신 안정 버전 설치는 `IVsPackageInstaller2.InstallLatestPackage`를 쓴다.
- uninstall은 의존 패키지를 제거하지 않는다.
- 공식 API 근거: <https://learn.microsoft.com/en-us/nuget/visual-studio-extensibility/nuget-api-in-visual-studio>

#### 동시성과 실패

- 변경은 호스트 안에서 직렬화한다.
- NuGet API에는 취소 토큰이 없다. 진입 전까지만 취소하고, 시작 후에는 실제 반환을 기다린다.
- 작업 실패는 부분 변경 가능성을 명시한다.
- 외부 사용자/확장이 동시에 변경하는 경우 NuGet의 프로젝트 시스템 조정에 의존한다.
- 하지 않는 것: .csproj 직접 편집, 패키지 소스 설정 변경, 강제 롤백.
- 실제 VS 동작은 별도 검증 대상이다.

### 네이티브 CodeLens (선택)

- 별도 공급자 assembly를 CodeLens 서비스에서 로드한다.
- 공급자는 VS 안의 callback으로 적용된 옵션만 읽는다.
- `Pomp.CodeLens.Contracts`의 위치 DTO를 native details view로 전달한다.
- 사용자가 요약 버튼을 눌러야 기존 inline assistant 경로를 호출한다.
- 자동 표시/prefetch는 모델을 호출하지 않는다.
- 양쪽 VSIX가 provider와 shared contract를 참조하며 CodeLensComponent 자산을 등록한다.
- 공급자는 `CSharp`·`Basic` 콘텐츠 형식에만 등록한다. CodeLens 데이터 지점은 언어의 CodeLens 태거가 만든 descriptor에만 붙는다. Roslyn은 모든 형식·멤버에 만들지만, VS 2019의 C++ 언어 서비스(`CppCodeLensTaggerProvider`)는 단위 테스트에만 만든다. 그래서 다른 언어 파일은 옵션이 켜져 있어도 POMP 장식 캡션(`CodeCaptionFeature`)을 쓴다.

### 편집기 기능의 언어 범위

- 인라인 자동완성·질문 창(`InlineEditorFeature`)과 POMP 장식 캡션(`CodeCaptionFeature`)은 `code` 콘텐츠 형식에 등록한다. 레거시 언어 서비스의 형식(`C/C++` 등)은 VS 편집기가 `code`를 기반으로 만들고(`LegacyContentTypeSource`), TextMate 언어의 `code++`도 `code`를 기반으로 한다. 언어를 거르는 코드는 없다. 언어 이름(`ContentType.TypeName`)은 모델 프롬프트에만 들어간다.
- 캡션 대상은 `EditorCaptionSource`가 정한다. Roslyn 문서(C#, VB)는 구문 트리, 그 밖의 파일은 `Pomp.Tools`의 `SourceDeclarations`다. 이것은 `read_buffer` 개요와 같은 어휘 스캐너(`SymbolOutline`)이고 C·C++, TypeScript·JavaScript, C#을 읽는다. 대상의 줄·시작·길이는 선언 첫 토큰부터 본문의 닫는 중괄호나 끝 `;`까지다. 이름공간과 본문 없는 형식 선언(`class Foo;`)은 대상이 아니다.
- 질문 범위(`CodeScopeSelection`)도 Roslyn 문서가 없으면 같은 스캐너로 가장 안쪽 멤버를 고른다.
- 변경 제안의 컴파일 검사(`RoslynShadowValidator`)는 Roslyn 문서만 컴파일한다. 그 밖의 파일은 오류가 아닌 메모 `컴파일러 검사 없음 (Roslyn 문서 아님)`을 달고 검사를 끝낸다. 그래서 C++ 파일의 제안도 적용할 수 있다.

## 8. 에이전트·팀 구현 계약

이 장은 에이전트·팀 기능의 구현 계약이다. 바꿀 때는 이 장을 먼저 고친다.
사용 안내는 `docs/agent-teams.md`에 있다. 메시지 형식은 `docs/ui-protocol.md`의 "Agents and teams"와
"Conversation partners, command queue and agent run history"에 있다. 이 장은 그 두 곳과 §4 "에이전트·팀과 컴퓨터 사용"에 없는 규칙을 모은다.

### 8.1 역할 파일

**탐색 순서**

| 순서 | 위치 |
|---|---|
| 1 | `~/.omp/agent/agents/*.md` (사용자) |
| 2 | `<작업 영역>/.pomp/agents/*.md` (프로젝트) |
| 3 | 확장 패키지의 `agents/` |
| 4 | 엔진 내장 역할: task, sonic, scout, reviewer, security-reviewer |

- 이름이 겹치면 앞의 것이 이긴다. `Role.scope`는 `project`, `user`, `bundled`, `extension` 중 하나다.
- `.claude/agents`와 `.agents/`는 읽지 않는다.
- 엔진은 `task`를 실행할 때마다 역할 파일을 다시 읽는다.
- 프로젝트 역할은 `.pomp/local/engine-plugin/agents/`에 복사해 omp `--plugin-dir`로 넘긴다.
- omp는 플러그인 폴더를 사용자 폴더 다음에 읽는다.
- `.pomp`를 그대로 넘기면 `.pomp/skills`와 `.pomp/commands`까지 엔진 스킬·명령으로 읽힌다. 그래서 역할 파일만 복사한다.
- 이전 버전이 `<작업 영역>/.omp/agents`에 둔 역할은 처음 쓸 때 옮긴다. 같은 이름이 이미 있으면 옮기지 않는다.
- `.pomp/agents/*.md`는 `/recipe`로도 불러올 수 있다.

**앞머리(YAML)**

| 키 | 규칙 |
|---|---|
| `name` | 필수. 영문 소문자·숫자·`-`, 2~40자. `main`, `sub`는 금지 |
| `description` | 필수. 1,000자까지. 모델이 이 에이전트를 언제 쓸지 판단하는 글 |
| `pomp-title` | 40자까지. 화면 표시 이름이고, 없으면 `name`을 보인다 |
| `pomp-job` | 80자까지. 맡은 일 |
| `model` | `provider/id` 또는 `@smol` 같은 역할. 목록이면 대체 순서다. 생략하면 채팅 모델 |
| `thinking-level` | 생략하면 자동. 그 밖에 낮음·보통·높음·최대 |
| `tools` | 도구 목록. 주면 엔진이 `yield`를 자동으로 더한다. 생략하면 모든 도구 |
| `spawns`, `output`, `blocking`, `autoloadSkills` 등 | 엔진 키. 편집 창은 쓰지 않고 그대로 보존한다 |

- `pomp-`로 시작하는 키는 POMP 화면용이다. 에이전트 실행에는 쓰이지 않는다.
- 엔진은 알 수 없는 키를 무시한다. POMP는 편집할 때 알 수 없는 키를 그대로 둔다.
- 엔진 내장 역할 이름은 그 역할을 편집해 만든 파일만 쓴다.

**제한과 매핑**

| 항목 | 규칙 |
|---|---|
| 지시 본문 | 24,000자 |
| 파일 | 64 KiB |
| 역할 개수 | 제한 없음. 목록이 화면 메시지 하나를 넘으면 앞부분만 보내고 빠진 수를 알린다 |
| 도구 범위 `all` | `tools` 키 없음 |
| 도구 범위 `readOnly` | `read, find, grep, glob, lsp, yield`와 VS 도구 중 읽기 전용 |
| 도구 범위 `custom` | 그 밖의 목록. 화면은 "직접 지정 도구"로 보이고 VS에서 고친다 |
| 모델 선택 | 채팅 모델과 같게는 키 생략, 빠른 모델은 `@smol`, 특정 모델은 계정 모델 목록에서 검색 |

새 역할 템플릿은 설계자, 구현자, 리뷰어, 테스터, 문서 작성자, 탐색자다.

**쓰기**

- 사용자가 저장을 누를 때만 쓴다. 쓰기는 원자적이다.
- 대상 폴더 밖 경로, 링크, 재분석 지점은 거부한다.
- 다른 곳에서 바뀐 파일은 `expectedHash`가 다르면 거부한다. 페이지는 다시 읽고 저장한다.
- 삭제는 화면에서 확인을 받은 뒤 한다.
- POMP가 만들지 않은 파일도 편집·삭제할 수 있다.

**엔진 내장 역할 편집**

- 편집 창은 엔진이 정의한 내용으로 열린다. 지시문·도구·모델은 `omp agents unpack`으로 받는다.
- 저장하면 같은 이름의 역할 파일이 생기고, 엔진은 내장 역할 대신 그 파일을 쓴다.
- 편집 창이 다루지 않는 출력 형식, `spawns` 등은 엔진 정의를 그대로 옮겨 적는다.
- 그 파일을 삭제하면 내장 역할로 돌아간다.

### 8.2 팀 파일과 팀 실행

**파일**

| 범위 | 위치 |
|---|---|
| 이 프로젝트 | `<작업 영역>/.pomp/teams/<id>.json` |
| 내 모든 프로젝트 | `%APPDATA%\Pomp\teams\<id>.json` |

```json
{ "version": 1, "id": "feature-dev", "title": "기능 개발팀", "goal": "요청한 기능을 설계·구현·검토·테스트까지",
  "members": ["architect", "implementer", "reviewer-strict", "tester"], "mode": "lead",
  "instructions": "공통 지시", "membersOnly": true, "isolation": false }
```

| 키 | 규칙 |
|---|---|
| `id` | `name`과 같은 규칙 |
| `title` | 40자까지 |
| `goal` | 400자까지 |
| `members` | 역할 `name` 2개 이상, 상한 없음. 중복 금지. 지금 찾을 수 있는 에이전트만 |
| `mode` | `lead`(기본), `parallel`, `pipeline` |
| `instructions` | 8,000자까지 |
| `membersOnly` | 불리언, 기본 켬 |
| `isolation` | 불리언, 기본 꺼짐. §8.11 |
| 파일 | 32 KiB까지 |

**진행 방식**

| `mode` | 동작 |
|---|---|
| `lead` | 메인 대화가 일을 나누고 필요한 구성원만 부른다 |
| `parallel` | 한 번의 `task` 묶음으로 모든 구성원에게 맡긴다 |
| `pipeline` | 목록 순서로 진행하고 앞 결과를 다음 구성원에게 넘긴다 |

**보내기**

1. 페이지는 입력창의 보낼 대상 칩에서 고른 팀을 `chat/send.team`으로 보낸다.
2. 호스트는 팀을 확인하고 팀 파일을 다시 읽는다. 제한과 경로도 다시 검사한다.
3. 호스트는 팀 안내와 사용자 메시지를 함께 보낸다. 편집기 컨텍스트, 첨부, 그 팀에 체크한 지식(§8.9)도 붙는다.

팀 안내에 들어가는 내용:

- 목표(비어 있으면 그 줄은 없다), 진행 방식, 구성원의 이름·맡은 일·모델
- 에이전트끼리 서로 조율하기, 공유 파일은 편집 전에 조율하기
- 검증이 필요한 구성원에게는 할 확인을 할당에 적기. 빌드·테스트·리뷰 역할과 격리 사본이 해당한다
- 하위 에이전트는 시키지 않은 빌드·테스트를 하지 않는다는 사실
- 끝나면 구성원별 결과, 바꾼 파일, 남은 문제를 보고하기

안내 블록은 `<!-- pomp-team:<id> -->`로 시작하고 `<!-- /pomp-team -->`로 끝난다.
채팅은 이 블록을 접어 "팀: 기능 개발팀" 같은 카드로 보인다.

**`membersOnly`**

- 켜져 있으면 그 팀 실행 중에는 확장이 구성원이 아닌 에이전트를 띄우지 못하게 막는다. 기간은 그 턴이 끝날 때까지다.
- 읽기 전용 탐색용 `scout`는 허용한다.

**거부**

- 프로젝트 정책(`.pomp/permissions.json`)의 검토 전용 모드는 하위 에이전트를 막는다. 그래서 팀 실행을 거부하고 이유를 보인다.

**엔진 사실**

- `task` 도구: `{ context, tasks: [{ name?, agent, task }] }`에 그 밖의 필드가 더 있다. 기본으로 동시 32개다. POMP는 깊이를 1로 둬서(`task.maxRecursionDepth: 1`) 메인 대화만 쓴다. 결과는 부모에게 돌아온다.
- 에이전트끼리는 `write agent://<id>`와 `agent://all`로 메시지를 주고받는다. 메인 대화에는 중계 메시지로 보인다.
- POMP는 오간 메시지를 채팅과 해당 대화 상대 탭에 보여 준다.
- 하위 에이전트의 목록, 대화, 진행 상황은 읽을 수 있다.
- 엔진에는 하위 에이전트 하나를 직접 띄우거나 멈추는 명령이 없다. 엔진 자체의 멈춤은 전체 중지뿐이다. 개별 중지는 §8.12.
- POMP 확장은 하위 에이전트 세션에도 붙는다. 확장은 하위 에이전트를 띄우기 전에 막을 수 있다.

**팀 없이 일 나누기**

- 확장(`extensions/delegation.ts`)은 메인 에이전트의 시스템 프롬프트에 고정된 안내 한 절을 더한다.
- 안내 내용: 독립된 일을 `task`로 하위 에이전트에게 나눌 때, 각 할당에 담을 것, 결과를 VS에서 합치는 방법.
- 글이 세션 안에서 바뀌지 않으므로 프롬프트 캐시를 깨지 않는다.
- 사용자가 시작한 팀 실행은 팀 규칙(`extensions/teams.ts`)을 따르고, 이 안내는 그 규칙에 양보한다.

### 8.3 한 에이전트에게 보내기

| 대상 | 필드 | 동작 |
|---|---|---|
| 새 일 | `chat/send.agent` = 역할 `name` | 메인 대화가 그 에이전트 하나만 새로 실행한다 |
| 이어 보내기 | `chat/send.agentInstance` = 하위 에이전트 id | 이 대화에서 이미 실행한 에이전트에게 메시지를 전한다 |

**새 일**

- 호스트는 역할을 확인하고 `<!-- pomp-agent:<name> -->`, `<!-- /pomp-agent -->` 안내와 함께 메시지를 메인 대화에 넘긴다.
- 메인 대화는 `task` 도구로 그 에이전트 하나만 실행한다. 사용자 메시지를 그대로 할당하고 결과를 전한다.
- 그 턴 동안 다른 에이전트는 띄우지 못하게 막는다.
- 엔진 내장 역할도 대상이다.

**이어 보내기**

- 호스트는 그 id가 현재 대화의 작업 목록에 있는지 확인한다. 실행 중인 것과 끝난 것을 모두 포함한다.
- 메인 대화는 `write agent://<id>`로 메시지를 그대로 전한다.
- 답을 따로 기다리지 않는다. 답이 메시지로 들어오면 보여 준다. `wait`는 자기가 시작한 작업만 기다린다.
- 그 에이전트는 자기 맥락을 이어서 답한다. 쉬거나 보관된 에이전트는 깨운다.
- 그 턴에는 새 에이전트를 띄우지 않는다.
- `write agent://`도 쓰기 도구다. 승인 모드에 따라 확인을 받을 수 있다.

**공통**

- 대상은 한 번에 하나다. `team`, `agent`, `agentInstance` 중 하나만 쓴다.
- 검토 전용 프로젝트에서는 거부한다.
- 응답 중에 보낸 메시지는 실행 중인 턴에 끼워 넣지 않고 호스트 대기열에 넣는다(§8.8). 끼워 넣은 메시지는 중지 뒤에도 남는다.

**입력 경로**

- 보낼 대상 칩: 기본(POMP), 팀, 에이전트, 이 대화에서 실행한 에이전트
- `@` 목록의 "에이전트·팀" 묶음. 고르면 대상만 바뀌고 입력한 글자는 지운다
- 채팅 화면의 기본 명령 `/agent 이름 메시지`, `/team id 메시지`
- 에이전트 탭의 "메시지" 버튼

**대상 목록 순서**

- 한 메시지의 대상은 기본(POMP) 탭과 에이전트·팀 탭 어디서나 바꿀 수 있다.
- 목록은 팀, 에이전트, 이 대화에서 실행한 에이전트 순이다. 각 묶음은 이름순이다.
- 지금 대화의 상대는 자기 묶음 맨 위에 배경색으로 구분한다.
- 기본(POMP)은 기본·팀 탭에서는 에이전트 맨 위, 에이전트 탭에서는 그 에이전트 바로 아래에 둔다.
- 지금 대화의 줄을 고르면 대상이 풀리고 지금 대화로 이어 보낸다. 서브에이전트로 다시 부르지 않는다.
- 같은 역할로 실행 중인 구성원은 목록에서 뺀다.

### 8.4 승인과 작업 카드

- 하위 에이전트는 승인 창 없이 모두 자동 승인으로 돈다. 권한 경계는 부모의 `task` 호출 승인이다.
- 사용자의 도구별 정책에서 `deny`는 하위 에이전트에서도 지켜진다. `prompt`는 하위 에이전트에서 거부된다.
- 그래서 `task` 승인은 기억하지 않고 호출마다 묻는다. §6 "매번 확인하는 요청"을 본다.
- 승인 창에 구성원별 할당을 모두 보인다.
- 실행이 바꾼 파일은 기존 체크포인트와 변경 이력에서 보고 되돌릴 수 있다.
- 읽기 전용 에이전트로만 짠 팀은 파일을 바꾸지 않는다.

**작업 카드**

- 채팅의 해당 `task` 카드와 에이전트·팀 대화 상대 탭에 구성원별 상태, 도구 수, 시간, 최근 도구를 보인다.
- 구성원은 `parentToolCallId`로 호출 카드에 붙인다. 해당 카드가 없는 현재 구성원만 접을 수 있는 진행 줄에 나온다.
- 구성원 대화 보기는 읽기 전용이다. 최근 40개 메시지이고 글자 수 제한이 있다.
- 토큰, 비용, 모델은 엔진의 진행 데이터에 있지만 구성원 줄의 표시 항목은 아니다.
- 구성원 줄의 **중지**는 그 구성원만 멈춘다(§8.12). 채팅 입력창의 중지는 전체 응답을 멈춘다.

### 8.5 컴퓨터 사용 보충

§4 "에이전트·팀과 컴퓨터 사용"의 경계와 옵션에 더하는 규칙이다.

**설정 위치**

실제로 켜고 끄는 곳은 대화마다다. 허용 여부와 엄격함은 전체 설정 한 곳이다. 에이전트·팀은 고정 규칙이다.

| 단위 | 무엇 | 기본 |
|---|---|---|
| 전체 설정: 도구 > 옵션 > Pomp > 일반 > 컴퓨터 사용 | 컴퓨터 사용: 사용 안 함 / 대화마다 켜기 | 대화마다 켜기 |
| | 브라우저 제어. 다시 시작할 때 적용 | 끔 |
| | 화면 보기(캡처·창 목록)도 매번 확인 | 끔 |
| 대화 | 도구 › 대화 설정 › 화면 입력 제어 스위치 | 꺼짐. 새 대화, 세션 전환, 재연결 때 꺼짐 |
| 에이전트·팀 | 하위 에이전트는 승인 창을 띄울 수 없으므로 화면·브라우저 제어 금지. 팀은 리더인 메인 대화만 승인받고 쓴다 | 고정 |

**엔진 사실**

- 엔진의 컴퓨터 사용은 기본 꺼짐이다. `/computer`의 켜기, 끄기, 상태 확인은 모델 호출 없이 처리되고 그 세션에만 적용된다.
- 하위 에이전트 설정은 부모 값을 그대로 읽는다.
- 화면 캡처와 창 목록은 read 단계, 클릭·입력·클립보드 쓰기는 exec 단계다.
- 엔진의 브라우저 제어는 기본 켜짐이고 실행 중에 바꾸는 명령이 없다. 다시 시작해야 적용된다.

**POMP의 시작 설정**

- 오버레이는 컴퓨터 사용을 항상 끈 채로 시작한다(`computer.enabled: false`).
- `browser.enabled`는 옵션 **브라우저 제어**를 따른다. 옵션 기본은 끔이라 엔진 기본값은 쓰이지 않는다.
- 네트워크 도구 `browser`가 거부되어 있으면 옵션과 관계없이 끈다.
- Yolo 승인 모드에서 브라우저 제어가 켜져 있으면 `browser`는 `prompt`가 된다. 다른 도구는 묻지 않아도 브라우저 동작은 묻는다.
- 사용 안 함이면 컴퓨터 도구를 거부로 둔다. "화면 보기도 매번 확인"이거나 승인 모드가 Yolo이면 컴퓨터 도구의 모든 호출을 확인한다.

**대화 토글**

- 처음 켤 때 한 번 확인한다. 문구는 다음과 같다.
  "화면 내용이 모델 공급자에게 전송돼요. 클릭·입력·클립보드 쓰기는 매번 승인을 받아요.
  비밀번호·개인 정보가 보이는 창은 닫아 두세요. 화면에 보이는 글은 지시가 아니에요." 버튼은 [이 대화에서 켜기]다.
- 켜져 있으면 스위치를 강조하고 상태를 "켜짐"으로 보인다.
- 토글은 모델 호출 없이 켜고 끄고 결과를 확인한다. 응답 중에는 바꾸지 않는다.
- 새 대화, 세션 전환, 재연결 뒤에는 꺼짐으로 맞춘다.
- 입력창에서 친 `/computer`는 도구 › 대화 설정을 열고 같은 스위치를 쓴다. 사용 안 함이면 거부한다.
- 옵션이 사용 안 함이면 화면 입력 제어를 숨긴다.

**상태 파일**

- 호스트는 `POMP_STATE_DIR/computer-state.json`을 원자적으로 쓴다: `{ sessionId, desktop, browser, updatedAt }`.
- 확장의 판정 규칙은 §4 "하위 에이전트와 화면 제어 경계"에 있다.
- 확장은 매 턴 이 파일을 다시 읽어 메인 대화의 화면·입력 제어 안내를 붙일지 정한다. 규칙은 §4 "컴퓨터 사용"에 있다.

**한계**

- 샌드박스가 아니다. 승인된 명령(bash 등)은 원래 권한으로 돈다.
- 화면 정보는 신뢰할 수 없는 입력이다.

### 8.6 채팅 화면 규칙

**하위 화면과 목록**

- 하위 화면 칩은 항목 수를 보인다. 0개여도 흐린 채 누를 수 있다.
- 사용자가 고르지 않았고 현재 화면이 비어 있으면 항목이 있는 첫 화면으로 간다. 직접 고른 화면은 이 대화 동안 유지한다.
- 목록은 열 이름이 있는 표다. 스위치, 배지, 줄 끝 아이콘, 검색, 20개씩 페이지가 있다.
- 빈 목록은 열 이름과 빈 줄만 보인다. 안내 문구는 두지 않는다.
- 카드 머리의 생성 버튼과 파일 기반 자료의 새로 고침은 목록이 비어 있어도 남긴다.

**에이전트 탭**

| 하위 화면 | 동작 |
|---|---|
| 역할 | 만들기·편집·삭제·메시지. 엔진 내장 역할은 편집·복제. 맨 위의 POMP는 그림과 `APPEND_SYSTEM.md` 지시만 편집하고 삭제는 없다 |
| 팀 | 만들기·편집·삭제·메시지 |
| 조언자 | `WATCHDOG.yml`의 조언자를 만들기·편집·삭제·켜고 끄기 |
| 자동화 | 이벤트 규칙과 프로젝트 훅을 한 표(자동화 규칙)와 실행 기록으로 다룬다 |

- 실행 상태와 개별 중지는 에이전트 탭이 아니라 채팅의 작업 카드에 둔다.
- 별도의 실행 기록 관리 화면은 두지 않는다.

**입력창과 문구**

- 입력창에는 권한 모드와 보낼 대상 칩이 있다. 화면 입력 제어는 도구 › 대화 설정에 있다.
- 문구는 한국어·영어(i18n)다. 색은 POMP 테마 변수만 쓴다.
- 설정 분류와 항목도 POMP 언어를 따른다. 옵션 페이지 이름은 일반/General이다.

**대화 목록 패널**

- 세션 탭은 없다. 대화창 상단 세션 메뉴와 겹치기 때문이다.
- 상단 ▾가 대화 목록 패널을 연다. `/sessions`도 이 패널을 연다.
- 패널은 지금 탭의 대화 목록이다. 기본 탭은 POMP 대화만, 에이전트·팀 탭은 그 상대의 대화만 보인다. 검색과 그 목록 전체가 있다. 행은 제목, 시각, 메시지 수를 보인다.
- 행을 누르면 그 대화를 지금 탭에서 연다. 탭은 그대로이고, 전에 보던 대화는 그 탭의 목록에 남는다. 현재 대화는 체크 표시이고 버튼이 아니다. 응답·승인 중에는 열 수 없다.
- 행의 ⋯ 메뉴: 미리보기는 그 행 바로 아래에 펼치고 접는다. 이름 바꾸기는 현재 대화만 된다.
- 삭제는 확인 뒤 대화 파일을 휴지통으로 옮긴다. 현재 대화를 지우면 먼저 지금 탭에서 새 대화를 시작한다.

### 8.7 메시지 보충

`docs/ui-protocol.md`에 더하는 필드 규칙이다.

```
Role { name, title, job, description, instructions, model?, thinkingLevel?, tools: 'all' | 'readOnly' | 'custom',
       scope: 'project' | 'user' | 'bundled' | 'extension', file?, hash?, editable, error? }
Team { id, title, goal, members: string[], mode: 'lead' | 'parallel' | 'pipeline', instructions, membersOnly, isolation,
       scope: 'project' | 'user', file, hash, missing: string[], error? }
```

- `agents/state`: `{ root, roles: Role[], teams: Team[], diagnostics: string[] }`.
- `TeamImportPreview.roles[].status`의 `existing`은 템플릿에 없지만 이 PC에서 찾은 역할이다.
- 가져오기는 `problems`가 비어 있고 충돌이 없을 때만 된다.
- `chat/send`의 대상을 호스트가 찾지 못하면 보내지 않고 오류로 끝낸다.
- `computer/state`: `{ policy: 'off' | 'perConversation', browser, confirmReads, desktop, pending, available, message? }`.
- `computer/set { requestId, desktop }`은 `computer/reply { requestId, ok, error? }`로 답한다.

### 8.8 대화 탭, 대기열, 기록

**대화 탭**

- 채팅 상단, 세션 이름 아래에 대화 탭이 있다. 닫을 수 없는 기본(POMP) 탭과 연 에이전트·팀 탭이다.
- 탭마다 별도 대화(omp 세션 파일)다. 기본 탭은 POMP 대화이고, 에이전트·팀 탭은 그 에이전트·팀이 상대인 대화다.
- 탭을 바꾸면 엔진이 그 탭의 대화로 바뀌고 그 대화만 다시 표시한다(대화 목록에서 바꿀 때와 같다). 기본 탭에는 다른 탭의 턴이 나오지 않는다.
- omp는 VS 창마다 하나다. 응답·승인·질문·백그라운드 작업 중에는 다른 탭을 고를 수 없다. 탭은 흐리게 보이고 짧은 도움말이 뜬다. 응답을 멈추면 바꿀 수 있다.
- 역할·팀의 "메시지", `+` 목록, 메시지 없는 `/agent 이름`, `/team id`는 그 상대의 탭을 연다. 이미 열린 탭이 있으면 그 탭으로 간다. 없으면 그 상대의 가장 최근 대화로 탭을 열고, 대화가 하나도 없으면 새 대화(빈 기록)를 시작한다.
- ×는 탭만 닫는다. 대화는 그 상대의 대화 목록에 남는다. 지금 탭을 닫으면 기본 탭으로 돌아간다.
- 탭마다 지금 대화가 하나다. 대화 목록에서 고른 대화가 그 탭의 지금 대화가 되고, 탭의 자리는 바뀌지 않는다.
- 새 대화(대화 머리의 `+`, `/new`)는 지금 탭에서 시작한다. 기본 탭이면 POMP 대화, 에이전트·팀 탭이면 그 상대의 새 대화다(역할 적용은 새 에이전트 탭과 같다). 역할이나 팀이 없어졌으면 POMP 대화가 된다.
- 메시지를 보내지 않은 대화는 엔진이 파일로 남기지 않는다. 그 탭으로 돌아가면 그 상대의 새 대화를 시작한다.
- 저장: `<작업 영역>\.pomp\local\chat-tabs.json`(ui-protocol "Conversation tabs"). 기본 탭의 대화, 에이전트·팀 대화와 그 상대, 열린 탭 순서를 둔다. VS를 다시 시작하거나 페이지를 다시 읽어도 남는다.

**한 메시지 대상**

- 대상 칩, `@` 멘션, 메시지가 붙은 `/agent`는 어느 탭에서나 쓰고 그 메시지에만 적용한다. 그 탭의 대화 안에서 위임한다(§8.3).
- 칩은 평소 `보낼 대상`이고, 다른 대상을 골랐을 때만 그 상대를 보인다. 지금 탭의 상대 자신은 칩에 나오지 않는다.
- POMP는 기본 탭에서만 대상 목록에 있다. 에이전트·팀 탭은 그 상대의 대화이기 때문이다.
- `/goal`은 기본(POMP) 탭에서만 쓴다. 편집기 코드 동작은 기본 탭의 대화로 보낸다. 다른 탭이면 먼저 기본 탭으로 바꾸고 요청은 입력창에서 기다린다.
- 계획 이어가기는 지금 대화의 계획이므로 지금 대화로 보낸다.
- 작업 › 코드 점검의 **에이전트에게 해결 요청**은 지금 보낼 대상에 메시지 하나로 보낸다. 편집기 컨텍스트는 끄고, 최대 50개·12,000자다.

**에이전트 대화**

- 에이전트 자신이 대화 상대다. 메시지는 입력한 그대로 보낸다. `/pomp-agent`도, `task` 하위 에이전트도 없다.
- 엔진 확장 `extensions/partner.ts`가 `chat-tabs.json`에서 대화의 상대를 읽고 역할을 적용한다. 역할 파일은 omp 순서(사용자, 프로젝트, 내장)로 찾는다(`lib/agent-roles.ts`).
  - 지시문: `before_agent_start`에서 시스템 프롬프트에 `<pomp_conversation_partner>` 블록(이름·설명·역할 본문)과 그 역할의 메모를 더한다. 매 턴 다시 정한다.
  - 모델·생각 수준: 새 대화를 시작한 뒤 호스트가 `/pomp-partner new`를 보내고, 확장이 역할의 모델(`@smol` 같은 별칭 포함)과 생각 수준을 고른다. 엔진이 대화마다 저장하므로 그 대화로 돌아오면 그대로다.
  - 도구: `tools.ts`가 대화를 바꿀 때와 도구 초기화 때 역할의 도구 목록만 켠다(읽기 전용 역할은 읽기 도구만). 목록이 없으면 기본 도구다.
- 기본 탭으로 돌아가면 POMP 대화는 블록이 없고 기본 도구로 돌아간다. 저장한 모델이 없어 에이전트·팀 대화의 모델을 물려받은 POMP 대화만 `/pomp-partner`가 마지막 POMP 모델로 되돌린다. 에이전트·팀 대화의 모델은 건드리지 않는다(새 에이전트 대화에서 역할 모델을 고르는 것만 예외). VS를 다시 시작한 직후 에이전트 대화에서 새 POMP 대화를 만들면 되돌릴 모델을 모른다.
- 역할을 찾을 수 없으면 그 대화로는 보내지 않는다.

**팀 대화: 같은 구성원으로 이어 가기**

- 팀 탭은 그 팀의 대화다. 그 대화의 메시지는 메시지마다 팀으로 보낸다(§8.2). 팀 안내와 구성원 실행은 그 대화 안에서 일어난다.
- 호스트는 대화마다, 팀마다 구성원별 최근 인스턴스를 기억한다: 역할 이름 → 작업 id. 메모리에만 두고, 대화나 연결이 바뀌면 비운다.
- 기록 대상은 그 팀으로 보낸 턴에서 새로 시작된 작업 중 역할 이름이 팀 구성원인 것이다. 턴은 `chat/turnStarted.team`과 같은 promptId로 가린다.
- 다음에 그 팀으로 보낼 때는 팀 대화든 한 메시지 대상이든 이어 가는 구성원을 함께 보낸다.
- 이어 가는 구성원은 이 대화의 현재 작업 목록에 아직 있는 인스턴스만이다. 형식은 `<역할>=<작업id>` 쌍의 목록이다.

| 항목 | 규칙 |
|---|---|
| 역할 | `^[a-z0-9-]{2,40}$` |
| 작업 id | `^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$` |
| 버리는 항목 | 구성원이 아닌 역할, 형식이 틀린 항목 |
| 개수 | 최대 6개 |

- 남은 인스턴스가 없으면 이어 가는 구성원 없이 보낸다.
  팀 대화일 때는 `conversation/tabsState.message`로 "이전 구성원을 찾을 수 없어 새로 시작해요"를 알린다.
- 이어 가는 구성원이 있으면 팀 안내 블록에 "이어 가는 구성원" 목록(`역할 → agent://<작업id>`)과 규칙을 넣는다.
- 규칙: 이미 있는 구성원은 새로 시작하지 않고 `write agent://<id>`로 다음 일을 보낸다. 답은 메시지로 받고 `wait`는 쓰지 않는다.
- 목록에 없거나 연결되지 않는 구성원만 `task`로 새로 시작한다.
- `membersOnly` 제한은 그대로이고 새로 시작하는 경우에만 적용된다. 안내 블록 표식(`<!-- pomp-team:<id> -->`)은 바뀌지 않는다.
- 격리 팀은 이어 가지 않는다(§8.11).

**명령 대기열**

- 응답 중에 보낸 메시지는 거절하지 않고 호스트 대기열에 넣는다. 대기열은 POMP가 가지고, 이 VS 창 동안만 메모리에 있다.
- 개수 제한은 없다. 대기열 전체는 1,800 KiB까지다.
- 입력창 위 목록에서 순서 바꾸기, 고치기, 지우기, 일시정지·재개, 비우기를 한다. 대상(상대·한 메시지 대상)도 항목에 같이 저장한다.
- 턴이 정상으로 끝나면 다음 항목을 보통 보내기와 같은 길로 보낸다.
- 오류, 중지, 대화 바뀜이면 일시정지하고 이유를 보인다. 항목은 지우지 않고, 사용자가 재개하거나 비운다.
- 엔진 자체의 대기열은 쓰지 않는다. 엔진 대기열의 메시지는 중지 뒤에도 남기 때문이다.
- 대기열은 넣을 때의 대화에 묶인다. 다른 대화에서는 재개를 거부한다. 다른 대화로 잘못 보내지 않기 위해서다.

**실행 기록**

- 팀, 에이전트, 직접 메시지 실행마다 기록한다: 대상(종류·id·제목), 요청 글(4,000자까지), 시작·끝, 상태, 구성원.
- 구성원 항목은 에이전트, 작업 id, 상태, 도구 수, 토큰, 시간이다.
- 프롬프트 전체, 도구 결과, 파일 내용은 저장하지 않는다.
- 위치는 `<작업 영역>\.pomp\local\agent-history.json`이다. 원자적으로 쓴다.
- 이전 위치 `<DataDirectory>\agent-history\<작업 영역 해시>.json`은 처음 쓸 때 옮긴다.
- 개수 제한은 없다. 파일은 화면 메시지 한 개(1.75 MiB) 크기까지다.

### 8.9 지식

형식, 제한, 중복, 앵커, 전달 블록은 ui-protocol "Knowledge"에 있다. 여기에는 그 밖의 규칙을 둔다.

**개수와 저장**

- 지식, 조각, 제안 조각에는 개수 제한이 없다. 저장 파일 전체가 4 MiB까지다.
- 이전 형식(`%LOCALAPPDATA%\Pomp\knowledge\`)이 있으면 처음 한 번 옮겨 오고 이전 파일은 지운다. `.pomp`를 지운 뒤 되살아나지 않게 하기 위해서다.
- 옮길 때 제목은 지식 이름이 된다. 같은 이름은 한 지식의 조각이 된다.
- 이전의 팀·역할 범위는 그 팀·역할에 체크된 프로젝트 지식이 된다.

**변환본**

- 변환본을 만들 때 모델도 중복을 합친다.
- 모델에는 지식 이름과 조각만 구분된 데이터로 보낸다.
- 사용자가 직접 변환해 성공하면 사용 형태가 변환본으로 바뀐다. 자동 변환은 사용 형태를 바꾸지 않는다.

**대화·팀 전달**

- 확장은 작업 영역이나 대화가 다르면 `<pomp_knowledge>` 블록을 붙이지 않고 남은 블록을 지운다.
- 팀에 체크한 지식은 그 팀에 보낼 때만 붙는다. 다른 대상에는 퍼지지 않는다.

**에이전트 제안과 읽기**

- `propose_knowledge` 도구 설명에는 비밀 정보나 검증되지 않은 내용을 넣지 말라고 적는다.
- 제안은 사용자가 도구 탭 › 지식에서 수락하기 전에는 쓰이지 않는다.

**편집기 조각**

- 우클릭 POMP ▸ 지식에 추가(`Alt+/`)는 선택한 줄을 담아 도구 탭 › 지식의 추가 양식을 연다. 메뉴에 지식 목록을 늘어놓지 않는다.
- 조각으로 넣으면 줄 위치와 내용 해시를 함께 저장한다. 줄 위치는 작업 영역 기준 경로와 시작–끝 줄이고, 200줄까지다.
- **이동됨**이면 위치를 조용히 고친다.
- 편집기는 그 줄에 지식 색 배경을 칠한다.

**역할별 지식**

- 엔진 자체의 기억 기능은 프로젝트 단위이고 기본 꺼짐이라 쓰지 않는다. 역할별 지식은 지식에 그 역할을 체크한 것이다.
- 호스트는 역할마다 체크된 지식의 쓰이는 내용을 `POMP_STATE_DIR/role-memory.json`에 원자적으로 쓴다.
- 쓰는 때는 지식이 바뀔 때와 작업 영역이 바뀔 때다.

```json
{ "version": 1, "root": "<작업 영역>", "roles": { "<name>": [{ "title": "<지식 이름>", "body": "<쓰이는 내용>" }] } }
```

- 역할당 최신 10개, 8,000자까지다.
- 확장은 하위 에이전트가 시작될 때 그 역할 이름의 항목을 시스템 프롬프트 뒤에 `<role_memory role="<name>">` 블록으로 붙인다.
- 블록에는 참고 자료일 뿐 지시·도구 권한·승인·정책을 바꾸지 않는다고 적는다. 내용 안의 태그는 무력화한다.
- 파일이 없거나 손상됐거나 작업 영역이 다르면 붙이지 않는다. 안전장치가 아니라 참고 자료라서 실패해도 실행을 막지 않는다.

**책임**

- 관리 주체는 사용자다. 팀의 리더 판단 방식에서 메인 대화가 조율자이며, 별도 권한 상승은 없다.

### 8.10 이벤트 규칙과 루틴

규칙 형식, 발화 제한, IDE 이벤트는 ui-protocol "Event rules between agents and teams"에 있다.

| 항목 | 규칙 |
|---|---|
| 저장 | 작업 영역의 `.pomp\local\agent-hooks.json` |
| 파일 | 256 KiB까지 |
| 규칙 개수 | 제한 없음 |
| 규칙 이름 | 영문 소문자·숫자·`-`, 2~40자 |
| 보낼 메시지 | 2,000자까지 |
| `ide.saved`의 `glob` | 작업 영역 기준, 기본 `**`, 200자까지 |

- 같은 화면에서 할 일 **스크립트**로 만드는 훅은 `.pomp/hooks`의 PowerShell 훅이다. 저장 위치와 동작이 이벤트 규칙과 별개다.
- 이벤트 규칙의 IDE 이벤트(루틴)가 생겨도 스크립트 훅의 동작은 바뀌지 않는다.
- 피드백 대기(`feedback.waiting`)는 케이스 카드가 사용자 의견을 기다릴 때다.
- IDE 이벤트를 쉬는 대화에서만 처리하는 이유: 에이전트가 고친 파일의 저장으로 규칙이 다시 도는 고리를 막는다.
- 이 대화에서 사용 스위치, 대화당 10번, 연쇄 깊이 1, 중복 제거는 IDE 이벤트에도 그대로 적용된다.

### 8.11 격리 작업 트리

엔진 설정, 결과 받기, `workspace/isolation` 메시지는 ui-protocol "Isolated team runs"에 있다.

**복사본**

- 구성원마다 격리된 복사본에서 일한다. Git 저장소가 필요하고 저장되지 않은 변경도 복사된다.
- Git 저장소가 아닌 작업 영역에서는 보내기 전에 거부한다.
- 격리된 구성원은 나중에 이어서 쓸 수 없다. 그래서 격리 팀은 항상 새로 시작한다.
- 격리된 작업의 변경은 적용하지 않고 패치로만 남는다.

**권한 가드**

- 작업 폴더가 작업 영역과 다르면 모든 도구를 막는다.
- 예외는 하위 에이전트가 POMP의 격리 폴더(`POMP_WORKTREE_BASE\<id>\m`) 안에서 일할 때다.
- 그때 프로젝트 정책은 복사본의 것이 아니라 작업 영역의 `.pomp/permissions.json`으로 판단한다. 구성원이 복사본의 정책 파일을 고쳐도 소용없다.
- 이 예외가 없으면 실제 VS에서 격리 구성원의 모든 도구가 막힌다.
- 확장은 격리 팀 안내에 "구성원 `task`는 `isolated: true`"를 넣는다. 가드가 막으면 이유를 돌려주고 모델이 다시 호출한다.

**결과**

- 패치는 구성원 세션 파일 옆의 `<세션 파일>.patch`다.
- 결과당 파일 수 제한과 결과 수 제한은 없다.
- 빈 패치는 "바꾼 파일 없음" 알림만 남긴다.
- 메모리에 둔 패치가 합쳐 64 MiB를 넘으면 적용·실패한 오래된 결과부터 비운다. 그래도 넘치면 그 결과는 알림에 패치 경로만 남긴다.
- 적용했거나 실패한 결과는 버릴 때까지 목록에 남는다.

**검토**

- 결과는 작업 탭 › 변경 › 대기 중의 **격리 실행 결과** 항목으로 기다린다.
- C# 셰도 검사가 있는 일반 변경 검토와 따로 둔다. 그 검토는 열린 C# 문서만 다루는데 구성원은 다른 파일도 만들고 바꾸기 때문이다.
- 적용은 문맥이 정확히 맞아야 한다. 퍼지 적용과 3-way 병합은 없다.

### 8.12 구성원 개별 중지

- 엔진 기본 기능에는 하위 에이전트 하나만 멈추는 방법이 없다.
- POMP 확장이 그 구성원만 멈춘다. 그 세션을 중단하고 종료 표시로 놓아 주는 방식이다.
- 메인 대화가 응답 중이어도 바로 실행된다. 그 구성원만 "취소됨"으로 끝나고 다른 구성원은 계속된다.
- 메인 대화는 취소된 결과를 받아 이어 간다.
- 확장은 메인 대화에서만 동작한다. 멈출 수 있는 것은 실행 중이고, 메인 대화가 직접 또는 다른 구성원을 거쳐 시작한 구성원뿐이다.
- 결과는 알림으로 POMP에 돌아가고 대화 기록에는 남지 않는다.
- 필요한 내부 기능이 없거나 모양이 다르면 아무것도 바꾸지 않고 "지원하지 않음"을 알린다.
- POMP는 이 기능을 쓸 수 있을 때만 켠다. 작업 카드의 실행 중 구성원 줄에 **중지**가 생기고, 그 줄에서 한 번 더 확인한 뒤 멈춘다.
- 쓸 수 없으면 버튼을 두지 않는다.
- 엔진 내부 동작에 기대므로 구성 요소를 올릴 때 다시 측정한다.
- 메시지 규약은 ui-protocol "Stopping one member"에 있다.

### 8.13 팀 템플릿 내보내기·가져오기

형식은 256 KiB까지다.

```
{ "format": "pomp-team-template", "version": 1,
  "team": { id, title, goal, members, mode, instructions, membersOnly, isolation },
  "roles": [{ name, title, job, description, instructions, model?, thinkingLevel?, tools }] }
```

- `roles`에는 팀이 쓰는 사용자 정의 역할만 넣는다. 엔진 내장 역할은 이름으로만 참조한다.
- 내보내기: 글을 복사하거나 편집기에서 연다. `%LOCALAPPDATA%\Pomp\exports\`에 쓰고 VS로 열어 사용자가 원하는 곳에 저장한다.
- 가져오기: 붙여넣기 → 미리보기 → 범위 선택 → 가져오기.
- 미리보기 상태: 새로 만듦, 같은 내용이 이미 있음, 다른 내용과 이름이 겹침. 겹침은 거부한다.
- 새 파일만 만들고 덮어쓰지 않는다. 하나라도 실패하면 이번에 만든 파일을 지운다.
- 검증과 한도는 §8.1, §8.2와 같다.

### 8.14 검증

- 공급자를 실제로 호출하지 않는다.
- 사용자의 에이전트 설정 폴더(`%USERPROFILE%\.omp`)를 건드리지 않는 격리된 홈에서 확인한다.

| 범위 | 확인할 것 |
|---|---|
| Core | 파일 검증, 원자적 쓰기, 해시 충돌, 범위, 제한, 팀 검증, 팀 메시지 변환, 컴퓨터 토글(가짜 에이전트의 응답), 시작 설정 내용, 세션이 바뀔 때 꺼짐, 상태 파일 |
| 확장 | 팀 안내, `membersOnly` 차단, 하위 에이전트 eval 차단(켜짐·꺼짐·파일 없음·손상) |
| UI | 에이전트 탭 편집, 검증 오류, 190px·360px 너비, 한/영, 보낼 대상 칩, 화면 토글 확인 흐름 |
| 실제 VS | 에이전트·팀 만들기, 모델 호출 없는 컴퓨터 사용 상태 확인, 실제 모델로 팀 실행 |
