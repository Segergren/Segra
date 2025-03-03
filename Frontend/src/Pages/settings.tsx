import React, {useEffect, useState} from 'react';
import {useSettings, useSettingsUpdater} from '../Context/SettingsContext';
import {sendMessageToBackend} from '../Utils/MessageUtils';
import {themeChange} from 'theme-change';
import {AudioDevice} from '../Models/types';
import {supabase} from '../lib/supabase/client';
import {FaDiscord} from 'react-icons/fa';
import {useAuth} from '../Hooks/useAuth';
import {useProfile} from '../Hooks/userProfile';
import {MdOutlineLogout, MdWarning} from 'react-icons/md';
import {useUpdate} from '../Context/UpdateContext';

export default function Settings() {
	const {session} = useAuth();
	const {data: profile, error: profileError} = useProfile();
	const [authInProgress, setAuthInProgress] = useState(false);
	const [error, setError] = useState('');
	const [email, setEmail] = useState('');
	const [password, setPassword] = useState('');
	const {openReleaseNotesModal} = useUpdate();
	const settings = useSettings();
	const updateSettings = useSettingsUpdater();
	const [localStorageLimit, setLocalStorageLimit] = useState<number>(settings.storageLimit);

	// Handle OAuth callback and initial session check
	useEffect(() => {
		const handleAuthCallback = async () => {
			try {
				const urlParams = new URLSearchParams(window.location.search);
				const code = urlParams.get('code');

				if (code) {
					setAuthInProgress(true);
					const {error} = await supabase.auth.exchangeCodeForSession(code);

					if (error) throw error;
					// Clean URL after successful login
					if(session) {
						sendMessageToBackend("Login", {
						  accessToken: session.access_token,
						  refreshToken: session.refresh_token
						});
					  }
					window.history.replaceState({}, document.title, window.location.pathname);
				}
			} catch (err) {
				setError(err instanceof Error ? err.message : 'Authentication failed');
			} finally {
				setAuthInProgress(false);
			}
		};

		handleAuthCallback();
	}, []);

	// Rest of your existing settings logic
	useEffect(() => {
		setLocalStorageLimit(settings.storageLimit);
	}, [settings.storageLimit]);

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
		setAuthInProgress(true);
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
		} finally {
			setAuthInProgress(false);
		}
	};

	const handleEmailLogin = async (e: React.FormEvent) => {
		e.preventDefault();
		setAuthInProgress(true);
		setError('');
		try {
			const {error} = await supabase.auth.signInWithPassword({email, password});
			if (error) throw error;
		} catch (err) {
			setError(err instanceof Error ? err.message : 'Login failed');
		} finally {
			setAuthInProgress(false);
		}
	};

	const handleLogout = async () => {
		await supabase.auth.signOut({scope: 'local'});
	};

	// Auth UI components
	const authSection = !session ? (
		<div className="card bg-base-300 shadow-xl mb-8">
			<div className="card-body">
				<h2 className="card-title text-2xl font-bold mb-4 justify-center">Login to Segra</h2>

				{error && <div className="alert alert-error text-white mb-4">{error}</div>}

				<button
					onClick={handleDiscordLogin}
					disabled={authInProgress}
					className={`btn btn-neutral w-full gap-2 font-semibold text-white ${authInProgress ? 'loading' : ''}`}
				>
					<FaDiscord className="w-5 h-5" />
					{authInProgress ? 'Connecting...' : 'Continue with Discord'}
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
							disabled={authInProgress}
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
							disabled={authInProgress}
							required
						/>
					</div>

					<button
						type="submit"
						disabled={authInProgress}
						className={`btn btn-neutral w-full font-semibold text-white ${authInProgress ? 'loading' : ''}`}
					>
						Sign in with Email
					</button>
				</form>
			</div>
		</div>
	) : (
		<div className="space-y-4">
			<div className="flex items-center justify-between flex-wrap gap-4">
				<div className="flex items-center gap-4 min-w-0">
					{/* Avatar Container */}
					<div className="relative w-20 h-20">
						<div className="w-full h-full rounded-full overflow-hidden bg-base-200 bg-base-300 ring-4 ring-base-300">
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

					{/* Profile Info with Dropdown */}
					<div className="min-w-0 flex-1">
						<div className="flex items-center gap-2">
							<h1 className="text-3xl font-bold truncate flex items-center gap-2">
								{profile?.username ? (
									profile.username
								) : (
									<div className="skeleton h-[36px] w-24"></div>
								)}
							</h1>
							<div className="dropdown dropdown-end" onClick={(e) => e.stopPropagation()}>
								<div tabIndex={0} role="button" className="btn btn-ghost btn-xs p-1 h-8 w-8">
									<svg fill="currentColor" viewBox="0 0 32.055 32.055">
										<path d="M3.968,12.061C1.775,12.061,0,13.835,0,16.027c0,2.192,1.773,3.967,3.968,3.967c2.189,0,3.966-1.772,3.966-3.967 C7.934,13.835,6.157,12.061,3.968,12.061z M16.233,12.061c-2.188,0-3.968,1.773-3.968,3.965c0,2.192,1.778,3.967,3.968,3.967 s3.97-1.772,3.97-3.967C20.201,13.835,18.423,12.061,16.233,12.061z M28.09,12.061c-2.192,0-3.969,1.774-3.969,3.967 c0,2.19,1.774,3.965,3.969,3.965c2.188,0,3.965-1.772,3.965-3.965S30.278,12.061,28.09,12.061z"></path>
									</svg>
								</div>
								<ul tabIndex={0} className="dropdown-content menu bg-base-300 rounded-box z-[999] w-52 p-2 shadow">
									<li>
										<button
											onClick={() => {
												(document.activeElement as HTMLElement).blur();
												handleLogout();
											}}
											className="flex w-full items-center gap-2 px-4 py-3 text-error hover:bg-error/10 active:bg-error/20 rounded-lg transition-all duration-200 hover:pl-5 outline-none"
											aria-busy={false}
										>
											<MdOutlineLogout size="20" />
											Logout
										</button>
									</li>
								</ul>
							</div>
						</div>
					</div>
				</div>
			</div>

			{/* Error State */}
			{profileError && (
				<div
					className="alert alert-error mt-3"
					role="alert"
					aria-live="assertive"
				>
					<svg xmlns="http://www.w3.org/2000/svg" className="stroke-current shrink-0 h-6 w-6" fill="none" viewBox="0 0 24 24">
						<path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M10 14l2-2m0 0l2-2m-2 2l-2-2m2 2l2 2m7-2a9 9 0 11-18 0 9 9 0 0118 0z" />
					</svg>
					<div>
						<h3 className="font-bold">Profile load failed!</h3>
						<div className="text-xs">{profileError.message || 'Unknown error occurred'}</div>
					</div>
				</div>
			)}
		</div>
	);
	// Helper function to check if the selected device is available
	const isDeviceAvailable = (deviceId: string, devices: AudioDevice[]) => {
		return devices.some((device) => device.id === deviceId);
	};

	// Get the name of the selected input device, or indicate if it's unavailable
	const selectedInputDevice = settings.state.inputDevices.find((device) => device.id === settings.inputDevice);
	const inputDeviceName = selectedInputDevice ? selectedInputDevice.name : settings.inputDevice ? 'Unavailable Device' : 'Select Input Device';

	// Get the name of the selected output device, or indicate if it's unavailable
	const selectedOutputDevice = settings.state.outputDevices.find((device) => device.id === settings.outputDevice);
	const outputDeviceName = selectedOutputDevice ? selectedOutputDevice.name : settings.outputDevice ? 'Unavailable Device' : 'Select Output Device';
	return (
		<div className="p-5 space-y-6 rounded-lg">
			<h1 className="text-3xl font-bold">Settings</h1>
			{authSection}

			{/* Segra AI Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md">
				<h2 className="text-xl font-semibold mb-4">Segra AI</h2>
				<div className="bg-base-100 p-4 rounded-lg">
					<div className="flex items-center justify-between">
						<div className="flex items-center gap-2">
							<span className="font-medium">Enable Segra AI</span>
						</div>
						<input
							type="checkbox"
							name="enableAI"
							checked={settings.enableAi}
							onChange={(e) => updateSettings({enableAi: e.target.checked})}
							className="toggle"
						/>
					</div>
				</div>
			</div>

			{/* Video Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md">
				<h2 className="text-xl font-semibold mb-4">Video Settings</h2>
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
			<div className="p-4 bg-base-300 rounded-lg shadow-md">
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

			{/* Input/Output Devices */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md">
				<h2 className="text-xl font-semibold mb-4">Input/Output Devices</h2>
				<div className="grid grid-cols-2 gap-4">
					{/* Input Device */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Input Device</span>
						</label>
						<div className="relative">
							<select
								name="inputDevice"
								value={settings.inputDevice}
								onChange={handleChange}
								className="select select-bordered w-full"
							>
								<option value={''}>
									Select Input Device
								</option>
								{/* If the selected device is not available, show it with a warning */}
								{!isDeviceAvailable(settings.inputDevice, settings.state.inputDevices) && settings.inputDevice && (
									<option value={settings.inputDevice}>
										⚠️ &lrm;
										{inputDeviceName}
									</option>
								)}
								{/* List available input devices */}
								{settings.state.inputDevices.map((device) => (
									<option key={device.id} value={device.id}>
										{device.name}
									</option>
								))}
							</select>
						</div>
					</div>

					{/* Output Device */}
					<div className="form-control">
						<label className="label">
							<span className="label-text">Output Device</span>
						</label>
						<div className="relative">
							<select
								name="outputDevice"
								value={settings.outputDevice}
								onChange={handleChange}
								className="select select-bordered w-full"
							>
								<option value={''}>
									Select Output Device
								</option>
								{/* If the selected device is not available, show it with a warning */}
								{!isDeviceAvailable(settings.outputDevice, settings.state.outputDevices) && settings.outputDevice && (
									<option value={settings.outputDevice}>
										⚠️ &lrm;
										{outputDeviceName}
									</option>
								)}
								{/* List available output devices */}
								{settings.state.outputDevices.map((device) => (
									<option key={device.id} value={device.id}>
										{device.name}
									</option>
								))}
							</select>
						</div>
					</div>
				</div>
			</div>

			{/* Advanced Settings */}
			<div className="p-4 bg-base-300 rounded-lg shadow-md">
				<h2 className="text-xl font-semibold mb-4">Advanced Settings</h2>
				<div className="bg-base-100 p-4 rounded-lg">
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
							className="toggle toggle-warning"
						/>
					</div>

					<div className="mt-3 bg-amber-900 bg-opacity-30 border border-amber-500 rounded px-3 py-2 text-amber-400 text-sm flex items-center">
						<MdWarning className="h-5 w-5 mr-2 flex-shrink-0" />
						<span>
							This feature enables recording of games that do not support game hook.
							<strong className="text-amber-300"> WARNING: This WILL cause lag</strong> during gameplay as it uses display capture instead of game capture.
							For more details, see <a href="https://github.com/Segergren/Segra/issues/1" target="_blank" rel="noopener noreferrer" className="text-amber-300 hover:text-amber-200 underline">GitHub Issue #1</a>.
						</span>
					</div>
				</div>
			</div>

			{/* Version */}
			<div className="text-center mt-4 text-sm text-gray-500">
				<div className="flex flex-col items-center gap-2">
					<button
						onClick={() => openReleaseNotesModal(null)}
						className="btn btn-sm btn-ghost text-gray-400 hover:text-gray-300"
					>
						View Release Notes
					</button>
					<div>Segra {__APP_VERSION__}</div>
				</div>
			</div>
		</div>
	);
}
