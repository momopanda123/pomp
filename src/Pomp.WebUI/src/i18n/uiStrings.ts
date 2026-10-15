/** Labels of the shared page controls (components/ui.tsx) and the reworked Work/Agents/Tools/Accounts layouts. */
const ko = {
  'ui.search': '검색', 'ui.pager.previous': '이전 페이지', 'ui.pager.next': '다음 페이지',
  'ui.edit': '편집', 'ui.delete': '삭제', 'ui.open': 'VS에서 열기', 'ui.copy': '복제', 'ui.message': '메시지', 'ui.more': '더 보기',
  'ui.cancel': '취소', 'ui.save': '저장', 'ui.on': '켜짐', 'ui.off': '꺼짐', 'ui.use': '사용',
  'ui.col.name': '이름', 'ui.col.scope': '범위', 'ui.col.status': '상태', 'ui.col.model': '모델', 'ui.col.time': '시각', 'ui.col.file': '파일',
  'ui.scope.project': '프로젝트', 'ui.scope.user': '사용자',
  'ui.sub': '구독', 'ui.apiKey': 'API 키',
} as const;

const en: Record<keyof typeof ko, string> = {
  'ui.search': 'Search', 'ui.pager.previous': 'Previous page', 'ui.pager.next': 'Next page',
  'ui.edit': 'Edit', 'ui.delete': 'Delete', 'ui.open': 'Open in VS', 'ui.copy': 'Duplicate', 'ui.message': 'Message', 'ui.more': 'More',
  'ui.cancel': 'Cancel', 'ui.save': 'Save', 'ui.on': 'On', 'ui.off': 'Off', 'ui.use': 'Use',
  'ui.col.name': 'Name', 'ui.col.scope': 'Scope', 'ui.col.status': 'Status', 'ui.col.model': 'Model', 'ui.col.time': 'Time', 'ui.col.file': 'File',
  'ui.scope.project': 'Project', 'ui.scope.user': 'User',
  'ui.sub': 'Subscription', 'ui.apiKey': 'API key',
};

export const uiStrings = { ko, en };
