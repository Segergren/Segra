import React, {useEffect, useState, useRef} from 'react';
import {useSettings, useSettingsUpdater} from '../Context/SettingsContext';
import {sendMessageToBackend} from '../Utils/MessageUtils';
import {themeChange} from 'theme-change';
import {AudioDevice, KeybindAction} from '../Models/types';
import {supabase} from '../lib/supabase/client';
import {FaDiscord} from 'react-icons/fa';
import {useAuth} from '../Hooks/useAuth.tsx';
import {useProfile} from '../Hooks/useUserProfile';
import {MdOutlineLogout, MdWarning, MdLock, MdOutlineDescription} from 'react-icons/md';
import {useUpdate} from '../Context/UpdateContext';
import GameListManager from '../Components/GameListManager';
import { SiGithub } from 'react-icons/si';
import { motion, AnimatePresence } from 'framer-motion';

export default function Settings() {
	const {session, authError, isAuthenticating, clearAuthError, signOut} = useAuth();
	const {data: profile, error: profileError} = useProfile();
	const [error, setError] = useState('');
	const [email, setEmail] = useState('');
	const [password, setPassword] = useState('');
	const {openReleaseNotesModal, checkForUpdates} = useUpdate();
	const settings = useSettings();
	const updateSettings = useSettingsUpdater();
	const [localStorageLimit, setLocalStorageLimit] = useState<number>(settings.storageLimit);
	const [localReplayBufferDuration, setLocalReplayBufferDuration] = useState<number>(settings.replayBufferDuration);
	const [localReplayBufferMaxSize, setLocalReplayBufferMaxSize] = useState<number>(settings.replayBufferMaxSize);
	const [isLoggingOut, setIsLoggingOut] = useState(false);
	const [isCapturingKey, setIsCapturingKey] = useState<number | null>(null);
	const activeKeysRef = useRef<number[]>([]);
	const [draggingVolume, setDraggingVolume] = useState<{ deviceId: string | null; volume: number | null }>({ deviceId: null, volume: null });

	// Helper function to get a display name for a key code
	const getKeyDisplayName = (keyCode: number): string => {
		// Function keys
		if (keyCode >= 112 && keyCode <= 123) {
			return `F${keyCode - 111}`;
		}

		// Common keys
		const keyMap: Record<number, string> = {
			8: 'Backspace',
			9: 'Tab',
			13: 'Enter',
			16: 'Shift',
			17: 'Ctrl',
			18: 'Alt',
			19: 'Pause',
			20: 'Caps Lock',
			27: 'Esc',
			32: 'Space',
			33: 'PgUp',
			34: 'PgDn',
			35: 'End',
			36: 'Home',
			37: '←',
			38: '↑',
			39: '→',
			40: '↓',
			45: 'Insert',
			46: 'Delete',
			91: 'Win',
			93: 'Menu',
			144: 'Num Lock',
			186: ';',
			187: '=',
			188: ',',
			189: '-',
			190: '.',
			191: '/',
			192: '`',
			219: '[',
			220: '\\',
			221: ']',
			222: '\''
		};

		// Numbers and letters
		if (keyCode >= 48 && keyCode <= 57) {
			return String.fromCharCode(keyCode); // 0-9
		}
		if (keyCode >= 65 && keyCode <= 90) {
			return String.fromCharCode(keyCode); // A-Z
		}

		return keyMap[keyCode] || `Key(${keyCode})`;
	};

	// Set error from auth if present
	useEffect(() => {
		if (authError) {
			setError(authError);
			clearAuthError();
		}
	}, [authError, clearAuthError]);

	// Rest of your existing settings logic
	useEffect(() => {
		setLocalStorageLimit(settings.storageLimit);
	}, [settings.storageLimit]);

	useEffect(() => {
		setLocalReplayBufferDuration(settings.replayBufferDuration);
	}, [settings.replayBufferDuration]);

	useEffect(() => {
		setLocalReplayBufferMaxSize(settings.replayBufferMaxSize);
	}, [settings.replayBufferMaxSize]);

	const handleChange = (event: React.ChangeEvent<HTMLInputElement | HTMLSelectElement>) => {
		const {name, value} = event.target;
		const numericalFields = ['frameRate', 'bitrate', 'storageLimit', 'keyframeInterval', 'crfValue', 'cqLevel'];
		updateSettings({
			[name]: numericalFields.includes(name) ? Number(value) : value,
		});
	};

	const handleBrowseClick = () => {
		sendMessageToBackend('SetVideoLocation');
	};

	useEffect(() => {
		themeChange(false);
	}, []);

	// Updated Discord login handler
	const handleDiscordLogin = async () => {
		setError('');
		try {
			const {error} = await supabase.auth.signInWithOAuth({
				provider: 'discord',
				options: {
					redirectTo: window.location.href,
					queryParams: {prompt: 'consent'}
				}
			});

			if (error) throw error;
		} catch (err) {
			setError(err instanceof Error ? err.message : 'Failed to start authentication');
		}
	};

	const handleEmailLogin = async (e: React.FormEvent) => {
		e.preventDefault();
		setError('');
		try {
			const {error} = await supabase.auth.signInWithPassword({email, password});
			if (error) throw error;
		} catch (err) {
			setError(err instanceof Error ? err.message : 'Login failed');
		}
	};

	const handleLogout = async () => {
		try {
			setIsLoggingOut(true);
			// Use the AuthProvider's signOut method to ensure UI is updated
			await signOut();
		} catch (err) {
			console.error("Logout failed:", err);
			setError(err instanceof Error ? err.message : 'Logout failed');
		} finally {
			setIsLoggingOut(false);
		}
	};

	// Render the authentication section based on login state
	const renderAuthSection = () => {
		if (!session) {
			return (
				<div className="p-4 bg-base-300 rounded-lg shadow-md">
					<h2 className="text-xl font-semibold mb-4">Authentication</h2>
					
					{error && (
						<div className="alert alert-error mb-4" role="alert">
							<MdWarning className="w-5 h-5" />
							<span>{error}</span>
						</div>
					)}
					
					<div className="bg-base-100 p-6 rounded-lg space-y-4">
						<button
							onClick={handleDiscordLogin}
							disabled={isAuthenticating}
							className={`btn btn-neutral w-full gap-2 font-semibold text-white ${isAuthenticating ? 'btn-loading' : ''}`}
						>
							<FaDiscord className="w-5 h-5" />
							{isAuthenticating ? 'Connecting...' : 'Continue with Discord'}
						</button>

						<div className="divider">or use email</div>

						<form onSubmit={handleEmailLogin} className="space-y-4">
							<div className="form-control">
								<label className="label">
									<span className="label-text">Email</span>
								</label>
								<input
									type="email"
									value={email}
									onChange={(e) => setEmail(e.target.value)}
									className="input input-bordered"
									disabled={isAuthenticating}
									required
								/>
							</div>

							<div className="form-control">
								<label className="label">
									<span className="label-text">Password</span>
								</label>
								<input
									type="password"
									value={password}
									onChange={(e) => setPassword(e.target.value)}
									className="input input-bordered"
									disabled={isAuthenticating}
									required
								/>
							</div>

							<button
								type="submit"
								disabled={isAuthenticating}
								className={`btn btn-neutral w-full font-semibold text-white ${isAuthenticating ? 'btn-loading' : ''}`}
							>
								Sign in with Email
							</button>
						</form>
					</div>
				</div>
			);
		}
		
		return (
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Account</h2>
				
				<div className="bg-base-100 p-4 rounded-lg border border-custom">
					<div className="flex items-center justify-between flex-wrap gap-4">
						<div className="flex items-center gap-4 min-w-0">
							{/* Avatar Container */}
							<div className="relative w-16 h-16">
								<div className="w-full h-full rounded-full overflow-hidden bg-base-200 ring-2 ring-base-300">
									{profile?.avatar_url ? (
										<img
											src={profile.avatar_url}
											alt={`${profile.username}'s avatar`}
											className="w-full h-full object-cover"
											onError={(e) => {
												(e.target as HTMLImageElement).src = '/default-avatar.png';
											}}
										/>
									) : (
										<div
											className="w-full h-full bg-base-300 flex items-center justify-center"
											aria-hidden="true"
										>
											<span className="text-2xl"></span>
										</div>
									)}
								</div>
							</div>

							{/* Profile Info */}
							<div className="min-w-0 flex-1">
								<h3 className="font-bold truncate">
									{profile?.username ? (
										profile.username
									) : (
										<div className="skeleton h-[24px] w-24"></div>
									)}
								</h3>
								<p className="text-sm opacity-70 truncate">
									{session?.user?.email || 'Authenticated User'}
								</p>
							</div>
							
							{/* Logout Button */}
							<button
								onClick={handleLogout}
								className="btn btn-sm no-animation btn-outline btn-error"
								disabled={isLoggingOut}
							>
								{!isLoggingOut && <MdOutlineLogout className="w-4 h-4" />}
								{isLoggingOut ? 'Logging out...' : 'Logout'}
							</button>
						</div>
					</div>
					
					{/* Error State */}
					{profileError && (
						<div
							className="alert alert-error mt-3"
							role="alert"
							aria-live="assertive"
						>
							<MdWarning className="w-5 h-5" />
							<div>
								<h3 className="font-bold">Profile load failed!</h3>
								<div className="text-xs">{profileError.message || 'Unknown error occurred'}</div>
							</div>
						</div>
					)}
				</div>
			</div>
		);
	};

	// Helper function to check if the selected device is available
	const isDeviceAvailable = (deviceId: string, devices: AudioDevice[]) => {
		return devices.some((device) => device.id === deviceId);
	};

	// Check if any selected input device is unavailable
	const hasUnavailableInputDevices = settings.inputDevices.some(
		deviceSetting => !isDeviceAvailable(deviceSetting.id, settings.state.inputDevices)
	);

	// Check if any selected output device is unavailable
	const hasUnavailableOutputDevices = settings.outputDevices.some(
		deviceSetting => !isDeviceAvailable(deviceSetting.id, settings.state.outputDevices)
	);

	// Function to toggle input device selection
	const toggleInputDevice = (deviceId: string) => {
		const isSelected = settings.inputDevices.some(d => d.id === deviceId);
		let updatedDevices;

		if (isSelected) {
			// Remove the device
			updatedDevices = settings.inputDevices.filter(d => d.id !== deviceId);
		} else {
			// Add the device
			const deviceToAdd = settings.state.inputDevices.find(d => d.id === deviceId);
			if (deviceToAdd) {
				updatedDevices = [...settings.inputDevices, { id: deviceId, name: deviceToAdd.name, volume: 1.0 }]; // Default volume 1.0
			}
		}
		updateSettings({ inputDevices: updatedDevices });
	};

	// Function to toggle output device selection
	const toggleOutputDevice = (deviceId: string) => {
		const isSelected = settings.outputDevices.some(d => d.id === deviceId); // Check based on ID
		let updatedDevices;

		if (isSelected) {
			updatedDevices = settings.outputDevices.filter(d => d.id !== deviceId); // Filter based on ID
		} else {
			// Add the device - find its name from the available list
			const deviceToAdd = settings.state.outputDevices.find(d => d.id === deviceId);
			if (deviceToAdd) {
				// Add as DeviceSetting with volume 1.0
				updatedDevices = [...settings.outputDevices, { id: deviceId, name: deviceToAdd.name, volume: 1.0 }]; 
			}
		}
		updateSettings({ outputDevices: updatedDevices });
	};

	// Function to handle input device volume change
	const handleInputVolumeChange = (deviceId: string, volume: number) => {
		const updatedDevices = settings.inputDevices.map(device =>
			device.id === deviceId ? { ...device, volume: volume } : device
		);

		updateSettings({ inputDevices: updatedDevices });
	};

	return (
		<div className="p-5 space-y-6 rounded-lg">
			<h1 className="text-3xl font-bold">Settings</h1>
			
			{/* Authentication Section */}
			{renderAuthSection()}

			{/* Segra AI Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Segra AI</h2>
				<div className="bg-base-100 p-4 rounded-lg border border-custom space-y-4">
					{!session && (
						<div className="flex items-center gap-2 mb-3 text-sm text-warning">
							<MdLock className="w-4 h-4" />
							<span>Sign in to access AI features</span>
						</div>
					)}
					<div className="flex items-center justify-between">
						<div className="flex items-center gap-2">
							<span className="font-medium">Enable Segra AI</span>
						</div>
						<input
							type="checkbox"
							name="enableAI"
							checked={settings.enableAi}
							onChange={(e) => updateSettings({enableAi: e.target.checked})}
							className="toggle toggle-primary"
							disabled={!session}
						/>
					</div>
					<div className="flex items-center justify-between">
						<div className="flex items-center gap-2">
							<span className="font-medium">Auto-generate Highlights</span>
						</div>
						<input
							type="checkbox"
							name="autoGenerateHighlights"
							checked={settings.autoGenerateHighlights}
							onChange={(e) => updateSettings({autoGenerateHighlights: e.target.checked})}
							className="toggle toggle-primary"
							disabled={!session || !settings.enableAi}
						/>
					</div>
				</div>
			</div>

			{/* Capture Mode */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Capture Mode</h2>
				<div className="grid grid-cols-2 gap-6">
					<div 
						className={`bg-base-100 p-4 rounded-lg flex flex-col transition-all border ${settings.recordingMode == 'Session' ? 'border-primary' : 'border-custom'} ${settings.state.recording ? 'opacity-60 cursor-not-allowed' : 'cursor-pointer hover:bg-opacity-80'}`}
						onClick={() => !settings.state.recording && updateSettings({ recordingMode: 'Session' })}
					>
						<div className="text-lg font-semibold mb-3">Session Recording</div>
						<div className="text-sm text-left text-base-content">
							<p className="mb-2">
								Records your entire gaming session from start to finish. Ideal for content creators who want complete gameplay recordings.
							</p>
							<div className="text-xs text-base-content text-opacity-70">
								• Uses more storage space<br/>
								• Full game integration features<br/>
								• Access to AI-generated clips<br/>
								• Access to Bookmarks
							</div>
						</div>
					</div>
					<div 
						className={`bg-base-100 p-4 rounded-lg flex flex-col transition-all border ${settings.recordingMode == 'Buffer' ? 'border-primary' : 'border-custom'} ${settings.state.recording ? 'opacity-60 cursor-not-allowed' : 'cursor-pointer hover:bg-opacity-80'}`}
						onClick={() => !settings.state.recording && updateSettings({ recordingMode: 'Buffer'})}
					>
						<div className="flex items-center gap-2 mb-3">
							<div className="text-lg font-semibold text-center">Replay Buffer</div>
							<div className="badge badge-warning badge-sm">Beta</div>
						</div>
						<div className="text-sm text-left text-base-content">
							<p className="mb-2">
								Continuously records in the background. Save only your best moments with a hotkey press.
							</p>
							<div className="text-xs text-base-content text-opacity-70">
								• Efficient storage usage<br/>
								• No game integration<br/>
								• No bookmarks
							</div>
						</div>
					</div>
				</div>
			</div>

			{/* Video Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Video Settings</h2>
				
				{/* Replay Buffer Settings - Only show when Replay Buffer mode is selected */}
				<AnimatePresence>
					{settings.recordingMode === 'Buffer' && (
						<motion.div 
							className="bg-base-100 rounded-lg border border-custom"
							initial={{ opacity: 0, height: 0, overflow: 'hidden' }}
							animate={{ 
								opacity: 1, 
								height: 'fit-content',
								transition: { 
									duration: 0.3,
									height: { type: 'spring', stiffness: 300, damping: 30 }
								}
							}}
							exit={{ 
								opacity: 0,
								height: 0,
								transition: { 
									duration: 0.2
								}
							}}
							//layout
						>
							<div className="content-wrapper mb-4 p-3 pb-0">
								<motion.h3 
									className="text-md font-medium mb-3"
									initial={{ opacity: 0 }}
									animate={{ opacity: 1, transition: { delay: 0.1 } }}
								>
									Replay Buffer Settings
								</motion.h3>
								<motion.div 
									className="grid grid-cols-2 gap-4"
									initial={{ opacity: 0 }}
									animate={{ opacity: 1, transition: { delay: 0.2 } }}
								>
									{/* Buffer Duration */}
									<div className="form-control">
										<label className="label">
											<span className="label-text">Buffer Duration (seconds)</span>
										</label>
										<input
											type="number"
											name="replayBufferDuration"
											value={localReplayBufferDuration}
											onChange={(e) => setLocalReplayBufferDuration(Number(e.target.value))}
											onBlur={() => updateSettings({ replayBufferDuration: localReplayBufferDuration })}
											min="5"
											max="600"
											disabled={settings.state.recording != null}
											className={`input input-bordered disabled:bg-base-100 disabled:input-bordered disabled:opacity-80`}
										/>
										<div className="help-text-container">
											<span className="text-xs text-base-content text-opacity-60 mt-1">How many seconds of gameplay to keep in memory</span>
										</div>
									</div>

									{/* Buffer Max Size */}
									<div className="form-control">
										<label className="label">
											<span className="label-text">Maximum Size (MB)</span>
										</label>
										<input
											type="number"
											name="replayBufferMaxSize"
											value={localReplayBufferMaxSize}
											onChange={(e) => setLocalReplayBufferMaxSize(Number(e.target.value))}
											onBlur={() => updateSettings({ replayBufferMaxSize: localReplayBufferMaxSize })}
											min="100"
											max="5000"
											disabled={settings.state.recording != null}
											className="input input-bordered disabled:bg-base-100 disabled:input-bordered disabled:opacity-80"
										/>
										<div className="help-text-container">
											<span className="text-xs text-base-content text-opacity-60 mt-1">Maximum buffer size in megabytes</span>
										</div>
									</div>
								</motion.div>
							</div>
						</motion.div>
					)}
				</AnimatePresence>
				<div className="grid grid-cols-2 gap-4">
					{/* Resolution */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Resolution</span>
						</label>
						<select
							name="resolution"
							value={settings.resolution}
							onChange={handleChange}
							className="select select-bordered"
						>
							<option value="720p">720p</option>
							<option value="1080p">1080p</option>
							<option value="1440p">1440p</option>
							<option value="4K">4K</option>
						</select>
					</div>

					{/* Frame Rate */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Frame Rate (FPS)</span>
						</label>
						<select
							name="frameRate"
							value={settings.frameRate}
							onChange={handleChange}
							className="select select-bordered"
						>
							<option value="24">24</option>
							<option value="30">30</option>
							<option value="60">60</option>
							<option value="120">120</option>
							<option value="144">144</option>
						</select>
					</div>

					{/* Rate Control */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Rate Control</span>
						</label>
						<select
							name="rateControl"
							value={settings.rateControl}
							onChange={handleChange}
							className="select select-bordered"
						>
							<option value="CBR">CBR (Constant Bitrate)</option>
							<option value="VBR">VBR (Variable Bitrate)</option>
							<option value="CRF">CRF (Constant Rate Factor)</option>
							<option value="CQP">CQP (Constant Quantization Parameter)</option>
						</select>
					</div>

					{/* Bitrate (for CBR and VBR) */}
					{(settings.rateControl === 'CBR' || settings.rateControl === 'VBR') && (
						<div className="form-control">
							<label className="label">
								<span className="label-text">Bitrate (Mbps)</span>
							</label>
							<select
								name="bitrate"
								value={settings.bitrate}
								onChange={handleChange}
								className="select select-bordered"
							>
								{Array.from({length: 19}, (_, i) => (i + 2) * 5).map((value) => (
									<option key={value} value={value}>
										{value} Mbps
									</option>
								))}
							</select>
						</div>
					)}

					{/* CRF Value (for CRF) */}
					{settings.rateControl === 'CRF' && (
						<div className="form-control">
							<label className="label">
								<span className="label-text">CRF Value (0-51)</span>
							</label>
							<input
								type="number"
								name="crfValue"
								value={settings.crfValue}
								onChange={handleChange}
								min="0"
								max="51"
								className="input input-bordered"
							/>
						</div>
					)}

					{/* CQ Level (for CQP) */}
					{settings.rateControl === 'CQP' && (
						<div className="form-control">
							<label className="label">
								<span className="label-text">CQ Level (0-30)</span>
							</label>
							<input
								type="number"
								name="cqLevel"
								value={settings.cqLevel}
								onChange={handleChange}
								min="0"
								max="30"
								className="input input-bordered"
							/>
						</div>
					)}

					{/* Encoder */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Video Encoder</span>
						</label>
						<select
							name="encoder"
							value={settings.encoder}
							onChange={handleChange}
							className="select select-bordered"
						>
							<option value="gpu">GPU</option>
							<option value="cpu">CPU</option>
						</select>
					</div>

					{/* Codec */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Codec</span>
						</label>
						<select
							name="codec"
							value={settings.codec}
							onChange={handleChange}
							className="select select-bordered"
						>
							<option value="h264">H.264</option>
							<option value="h265">H.265</option>
						</select>
					</div>
				</div>
			</div>

			{/* Storage Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Storage Settings</h2>
				<div className="grid grid-cols-2 gap-4">
					{/* Recording Path */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Recording Path</span>
						</label>
						<div className="flex space-x-2">
							<input
								type="text"
								name="contentFolder"
								value={settings.contentFolder}
								onChange={handleChange}
								placeholder="Enter or select folder path"
								className="input input-bordered flex-1"
							/>
							<button onClick={handleBrowseClick} className="btn btn-neutral font-semibold">
								Browse
							</button>
						</div>
					</div>

					{/* Storage Limit */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Storage Limit (GB)</span>
						</label>
						<input
							type="number"
							name="storageLimit"
							value={localStorageLimit}
							onChange={(e) => setLocalStorageLimit(Number(e.target.value))}
							onBlur={() => updateSettings({storageLimit: localStorageLimit})}
							placeholder="Set maximum storage in GB"
							min="1"
							className="input input-bordered"
						/>
					</div>
				</div>
			</div>

			{/* Audio Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Audio</h2>
				<div className="grid grid-cols-2 gap-4">
					{/* Input Devices (Multiple Selection) */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Input Devices</span>
						</label>
						<div className="bg-base-100 rounded-lg p-2 max-h-48 overflow-y-auto border border-custom">
							{/* Warning for unavailable devices */}
							{hasUnavailableInputDevices && (
								<div className="text-warning text-xs mb-2 flex items-center">
									<span className="mr-1">⚠️</span> Some selected devices are unavailable
								</div>
							)}

							{/* List available input devices as checkboxes */}
							{settings.state.inputDevices.map((device) => (
								<div key={device.id} className="form-control mb-1 last:mb-0">
									<label className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded">
										<input
											type="checkbox"
											className="checkbox checkbox-sm checkbox-accent"
											checked={settings.inputDevices.some(d => d.id === device.id)}
											onChange={() => toggleInputDevice(device.id)}
										/>
										<span className="label-text flex-1 mr-2">{device.name}</span>
										{/* Volume slider */} 
										{settings.inputDevices.some(d => d.id === device.id) && (
											<div className="flex items-center gap-1 w-32">
												<input
													type="range"
													min="0"
													max="2"
													step="0.01"
													value={draggingVolume.deviceId === device.id ? draggingVolume.volume ?? 0 : settings.inputDevices.find(d => d.id === device.id)?.volume ?? 1.0}
													className="range range-xs range-primary"
													onChange={(e) => {
														if (draggingVolume.deviceId === device.id) {
															setDraggingVolume({ ...draggingVolume, volume: parseFloat(e.target.value) });
														}
													}}
													onMouseDown={(e) => setDraggingVolume({ deviceId: device.id, volume: parseFloat(e.currentTarget.value) })}
													onMouseUp={(e) => {
														if (draggingVolume.deviceId === device.id) {
															handleInputVolumeChange(device.id, parseFloat(e.currentTarget.value));
															setDraggingVolume({ deviceId: null, volume: null }); // Reset dragging state
														}
													}}
												/>
												<span className="text-xs w-8 text-right">
													{/* Display percentage based on dragging state or global state */}
													{Math.round((draggingVolume.deviceId === device.id ? draggingVolume.volume ?? 0 : settings.inputDevices.find(d => d.id === device.id)?.volume ?? 1.0) * 100)}%
												</span>
											</div>
										)}
									</label>
								</div>
							))}

							{/* Show unavailable devices that are still selected */} 
							{settings.inputDevices
								.filter(deviceSetting => !isDeviceAvailable(deviceSetting.id, settings.state.inputDevices) && deviceSetting.id)
								.map(deviceSetting => (
									<div key={deviceSetting.id} className="form-control mb-1 last:mb-0">
										<label className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded">
											<input
												type="checkbox"
												className="checkbox checkbox-sm checkbox-warning"
												checked={true}
												onChange={() => toggleInputDevice(deviceSetting.id)}
											/>
											<span className="label-text text-warning flex items-center flex-1 mr-2">
												<span className="mr-1">⚠️</span>
												{deviceSetting.name} (Unavailable)
											</span>
											{/* Also show slider for unavailable but selected device */}
											<div className="flex items-center gap-1 w-32">
												<input
													type="range"
													min="0"
													max="1"
													step="0.01"
													value={draggingVolume.deviceId === deviceSetting.id ? draggingVolume.volume ?? 0 : deviceSetting.volume}
													className="range range-xs range-warning"
													onChange={(e) => {
														if (draggingVolume.deviceId === deviceSetting.id) {
															setDraggingVolume({ ...draggingVolume, volume: parseFloat(e.target.value) });
														}
													}}
													onMouseDown={(e) => setDraggingVolume({ deviceId: deviceSetting.id, volume: parseFloat(e.currentTarget.value) })}
													onMouseUp={(e) => {
														if (draggingVolume.deviceId === deviceSetting.id) {
															handleInputVolumeChange(deviceSetting.id, parseFloat(e.currentTarget.value));
															setDraggingVolume({ deviceId: null, volume: null }); // Reset dragging state
														}
													}}
												/>
												<span className="text-xs w-8 text-right">
													{/* Display percentage based on dragging state or global state */}
													{Math.round((draggingVolume.deviceId === deviceSetting.id ? draggingVolume.volume ?? 0 : deviceSetting.volume) * 100)}%
												</span>
											</div>
										</label>
									</div>
								))
							}

							{/* Show message when no devices are available */}
							{settings.state.inputDevices.length === 0 && (
								<div className="text-center py-2 text-gray-500">
									No input devices available
								</div>
							)}
						</div>
					</div>

					{/* Output Devices (Multiple Selection) */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Output Devices</span>
						</label>
						<div className="bg-base-100 rounded-lg p-2 max-h-48 overflow-y-auto border border-custom">
							{/* Warning for unavailable devices */}
							{hasUnavailableOutputDevices && (
								<div className="text-warning text-xs mb-2 flex items-center">
									<span className="mr-1">⚠️</span> Some selected devices are unavailable
								</div>
							)}

							{/* List available output devices as checkboxes */}
							{settings.state.outputDevices.map((device) => (
								<div key={device.id} className="form-control mb-1 last:mb-0">
									<label className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded">
										<input
											type="checkbox"
											className="checkbox checkbox-sm checkbox-accent"
											checked={settings.outputDevices.some(d => d.id === device.id)}
											onChange={() => toggleOutputDevice(device.id)}
										/>
										<span className="label-text">{device.name}</span>
									</label>
								</div>
							))}

							{/* Show unavailable devices that are still selected */}
							{settings.outputDevices
								.filter(deviceSetting => !isDeviceAvailable(deviceSetting.id, settings.state.outputDevices) && deviceSetting.id)
								.map(deviceSetting => (
									<div key={deviceSetting.id} className="form-control mb-1 last:mb-0">
										<label className="cursor-pointer flex items-center gap-2 p-1 hover:bg-base-200 rounded">
											<input
												type="checkbox"
												className="checkbox checkbox-sm checkbox-warning"
												checked={true}
												onChange={() => toggleOutputDevice(deviceSetting.id)}
											/>
											<span className="label-text text-warning flex items-center">
												<span className="mr-1">⚠️</span>
												{deviceSetting.name} (Unavailable)
											</span>
										</label>
									</div>
								))
							}

							{/* Show message when no devices are available */}
							{settings.state.outputDevices.length === 0 && (
								<div className="text-center py-2 text-gray-500">
									No output devices available
								</div>
							)}
						</div>
					</div>
				</div>
			</div>

			{/* Keybindings Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Keybindings</h2>
				<div className="bg-base-100 p-4 rounded-lg space-y-4 border border-custom">
					{settings.keybindings.map((keybind, index) => (
						<div key={index} className="flex items-center justify-between gap-0 p-2">
							<div className="flex items-center gap-1">
								<span className="font-medium w-36">{keybind.action == KeybindAction.CreateBookmark ? 'Create Bookmark' : 'Save Replay Buffer'}</span>
								<div className="flex items-center gap-3">
									<button 
										className={`kbd kbd-lg ${isCapturingKey === index ? 'animate-pulse' : ''}`}
										style={{ minWidth: '150px', display: 'flex', justifyContent: 'center' }}
										onClick={() => {
											activeKeysRef.current = [];
											setIsCapturingKey(index);
											
											const handleKeyDown = (e: KeyboardEvent) => {
												e.preventDefault();
												
												const newActiveKeys = [...activeKeysRef.current];
												
												if (e.ctrlKey && !newActiveKeys.includes(17)) newActiveKeys.push(17);
												if (e.altKey && !newActiveKeys.includes(18)) newActiveKeys.push(18);
												if (e.shiftKey && !newActiveKeys.includes(16)) newActiveKeys.push(16);
												
												if (e.keyCode !== 16 && e.keyCode !== 17 && e.keyCode !== 18 && !newActiveKeys.includes(e.keyCode)) {
													newActiveKeys.push(e.keyCode);
												}
												
												activeKeysRef.current = newActiveKeys;
											};
											
											const handleKeyUp = (e: KeyboardEvent) => {
												// Cancel if Escape key is pressed
												if (e.keyCode === 27) {
													window.removeEventListener('keydown', handleKeyDown);
													window.removeEventListener('keyup', handleKeyUp);
													setIsCapturingKey(null);
													activeKeysRef.current = [];
													return;
												}
												
												if (e.keyCode !== 16 && e.keyCode !== 17 && e.keyCode !== 18 && activeKeysRef.current.length > 0) {
													const updatedKeybindings = [...settings.keybindings];
													updatedKeybindings[index] = {
														...updatedKeybindings[index],
														keys: [...activeKeysRef.current]
													};
													updateSettings({ keybindings: updatedKeybindings });
													
													window.removeEventListener('keydown', handleKeyDown);
													window.removeEventListener('keyup', handleKeyUp);
													setIsCapturingKey(null);
													activeKeysRef.current = [];
												}
											};
											
											window.addEventListener('keydown', handleKeyDown);
											window.addEventListener('keyup', handleKeyUp);
										}}
									>
										{isCapturingKey === index ? 'Press a key combination...' : (
											<span>
												{keybind.keys.map((key, keyIndex) => {
													// Format special keys
													const isLastKey = keyIndex === keybind.keys.length - 1;
													
													// Check if this is a modifier key
													let keyName = '';
													if (key === 17) keyName = 'CTRL';
													else if (key === 18) keyName = 'ALT';
													else if (key === 16) keyName = 'SHIFT';
													else keyName = getKeyDisplayName(key);
													
													return (
														<span key={keyIndex} className="font-bold">
															{keyName}{!isLastKey && ' + '}
														</span>
													);
												})}
											</span>
										)}
									</button>
									<span className="text-xs text-gray-500">(Click to change key combination)</span>
								</div>
							</div>
							<div className="flex items-center gap-2">
								<input
									type="checkbox"
									checked={keybind.enabled}
									onChange={(e) => {
										const updatedKeybindings = [...settings.keybindings];
										updatedKeybindings[index] = {
											...updatedKeybindings[index],
											enabled: e.target.checked
										};
										updateSettings({ keybindings: updatedKeybindings });
									}}
									className="toggle toggle-primary"
								/>
							</div>
						</div>
					))}
				</div>
			</div>

			{/* Game Whitelist and Blacklist */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom mb-6">
				<h2 className="text-xl font-semibold mb-4">Game Lists</h2>
				<GameListManager listType="whitelist" />
				<GameListManager listType="blacklist" />
			</div>

			{/* Advanced Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md border border-custom">
				<h2 className="text-xl font-semibold mb-4">Advanced Settings</h2>
				<div className="bg-base-100 p-4 rounded-lg space-y-4 border border-custom">
					<div className="flex items-center justify-between mb-4">
						<div className="flex items-center gap-2">
							<span className="font-medium">Only Record Game and Discord Audio</span>
						</div>
						<input
							type="checkbox"
							name="onlyRecordGameAndDiscord"
							checked={settings.onlyRecordGameAndDiscord}
							onChange={(e) => updateSettings({onlyRecordGameAndDiscord: e.target.checked})}
							className="toggle toggle-primary"
						/>
					</div>
					<div className="flex items-center justify-between">
						<div className="flex items-center gap-2">
							<span className="font-medium">Update Channel</span>
						</div>
						<div className="flex items-center gap-2">
						<button
							onClick={() => openReleaseNotesModal(null)}
							className="btn btn-sm btn-secondary text-gray-400 hover:text-gray-300 flex items-center justify-center"
						>
							<SiGithub className="text-lg flex-shrink-0" aria-hidden="true" />
							<span className="inline-block">View Release Notes</span>
						</button>
							<button
								className="btn btn-sm btn-primary flex items-center gap-1"
								onClick={() => checkForUpdates()}
								disabled={settings.state.isCheckingForUpdates}
							>
								{settings.state.isCheckingForUpdates && (
									<span className="loading loading-spinner loading-xs"></span>
								)}
								Check for Updates
							</button>
							<select
								name="receiveBetaUpdates"
								value={settings.receiveBetaUpdates ? "beta" : "stable"}
								onChange={(e) => updateSettings({receiveBetaUpdates: e.target.value === "beta"})}
								className="select select-bordered select-sm w-32"
							>
								<option value="stable">Stable</option>
								<option value="beta">Beta</option>
							</select>
						</div>
					</div>

					<div className="flex items-center justify-between">
						<div className="flex items-center gap-2">
							<span className="font-medium">Run on Startup</span>
						</div>
						<input
							type="checkbox"
							name="runOnStartup"
							checked={settings.runOnStartup}
							onChange={(e) => updateSettings({runOnStartup: e.target.checked})}
							className="toggle toggle-primary"
						/>
					</div>

					<div className="flex items-center justify-between">
						<div className="flex items-center gap-2">
							<span className="font-medium">Enable Display Recording</span>
							<div className="badge badge-warning badge-sm">Alpha</div>
						</div>
						<input
							type="checkbox"
							name="enableDisplayRecording"
							checked={settings.enableDisplayRecording}
							onChange={(e) => updateSettings({enableDisplayRecording: e.target.checked})}
							className="toggle toggle-primary"
						/>
					</div>
					{settings.enableDisplayRecording && (
						<div className="mt-3 bg-amber-900 bg-opacity-30 border border-amber-500 rounded px-3 py-2 text-amber-400 text-sm flex items-center">
							<MdWarning className="h-5 w-5 mr-2 flex-shrink-0" />
							<span>
								This feature enables recording of games that do not support game hook.
								<strong className="text-amber-300"> WARNING: This WILL cause lag</strong> during gameplay as it uses display capture instead of game capture.
								For more details, see <a href="https://github.com/Segergren/Segra/issues/1" target="_blank" rel="noopener noreferrer" className="text-amber-300 hover:text-amber-200 underline">GitHub Issue #1</a>.
							</span>
						</div>
					)}
				</div>
			</div>

			{/* Version */}
			<div className="text-center mt-4 text-sm text-gray-500">
				<div className="flex flex-col items-center gap-2">
					<button
						onClick={() => sendMessageToBackend('OpenLogsLocation')}
						className="btn btn-sm btn-secondary text-gray-400 hover:text-gray-300 flex items-center justify-center"
					>
						<MdOutlineDescription className="text-lg flex-shrink-0" aria-hidden="true" />
						<span className="inline-block">View Logs</span>
					</button>
					<div>Segra {__APP_VERSION__}</div>
				</div>
			</div>
		</div>
	);
}
