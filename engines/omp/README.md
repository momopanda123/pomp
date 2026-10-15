# POMP의 omp 엔진

VSIX에는 **공식 omp 릴리스 바이너리**([oh-my-pi](https://github.com/can1357/oh-my-pi))가 수정 없이 들어간다. 버전·크기·SHA-256은
`engine.props`가 고정하고, 빌드가 내려받아 검증하며(`src/Pomp.VisualStudio/Pomp.Vsix.Common.targets`의 `PompPrepareOmpEngine`),
POMP는 실행 시 같은 값으로 다시 확인한다. `LICENSE.upstream`(MIT)과 릴리스의 `THIRD-PARTY-NOTICES.txt`가 함께 패키지에 들어간다.
정책 전체는 `docs/architecture.md` §4 "엔진 버전".

omp를 올릴 때는 `engine.props`만 바꾼다(버전, 릴리스 URL, 크기, SHA-256, 고지 파일 SHA-256 — 모두 공식 릴리스 값).

POMP는 엔진이 광고하는 경우에만 쓰는 선택 기능(`pompControlVersion`, `pomp_terminal` 등)을 지원하지만, 공식 바이너리에는 없으므로
번들 엔진에서는 꺼져 있다.
