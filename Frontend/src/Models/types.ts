export type ContentType = 'Session' | 'Buffer' | 'Clip' | 'Highlight';

export type RecordingMode = 'Session' | 'Buffer';

export interface Content {
	type: ContentType;
	title: string;
	game: string;
	bookmarks: Bookmark[];
	fileName: string;
	filePath: string;
	fileSize: string;
	duration: string;
	createdAt: string;
}

export interface State {
	recording?: Recording;
	hasLoadedObs: boolean;
	content: Content[];
	inputDevices: AudioDevice[];
	outputDevices: AudioDevice[];
	isCheckingForUpdates: boolean;
}

export enum BookmarkType {
	Manual = 'Manual',
	Kill = 'Kill',
	Assist = 'Assist',
	Death = 'Death'
}

export enum BookmarkSubtype {
	Headshot = 'Headshot'
}

export enum KeybindAction {
	CreateBookmark = 'CreateBookmark',
	SaveReplayBuffer = 'SaveReplayBuffer'
}

export interface Keybind {
	keys: number[];
	action: KeybindAction;
	enabled: boolean;
}

export interface Bookmark {
	id: number;
	type: BookmarkType;
	subtype?: BookmarkSubtype;
	time: string;
}

export interface Recording {
	startTime: Date;
	endTime: Date;
	game: string;
	isUsingGameHook: boolean;
	gameImage?: string; // Base64 encoded image of the game executable icon
}

export interface AudioDevice {
	id: string;
	name: string;
	isDefault?: boolean;
}

export interface DeviceSetting {
	id: string;
	name: string;
	volume: number; // Volume from 0.0 to 1.0
}

export interface Game {
	name: string;
	path: string;
}

export interface Settings {
	theme: 'segra' | 'rich' | 'dark' | 'night' | 'dracula' | 'black' | 'luxury' | 'forest' | 'halloween' | 'coffee' | 'dim' | 'sunset';
	resolution: '720p' | '1080p' | '1440p' | '4K';
	frameRate: number;
	rateControl: string;
	crfValue: number;
	cqLevel: number;
	bitrate: number;
	encoder: 'gpu' | 'cpu';
	codec: 'h264' | 'h265';
	storageLimit: number;
	contentFolder: string;
	inputDevices: DeviceSetting[];
	outputDevices: DeviceSetting[];
	enableDisplayRecording: boolean;
	enableAi: boolean;
	autoGenerateHighlights: boolean;
	onlyRecordGameAndDiscord: boolean;
	runOnStartup: boolean;
	receiveBetaUpdates: boolean;
	recordingMode: RecordingMode;
	replayBufferDuration: number; // in seconds
	replayBufferMaxSize: number; // in MB
	keybindings: Keybind[];
	whitelist: Game[];
	blacklist: Game[];
	state: State;
}

export const initialState: State = {
	recording: undefined,
	hasLoadedObs: false,
	content: [],
	inputDevices: [],
	outputDevices: [],
	isCheckingForUpdates: false,
};

export const initialSettings: Settings = {
	theme: 'segra',
	resolution: '720p',
	frameRate: 30,
	rateControl: 'CBR',
	crfValue: 23,
	cqLevel: 20,
	bitrate: 10,
	encoder: 'gpu',
	codec: 'h264',
	storageLimit: 100,
	contentFolder: '',
	inputDevices: [],
	outputDevices: [],
	enableDisplayRecording: false,
	enableAi: true,
	autoGenerateHighlights: true,
	onlyRecordGameAndDiscord: false,
	runOnStartup: false,
	receiveBetaUpdates: false,
	recordingMode: 'Session',
	replayBufferDuration: 30, // 30 seconds default
	replayBufferMaxSize: 500, // 500 MB default
	keybindings: [
		{ keys: [119], action: KeybindAction.CreateBookmark, enabled: true }, // 119 is F8
		{ keys: [121], action: KeybindAction.SaveReplayBuffer, enabled: true } // 121 is F10
	],
	whitelist: [],
	blacklist: [],
	state: initialState,
};

export interface Selection {
    id: number;
    type: ContentType;
    startTime: number;
    endTime: number;
    thumbnailDataUrl?: string;
    isLoading: boolean;
    fileName: string;
    game?: string;
}

export interface SelectionCardProps {
    selection: Selection;
    index: number;
    moveCard: (dragIndex: number, hoverIndex: number) => void;
    formatTime: (time: number) => string;
    isHovered: boolean;
    setHoveredSelectionId: (id: number | null) => void;
    removeSelection: (id: number) => void;
}

export interface AiProgress {
    id: string;
    progress: number;
    status: 'processing' | 'done';
    message: string;
    content: Content;
}