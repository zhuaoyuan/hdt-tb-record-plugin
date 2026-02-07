using System;
using System.Diagnostics;
using System.IO;
using Hearthstone_Deck_Tracker.Utility.Logging;
using Newtonsoft.Json;

namespace HdtTbRecordPlugin
{
	public class PluginConfig
	{
		// JSON 记录输出目录。
		public string OutputDirectory { get; set; } = DefaultOutputDirectory;
		// 是否尝试从日志推断商店阶段行为。
		public bool RecordShopEvents { get; set; } = true;
		// 是否保存原始 block 事件用于离线解析。
		public bool SaveRawBlocks { get; set; } = true;
		// 是否保留解析过程日志（默认关闭）。
		public bool KeepDebugLogs { get; set; } = false;
		// 解析过程日志路径。
		public string DebugLogPath { get; set; } = DefaultDebugLogPath;

		public static string PluginDirectory =>
			Path.Combine(Hearthstone_Deck_Tracker.Config.AppDataPath, "Plugins", "hdt-tb-record-plugin");

		public static string DefaultOutputDirectory =>
			Path.Combine(PluginDirectory, "records");

		public static string DefaultDebugLogPath =>
			Path.Combine(PluginDirectory, "debug.log");

		public static string ConfigPath =>
			Path.Combine(PluginDirectory, "config.json");

		public static PluginConfig Load()
		{
			try
			{
				// 若存在配置文件则直接读取。
				if(File.Exists(ConfigPath))
				{
					var json = File.ReadAllText(ConfigPath);
					var cfg = JsonConvert.DeserializeObject<PluginConfig>(json);
					if(cfg != null)
						return cfg;
				}
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
			// 首次运行或读取失败时写入默认配置。
			var defaultConfig = new PluginConfig();
			defaultConfig.Save();
			return defaultConfig;
		}

		public void Save()
		{
			try
			{
				// 确保插件配置目录存在。
				Directory.CreateDirectory(PluginDirectory);
				var json = JsonConvert.SerializeObject(this, Formatting.Indented);
				File.WriteAllText(ConfigPath, json);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		public static void OpenConfigFile()
		{
			try
			{
				// 打开前确保配置文件已存在。
				var cfg = Load();
				cfg.Save();
				Process.Start(new ProcessStartInfo
				{
					FileName = ConfigPath,
					UseShellExecute = true
				});
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		public static void OpenOutputDirectory(string? outputDirectory)
		{
			try
			{
				// 打开（并创建）输出目录。
				var path = string.IsNullOrWhiteSpace(outputDirectory) ? DefaultOutputDirectory : outputDirectory;
				Directory.CreateDirectory(path);
				Process.Start(new ProcessStartInfo
				{
					FileName = path,
					UseShellExecute = true
				});
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}
	}
}
