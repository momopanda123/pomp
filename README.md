# POMP

**한국어** | [English](README.en.md)

**Visual Studio 안의 AI 코딩 파트너.** 편집기 옆 도구 창에서 AI 에이전트와 대화하며 코드를 읽고, 고치고, 빌드·테스트·디버깅까지
맡길 수 있다. Visual Studio 2019(16.10+) · 2022 · 2026을 지원한다.

| 구성 요소 | 버전 |
|---|---|
| POMP | 0.5.4 |
| omp | 18.10.4 |

![POMP 소개](docs/media/pomp-intro.gif)

<!-- howto-videos -->
## 기능별 영상

화면은 한국어 UI다. 단축키는 도구 › 옵션 › Pomp에서 바꿀 수 있어 영상에 넣지 않았다.

**편집기에서 코드 설명** — 코드를 선택하고 우클릭 › POMP › 이 코드 설명. 설명은 채팅에 나온다.

![편집기에서 코드 설명](docs/media/howto-explain.gif)

**POMP에게 질문** — 선택한 줄에 대해 그 자리에서 묻고, 채팅으로 보낸다.

![POMP에게 질문](docs/media/howto-ask.gif)

**수정 제안 승인과 작업 탭** — 고칠 내용을 Diff로 보고 허용하면 편집기 버퍼에 들어간다. 적용 기록은 작업 탭에 남는다.

![수정 제안 승인과 작업 탭](docs/media/howto-fix.gif)

**선택한 줄을 지식으로** — 선택한 줄을 지식에 담아 이름을 붙인다. 대화와 에이전트에 함께 보낼 수 있다.

![선택한 줄을 지식으로](docs/media/howto-knowledge.gif)

**컨텍스트 바구니** — 파일을 바구니에 담고, 담긴 파일 범위로 질문한다.

![컨텍스트 바구니](docs/media/howto-basket.gif)

<table>
  <tr>
    <td align="center" valign="top" width="50%"><b>슬래시 명령 · @ 멘션 · 권한 모드 · 보낼 대상</b><br>입력창에서 명령과 파일을 고르고, 권한 모드와 보낼 대상을 정한다.<br><br><img src="docs/media/howto-slash.gif" width="400" alt="슬래시 명령 · @ 멘션 · 권한 모드 · 보낼 대상"></td>
    <td align="center" valign="top" width="50%"><b>에이전트에게 위임</b><br>&nbsp;<br>역할 에이전트를 대화 상대 탭으로 열고 일을 맡긴다.<br><br><img src="docs/media/howto-partner.gif" width="400" alt="에이전트에게 위임"></td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><b>조언자</b><br>조언자를 켜면 주 에이전트의 작업을 지켜보고 의견을 낸다.<br><br><img src="docs/media/howto-advisor.gif" width="400" alt="조언자"></td>
    <td align="center" valign="top" width="50%"><b>역할 그림과 POMP 지시</b><br>역할마다 그림을 정하고, POMP 자신의 지시를 편집한다.<br><br><img src="docs/media/howto-roles.gif" width="400" alt="역할 그림과 POMP 지시"></td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><b>팀 만들기</b><br>여러 역할을 묶어 팀을 만든다.<br><br><img src="docs/media/howto-team.gif" width="400" alt="팀 만들기"></td>
    <td align="center" valign="top" width="50%"><b>자동화 규칙</b><br>빌드 실패 같은 이벤트에 알림·채팅 전달·스크립트를 건다.<br><br><img src="docs/media/howto-automation.gif" width="400" alt="자동화 규칙"></td>
  </tr>
  <tr>
    <td align="center" valign="top" width="50%"><b>도구 탭</b><br>&nbsp;<br>대화 설정·권한·스킬·MCP·규칙·워크플로·지식을 한곳에서 관리한다.<br><br><img src="docs/media/howto-tools.gif" width="400" alt="도구 탭"></td>
    <td align="center" valign="top" width="50%"><b>계정 · 모델 · 추론 수준</b><br>계정을 연결하고, 입력창 아래에서 모델과 추론 수준을 고른다.<br><br><img src="docs/media/howto-accounts.gif" width="400" alt="계정 · 모델 · 추론 수준"></td>
  </tr>
</table>
<!-- /howto-videos -->

POMP의 에이전트는 VS 자체를 도구로 쓴다 — 열려 있는 편집기 버퍼(저장하지 않은 변경 포함), 솔루션과 프로젝트, 오류 목록, 빌드,
테스트, 디버거, NuGet, Git. 파일을 바꾸거나 무언가를 실행하기 전에는 먼저 묻고, 바꿀 내용은 정확한 Diff로 보여 준다.
VSIX 하나만 설치하면 되고 따로 설치할 것이 없다.

## 주요 기능

- **편집기에서 바로** — 코드를 선택하고 우클릭 **POMP** 메뉴나 단축키로 설명·리뷰·고치기·테스트 작성을 맡긴다. **POMP에게 질문**은
  그 자리에서 선택 영역에 대해 채팅으로 묻거나, 질문을 입력창으로 옮기거나, 코드 수정 제안을 받는다. 보낸 질문에는 어느 파일 몇 번째
  줄인지가 함께 붙는다. 선택한 줄은 색으로 표시되는 **지식**에 담아 두고 대화와 에이전트에 함께 보낼 수 있다.
- **컨텍스트 바구니** — 입력창 위 바구니에 파일·폴더·심볼을 담고, 체크한 것만 다음 메시지와 슬래시 명령·스킬의 범위가 된다.
  **편집기 컨텍스트 포함**을 켜면 지금 보고 있는 파일과 선택 영역도 함께 간다. 자주 쓰는 구성은 프리셋으로 저장한다.
- **승인한 것만 적용** — 변경은 Diff로 보여 주고 승인한 그대로 편집기 버퍼에 넣는다. 파일마다 Undo 한 번으로 되돌릴 수 있고,
  저장은 사용자가 한다. 빌드·테스트·Git·NuGet·디버거 조작도 승인 뒤에 한다. 승인 카드는 `Enter` 허용, `Esc` 거부다.
- **에이전트와 팀** — 리뷰어·설계자·탐색자 같은 역할 에이전트를 대화 상대 탭으로 열어 따로 대화하고, 에이전트가 다른 에이전트에게
  일을 나눠 맡기게 할 수 있다. 여러 역할을 묶은 팀도 같은 방식으로 쓰며, 채팅의 작업 카드에서 구성원 상태를 보고 구성원별로 멈춘다.
  역할마다 그림(모양·색·이미지)을 정하고, POMP에 들어 있는 역할과 POMP 자신의 지시도 편집한다. **조언자**는 대화 옆에서 주 에이전트의
  작업을 지켜보고 의견을 내며, 대화마다 켜고 끄고 의견 기록을 본다. **자동화**는 에이전트 끝남·빌드 실패·파일 저장 같은 이벤트에
  알림·채팅 전달·스크립트를 건다.
- **작업 탭** — **진행**(작업 계획·워크플로·자동 계속), **변경**(대기 중 제안·격리 실행 결과·적용 기록과 되돌리기),
  **코드 점검**(에이전트 지적과 VS 컴파일러 오류·경고를 골라 한 번에 해결 요청; 지적한 에이전트의 그림과 이름이 보인다).
- **도구 탭** — **대화 설정 · 권한 · 스킬 · MCP · 규칙 · 워크플로 · 지식**. 대화마다 쓸 도구를 고르고, 스킬·규칙·워크플로·지식을
  화면 안 표에서 추가·편집·삭제하고, MCP 서버를 추가·테스트하거나 준비된 서버(GitHub·Context7)를 설치한다. 설계·테스트·디버깅·문서 그림
  같은 스킬이 들어 있어 켜기만 하면 된다.
- **여러 계정, 여러 모델** — OpenAI·Anthropic 등 구독 계정 로그인이나 API 키로 연결하고, 직접 띄운 모델 서버(OpenAI 호환 주소)도
  추가한다. 연결한 공급자의 모델을 채팅과 에이전트마다 골라 쓰고, 자동완성·캡션·입력 예측 같은 보조 작업은 **보조 모델**이 맡는다.
  입력창 아래에서 사용량 한도(5시간·주간)를 보고, 초기화 시각이 되면 바로 갱신된다.
- **프로젝트 안의 `.pomp` 폴더** — 프로젝트 범위로 만든 에이전트·팀·규칙·워크플로·스킬·훅·권한 정책은 `.pomp\`에 두어 저장소로
  함께 나누고, 대화·지식·프리셋 같은 POMP 자체 데이터는 `.pomp\local\`(커밋에서 제외)에 둔다. 폴더를 지우면 그 프로젝트의 POMP
  내용이 초기화된다.

## 화면

<table>
  <tr>
    <td align="center"><img src="docs/media/chat-explain.png" width="260" alt="선택한 코드 설명"><br>선택한 코드 설명</td>
    <td align="center"><img src="docs/media/approval-card.png" width="260" alt="승인 카드"><br>실행 전 승인</td>
    <td align="center"><img src="docs/media/partner-delegation.png" width="260" alt="에이전트 위임"><br>에이전트에게 위임</td>
  </tr>
</table>

![Visual Studio 안의 POMP](docs/media/vs-overview.png)

화면별 설명은 [화면 가이드](docs/screen-guide.md)에 있다.

## 설치

필요한 것
- Windows 10/11, Visual Studio 2019 16.10 이상, 2022, 2026 (Community · Professional · Enterprise)
- Microsoft Edge WebView2 Runtime 111 이상 (Windows 11과 최신 VS에는 이미 들어 있다)
- AI 모델 공급자 계정(구독 로그인) 또는 API 키

VS 2017·2019는 32비트, 2022·2026은 64비트 프로그램이어서 패키지를 나눠 배포한다. 64비트 패키지 하나가 2022와 2026을 함께 커버한다.

| 패키지 | 확장 관리에 보이는 이름 | 설치되는 VS |
|---|---|---|
| `Pomp.VS2017-<버전>.vsix` | POMP (VS2017) | Visual Studio 2017 15.9 (기본 CodeLens 캡션 없음) |
| `Pomp.VS2019-<버전>.vsix` | POMP (VS2019) | Visual Studio 2019 16.10 – 16.11 |
| `Pomp.VS2022-<버전>.vsix` | POMP (VS2022) | Visual Studio 2022 (17.x) · 2026 (18.x) |

설치
1. **Visual Studio Marketplace** (등록 준비 중): VS의 **확장 > 확장 관리**에서 `POMP`를 검색해 설치한다.
2. **VSIX 파일**: VS 버전에 맞는 VSIX를 고르고(위 표), Visual Studio를 모두 닫은 뒤 더블클릭해 설치한다.
   직접 빌드하려면 [개발 문서](docs/development.md#빌드)를 따른다(`build\build.ps1` → `artifacts\`).
3. VS를 다시 열고 **보기 > 다른 창 > POMP**(또는 `Shift+Alt+Q`)로 연다. 처음에는 계정 탭이 열린다 — 로그인하거나 API 키를 넣으면
   바로 시작한다.

처음 쓰는 순서는 [빠른 시작](docs/quick-start.md)에 단계별로 정리했다.

## 단축키

| 키 | 동작 |
|---|---|
| `Shift+Alt+Q` | POMP 열기 |
| `Shift+Alt+N` | 새 대화 |
| `Shift+Alt+E` / `R` / `F` / `U` | 이 코드 설명 / 리뷰 / 고치기 / 테스트 작성 |
| `Shift+Alt+I` | 현재 파일을 채팅 컨텍스트에 추가 |
| `Alt+M` | POMP에게 질문 |
| `Alt+/` | 선택한 줄을 지식에 추가 |
| `Alt+J` | 편집기 ↔ 채팅 포커스 전환 |
| `Alt+;` / `Alt+'` | 컨텍스트 바구니 열기 / 편집기 컨텍스트 포함 켜고 끄기 |
| `Ctrl+Alt+T`, `Alt+,` / `Alt+.` | 보낼 대상(에이전트·팀) 목록 / 이전·다음 대상 |
| `Alt+Q` | 권한 모드 고르기 |
| `Alt+A` / `Alt+D` | 승인 카드 허용 / 거부 |

모든 키는 도구 › 옵션 › Pomp › 일반 › 단축키에서 바꾼다(채팅 창 안의 키도 같은 곳). 채팅 입력창에서는 `Enter` 전송, `Shift+Enter` 줄바꿈, `@` 파일 멘션, `/` 명령.

## 문서

| 문서 | 내용 |
|---|---|
| [개요](docs/overview.md) | POMP가 무엇이고 어떻게 동작하는지, 할 수 있는 일, 안전 모델 |
| [빠른 시작](docs/quick-start.md) | 설치부터 첫 질문·첫 변경·에이전트 위임까지 |
| [화면 가이드](docs/screen-guide.md) | 실제 화면으로 보는 탭·버튼 설명 |
| [설정 안내](docs/settings.md) | 도구 > 옵션 > Pomp의 각 설정이 하는 일·기본값·재시작 여부 |
| [에이전트·팀](docs/agent-teams.md) | 역할 에이전트, 팀, 대화 상대, 구성원 중지·자동화의 동작 규칙 |
| [개발 문서](docs/development.md) | 빌드, 디버깅, 구성, 구현 범위 |
| [아키텍처](docs/architecture.md) · [UI 프로토콜](docs/ui-protocol.md) | 구현 기준과 내부 메시지 규약 |

## 안전에 관해

- 기본 승인 모드는 매번 묻기(`Always ask`)다. 파일·프로젝트 변경, 빌드·테스트·셸 실행, Git·NuGet·디버거 조작은 승인 뒤에만 한다.
  "이 세션 동안 허용"·"항상 허용"을 고르면 그 도구는 다시 묻지 않는다.
- 하위 에이전트 실행(`task`)을 허용하면 그 에이전트는 작업이 끝날 때까지 도구별로 다시 묻지 않는다 — 승인 카드가 이를 알려 준다.
- POMP는 OS 샌드박스가 아니다. 신뢰할 수 없는 저장소의 도구 규칙·MCP 설정 같은 실행 설정은 시작 전 신뢰 카드에서 검토한다.
- 네트워크 도구(웹 검색·URL 읽기·GitHub·브라우저)는 기본으로 허용되고 모두 기록된다. 옵션 **네트워크 도구 차단**으로 끈다.
- 사용 통계는 이 PC의 `%LOCALAPPDATA%\Pomp\usage-metrics.json`에만 남고 어디로도 보내지 않는다.

자세한 내용은 [개요의 안전 모델](docs/overview.md#안전-모델)과 [개발 문서의 보안 주의](docs/development.md#보안-주의).

## 라이선스

**POMP**(이 저장소의 코드·문서·화면)는 **`momopanda123`**이 만들고 배포하는 소스 공개(source-available) 소프트웨어로,
**PolyForm Noncommercial License 1.0.0**을 따른다. 개인·비상업적 사용은 무료이며 상업적 사용은 허용하지 않는다.
전체 조건은 [`LICENSE.txt`](LICENSE.txt). Marketplace Publisher ID는 **`momopanda123`**이다.

POMP의 대화·에이전트 엔진 **omp**는 [oh-my-pi](https://github.com/can1357/oh-my-pi)(**MIT 라이선스**)의 공식 릴리스 바이너리로,
POMP의 PolyForm 라이선스가 적용되지 않는다. VSIX 안의
`omp/THIRD-PARTY-NOTICES.txt`에 omp 자체와 포함된 오픈 소스 구성 요소의 라이선스(MIT 등)가 들어 있다.
그 밖의 포함된 오픈 소스 구성 요소도 각자의 라이선스를 유지한다(포함된 구성 요소의 `LICENSE.txt`·`THIRD-PARTY-NOTICES.txt` 참조).

이 문서와 화면에 나오는 다른 회사의 제품·서비스 이름은 각 소유자의 상표다. POMP는 그 회사들과 관계가 없고, 그 이름은 연동·호환 대상을
가리키는 데만 쓴다.
