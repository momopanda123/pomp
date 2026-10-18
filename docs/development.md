# POMP 개발 문서

POMP를 빌드하고 디버깅하고 수정하는 사람을 위한 문서다. 사용법은 [빠른 시작](quick-start.md)과 [화면 가이드](screen-guide.md)를 본다.

| 문서 | 내용 |
|---|---|
| [`architecture.md`](architecture.md) | 구현 기준. 에이전트·팀 구현 계약은 [§8](architecture.md#8-에이전트팀-구현-계약) |
| [`ui-protocol.md`](ui-protocol.md) | UI ⇄ 호스트 메시지 규약 |
| [`agent-teams.md`](agent-teams.md) | 에이전트·팀 사용 안내 |

## 구성

**pomp**는 `pinb`와 `omp`를 합친 것이다.

- `pinb`: VSIX 호스트와 IDE 어댑터.
- `omp`: oh-my-pi 에이전트 코어.

| 경로 | 내용 |
|---|---|
| `src/Pomp.Contracts` | 공용 계약. omp RPC 모델, 브릿지·툴·권한·어댑터 인터페이스, UI 프로토콜 |
| `src/Pomp.Core` | omp 프로세스 브릿지, NDJSON/RPC, 툴 디스패치, 권한 게이트, 세션 컨트롤러 |
| `src/Pomp.Tools` | omp에 노출하는 VS 호스트 툴. `read_buffer`, `query_solution`, `build_solution` 등 |
| `src/Pomp.CodeLens.Contracts` | CodeLens 프로세스 간 전달용 소스 위치 계약 |
| `src/Pomp.CodeLens.Provider` | 선택적 native CodeLens OOP 공급자. 기본 꺼짐이고, 명시적으로 클릭할 때만 요약을 요청한다 |
| `src/Pomp.VisualStudio` | VS 공용 소스. 패키지, 도구 창/WebView2, 테마, 옵션, 어댑터 |
| `src/Pomp.Vsix.VS2017` | VS 2017용 VSIX. x86, 15.9 SDK, 기본 CodeLens 캡션 없음 |
| `src/Pomp.Vsix.VS2019` | VS 2019용 VSIX. x86 |
| `src/Pomp.Vsix.VS2022` | VS 2022·2026용 VSIX. x64, 설치 범위 `[17.0,)` |
| `src/Pomp.WebUI` | 채팅 UI. Vite + React + TypeScript, 오프라인 번들 |
| `src/Pomp.OmpExtension` | omp 측 확장. pomp-context Extension, pre-edit Hook, vs-project Skill |
| `engines/omp` | 번들할 omp 엔진의 고정 버전·해시(`engine.props`)와 라이선스 |

## 개발 환경

### 필수

| 항목 | 비고 |
|---|---|
| Windows 10/11 | |
| Visual Studio 2022 또는 2026 | 빌드용 MSBuild |
| .NET Framework 4.7.2 타겟팅 팩 | |
| .NET SDK 8 이상 | `Pomp.Contracts`, `Core`, `Tools`는 netstandard2.0이다 |
| Node.js 20.19+ 또는 22.12+ | WebUI 빌드 |
| 인터넷 연결 | 첫 빌드에만 필요하다 |

첫 빌드는 고정된 omp 엔진을 공식 릴리스에서 내려받는다. 현재 버전은 18.12.1이고, `engines/omp/engine.props`에 고정되어 있다.

내려받은 엔진은 `artifacts\engines\omp\<버전>\`에 캐시한다. 오프라인이면 `-OmpExe`로 같은 파일의 로컬 사본을 준다.

### 권장

- Visual Studio의 **Visual Studio 확장 개발** 워크로드를 설치한다.
  - F5 디버깅(실험 인스턴스)과 VSIX 매니페스트 편집기에 쓴다.
  - VSIX 빌드 자체는 NuGet의 `Microsoft.VSSDK.BuildTools`만으로 된다.
- 저장소 루트의 [`.vsconfig`](../.vsconfig)를 Visual Studio Installer에서 가져온다.
  - 확장 개발 워크로드와 **.NET Framework 4.7.2 타깃팅 팩**을 추가할 수 있다.
  - 설치된 VS 2022/2026 인스턴스의 **더 보기 > 구성 가져오기**를 사용한다.
  - Node.js와 omp는 이 파일로 설치되지 않는다.
- .NET SDK는 빌드용 Visual Studio가 지원하는 버전을 사용한다.

### 검증 환경

2026-10-05 기준으로 다음을 확인했다.

| 환경 | 확인한 내용 |
|---|---|
| Windows 11, VS 2026 MSBuild | VSIX 빌드 |
| VS 2019 16.11 실험 인스턴스 | 실제 모델로 편집기 메뉴, 설명 요청, 대기열, 도구, 승인, 중첩 하위 에이전트 위임까지 끝까지 확인 |
| VS 2026 18.10 실험 인스턴스 | 64비트 VSIX의 설치, 엔진 연결, 메뉴, 단축키 |
| VS 2022 | 따로 확인하지 않았다. VS 2026과 같은 패키지다 |

### 확장 사용자 PC의 실행 환경

| 항목 | 설명 |
|---|---|
| .NET Framework 4.7.2 이상 | VS 설치 과정에서 준비된다. 4.8/4.8.1이 있으면 4.7.2 런타임을 따로 설치하지 않는다 |
| Microsoft Edge WebView2 Evergreen Runtime 111.0.1661.41 이상 | 채팅 화면에 필요하다. 이미 설치되어 있으면 재설치하지 않는다 |
| omp | 따로 설치하지 않는다. VSIX에 POMP가 시험한 omp 엔진이 들어 있다 |

- 개발 PC에는 실행용 런타임과 별개로 위의 **4.7.2 타깃팅 팩**이 필요하다.
- 배포된 VSIX를 사용하는 PC에는 .NET SDK, Node.js, 타깃팅 팩, 확장 개발 워크로드가 필요하지 않다.

### omp 엔진과 로그인

POMP 릴리스마다 시험한 omp 버전 하나가 VSIX에 들어 있다. 위치는 `<확장 폴더>\omp\omp.exe`이고, 현재 버전은 18.12.1이다.

- 시작할 때 크기와 SHA-256으로 그 파일이 맞는지 확인한다.
- omp 업데이트는 POMP 업데이트로만 들어온다.
- PC에 설치된 omp가 있거나 그것이 업데이트되어도 POMP에는 영향이 없다.
- 엔진 파일이 손상되면 다른 omp로 대신하지 않고 재설치를 안내한다.
- 검증된 내장 엔진이 아니면 `⋯` 메뉴 아래쪽에 "에이전트 <버전> · <어디서 온 실행 파일> · 검증 안 됨"이 보인다. 채팅 위에도 경고가 뜬다.

고급 설정으로 **도구 > 옵션 > Pomp**의 omp 경로에 다른 omp를 지정할 수 있다. 이 경우에도 "검증 안 됨"으로 표시되고 채팅 위에 경고가 뜬다.

버전이 다르면 일부 기능이 동작하지 않을 수 있다. 자세한 내용은 [`architecture.md`](architecture.md) §4 "엔진 버전"을 본다.

모델 제공자 로그인은 채팅 창의 계정 탭에서 한다. 터미널은 필요 없다.

## 빌드

```powershell
# WebUI 빌드 + 세 VSIX 빌드 → artifacts\Pomp.VS2017-<버전>.vsix, Pomp.VS2019-<버전>.vsix, Pomp.VS2022-<버전>.vsix (-Flavor로 하나만)
powershell -NoProfile -File build\build.ps1

# 오프라인 빌드: 고정 엔진의 로컬 사본을 사용 (SHA-256이 engine.props와 같아야 함)
powershell -NoProfile -File build\build.ps1 -OmpExe D:\offline\omp-windows-x64.exe
```

빌드 스크립트는 PowerShell 실행 정책을 변경하지 않는다. 조직 정책이 실행을 막으면 그 정책에 맞는 승인이나 서명 절차를 따른다.

C# 언어 버전은 `12.0`으로 고정되어 있다. 설정 위치는 `Directory.Build.props`다.

### 엔진 번들

모든 VSIX에 고정 omp 엔진이 들어간다.

| 항목 | 크기 |
|---|---|
| 엔진 다운로드 | 약 229 MiB. 처음 한 번만 내려받는다 |
| Release VSIX | 약 117 MB |
| Debug VSIX | 약 245 MB. 압축하지 않는다 |

- 엔진의 크기나 해시가 `engines/omp/engine.props`와 다르면 빌드가 실패한다.
- `-OmpExe`의 상대 경로는 현재 폴더 기준으로 해석한다.
- `-OmpExe`의 파일이 없으면 npm/MSBuild 단계 전에 실패한다.

### MSBuild 속성

| 속성 | 용도 |
|---|---|
| `-p:PompBundleOmp=false` | 엔진 없는 개발 패키지를 만든다 |
| `-p:PompOmpExe=<경로>` | 엔진 파일을 직접 지정한다. 존재하는 파일의 **절대 경로**여야 한다 |
| `-p:PompAllowPlaceholderUi=true` | `dist`가 없을 때 Release 빌드에서도 자리표시 페이지를 쓴다 |

### 채팅 UI 번들 검사

VSIX에 넣는 채팅 UI는 `src\Pomp.WebUI\dist`의 빌드 결과다.

- `npm run build`는 UI 소스와 모든 산출물의 SHA-256 지문을 `dist\pomp-bundle.json`에 기록한다.
- VSIX 빌드는 `node scripts\bundle-integrity.mjs --verify`로 소스와 산출물이 지문과 같은지 확인한다.
- 확인 항목은 내용 변경, 누락·추가 파일, 외부 참조, `dist` 밖 참조다.
- 이 검사에는 빌드 PC의 Node.js가 필요하다. 설치 사용자는 필요 없다.

### 빌드 오류 코드

| 코드 | 조건 | Release | Debug |
|---|---|---|---|
| POMP1001, POMP1002 | `-p:PompOmpExe=`가 절대 경로가 아니거나 파일이 없음 | 오류 | 오류 |
| POMP1003 | `src\Pomp.WebUI\dist`가 없음 | 실패 | 경고, 자리표시 페이지 사용 |
| POMP1004 | `dist`가 WebUI 소스보다 오래됨 | 실패 | 경고 |
| POMP1006 | 소스나 산출물이 `pomp-bundle.json` 지문과 다름 | 실패 | 경고 |
| POMP1007 | `src\Pomp.OmpExtension`의 `vendor`가 없음 | 실패 | 프로젝트 규칙·리소스 확장이 동작하지 않음 |
| POMP1011 | 엔진 크기·해시가 `engine.props`와 다름 | 실패 | 실패 |
| POMP1012 | 엔진 고지 파일이 맞지 않음 | 실패 | 실패 |

## 실행 / 디버깅

### VSIX 설치

`artifacts\`의 `.vsix`를 더블클릭한다.

| 파일 | 대상 |
|---|---|
| `Pomp.VS2017-<버전>.vsix` | VS 2017 15.9 |
| `Pomp.VS2019-<버전>.vsix` | VS 2019 |
| `Pomp.VS2022-<버전>.vsix` | VS 2022·2026 |

### F5 디버깅

VS 2022/2026에서 다음 순서로 한다.

1. npm 단계를 먼저 실행한다. `build\build.ps1`을 한 번 실행해도 된다.
   - `src\Pomp.WebUI`에서 `npm ci` 후 `npm run build`를 실행한다.
   - `src\Pomp.OmpExtension`에서 `npm ci --ignore-scripts` 후 `npm run build:vendor`를 실행한다.
   - 결과물인 `dist`와 `vendor`는 생성물이므로 커밋하지 않는다.
   - `dist`가 없으면 VSIX에 자리표시 페이지가 들어간다. `vendor`가 없으면 프로젝트 규칙·리소스 확장이 동작하지 않는다.
2. 솔루션 탐색기에서 `Pomp.Vsix.VS2022`를 시작 프로젝트로 설정한다.
3. Debug 구성으로 F5를 누른다. `devenv /rootsuffix Exp` 실험 인스턴스가 열린다.

Debug 빌드는 실행 중인 IDE 주 버전에 맞는 플레이버만 실험 인스턴스에 배포한다. 다른 플레이버는 그 IDE에 설치될 수 없기 때문이다.

| 실행 중인 IDE | 배포하는 플레이버 |
|---|---|
| VS 2022·2026 | VS2022 |
| VS 2019 | VS2019 |
| VS 2017 | VS2017 |

### 실행 중 동작

- WebView2 런타임이 없거나 오래되면 도구 창에 설치 안내가 나온다. 이때 omp는 시작하지 않는다.
- 화면 언어는 **도구 > 옵션 > Pomp > Chat > Language**에서 고른다.
  - 선택지는 자동(VS 표시 언어), 한국어, English다.
  - 적용하면 즉시 바뀌고 엔진을 다시 시작하지 않는다.
- 도구 창을 닫았다 열거나 페이지를 다시 불러오면 호스트가 현재 대화를 다시 보여 준다. 최대 3 MiB, 3000개까지다.
- 엔진이 다시 시작되면 새 대화로 시작한다. 이전 대화는 채팅 상단 ▾ 대화 목록이나 `/sessions`에서 다시 열 수 있다.

### 런타임 데이터

| 위치 | 내용 |
|---|---|
| `%LOCALAPPDATA%\Pomp\` | 로그, 오버레이, VS 상태 파일, WebView2 데이터 |
| `%LOCALAPPDATA%\Pomp\logs\pomp-vs.log` | 로그 파일 |
| `%LOCALAPPDATA%\Pomp\workspace\` | 솔루션이 없을 때의 작업 폴더 |
| `<작업 영역>\.pomp\local\` | 프로젝트별 데이터. 대화, 체크포인트 등. 지식은 커밋되는 `.pomp\knowledge.json`에 있다. |

## 현재 구현 범위

### 기반

- 실제 omp RPC 브릿지
- 기본 호스트 툴 **19개**와 VS 기능 어댑터의 `propose_workspace_edits` 등록
- 스트리밍 채팅, 승인 UI, 세션/재연결
- 솔루션이나 옵션이 바뀌면 재시작
- 에디터 컨텍스트, 저장하지 않은 버퍼 읽기, 솔루션 탐색, 에러 목록
- 승인 후 빌드
- 디버거 상태·중단점·열린 문서 책갈피 조회

### 버퍼 편집 (`edit_buffer_hashline`)

- 모델이 완전히 본 줄만 기준으로 PUT/CUT 편집을 준비한다.
- 정확한 Diff로 승인을 받는다.
- 승인한 결과 그대로 VS 편집기 버퍼에 적용하고, 저장하지 않는다.
- 파일별로 Undo 1단계로 되돌릴 수 있다.
- 여러 파일을 편집하면 일부만 적용될 수 있고, 그 경우 그렇게 보고한다.

### 파일 추가 (`add_project_file`)

- C#/VB 프로젝트에 새 파일을 만들고 VS 프로젝트 시스템으로 등록한다.
- 만들 파일의 전체 내용을 Diff로 승인받은 뒤에만 쓴다.
- 기존 파일은 덮어쓰지 않는다.
- 실패하면 남은 파일을 보고한다.
- 다음 경로는 준비 단계에서 거부한다.
  - 앞뒤 공백이 있는 경로
  - Visual Studio 경로 길이를 넘는 경로. 파일은 259자, 폴더는 247자가 한도다.

### 추가 통합

| 영역 | 기능 |
|---|---|
| 프로젝트 | 프로젝트 파일 이름 변경, MSBuild 속성 조회 |
| 출력 | 출력 창 조회 |
| Git | 상태, 커밋, 푸시 |
| 테스트 | 탐색, 실행 |
| 디버거 | 중단점 변경, 디버거 제어 |
| 책갈피 | 책갈피 추가 |

변경·실행 작업은 기존 호스트 승인 게이트를 거친다. 테스트 **탐색도** 빌드·테스트 어댑터 코드를 실행할 수 있으므로 `testRun` 승인을 요구한다.

### 등록하지 않는 툴

- POMP의 `remove_project_file`은 계속 비활성으로 둔다.
- 파일 삭제는 omp 자체의 승인 게이트가 있는 `delete` 툴을 사용한다.
- 파일 이동도 omp의 승인 게이트가 있는 `move` 툴을 따른다.

### NuGet

- 설치 패키지 조회와 단일 프로젝트의 설치·업데이트·제거를 VS NuGet 서비스에 위임한다.
- 변경 전에 대상 프로젝트, 패키지, 버전, 네트워크 및 코드 실행 영향을 승인받는다.
- 승인 후 프로젝트와 패키지 상태를 다시 검사한다.
- 제거할 때 의존 패키지는 유지한다.
- NuGet 호출이 시작된 뒤의 취소는 즉시 중단할 수 없다. 실패하면 부분 변경 여부를 확인해야 한다.

### 채팅 UI

- @파일 멘션
- 개수 제한 없는 첨부
- 나란히 Diff. 좁은 창에서는 inline으로 보인다.
- 호스트 대화 재생
- 승인으로 가려진 질문의 초안 보존
- 응답 중 전송의 대기열. 개수 제한이 없다.
- 에이전트·팀 대화 상대 탭

질문이나 승인을 기다리는 동안에는 새 메시지 전송과 재시도를 막는다. 초안 입력은 유지한다.

중지는 omp가 기다리는 승인도 거부로 풀어 준다.

### 파일 보호의 한계

- 경로 검사는 작업 루트 아래 reparse point를 거부한다. 하지만 파일시스템 샌드박스는 아니다.
- pre-edit 훅은 지원하는 내장 편집 툴만 보호한다.
- pre-edit 훅은 상태 파일을 읽지 못하면 fail-open이다.
- 셸, MCP 등을 통한 모든 파일 변경을 막는 기능으로 간주하면 안 된다.

## 보안 주의

### 승인 계층

- 안전 경계는 다음으로 이루어진다.
  - omp의 read/write/exec 승인 계층
  - 기본 `--approval-mode always-ask`
  - `tools.approval`의 allow/prompt/deny 규칙
  - bash 명령 패턴 규칙. 치명적 파괴 명령은 기본 차단한다.
- omp는 OS 샌드박스를 제공하지 않는다.
- POMP의 실험적 AppContainer 실행기도 기본 꺼짐을 유지하며, 샌드박스로 안내하지 않는다.
- 쓰기·실행은 승인 게이트를 거친다. 다만 사용자가 부여한 세션/영구 허용 규칙이나 omp 승인 모드 변경에 따라 재확인이 생략될 수 있다.

### omp 확장과 작업 영역 구성

- omp는 기본적으로 `--no-extensions`로 시작한다.
  - 사용자 프로필, 플러그인, 솔루션 폴더의 `.omp/extensions`에 있는 omp 확장과 훅은 로드하지 않는다.
  - pomp 자체 패키지는 로드한다.
- 필요하면 **도구 > 옵션 > Pomp > Load discovered omp extensions**를 켠다.
  - 이런 확장은 샌드박스 없이 실행된다. 신뢰하는 솔루션에서만 켠다.
- 확장을 끈 상태에서도 omp는 솔루션 폴더의 다음 구성을 읽고 실행한다.
  - `.omp` 설정. 도구 승인 규칙을 포함한다.
  - MCP 서버
  - 사용자 정의 도구
  - 언어 서버 실행 파일
- 이런 구성이 있는 작업 영역에서는 사용자가 채팅의 신뢰 카드에서 그 목록을 신뢰한 뒤에만 pomp가 omp를 시작한다.
- 내용이 바뀌면 다시 묻는다. 자세한 내용은 [아키텍처의 "작업 영역 신뢰"](architecture.md)를 본다.
- omp는 POMP 데이터 폴더에서 시작하므로 솔루션의 `.env`는 읽지 않는다.

### 매번 묻는 승인

omp 하위 에이전트(`task`)는 omp 안에서 개별 승인 없이 실행된다. `task` 호출을 승인하면 그 안의 작업 전체를 승인하는 셈이다.

그래서 `task` 승인은 기억하지 않고 매번 묻는다. 다음 작업도 같다.

- 실행
- Git
- 패키지
- 디버거
- 테스트
- 컴퓨터 사용
- 정책·엔진 설정 파일 수정

### 자격 증명

- 옵션의 추가 인수는 평문으로 저장된다.
- API 키는 인수 대신 계정 탭의 API 키 저장이나 환경 변수를 쓴다.
- 로그에는 키 값이 가려진다.

### 지원 범위

- VS 2019의 최소 대상은 **16.10**이다.
- VS 2019 16.0~16.9와 ARM64 VS는 현재 지원 대상이 아니다.

## 편집기 기능

### 연결된 기능

- 선택 영역이나 커서 위치의 POMP 프롬프트. Alt+M으로 연다.
  - 채팅으로 질문
  - 채팅 입력창으로 옮겨 이어서 질문
  - 수정 제안
- 줄별 변경 검토와 Shadow 컴파일
- 편집기 버퍼 적용
- 선택적 자동완성. Tab 또는 Ctrl+Right로 받는다.

### 우클릭 메뉴

코드 편집창 우클릭 메뉴는 **POMP에게 질문**, **POMP** 순서다. 질문 항목은 하위 메뉴에 다시 넣지 않는다.

POMP 하위 메뉴는 다음 순서로 묶는다.

| 순서 | 그룹 | 항목 |
|---|---|---|
| 1 | 코드 동작 | 설명, 리뷰, 고치기, 테스트 작성 |
| 2 | 컨텍스트 | 파일 추가 |
| 3 | 에이전트·팀에게 | |
| 4 | 채팅 | 열기, 새 대화, 포커스 전환 |
| 5 | 켜기/끄기 | 자동완성, 코드 캡션 |

도구 메뉴에는 POMP 하위 메뉴가 없다. 편집기 우클릭 메뉴, 채팅 창과 중복되기 때문이다.

### 단축키

| 키 | 동작 | 범위 |
|---|---|---|
| `Shift+Alt+E/R/F/U/I` | 코드 동작과 파일 추가 | 텍스트 편집기 |
| `Alt+J` | 채팅 포커스 전환 | |

- VS 명령 키 열 개와 채팅 창 안의 키는 모두 **도구 > 옵션 > Pomp > Shortcuts**에서 정한다.
- 적용하면 VS 키보드 바인딩에 들어간다. VSCT에는 고정 바인딩이 없다.
- 채팅 창 안에서는 페이지가 같은 조합을 직접 처리한다. IME 조합 중에는 전환하지 않는다.

### 자동완성

자동완성은 기본 꺼짐이다. 켜면 주변 코드를 현재 설정한 모델에 전송하므로 제공자 비용이 생길 수 있다.

### 프로젝트 도구 권한 초기 설정

설치만 하면 바로 동작한다. `.pomp/permissions.json`이 없으면 기본 승인 모델을 그대로 쓴다.

- 읽기는 자동이다.
- 파일·프로젝트 변경과 실행은 승인 대화상자를 띄운다.

`.pomp/permissions.json`은 프로젝트를 더 좁게 제한하고 싶을 때만 쓰는 선택 사항이다. 파일이 있으면 다음과 같이 동작한다.

- 그 안에서는 기본 거부로 동작한다. 규칙에 없는 도구는 거부한다.
- 정책을 집행하는 POMP 확장 guard가 꺼져 있거나 없으면 시작을 거부한다.
- 프로젝트 hook은 정책 파일이 있는 작업 폴더에서만 실행된다.

정책 파일은 다음 순서로 적용한다.

1. `src/Pomp.OmpExtension/examples/`의 예제 중 하나를 고르고 내용을 검토한다.
   - `permissions.readonly.json`
   - `permissions.reviewed-src.json`: 검토 편집 예제. `src` 아래의 제안만 허용한다.
2. 필요한 작업공간의 `.pomp/permissions.json`에 직접 적용한다.

- 두 예제 모두 임의 셸 실행은 허용하지 않는다.
- 기존 정책을 덮어쓰거나 `*` 전체 허용으로 바꾸는 자동 동작은 없다.
- 승인 UI는 프로젝트의 명시적 거부를 무시하지 않는다.
- 이 정책이 OS sandbox를 제공한다는 뜻은 아니다.
