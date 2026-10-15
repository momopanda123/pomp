# POMP 개발 문서

POMP를 빌드·디버깅·수정하는 사람을 위한 문서다. 사용법은 [빠른 시작](quick-start.md)과 [화면 가이드](screen-guide.md)를 본다.

- 구현 기준: [`architecture.md`](architecture.md)
- UI ⇄ 호스트 메시지 규약: [`ui-protocol.md`](ui-protocol.md)
- 에이전트·팀 계약: [`agent-teams.md`](agent-teams.md)

## 구성

`pinb`(VSIX 호스트 + IDE 어댑터) + `omp`(oh-my-pi 에이전트 코어) = **pomp**.

| 경로 | 내용 |
|---|---|
| `src/Pomp.Contracts` | 공용 계약 (omp RPC 모델, 브릿지·툴·권한·어댑터 인터페이스, UI 프로토콜) |
| `src/Pomp.Core` | omp 프로세스 브릿지, NDJSON/RPC, 툴 디스패치, 권한 게이트, 세션 컨트롤러 |
| `src/Pomp.Tools` | omp에 노출하는 VS 호스트 툴 (`read_buffer`, `query_solution`, `build_solution` …) |
| `src/Pomp.CodeLens.Contracts` | CodeLens 프로세스 간 전달용 소스 위치 계약 |
| `src/Pomp.CodeLens.Provider` | 선택적 native CodeLens OOP 공급자 (기본 꺼짐, 명시 클릭으로만 요약 요청) |
| `src/Pomp.VisualStudio` | VS 공용 소스 (패키지, 도구 창/WebView2, 테마, 옵션, 어댑터) |
| `src/Pomp.Vsix.VS2017` / `VS2019` / `VS2022` | VS 2017(x86, 15.9 SDK, 기본 CodeLens 캡션 없음) / VS 2019(x86) / VS 2022·2026(x64, 설치 범위 `[17.0,)`) VSIX |
| `src/Pomp.WebUI` | 채팅 UI (Vite + React + TypeScript, 오프라인 번들) |
| `src/Pomp.OmpExtension` | omp 측 확장 (pomp-context Extension, pre-edit Hook, vs-project Skill) |
| `engines/omp` | 번들할 omp 엔진의 고정 버전·해시(`engine.props`)와 라이선스 |

## 개발 환경

필수
- Windows 10/11, Visual Studio 2022 또는 2026 (빌드용 MSBuild), .NET Framework 4.7.2 타겟팅 팩
- .NET SDK 8 이상 (`Pomp.Contracts`/`Core`/`Tools`는 netstandard2.0)
- Node.js 20.19+ 또는 22.12+ (WebUI 빌드)
- 인터넷 연결(첫 빌드만) — 고정된 omp 엔진(현재 18.10.4, `engines/omp/engine.props`)을 공식 릴리스에서 내려받아
  `artifacts\engines\omp\<버전>\`에 캐시한다. 오프라인이면 `-OmpExe`로 같은 파일의 로컬 사본을 준다.

권장
- Visual Studio의 **"Visual Studio 확장 개발"** 워크로드 — F5 디버깅(실험 인스턴스)과 VSIX 매니페스트 편집기용.
  VSIX 빌드 자체는 NuGet의 `Microsoft.VSSDK.BuildTools`만으로 된다.
- 저장소 루트의 [`.vsconfig`](../.vsconfig)를 Visual Studio Installer에서 가져오면 확장 개발 워크로드와
  **.NET Framework 4.7.2 타깃팅 팩**을 추가할 수 있다. 설치된 VS 2022/2026 인스턴스의 **더 보기 > 구성 가져오기**를 사용한다.
  Node.js와 omp는 이 파일로 설치되지 않는다.
- .NET SDK는 빌드용 Visual Studio가 지원하는 버전을 사용한다.

검증 환경(2026-10-05): Windows 11에서 VS 2026 MSBuild로 VSIX를 빌드하고, VS 2019 16.11 실험 인스턴스에 설치해 실제 모델로
편집기 메뉴·설명 요청·대기열·도구·승인·중첩 하위 에이전트 위임을 끝까지 확인했다. VS 2026 18.10 실험 인스턴스에서는 64비트
VSIX의 설치·엔진 연결·메뉴·단축키를 확인했다. VS 2022 자체에서의 실행은 따로 확인하지 않았다(VS 2026과 같은 패키지).

### 확장 사용자 PC의 실행 환경

- **.NET Framework 4.7.2 이상**: VS 설치 과정에서 준비된다. 4.8/4.8.1이 있으면 4.7.2 런타임을 따로 설치하지 않는다.
  개발 PC에는 실행용 런타임과 별개로 위의 **4.7.2 타깃팅 팩**이 필요하다.
- **Microsoft Edge WebView2 Evergreen Runtime 111.0.1661.41 이상**: 채팅 화면에 필요하다. 이미 설치되어 있으면 재설치하지 않는다.
- omp는 따로 설치하지 않는다. VSIX에 POMP가 시험한 omp 엔진이 들어 있다(아래 "omp 엔진").
- 배포된 VSIX를 사용하는 PC에는 .NET SDK, Node.js, 타깃팅 팩, 확장 개발 워크로드가 필요하지 않다.

### omp 엔진과 로그인

POMP 릴리스마다 시험한 omp 버전 하나가 VSIX에 들어 있고(`<확장 폴더>\omp\omp.exe`, 현재 18.10.4), 시작할 때 크기와
SHA-256으로 그 파일인지 확인한다. omp 업데이트는 POMP 업데이트로만 들어오며, PC에 설치된 omp가 있거나 그것이 업데이트되어도
POMP에는 영향이 없다. 엔진 파일이 손상되면 다른 omp로 대신하지 않고 재설치를 안내한다. 검증된 내장 엔진이 아니면 ⋯ 메뉴 아래쪽에
"에이전트 <버전> · <어디서 온 실행 파일> · 검증 안 됨"이 보이고 채팅 위에 경고가 뜬다.

고급: 도구 > 옵션 > Pomp의 omp 경로에 다른 omp를 지정할 수 있지만 "검증 안 됨"으로 표시되고 채팅 위에 경고가 뜬다.
버전이 다르면 일부 기능이 동작하지 않을 수 있다. 자세한 내용은 [`architecture.md`](architecture.md) §4 "엔진 버전".

모델 제공자 로그인은 채팅 창의 계정 탭에서 한다(터미널 불필요).

## 빌드

```powershell
# WebUI 빌드 + 세 VSIX 빌드 → artifacts\Pomp.VS2017-<버전>.vsix, Pomp.VS2019-<버전>.vsix, Pomp.VS2022-<버전>.vsix (-Flavor로 하나만)
powershell -NoProfile -File build\build.ps1

# 오프라인 빌드: 고정 엔진의 로컬 사본을 사용 (SHA-256이 engine.props와 같아야 함)
powershell -NoProfile -File build\build.ps1 -OmpExe D:\offline\omp-windows-x64.exe
```

빌드 스크립트는 PowerShell 실행 정책을 변경하지 않는다. 조직 정책이 실행을 막으면 해당 정책에 맞는 승인/서명 절차를 따른다.
모든 VSIX에 고정 omp 엔진이 들어간다(Release VSIX 약 117 MB, 압축하지 않는 Debug 패키지는 약 245 MB). 빌드는 엔진(약 229 MiB)을 처음 한 번만 내려받고, 크기·해시가
`engines/omp/engine.props`와 다르면 실패한다(POMP1011, 고지 파일은 POMP1012). 엔진 없는 개발 패키지는 `-p:PompBundleOmp=false`.
`-OmpExe`의 상대 경로는 현재 폴더 기준으로 해석되며, 파일이 없으면 npm/MSBuild 전에 실패한다.
MSBuild에 직접 `-p:PompOmpExe=`를 줄 때는 존재하는 파일의 **절대 경로**여야 한다(아니면 POMP1001/POMP1002 오류).
C# 언어 버전은 `12.0`으로 고정되어 있다(`Directory.Build.props`).
VSIX에 넣는 채팅 UI는 `src\Pomp.WebUI\dist`의 빌드 결과다. Release 빌드는 이것이 없으면(POMP1003, 자리표시 페이지를 쓰려면
`-p:PompAllowPlaceholderUi=true`) 또는 WebUI 소스보다 오래되었으면(POMP1004) 실패하고, Debug 빌드는 경고만 낸다.
`npm run build`는 UI 소스와 모든 산출물의 SHA-256 지문을 `dist\pomp-bundle.json`에 기록한다. VSIX 빌드는
`node scripts\bundle-integrity.mjs --verify`로 소스·산출물이 지문과 같은지(내용 변경, 누락·추가 파일, 외부 또는 `dist` 밖
참조)를 확인하며, 다르면 Release는 POMP1006으로 실패하고 Debug는 경고한다. 이 검사에는 빌드 PC의 Node.js가 필요하다
(설치 사용자는 필요 없음).

## 실행 / 디버깅

- VSIX 설치: `artifacts\`의 `.vsix` 더블클릭 (`Pomp.VS2017-<버전>.vsix` VS 2017 15.9, `Pomp.VS2019-<버전>.vsix` VS 2019,
  `Pomp.VS2022-<버전>.vsix` VS 2022·2026).
- 디버깅(F5, VS 2022/2026):
  1. npm 단계를 먼저 실행한다(또는 `build\build.ps1` 한 번): `src\Pomp.WebUI`에서 `npm ci` 후 `npm run build`, `src\Pomp.OmpExtension`에서
     `npm ci --ignore-scripts` 후 `npm run build:vendor`. 둘 다 생성물이라 커밋하지 않는다. `dist`가 없으면 VSIX에 자리표시 페이지가
     들어가고, `vendor`가 없으면 프로젝트 규칙·리소스 확장이 동작하지 않는다(Release 빌드는 각각 POMP1003·POMP1007로 실패).
  2. 솔루션 탐색기에서 `Pomp.Vsix.VS2022`를 시작 프로젝트로 설정하고 Debug로
     F5 → `devenv /rootsuffix Exp` 실험 인스턴스. Debug 빌드는 실행 중인 IDE 주 버전에 맞는 플레이버만 실험 인스턴스에 배포한다
     (VS 2022·2026 → VS2022, VS 2019 → VS2019, VS 2017 → VS2017). 다른 플레이버는 그 IDE에 설치될 수 없기 때문이다.
- WebView2 런타임이 없거나 오래되면 도구 창에 설치 안내가 나오고 omp는 시작하지 않는다.
- 화면 언어: **도구 > 옵션 > Pomp > Chat > Language** — 자동(VS 표시 언어) / 한국어 / English. 적용 즉시 바뀌며
  엔진을 다시 시작하지 않는다.
- 도구 창을 닫았다 열거나 페이지를 다시 불러오면 호스트가 현재 대화를 다시 보여 준다(최대 3 MiB/3000개). 엔진이 다시 시작되면
  새 대화로 시작하며, 이전 대화는 채팅 상단 ▾ 대화 목록이나 `/sessions`에서 다시 열 수 있다.
- 런타임 데이터: `%LOCALAPPDATA%\Pomp\` (로그 `logs\pomp-vs.log`, 오버레이, VS 상태 파일, WebView2 데이터,
  솔루션이 없을 때의 작업 폴더 `workspace\`). 프로젝트별 데이터는 `<작업 영역>\.pomp\local\`(대화·프리셋·지식·체크포인트 등).

## 현재 구현 범위

- 실제 omp RPC 브릿지와 기본 호스트 툴 **19개**와 VS 기능 어댑터의 `propose_workspace_edits` 등록, 스트리밍 채팅, 승인 UI, 세션/재연결, 솔루션·옵션 변경 시 재시작.
- 에디터 컨텍스트, 저장하지 않은 버퍼 읽기, 솔루션 탐색, 에러 목록, 승인 후 빌드, 디버거 상태·중단점·열린 문서 책갈피 조회.
- **버퍼 편집(`edit_buffer_hashline`):** 모델이 완전히 본 줄만 기준으로 한 PUT/CUT 편집을 준비해 정확한 Diff로 승인받고, 승인한
  결과 그대로 VS 편집기 버퍼에 적용한다(파일별 Undo 1단계, 저장하지 않음, 여러 파일은 부분 적용될 수 있고 그렇게 보고함).
- **파일 추가(`add_project_file`):** C#/VB 프로젝트에 새 파일을 만들고 VS 프로젝트 시스템으로 등록한다. 만들 파일의 전체
  내용을 Diff로 승인받은 뒤에만 쓰고, 기존 파일은 덮어쓰지 않으며, 실패하면 남은 파일을 보고한다. 앞뒤 공백이 있는 경로와
  Visual Studio 경로 길이(파일 259자, 폴더 247자)를 넘는 경로는 준비 단계에서 거부한다.
- **추가 통합:** 프로젝트 파일 이름 변경, MSBuild 속성·출력 창 조회, Git 상태/커밋/푸시, 테스트 탐색/실행,
  중단점 변경·디버거 제어·책갈피 추가. 변경·실행 작업은 기존 호스트 승인 게이트를 거친다.
  테스트 **탐색도** 빌드·테스트 어댑터 코드를 실행할 수 있어 `testRun` 승인을 요구한다.
- **영구 등록 제외:** POMP의 `remove_project_file`은 계속 비활성으로 두고, 파일 삭제는 omp 자체의 승인 게이트가 있는
  `delete` 툴을 사용한다. 파일 이동도 omp의 승인 게이트가 있는 `move` 툴을 따른다.
- **NuGet:** 설치 패키지 조회와 단일 프로젝트의 설치·업데이트·제거를 VS NuGet 서비스에 위임한다. 변경 전 대상 프로젝트·패키지·버전·네트워크 및 코드 실행 영향을 승인받고, 승인 후 프로젝트와 패키지 상태를 재검사한다. 제거 시 의존 패키지는 유지한다. NuGet 호출 시작 후 취소는 즉시 중단할 수 없으며 실패 시 부분 변경 여부를 확인해야 한다.
- **채팅 UI:** @파일 멘션, 개수 제한 없는 첨부, 나란히 Diff(좁은 창은 inline), 호스트 대화 재생, 승인으로 가려진
  질문의 초안 보존, 응답 중 전송의 대기열(개수 제한 없음), 에이전트·팀 대화 상대 탭. 질문·승인 대기 중에는 새 메시지 전송/재시도를
  막되 초안 입력은 유지한다. 중지는 omp가 기다리는 승인도 거부로 풀어 준다.
- 경로 검사는 작업 루트 아래 reparse point를 거부하지만 파일시스템 샌드박스는 아니다. pre-edit 훅은 지원하는 내장 편집 툴만 보호하며
  상태 파일을 읽지 못하면 fail-open이다. 셸·MCP 등을 통한 모든 파일 변경을 막는 기능으로 간주하면 안 된다.

### 보안 주의

- 안전 경계는 omp의 read/write/exec 승인 계층, 기본 `--approval-mode always-ask`, `tools.approval`의
  allow/prompt/deny와 bash 명령 패턴 규칙(치명적 파괴 명령 기본 차단)이다. omp는 OS 샌드박스를 제공하지 않으며,
  POMP의 실험적 AppContainer 실행기도 기본 꺼짐을 유지하고 샌드박스로 안내하지 않는다.
- 쓰기·실행은 승인 게이트를 거치지만, 사용자가 부여한 세션/영구 허용 규칙이나 omp 승인 모드 변경에 따라 재확인이 생략될 수 있다.
- omp는 기본적으로 `--no-extensions`로 시작한다: 사용자 프로필·플러그인·솔루션 폴더의 `.omp/extensions`에 있는 omp 확장과
  훅은 로드하지 않는다(pomp 자체 패키지는 로드). 필요하면 **도구 > 옵션 > Pomp > Load discovered omp extensions**를 켠다 —
  이런 확장은 샌드박스 없이 실행되므로 신뢰하는 솔루션에서만 켠다.
- 그래도 omp는 솔루션 폴더의 `.omp` 설정(도구 승인 규칙 포함)·MCP 서버·사용자 정의 도구·언어 서버 실행 파일을 읽고 실행한다.
  pomp는 이런 구성이 있는 작업 영역에서는 채팅의 신뢰 카드에서 사용자가 그 목록을 신뢰한 뒤에만 omp를 시작하고, 내용이 바뀌면
  다시 묻는다([아키텍처 "작업 영역 신뢰"](architecture.md)). omp는 POMP 데이터 폴더에서 시작하므로 솔루션의 `.env`는 읽지 않는다.
- omp 하위 에이전트(`task`)는 omp 안에서 개별 승인 없이 실행된다. `task` 호출을 승인하는 것이 그 안의 작업 전체를 승인하는 것이므로
  `task` 승인은 기억하지 않고 매번 묻는다(실행·Git·패키지·디버거·테스트·컴퓨터 사용과 정책·엔진 설정 파일 수정도 같다).
- 옵션의 추가 인수는 평문으로 저장된다. API 키는 인수 대신 계정 탭의 API 키 저장이나 환경 변수를 쓴다(로그에는 키 값이 가려진다).
- VS 2019의 최소 대상은 **16.10**이다. 16.0~16.9와 ARM64 VS는 현재 지원 대상이 아니다.

## 편집기 기능

선택 영역·커서 위치의 POMP 프롬프트(Alt+M: 채팅으로 질문, 채팅 입력창으로 옮겨 이어서 질문, 수정 제안), 줄별 변경 검토와 Shadow 컴파일, 편집기 버퍼 적용, 선택적 자동완성(Tab/Ctrl+Right)이 연결되어 있다.
코드 편집창 우클릭은 **POMP에게 질문 → POMP** 순서다. POMP 하위 메뉴는 코드 동작(설명·리뷰·고치기·테스트 작성) → 컨텍스트(파일 추가) → 에이전트·팀에게 → 채팅(열기·새 대화·포커스 전환) → 켜기/끄기(자동완성·코드 캡션)로 나누며, 질문은 중복하지 않는다.
코드 동작과 파일 추가의 단축키는 `Shift+Alt+E/R/F/U/I`(텍스트 편집기 범위), 채팅 포커스 전환은 `Alt+J`이다. VS 명령 키 열 개와
채팅 창 안의 키는 모두 도구 > 옵션 > Pomp > Shortcuts에서 정하고, 적용하면 VS 키보드 바인딩에 들어간다(VSCT에는 고정 바인딩이 없다;
채팅 창 안에서는 페이지가 같은 조합을 직접 처리하고 IME 조합 중에는 전환하지 않는다). 도구 > POMP는 채팅 → POMP에게 질문 → 켜기/끄기 순서다.
자동완성은 기본 꺼짐이며, 켜면 주변 코드를 현재 설정한 모델에 전송하므로 제공자 비용이 생길 수 있다.

### 프로젝트 도구 권한 초기 설정

설치만 하면 바로 동작한다: `.pomp/permissions.json`이 없으면 기본 승인 모델(읽기는 자동, 파일·프로젝트 변경과 실행은 승인 대화상자)을 그대로 쓴다. 이 파일은 프로젝트를 더 좁게 제한하고 싶을 때만 쓰는 선택 사항이며, 있으면 그 안에서는 기본 거부(규칙에 없는 도구는 거부)로 동작한다. 정책 파일이 있는 작업 폴더에서는 그 정책을 집행하는 POMP 확장 guard가 꺼져 있거나 없으면 시작을 거부한다. 프로젝트 hook은 정책 파일이 있는 작업 폴더에서만 실행된다.

`src/Pomp.OmpExtension/examples/permissions.readonly.json` 또는 `permissions.reviewed-src.json`의 내용을 검토한 뒤, 필요한 작업공간의 `.pomp/permissions.json`에 사용자가 직접 적용한다. 기존 정책을 덮어쓰거나 `*` 전체 허용으로 바꾸는 자동 동작은 없다. 두 예제 모두 임의 셸 실행은 허용하지 않으며, 검토 편집 예제는 `src` 아래의 제안만 허용한다. 승인 UI는 프로젝트의 명시적 거부를 무시하지 않는다. OS sandbox를 제공한다는 뜻은 아니다.
