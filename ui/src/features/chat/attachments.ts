import { api } from '../../api/client';

/**
 * Files sent with a message (attachments spec section 1). These lists mirror the gateway's
 * `AttachmentRules` so the picker and the pre-checks match; the gateway is the authority.
 */
export const MAX_ATTACHMENTS = 5;
export const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
export const MAX_TEXT_BYTES = 256 * 1024;

export const IMAGE_EXTENSIONS = ['.png', '.jpg', '.jpeg', '.gif', '.webp'];
export const TEXT_EXTENSIONS = [
  '.txt',
  '.md',
  '.markdown',
  '.csv',
  '.tsv',
  '.json',
  '.jsonl',
  '.yaml',
  '.yml',
  '.xml',
  '.log',
  '.ini',
  '.toml',
  '.cfg',
  '.conf',
  '.html',
  '.htm',
  '.css',
  '.scss',
  '.js',
  '.mjs',
  '.cjs',
  '.ts',
  '.tsx',
  '.jsx',
  '.py',
  '.cs',
  '.csproj',
  '.sln',
  '.slnx',
  '.props',
  '.targets',
  '.java',
  '.kt',
  '.go',
  '.rs',
  '.rb',
  '.php',
  '.c',
  '.h',
  '.cpp',
  '.hpp',
  '.swift',
  '.sh',
  '.bash',
  '.ps1',
  '.sql',
  '.graphql',
  '.proto',
  '.diff',
  '.patch',
  '.tf',
  '.bicep',
];
const TEXT_FILE_NAMES = ['dockerfile', 'makefile'];

/** For the file picker's `accept`. Extension-less names (Dockerfile) can still be dropped. */
export const ACCEPT = [...IMAGE_EXTENSIONS, ...TEXT_EXTENSIONS].join(',');

export type AttachmentKind = 'image' | 'text';

/** A file read and checked in the composer, waiting to be sent. `data` is base64. */
export interface PendingAttachment {
  key: string;
  fileName: string;
  kind: AttachmentKind;
  size: number;
  data: string;
  /** A `data:` URL for the thumbnail (images only); allowed by the CSP. */
  previewUrl?: string;
}

export function extensionOf(name: string): string {
  const dot = name.lastIndexOf('.');
  return dot > 0 ? name.slice(dot).toLowerCase() : '';
}

export function kindOf(name: string): AttachmentKind | null {
  const ext = extensionOf(name);
  if (IMAGE_EXTENSIONS.includes(ext)) return 'image';
  if (TEXT_EXTENSIONS.includes(ext) || (ext === '' && TEXT_FILE_NAMES.includes(name.toLowerCase())))
    return 'text';
  return null;
}

export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${Math.round(bytes / 1024)} KB`;
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

/** Why a file can't be attached, or null when it can. */
export function checkFile(file: { name: string; size: number }): string | null {
  const kind = kindOf(file.name);
  if (!kind) return `${file.name}: this file type can't be attached.`;
  if (file.size === 0) return `${file.name} is empty.`;
  const max = kind === 'image' ? MAX_IMAGE_BYTES : MAX_TEXT_BYTES;
  if (file.size > max) return `${file.name} is larger than ${formatSize(max)}.`;
  return null;
}

function readAsDataUrl(blob: Blob): Promise<string> {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(reader.result as string);
    reader.onerror = () => reject(reader.error ?? new Error('read failed'));
    reader.readAsDataURL(blob);
  });
}

let nextKey = 0;

/** Reads a checked file into a pending attachment. */
export async function readAttachment(file: File, kind: AttachmentKind): Promise<PendingAttachment> {
  const dataUrl = await readAsDataUrl(file);
  return {
    key: `att-${nextKey++}`,
    fileName: file.name,
    kind,
    size: file.size,
    data: dataUrl.slice(dataUrl.indexOf(',') + 1),
    previewUrl: kind === 'image' ? dataUrl : undefined,
  };
}

/** A pasted image often has a generic name ("image.png"); give it a distinct one. */
export function namePasted(file: File, index: number): File {
  const ext = extensionOf(file.name) || `.${file.type.split('/')[1] ?? 'png'}`;
  const stamp = new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19);
  return new File([file], `pasted-${stamp}${index ? `-${index}` : ''}${ext}`, { type: file.type });
}

/** Where a stored attachment downloads from (same origin, session cookie). */
export function attachmentUrl(threadId: string, attachmentId: string): string {
  return `/api/threads/${encodeURIComponent(threadId)}/attachments/${encodeURIComponent(attachmentId)}`;
}

/** Downloads stored attachments again so a failed message can be resent with its files. */
export async function refetchAttachments(
  threadId: string,
  attachments: { id: string; fileName: string }[],
): Promise<{ fileName: string; data: string }[]> {
  return Promise.all(
    attachments.map(async (a) => {
      const { data, error, response } = await api.GET('/api/threads/{id}/attachments/{attachmentId}', {
        params: { path: { id: threadId, attachmentId: a.id } },
        parseAs: 'blob',
      });
      if (error || !data) throw new Error(`Couldn't reload ${a.fileName} (${response.status}).`);
      const dataUrl = await readAsDataUrl(data);
      return { fileName: a.fileName, data: dataUrl.slice(dataUrl.indexOf(',') + 1) };
    }),
  );
}
