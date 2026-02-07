using System;
using System.Windows.Controls;
using Hearthstone_Deck_Tracker.API;
using Hearthstone_Deck_Tracker.Hearthstone;
using HearthWatcher.EventArgs;
using Hearthstone_Deck_Tracker.Plugins;
using Hearthstone_Deck_Tracker.Utility.Logging;

namespace HdtTbRecordPlugin
{
	public class PluginMain : IPlugin
	{
		// 单例解析器，用于从 Power.log 累积当前对局状态。
		private readonly PowerLogParser _parser = new PowerLogParser();
		private bool _active;
		private PluginConfig? _config;
		private MenuItem? _menuItem;

		public string Name => "HDT TB Record Plugin";
		public string Description => "Record Battlegrounds combat rounds to JSON files.";
		public string ButtonText => "Open Config";
		public string Author => "hdt-tb-record-plugin";
		public Version Version => new Version(0, 1, 0);
		public MenuItem MenuItem => _menuItem;

		public void OnLoad()
		{
			// 载入配置并开始监听 Power.log 行。
			_config = PluginConfig.Load();
			_parser.Configure(_config);
			_active = true;
			LogEvents.OnPowerLogLine.Add(OnPowerLogLine);
			Watchers.OpponentBoardStateWatcher.Change += OnOpponentBoardStateChange;

			// 菜单项：快速打开记录输出目录。
			_menuItem = new MenuItem
			{
				Header = "Open TB Records Folder"
			};
			_menuItem.Click += (_, __) => PluginConfig.OpenOutputDirectory(_config?.OutputDirectory);
		}

		public void OnUnload()
		{
			// 停止处理并落盘可能未写完的记录。
			_active = false;
			Watchers.OpponentBoardStateWatcher.Change -= OnOpponentBoardStateChange;
			_parser.Flush();
		}

		public void OnButtonPress()
		{
			PluginConfig.OpenConfigFile();
		}

		public void OnUpdate()
		{
		}

		private void OnPowerLogLine(string line)
		{
			if(!_active)
				return;
			try
			{
				// 将日志行解析到内存状态与快照中。
				_parser.ProcessLine(line);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}

		private void OnOpponentBoardStateChange(object sender, OpponentBoardArgs args)
		{
			if(!_active)
				return;
			try
			{
				_parser.UpdateOpponentBoardFromWatcher(args);
			}
			catch(Exception ex)
			{
				Log.Error(ex);
			}
		}
	}
}
