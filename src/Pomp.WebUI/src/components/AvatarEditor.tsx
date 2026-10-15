import { useEffect, useRef, useState } from 'react';
import { newId } from '../format';
import { useT } from '../i18n';
import { send, type AgentsReplyPayload, type AvatarShape, type Role } from '../protocol';
import { REPLY_TIMEOUT_MS } from '../setupCommands';
import { useAvatarImage } from '../avatarImages';
import { AgentDialog } from './AgentDialog';
import { ColorPicker } from './ColorPicker';
import { AVATAR_COLORS, Avatar, avatarColor } from './AgentsTables';
import { Icon } from './Icon';
import { revealControls } from './RevealControls';
import { Segmented } from './ui';

const SHAPES: readonly AvatarShape[] = ['circle', 'rounded', 'square', 'hexagon'];
/** Pictures are stored as a small square: big enough for every place a picture is shown, small enough to send on demand. */
const IMAGE_SIZE = 96;

/** The chosen image file as a square data URL (center crop, IMAGE_SIZE px; WebP when the browser writes it, else PNG). */
function squareImage(file: File): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onerror = () => reject(new Error('read'));
    reader.onload = () => {
      const image = new Image();
      image.onerror = () => reject(new Error('decode'));
      image.onload = () => {
        const side = Math.min(image.naturalWidth, image.naturalHeight);
        if (!side) { reject(new Error('empty')); return; }
        const canvas = document.createElement('canvas');
        canvas.width = canvas.height = IMAGE_SIZE;
        const context = canvas.getContext('2d');
        if (!context) { reject(new Error('canvas')); return; }
        context.imageSmoothingQuality = 'high';
        context.drawImage(image, (image.naturalWidth - side) / 2, (image.naturalHeight - side) / 2, side, side, 0, 0, IMAGE_SIZE, IMAGE_SIZE);
        const webp = canvas.toDataURL('image/webp', 0.9);
        resolve(webp.startsWith('data:image/webp') ? webp : canvas.toDataURL('image/png'));
      };
      image.src = String(reader.result);
    };
    reader.readAsDataURL(file);
  });
}

/**
 * The picture of a role: shape, color and an optional image the user picks (any image file, cropped to a square). Saving stores it for
 * the role (a project role's in the project, the others' for the user); "back to default" returns to the letter in a circle.
 */
export function AvatarEditor({ role, title, returnFocus, disabled, replies, onClose }: {
  role: Role; title: string; returnFocus: HTMLElement | null; disabled: boolean; replies: AgentsReplyPayload[]; onClose: () => void;
}) {
  const t = useT();
  const fileRef = useRef<HTMLInputElement>(null);
  const [shape, setShape] = useState<AvatarShape>(role.avatar?.shape ?? 'circle');
  const [color, setColor] = useState<string | undefined>(role.avatar?.color);
  // A color the user picked with "+" (any #rrggbb): it stays as a swatch of its own before "+", also once another swatch is chosen.
  const isCustom = (value: string | undefined): value is string => !!value && !AVATAR_COLORS.includes(value.toLowerCase());
  const [custom, setCustom] = useState<string | undefined>(isCustom(role.avatar?.color) ? role.avatar?.color?.toLowerCase() : undefined);
  const [picking, setPicking] = useState(false);
  const plusRef = useRef<HTMLButtonElement>(null);
  // undefined = keep the stored image, null = no image, a string = the image just chosen.
  const [image, setImage] = useState<string | null | undefined>(undefined);
  const stored = useAvatarImage(role.scope, role.name, role.avatar?.imageRev);
  const [error, setError] = useState('');
  const [saving, setSaving] = useState<string | null>(null);
  const shown = image === undefined ? stored ?? null : image;

  useEffect(() => {
    if (!saving) return;
    const reply = replies.find((item) => item.requestId === saving);
    if (reply) { setSaving(null); if (reply.ok) onClose(); else setError(reply.error || t('agents.saveFailed')); }
  }, [replies, saving, onClose, t]);
  useEffect(() => {
    if (!saving) return;
    const timer = window.setTimeout(() => { setSaving(null); setError(t('setup.noReply')); }, REPLY_TIMEOUT_MS);
    return () => window.clearTimeout(timer);
  }, [saving, t]);

  const save = (reset: boolean): void => {
    if (disabled || saving) return;
    const requestId = newId();
    setError('');
    const avatar = reset ? null : { shape, ...(color ? { color } : {}), ...(image !== undefined ? { image } : {}) };
    if (send('agents/command', { requestId, action: 'setAvatar', scope: role.scope === 'project' ? 'project' : 'user', name: role.name, avatar })) setSaving(requestId);
    else setError(t('error.hostUnavailable'));
  };
  const choose = async (file: File | undefined): Promise<void> => {
    if (!file) return;
    try { setImage(await squareImage(file)); setError(''); }
    catch { setError(t('avatar.imageError')); }
  };

  return <AgentDialog testId="avatar-editor" className="editor-dialog avatar-editor" labelledBy="avatar-editor-title" returnFocus={returnFocus} onClose={() => { if (!saving) onClose(); }}>
    <div className="dialog-head">
      <h4 id="avatar-editor-title">{revealControls(t('avatar.title', { name: title }))}</h4>
      <button type="button" className="icon-btn" disabled={!!saving} aria-label={t('agents.close')} onClick={onClose}><Icon name="x" size={13} /></button>
    </div>
    {error && <div className="dialog-error" role="alert"><Icon name="error" size={12} /><span>{revealControls(error)}</span></div>}
    <div className="dialog-form">
      <div className="avatar-editor-preview" data-testid="avatar-preview">
        <Avatar name={role.name} label={title} size={56} look={{ shape, color }} image={shown} />
      </div>
      <div className="form-field">
        <label>{t('avatar.shape')}</label>
        <Segmented<AvatarShape> label={t('avatar.shape')} testId="avatar-shape" value={shape} onChange={setShape} disabled={!!saving}
          options={SHAPES.map((id) => ({ id, label: t(`avatar.shape.${id}`), testId: `avatar-shape-${id}` }))} />
      </div>
      <div className="form-field">
        <label id="avatar-color-label">{t('avatar.color')}</label>
        <div className="avatar-swatches" role="radiogroup" aria-labelledby="avatar-color-label">
          <button type="button" role="radio" aria-checked={!color} className={`avatar-swatch auto${!color ? ' selected' : ''}`} data-testid="avatar-color-auto"
            style={{ background: avatarColor(role.name) }} title={t('avatar.color.auto')} aria-label={t('avatar.color.auto')} disabled={!!saving} onClick={() => setColor(undefined)}>
            <span aria-hidden="true">A</span>
          </button>
          {AVATAR_COLORS.map((value) => <button key={value} type="button" role="radio" aria-checked={color === value} className={`avatar-swatch${color === value ? ' selected' : ''}`}
            data-testid={`avatar-color-${value.slice(1)}`} style={{ background: value }} title={value} aria-label={value} disabled={!!saving} onClick={() => setColor(value)} />)}
          {custom && <button type="button" role="radio" aria-checked={color?.toLowerCase() === custom} className={`avatar-swatch${color?.toLowerCase() === custom ? ' selected' : ''}`}
            data-testid="avatar-color-custom" style={{ background: custom }} title={custom} aria-label={custom} disabled={!!saving} onClick={() => setColor(custom)} />}
          <button ref={plusRef} type="button" className={`avatar-swatch add${picking ? ' selected' : ''}`} data-testid="avatar-color-pick" title={t('avatar.color.pick')} aria-label={t('avatar.color.pick')}
            aria-expanded={picking} disabled={!!saving} onClick={() => setPicking(open => !open)}>
            <Icon name="plus" size={12} />
          </button>
        </div>
        {picking && <ColorPicker value={color ?? avatarColor(role.name)} disabled={!!saving} testId="avatar-color-picker"
          onChange={(value) => { setCustom(value); setColor(value); }} onClose={() => { setPicking(false); plusRef.current?.focus({ preventScroll: true }); }} />}
      </div>
      <div className="form-field">
        <label>{t('avatar.image')}</label>
        <div className="avatar-image-actions">
          <button type="button" className="btn small" data-testid="avatar-image-choose" disabled={!!saving} onClick={() => fileRef.current?.click()}><Icon name="file" size={12} />{t('avatar.image.choose')}</button>
          {shown && <button type="button" className="btn small subtle" data-testid="avatar-image-remove" disabled={!!saving} onClick={() => setImage(null)}><Icon name="x" size={12} />{t('avatar.image.remove')}</button>}
          <input ref={fileRef} type="file" accept="image/*" hidden data-testid="avatar-image-file" onChange={(event) => { void choose(event.target.files?.[0]); event.target.value = ''; }} />
        </div>
      </div>
    </div>
    <div className="dialog-foot">
      <button type="button" className="btn subtle" data-testid="avatar-reset" disabled={!!saving || disabled || !role.avatar} onClick={() => save(true)}>{t('avatar.reset')}</button>
      <span className="ui-grow" />
      <button type="button" className="btn subtle" disabled={!!saving} onClick={onClose}>{t('dialog.cancel')}</button>
      <button type="button" className="btn primary" data-testid="avatar-save" disabled={!!saving || disabled} onClick={() => save(false)}>{saving ? <span className="spinner" /> : t('agents.save')}</button>
    </div>
  </AgentDialog>;
}
