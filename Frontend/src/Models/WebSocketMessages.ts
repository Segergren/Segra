import { Settings, Game } from './types';

export interface ModalMessage {
  title: string;
  subtitle?: string;
  description: string;
  type: 'info' | 'warning' | 'error';
}

export interface UploadProgressMessage {
  fileName: string;
  progress: number;
  status: 'uploading' | 'processing' | 'done' | 'error';
  message?: string;
}

export interface ImportProgressMessage {
  id: string;
  fileName: string;
  progress: number;
  status: 'importing' | 'done' | 'error';
  totalFiles: number;
  currentFileIndex: number;
  message?: string;
}

export interface StorageWarningMessage {
  warningId: string;
  title: string;
  description: string;
  confirmText: string;
  cancelText: string;
  action: 'import';
  actionData: any;
}

export interface RecoveryFileData {
  recoveryId: string;
  fileName: string;
  filePath: string;
  type: string;
  typeLabel: string;
  fileSize: string;
  detectedGame?: string;
}

export interface RecoveryPromptMessage {
  files: RecoveryFileData[];
  totalCount: number;
}

export interface SettingsMessage {
  settings: Settings;
}

export interface UpdateProgressMessage {
  version: string;
  progress: number;
  status: 'downloading' | 'downloaded' | 'ready' | 'error';
  message: string;
}

export interface ReleaseNote {
  version: string;
  base64Markdown: string;
  releaseDate: string;
}

export interface ReleaseNotesMessage {
  releaseNotesList: ReleaseNote[];
}

export interface SelectedGameExecutableMessage {
  game: Game;
}

export interface WebSocketMessage<T = any> {
  method: string;
  parameters: T;
}

export type WebSocketMessageType =
  | 'uploadProgress'
  | 'importProgress'
  | 'settings'
  | 'UpdateProgress'
  | 'ReleaseNotes'
  | 'ShowModal'
  | 'SelectedGameExecutable';

export function isUpdateProgressMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'UpdateProgress';
}

export function isReleaseNotesMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'ReleaseNotes';
}

export function isShowReleaseNotesMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'ShowReleaseNotes';
}

export function isShowModalMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'ShowModal';
}

export function isSelectedGameExecutableMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'SelectedGameExecutable';
}

export function isStorageWarningMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'StorageWarning';
}

export function isRecoveryPromptMessage(message: WebSocketMessage<any>): boolean {
  return message.method === 'RecoveryPrompt';
}
