import DOMPurify from 'dompurify';
import { useEffect, useState } from 'react';
import { useT } from '../i18n';

let counter = 0;

/**
 * A Mermaid diagram from a case (model-written source). Mermaid loads only when a diagram is shown, runs in strict mode with
 * plain SVG text labels (no HTML, links or scripts), and its SVG is sanitized again before it reaches the page. A source that
 * does not parse is shown as text instead.
 */
export function CaseDiagram({ source, label }: { source: string; label: string }) {
  const t = useT();
  const [svg, setSvg] = useState('');
  const [failed, setFailed] = useState(false);
  const dark = document.documentElement.dataset.theme !== 'light';
  useEffect(() => {
    let cancelled = false;
    setSvg(''); setFailed(false);
    (async () => {
      try {
        const { default: mermaid } = await import('mermaid');
        mermaid.initialize({ startOnLoad: false, securityLevel: 'strict', theme: dark ? 'dark' : 'default', htmlLabels: false, flowchart: { htmlLabels: false } });
        await mermaid.parse(source);
        const { svg: drawn } = await mermaid.render(`case-diagram-${++counter}`, source);
        const clean = DOMPurify.sanitize(drawn, { USE_PROFILES: { svg: true, svgFilters: true } });
        if (!cancelled) setSvg(clean);
      } catch {
        if (!cancelled) setFailed(true);
      }
    })();
    return () => { cancelled = true; };
  }, [source, dark]);
  if (failed) return <div className="case-card-diagram failed"><p className="case-card-note" role="status">{t('cases.diagramError')}</p><pre>{source}</pre></div>;
  if (!svg) return <p className="case-card-note" role="status">{t('cases.diagramLoading')}</p>;
  return <div className="case-card-diagram" role="img" aria-label={label} dangerouslySetInnerHTML={{ __html: svg }} />;
}
