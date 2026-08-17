export interface ModalMessage {
  title: string;
  subtitle?: string;
  description: string;
  type: 'info' | 'warning' | 'error';
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

export interface ReleaseNote {
  version: string;
  base64Markdown: string;
  releaseDate: string;
}

export interface WebSocketMessage<T = any> {
  method: string;
  parameters: T;
}

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
