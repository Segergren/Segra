import {useState, useEffect, useCallback} from "react";
import {PreRecording, Recording, GameResponse} from "../Models/types";
import {LuGamepad2} from "react-icons/lu";
import {BsDisplay} from "react-icons/bs";
import {useSettings} from "../Context/SettingsContext";

interface RecordingCardProps {
	recording?: Recording;
	preRecording?: PreRecording;
}

const RecordingCard: React.FC<RecordingCardProps> = ({recording, preRecording}) => {
	const [elapsedTime, setElapsedTime] = useState({hours: 0, minutes: 0, seconds: 0});
	const {showGameBackground} = useSettings();
	const [coverUrl, setCoverUrl] = useState<string | null>(null);

	useEffect(() => {
		if(preRecording) {
			setElapsedTime({hours: 0, minutes: 0, seconds: 0});
			return;
		}

		if (!recording?.startTime) return;

		const startTime = new Date(recording.startTime).getTime(); // Get the timestamp in milliseconds

		const updateElapsedTime = () => {
			const now = Date.now(); // Current time in milliseconds
			const secondsElapsed = Math.max(0, Math.floor((now - startTime) / 1000));

			const hours = Math.floor(secondsElapsed / 3600);
			const minutes = Math.floor((secondsElapsed % 3600) / 60);
			const seconds = secondsElapsed % 60;

			setElapsedTime({hours, minutes, seconds});
		};

		// Update the timer every second
		const intervalId = setInterval(updateElapsedTime, 1000);

		// Clean up the interval when the component unmounts
		return () => clearInterval(intervalId);
	}, [recording?.startTime, preRecording]);

	// Fetch game data from Segra.tv API
	const fetchGameData = useCallback(async () => {
		// Skip API call entirely if game background is disabled
		if (!showGameBackground) {
			setCoverUrl(null);
			return;
		}
		
		const gameName = preRecording ? preRecording.game : recording?.game;
		
		// Don't fetch for "Manual Recording"
		if (!gameName || gameName === "Manual Recording") {
			setCoverUrl(null);
			return;
		}
		
		try {
			const response = await fetch(`https://segra.tv/api/games/search?name=${encodeURIComponent(gameName)}`);
			
			if (!response.ok) {
				throw new Error('Game not found');
			}
			
			const data: GameResponse = await response.json();
			
			// If we have a cover image_id, fetch the cover URL
			if (data.game?.cover?.image_id) {
				setCoverUrl(`https://segra.tv/api/games/cover/${data.game.cover.image_id}`);
			}
		} catch (error) {
			console.error('Error fetching game data:', error);
			setCoverUrl(null);
		}
	}, [preRecording, recording, setCoverUrl, showGameBackground]);
	
	// Call fetchGameData when game changes
	useEffect(() => {
		fetchGameData();
	}, [fetchGameData]);

	return (
		<div className="mb-2 px-2">
			<div className="bg-base-300 border border-primary border-opacity-75 rounded-lg px-3 py-3.5 cursor-default relative">
				{/* Background image with game cover */}
				{coverUrl && showGameBackground && (
					<div className="absolute inset-0 z-0 opacity-15">
						<div className="absolute inset-0 rounded-[7px]" style={{
							backgroundImage: `url(${coverUrl})`,
							backgroundSize: "cover",
							backgroundPosition: "center",
							backgroundRepeat: "no-repeat"
						}}></div>
					</div>
				)}

				{/* Recording Indicator */}
				<div className="flex items-center mb-1 relative z-10">
					<div className="flex items-center">
						<span className={`w-3 h-3 rounded-full mr-2 ${preRecording ? 'bg-orange-500' : 'bg-red-500 animate-pulse'}`}></span>
						<span className="text-gray-200 text-sm font-medium">
							{preRecording ? preRecording.status : 'Recording'}
						</span>
					</div>
					{!preRecording && (
						<div className={`tooltip tooltip-right ${recording?.isUsingGameHook ? 'tooltip-success' : 'tooltip-warning'} flex items-center ml-1.5`} data-tip={`${recording?.isUsingGameHook ? 'Game capture (using game hook)' : 'Display capture (not using game hook)'}`}>
							<div className={`swap swap-flip cursor-default overflow-hidden justify-center`}>
								<input type="checkbox" checked={recording?.isUsingGameHook} />
								<div className={`swap-on`}>
									<LuGamepad2 className="h-5 w-5 text-gray-300" />
								</div>	
								<div className={`swap-off`}>
									<BsDisplay className="h-5 w-5 text-gray-300 scale-90" />
								</div>
							</div>
						</div>
					)
				}
				</div>

				{/* Recording Details */}
				<div className="flex items-center text-gray-400 text-sm relative z-10">
					<div className="flex items-center max-w-[105%]">
						<span className="countdown">
							{elapsedTime.hours > 0 && (
								<>
									<span style={{"--value": elapsedTime.hours} as React.CSSProperties}></span>:
								</>
							)}
							<span style={{"--value": elapsedTime.minutes} as React.CSSProperties}></span>:
							<span style={{"--value": elapsedTime.seconds} as React.CSSProperties}></span>
						</span>
						<p className="truncate ml-2">{preRecording ? preRecording.game : recording?.game}</p>
					</div>
				</div>
			</div>
		</div>
	)
};

export default RecordingCard;
