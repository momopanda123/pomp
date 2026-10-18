---
name: chat-style
description: "Reply formatting for the POMP chat in Visual Studio: bold one-line conclusion first, ### headings with one emoji, traffic-light status marks, blank line between items, --- between sections. Use for every user-facing reply in POMP chat that reports status, findings, options or next steps, or when the user says /skill:chat-style."
---

# POMP 채팅 답변 스타일

POMP 채팅 창에서 읽기 쉬운 답변 서식이다. 상태 보고, 조사 결과, 선택지, 다음 할 일을 답할 때 쓴다.
한두 문장짜리 짧은 대답에는 소제목·구분선 없이 문장만 쓴다.

## 구조
1. 맨 위에 `### 📌 결론` 소제목과 굵은 한 줄 결론을 둔다. 결론이 먼저, 근거는 뒤에.
2. 문단(소제목 묶음) 사이에는 `---` 구분선을 둔다.
3. 줄(항목) 사이에는 빈 줄 1개를 둔다.
4. 끝에는 `### ➡️ 다음 할 일`로 사용자가 정할 것이나 내가 할 일을 적는다. 정할 것은 `❓`로 시작한다.
5. 내용이 없는 소제목은 만들지 않는다.

## 소제목 이모지 (고정)
- 📌 결론 · 정리
- 📋 현재 상태 · 목록 · 적용 규칙
- 🔍 조사 · 확인한 것
- ⚠️ 위험 · 주의 · 확인하지 못한 것
- ➡️ 다음 할 일

소제목 글은 사용자가 쓰는 언어로 쓴다. 영어로 대화하면 Conclusion, Current state, Findings, Risks, Next steps로 쓰고 이모지는 그대로 둔다.

## 상태 신호 (줄 맨 앞에 하나만)
- 🟢 확인됨 · 통과 · 완료 - 이번 턴에 도구로 직접 확인한 것만
- 🟡 주의 · 미확인 · 결정 필요 · 추정(`[INFERENCE]` 를 함께 적는다)
- 🔴 실패 · 위험 · 복구 불가
- 🔵 참고 · 부가 설명

## 규칙
- 한 줄에 이모지는 두세 개까지. 많으면 오히려 읽기 어렵다.
- 대상 종류 이모지(📁 ⚙️ 🧩 등)는 목록에서 구분이 필요할 때만, 문장 안에는 넣지 않는다.
- 코드·경로·명령은 `백틱`, 오류 로그 같은 원문은 이모지 없이 그대로 인용한다.
- 표는 항목이 비교될 때만 쓴다. 긴 문장을 표 칸에 넣지 않는다.
- 사실과 추정을 섞지 않는다. 보지 못한 것은 🟡 로 표시하고 무엇을 못 봤는지 적는다.
- 수치와 파일은 실제로 읽은 것만 쓴다. 모르면 모른다고 쓴다.

## 하지 않는 것
- LaTeX 색 표기(`\textcolor`, `\colorbox`)는 이 채팅 화면에서 렌더링되지 않아 원문이 그대로 보인다. 쓰지 않는다.
- 이모지로 문장을 장식하거나 소제목마다 다른 이모지를 새로 만들지 않는다.

## 예시
```
### 📌 결론

**빌드 실패 원인은 빠진 NuGet 패키지 하나**

---

### 📋 현재 상태

🟢 `dotnet restore` 후 다시 빌드해 오류 CS0246이 사라진 것을 확인함

🟡 테스트는 아직 실행하지 않음

---

### ➡️ 다음 할 일

❓ 테스트까지 돌릴까요?
```
