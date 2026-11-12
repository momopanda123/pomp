# POMP의 omp 엔진

VSIX에는 [oh-my-pi](https://github.com/can1357/oh-my-pi) **v18.18.9** 소스(커밋 `50da2d357039404a81d7c64622740ce13e4c54b4`)에
[`pomp-v18.18.9.patch`](pomp-v18.18.9.patch)를 적용해 빌드한 Windows x64 `omp.exe`가 들어간다. 패치는 새 Win32 창을 여는
`takeover` 클릭과, 다른 앱이 앞에 있을 때의 첫 `takeover`·`raise` 창 활성화를 고친다(Windows는 마지막 입력을 보낸 프로세스에만
포그라운드를 넘기므로, 활성화 직전 이동 거리 0인 마우스 입력 하나로 그 권한을 얻는다). 또 모델 공유용 auth gateway의 `/v1/models`
행마다 그 모델의 노력 단계(`reasoning_efforts`)를 넣는다. 공유 PC가 실행 중에 찾은 모델은 연결한 PC의 카탈로그에 없어서, 이 목록이
없으면 그 모델의 추론 단계를 고를 수 없다. 18.12.6 패치에 있던 Codex 하위 에이전트의 진행 중 메시지 처리는 상위가 고쳐서 뺐다(GPT
실시간 조정은 기본 꺼짐, 다음 도구 경계까지 기다림). 공식 릴리스 실행 파일을 대신 담으면 안 된다.
`engine.props`의 크기·SHA-256을 VSIX 빌드와 POMP 시작 시 모두 검증한다. MIT `LICENSE.upstream`과 함께, 태그에서 만든 고지
파일 `THIRD-PARTY-NOTICES.txt`(`omp --license`가 출력하는 내용: OMP 라이선스, CLI와 네이티브 애드온의 고지)도 별도 해시로
검증해 패키지에 넣는다. 18.13부터 릴리스에 고지 파일이 따로 없다. 정책은 `docs/architecture.md` §4 "엔진 버전" 참조.

깨끗한 작업 트리에서 `build/build-omp.ps1`을 실행하려면 Git, Bun 1.4.4+, Rust/Cargo(MSVC x64), Visual Studio C++
빌드 도구가 필요하다. 스크립트가 고정 커밋을 가져와 패치를 적용하고 네이티브 애드온과 실행 파일을 만든 뒤
`artifacts/engines/omp/18.18.9/`에 보관한다. `build/build.ps1`도 캐시가 없거나 해시가 다르면 이 스크립트를 실행한다.
버전을 올릴 때는 소스 커밋·패치·빌드 스크립트·`engine.props`의 크기/해시/출처를 함께 갱신하고 실제 동작을 검증한다.

18.18.9 패치 빌드는 상위 CI처럼 `HOME`·`USERPROFILE`·`LOCALAPPDATA`·`XDG_DATA_HOME`을 임시 폴더로 격리한
`omp --smoke-test`를 통과했다. POMP 확장을 로드한 격리 RPC에서도 `pomp-*` 명령 23개, 읽기·승인 후 bash 실행,
`/context`의 지시 파일 토큰 합산과 확장 오류 없음을 확인했다.

POMP는 엔진이 광고하는 경우에만 쓰는 선택 기능(`pompControlVersion`, `pomp_terminal` 등)을 지원하지만,
이 패치는 해당 기능을 추가하지 않으므로 번들 엔진에서는 꺼져 있다.
