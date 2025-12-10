import { sendMessageToBackend } from './MessageUtils';

export const openFileLocation = (filePath: string) => {
  if (!filePath) return;
  sendMessageToBackend('OpenFileLocation', { FilePath: filePath });
};
