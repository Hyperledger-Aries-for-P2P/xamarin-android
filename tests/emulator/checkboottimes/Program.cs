using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace checkboottimes
{
	class Program
	{
		static string adbPath;
		static string avdManagerPath;
		static string emulatorPath;
		static string sdkManagerPath;
		static string deviceName;
		static int executionTimes = 1;

		static async Task<int> Main (string [] args)
		{
			Console.WriteLine ($"Testing emulator startup times. This may take several minutes.");

			if (args.Length > 0) {
				if (!int.TryParse (args [0], out executionTimes)) {
					Console.WriteLine ($"Argument should be integer. It represents number of times we want to run emulator. If not provided default value equals 1");
					Console.WriteLine ($"Usage:{Environment.NewLine} checkboottimes [executionTimes]{Environment.NewLine}");
					Console.WriteLine ($"i.e: checkboottimes 5");
					return 1;
				}
			}

			if (!ToolsExist ()) {
				return 1;
			}

			if (!await CheckAccelerationType ()) {
				return 1;
			}

			if (!await CheckVirtualDeviceExistsAndTryToCreateIfNeeded (false /* use skin */, false /* use Pixel device */)) {
				return 1;
			}

			if (await GetBootTime (true /*cold boot */) < 0) {
				return 1;
			}

			if (await GetBootTime (false /* cold boot */) < 0) {
				return 1;
			}

			if (!await CheckVirtualDeviceExistsAndTryToCreateIfNeeded (true /* use skin */, true /* use Pixel device */)) {
				return 1;
			}

			if (await GetBootTime (true /*cold boot */) < 0) {
				return 1;
			}

			if (await GetBootTime (false /* cold boot */) < 0) {
				return 1;
			}

			return 0;
		}

		static async Task<double> GetBootTime (bool coldBoot)
		{
			var times = new List<double> ();
			int errors = 0;

			for (int i = 0; i < executionTimes; i++) {
				bool validation (string data, ManualResetEvent mre)
				{
					if (!string.IsNullOrWhiteSpace (data) && data.StartsWith ("emulator: ERROR: x86 emulation currently requires hardware acceleration!", StringComparison.OrdinalIgnoreCase)) {
						Console.WriteLine (data);
						errors++;
						mre.Set ();
						return false;
					}

					var targetString = "emulator: INFO: boot time";
					if (!string.IsNullOrWhiteSpace (data) && data.StartsWith (targetString, StringComparison.OrdinalIgnoreCase)) {
						data = data.Substring (targetString.Length + 1, data.Length - (targetString.Length + 4));
						times.Add (double.Parse (data));
						mre.Set ();
					}

					return true;
				}

				var bootOptions = string.Empty;
				if (coldBoot) {
					bootOptions = "-no-snapshot-load -wipe-data";
				}

				await RunProcess (emulatorPath, $"-avd {deviceName} {bootOptions} ", 300000, validation);

				var activity = i % 2 == 0 ? "com.google.android.apps.photos/.home.HomeActivity" : "com.android.settings/.wifi.WifiStatusTest";
				await RunProcess (adbPath, $"-e shell am start -n '{activity}'", 5000, (data, mre) => {
					if (!string.IsNullOrWhiteSpace (data) && data.IndexOf ("com.android") != -1) {
						mre.Set ();
					}
					return true;
				});

				await KillEmulator ();
			}

			if (errors > 0) {
				Console.WriteLine ($"Unable to boot emulator {errors} time(s)");
			}

			if (times.Count == 0) {
				return -1;
			}

			var time = times.Average ();
			Console.WriteLine ($"{deviceName} Average {(coldBoot ? "Cold" : "Hot")} Boot Time for {times.Count} run(s) out of {executionTimes} request(s): {time} ms");
			return time;
		}

		static async Task<bool> KillEmulator ()
		{
			bool validation (string data, ManualResetEvent mre)
			{
				if (!string.IsNullOrWhiteSpace (data) && data.StartsWith ("OK: killing emulator, bye bye", StringComparison.OrdinalIgnoreCase)) {
					mre.Set ();
				}

				return true;
			}

			if (!await RunProcess (adbPath, "-s emulator-5554 emu kill", 30000, validation)) {
				Console.WriteLine ("unable to quit emulator.");
				return false;
			}

			return true;
		}

		static async Task<bool> CheckAccelerationType ()
		{
			var accel = new List<string> ();
			bool validation (string data, ManualResetEvent mre)
			{
				if (!string.IsNullOrWhiteSpace (data)) {
					if (!data.StartsWith ("accel", StringComparison.OrdinalIgnoreCase)) {
						accel.Add (data);
					}

					if (data.EndsWith ("accel", StringComparison.OrdinalIgnoreCase)) {
						mre.Set ();
					}
				}

				return true;
			}

			if (!await RunProcess (emulatorPath, "-accel-check", 1000, validation)) {
				Console.WriteLine ("unable to detect acceleration type.");
				return false;
			}

			Console.WriteLine ($"Acceleration type: {string.Join (", ", accel)}");
			return true;
		}

		static async Task<bool> CheckVirtualDeviceExistsAndTryToCreateIfNeeded (bool useSkin, bool usePixelDevice)
		{
			deviceName = "XamarinPerfTest" + (usePixelDevice ? "Pixel" : string.Empty) + (useSkin ? "WithSkin" : string.Empty);

			if (await CheckVirtualDeviceExists ()) {
				return true;
			} else {
				Console.WriteLine ($"{deviceName} virtual device not found.");
				return await CreateVirtualDevice (useSkin, usePixelDevice);
			}
		}

		static async Task<bool> CheckVirtualDeviceExists ()
		{
			bool validation (string data, ManualResetEvent mre)
			{
				if (!string.IsNullOrWhiteSpace (data) && data == deviceName) {
					mre.Set ();
				}

				return true;
			}

			return await RunProcess (emulatorPath, "-list-avds", 10000, validation);
		}

		static async Task<bool> CreateVirtualDevice (bool useSkin, bool usePixelDevice)
		{
			Console.WriteLine ($"Creating {deviceName} virtual device.");

			await InstallSdk ();

			var contents = new List<string> ();
			bool validation (string data, ManualResetEvent mre)
			{
				contents.Add (data);
				if (!string.IsNullOrWhiteSpace (data) &&
					(data.IndexOf ("Do you wish to create a custom hardware profile?", StringComparison.OrdinalIgnoreCase) != -1 ||
					data.IndexOf ("100% Fetch remote repository...", StringComparison.OrdinalIgnoreCase) != -1)) {
					mre.Set ();
				}

				return true;
			}

			var filename = RunningOnWindowsEnvironment ? "cmd" : "bash";

			string deviceType = string.Empty;
			if (usePixelDevice) {
				deviceType = "--device \"pixel\"";
			}

			var args = $"{(RunningOnWindowsEnvironment ? "/" : "-")}c \" echo no | {avdManagerPath} create avd --force --name {deviceName} --abi google_apis_playstore/x86 --package system-images;android-29;google_apis_playstore;x86 {deviceType} \"";
			await RunProcess (filename, args, 10000, validation);
			await Task.Delay (5000);
			if (!await CheckVirtualDeviceExists ()) {
				Console.WriteLine ($"unable to create {deviceName} virtual device.");
				foreach (var data in contents) {
					Console.WriteLine (data);
				}

				return false;
			}

			if (useSkin) {
				if (!await UpdateVirtualDevice ()) {
					Console.WriteLine ($"unable to update {deviceName} with skin.");
					return false;
				}
			}

			Console.WriteLine ($"{deviceName} virtual device created.");
			return true;
		}

		static async Task<bool> UpdateVirtualDevice ()
		{
			var homePath = GetHomePath ();
			var vdPath = Path.Combine (homePath, $".android/avd/{deviceName}.avd/config.ini");

			if (!File.Exists (vdPath)) {
				return false;
			}

			var sdkPath = new FileInfo (emulatorPath).Directory.Parent.FullName;

			var content = "skin.name=pixel_2" + Environment.NewLine;
			content += "skin.dynamic=yes" + Environment.NewLine;
			content += $"skin.path={sdkPath}/skins/pixel_2" + Environment.NewLine;

			await File.AppendAllTextAsync (vdPath, content);

			return true;
		}

		static async Task InstallSdk ()
		{
			bool validation (string data, ManualResetEvent mre)
			{
				if (!string.IsNullOrWhiteSpace (data) && data.IndexOf ("100%") != -1) {
					mre.Set ();
				}

				return true;
			}

			var filename = RunningOnWindowsEnvironment ? "cmd" : "bash";
			var args = $"{(RunningOnWindowsEnvironment ? "/" : "-")}c \" {sdkManagerPath} --install system-images;android-29;google_apis_playstore;x86 \"";
			await RunProcess (filename, args, 600000, validation);
		}

		static Task<bool> RunProcess (string filename, string arguments, int timeout, Func<string, ManualResetEvent, bool> validation, Action processTimeout = null)
		{
			return Task.Run (() => {
				using (var process = new Process ()) {

					process.StartInfo.FileName = filename;
					process.StartInfo.Arguments = arguments;
					process.StartInfo.UseShellExecute = false;
					process.StartInfo.CreateNoWindow = true;
					process.StartInfo.WindowStyle = ProcessWindowStyle.Hidden;
					process.StartInfo.RedirectStandardOutput = true;
					process.StartInfo.RedirectStandardOutput = true;
					process.StartInfo.RedirectStandardError = true;
					process.EnableRaisingEvents = true;

					var mre = new ManualResetEvent (false /* -> nonsignaled */);

					bool error = false;
					void dataReceived (object sender, DataReceivedEventArgs args)
					{
						if (!validation (args.Data, mre)) {
							error = true;
						}
					}

					process.OutputDataReceived += dataReceived;
					process.ErrorDataReceived += dataReceived;

					process.Start ();

					process.BeginOutputReadLine ();
					process.BeginErrorReadLine ();

					if (!mre.WaitOne (timeout)) {
						processTimeout?.Invoke ();

						if (!process.WaitForExit (timeout)) {
							if (!process.HasExited) {
								process.Kill ();
							}
						}

						return false;
					}

					if (error) {
						return false;
					}
				}

				return true;
			});
		}
		static bool ToolsExist ()
		{
			adbPath = GetProgramPath ("adb", "adb.exe", "adb.bat");
			if (string.IsNullOrWhiteSpace (adbPath)) {
				Console.WriteLine ("adb not on path.");
				return false;
			}

			avdManagerPath = GetProgramPath ("avdmanager", "avdmanager.exe", "avdmanager.bat");
			if (string.IsNullOrWhiteSpace (avdManagerPath)) {
				Console.WriteLine ("avdmanager not on path.");
				return false;
			}

			emulatorPath = GetProgramPath ("emulator", "emulator.exe", "emulator.bat");
			if (string.IsNullOrWhiteSpace (emulatorPath)) {
				Console.WriteLine ("emulator not on path.");
				return false;
			}

			sdkManagerPath = GetProgramPath ("sdkmanager", "sdkmanager.exe", "sdkmanager.bat");
			if (string.IsNullOrWhiteSpace (sdkManagerPath)) {
				Console.WriteLine ("sdkmanager not on path.");
				return false;
			}

			return true;
		}

		static string GetProgramPath (params string [] filenames)
		{
			foreach (var filename in filenames) {
				var programPath = GetFullPath (filename);
				if (string.IsNullOrWhiteSpace (programPath)) {
					var homePath = GetHomePath ();

					var potentialLocations = new []{
						"AppData/Local/Android/Sdk/platform-tools",
						"AppData/Local/Android/Sdk/emulator",
						"AppData/Local/Android/Sdk/tools",
						"AppData/Local/Android/Sdk/tools/bin",
						"Library/Android/sdk/platform-tools",
						"Library/Android/sdk/emulator",
						"Library/Android/sdk/tools",
						"Library/Android/sdk/tools/bin",
						"android-toolchain/sdk/platform-tools",
						"android-toolchain/sdk/emulator",
						"android-toolchain/sdk/tools",
						"android-toolchain/sdk/tools/bin",
					};

					foreach (var location in potentialLocations) {
						var programLocation = Path.Combine (homePath, location, filename);
						if (File.Exists (programLocation)) {
							return Path.GetFullPath (programLocation);
						}
					}
				} else {
					return programPath;
				}
			}

			return null;
		}

		static string GetHomePath ()
		{
			var homePath = RunningOnWindowsEnvironment
						? Environment.ExpandEnvironmentVariables ("%HOMEDRIVE%%HOMEPATH%")
						: Environment.GetEnvironmentVariable ("HOME");

			return homePath;
		}

		static string GetFullPath (string fileName)
		{
			if (File.Exists (fileName)) {
				return Path.GetFullPath (fileName);
			}

			var values = Environment.GetEnvironmentVariable ("PATH");
			foreach (var path in values.Split (Path.PathSeparator)) {
				var fullPath = Path.Combine (path, fileName);
				if (File.Exists (fullPath))
					return fullPath;
			}
			return null;
		}

		static bool RunningOnWindowsEnvironment => !(Environment.OSVersion.Platform == PlatformID.Unix || Environment.OSVersion.Platform == PlatformID.MacOSX);
	}
}
