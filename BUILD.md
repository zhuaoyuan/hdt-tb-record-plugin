# 构建与安装

## 前置条件
- Windows
- Visual Studio 开发者命令提示符（随 VS 或 Build Tools 安装）
- 已克隆 HDT 源码到 `C:\projects\github\Hearthstone-Deck-Tracker`

## 构建
在 **VS 开发者命令提示符** 中，进入插件目录执行：

```
msbuild hdt-tb-record-plugin.csproj /p:Configuration=Release
```

如果你在 **PowerShell** 中执行，可用一条命令先加载 VS 开发者环境再构建：

```
& "C:\Windows\System32\cmd.exe" /c '"C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" && msbuild "c:\projects\github\                                                                                                           \hdt-tb-record-plugin\hdt-tb-record-plugin.csproj" /p:Configuration=Release'
```

输出目录：
- `C:\projects\github\hdt-tb-record-plugin\bin\Release\net472\`

## 安装到 HDT
将生成的插件 DLL 拷贝到：

```
%AppData%\HearthstoneDeckTracker\Plugins
```

然后在 HDT 中：
- 进入 `Options > Tracker > Plugins`
- 启用插件

## 配置与输出
- 配置文件：`%AppData%\HearthstoneDeckTracker\Plugins\hdt-tb-record-plugin\config.json`
- 输出目录：`%AppData%\HearthstoneDeckTracker\Plugins\hdt-tb-record-plugin\records`

可在插件按钮中打开配置文件，或通过插件菜单打开输出目录。
